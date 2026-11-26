# LimoClip 架构与模块实现说明

> 本文面向开发者，描述当前代码**如何工作**：解决方案结构、模块边界、关键数据结构与每个模块的具体实现方式。
> 产品介绍、下载与使用说明见 `README.md`；历史阶段设计文档已归档到根目录 `archive/`（仅用于追溯，不再维护）。
> 运行时以仓库当前代码为准；文中的「模块」均对应 `src/` 下的工程或子目录。
>
> 可视化架构图：[静态 SVG](docs/images/architecture.svg) · [可交互版本](https://heyiai02.github.io/LimoClip/architecture.html)（使用 Archify 3.3.0 生成，源 IR 见 `docs/architecture.source.json`）。

---

## 1. 产品定位与技术栈

LimoClip 是 Windows 桌面端的多平台直播**监控 + 录制 + 剪辑**工作台：

- 同时监控 20~30 路直播（默认自适应平铺 + 焦点放大）；
- 独立录制中心：开播自动录制、独立 ffmpeg 进程落盘、断电/崩溃抗性 MKV；
- 剪辑台：双波形（音频包络 + 弹幕密度）定位、无损切段、硬编回填两版导出、云端 ASR 逐句转写；
- 六平台接入：B 站、Twitch、YouTube、虎牙、斗鱼、抖音；另支持**自定义清流**（直接粘贴 HLS/FLV/RTMP/RTSP/SRT 等直链进行监控、录制与剪辑）；
- 插件化平台扩展：内置插件 + Ed25519 验签的外部插件热更新（GitHub 与 Gitee 双源）。

| 层 | 选型 |
| --- | --- |
| 运行环境 | .NET 8（`net8.0` 类库；`Data`/`Media`/`Desktop` 及其测试工程为 `net8.0-windows`），Windows 10/11 x64 |
| UI | Avalonia 11（Fluent + Semi.Avalonia 主题）、CommunityToolkit.Mvvm、Dock.Avalonia（可拖拽/序列化的布局系统） |
| 播放 | libmpv（LibMpv.Avalonia）进程内渲染，硬件解码自动降级 |
| 录制/转码 | 独立 FFmpeg 进程（随包 `tools/ffmpeg`），`-c copy` 不转码 |
| 弹幕 | B 站/虎牙/抖音 WebSocket；YouTube InnerTube 轮询；斗鱼原始 TCP（皆为自实现协议层） |
| 数据 | SQLite（Microsoft.Data.Sqlite，WAL），本地文件（MKV/JSONL/波形缓存） |
| 凭据 | Windows DPAPI（CurrentUser）加密后入库 |
| WebView 登录 | WebView2（临时用户目录，登录后采集 Cookie） |
| 平台解析 | 各家 HTTP API + Jint（斗鱼签名、抖音 sign.js）；yt-dlp 作为 YouTube/Twitch 兜底 |
| 插件签名 | Ed25519（BouncyCastle）+ SHA256 清单校验 |
| 测试 | xUnit（6 个测试工程，当前共 2990 个用例） |

### 平台支持矩阵

| 平台 | 枚举值 | 拉流 | 弹幕 | 登录/Cookie 用途 | 备注 |
| --- | --- | --- | --- | --- | --- |
| B 站 | 1 | 原生 API（FLV/DASH/HLS） | WebSocket 二进制协议 | 高画质、独轮车过滤所需信息、风控 | WBI 签名 |
| 抖音 | 2 | `enter` 接口 + FLV | Protobuf PushFrame（gzip/ack） | 完整 Cookie 提升画质/通过风控 | `sign.js` 运行时获取（AGPL，不随包） |
| 斗鱼 | 3 | `betard` + `getH5Play` | `danmuproxy:8601` STT 协议 | 提升画质/通过风控 | Jint 执行下发 JS 求签名 |
| 虎牙 | 4 | `mp.huya.com` 移动接口 + FLV | `wss://wsapi.huya.com`（Tars/Wup） | 高码率档位；线路优先 HS | CDN 单连接寿命短 → 分段录制 |
| Twitch | 5 | 原生 GQL + Usher HLS（`embed` 去广告）→ yt-dlp 兜底 | 原生 IRC WebSocket（`TwitchDanmakuClient`） | `auth-token` 用于 OAuth 头；可选大陆/去广告代理模板 | 去广告非永久保证 |
| YouTube | 6 | yt-dlp `-j` 解析（含分离音频轨） | InnerTube `live_chat` 轮询 | 登录态 Cookie 传给 yt-dlp 提升画质 | 需要 yt-dlp |
| 自定义清流 | 99 | 直链交给 libmpv / FFmpeg：http(s)（HLS/FLV/TS/MPD、无后缀 Token 直链）、rtmp(s)/rtsp(s)/srt/mms | 无（`NullDanmakuClient`） | 无需 Cookie；可选 Referer/UA 请求头（仅 http(s) 生效） | 无房间号概念（`cs_xxxxxxxx`）；手动开录，不参与自动录制轮询 |

---

## 2. 核心设计决策

1. **进程内多路 libmpv，而不是内嵌浏览器**：每路直播一个 `MpvPlayerView`（libmpv 上下文），避免 20+ 路 WebView 的内存/CPU 开销；解码默认硬解自动（`hwdec=auto-safe`），超出阈值可降级软解。
2. **预览与录制解耦**：监控用 libmpv，录制用独立 FFmpeg 进程；录制不依赖监控窗格是否可见/是否切到后台，切换视图不会中断录制。
3. **MP4 风险规避 → MKV + `-c copy`**：录制不转码，直接封装 H.264/AAC 到 MKV；崩溃时已写入的分片仍可播放。对「单连接寿命短」的 CDN（虎牙）额外采用**分段录制 + 合并**（见 §9.1）。
4. **房间主数据与窗口方案解耦**：`LiveRooms` 是全局房间库；`LayoutProfiles + LayoutSlots` 保存「哪些房间放在哪个窗格」；播放器实例按房间存在，切换方案时按 `LiveRoomId` 做差量复用（`LayoutDiffService`），保留房间的播放器不断流。
5. **弹幕是独立会话层**：`DanmakuManager` 按 `(平台, 房间号)` 维护连接引用计数，监控、录制、剪辑回放共享同一订阅；过滤器与高能检测在会话层统一处理。
6. **本地优先与凭据安全**：所有数据落本地 SQLite/文件；Cookie、ASR API Key 一律 DPAPI 加密；不经过任何第三方服务端。
7. **平台统一契约 + 插件化**：所有平台实现 `IPlatformProvider`/`IDanmakuClient`；通过 `IPlatformPlugin` 注册，内置插件与外部签名插件走同一加载/信任流程。
8. **稳定优先的容错策略**：断流显示「重连中」而不是「未开播」；自动重连 3/10/30s 退避 → 60s 复检；录制卡死 30s 判失败；录制异常退出自动续段（最多连续 6 次失败）；所有网络请求带超时与中文错误提示。

---

## 3. 解决方案结构与工程职责

```
LiveMonitor.sln
├─ src/
│  ├─ LiveMonitor.Core              # 领域模型、枚举、接口契约、配置键（无外部依赖）
│  ├─ LiveMonitor.Data              # SQLite 数据库、仓储、DPAPI 凭据存储
│  ├─ LiveMonitor.Platforms         # 平台工厂、弹幕工厂、插件宿主/加载/信任/热更新
│  ├─ LiveMonitor.Platforms.Common  # 平台公共设施（URL/JSON/yt-dlp/CliUpdater/工具路径）
│  ├─ LiveMonitor.Platforms.Bilibili# B 站原生 Provider + 弹幕 + WBI 签名
│  ├─ LiveMonitor.Platforms.Twitch  # Twitch 原生 Provider（GQL/Usher/embed）+ 弹幕
│  ├─ LiveMonitor.Plugins.YouTube   # YouTube 插件（yt-dlp + InnerTube 弹幕）
│  ├─ LiveMonitor.Plugins.Huya      # 虎牙插件（移动 API + Tars/Wup + wsapi 弹幕）
│  ├─ LiveMonitor.Plugins.Douyu     # 斗鱼插件（betard/getH5Play + Jint 签名 + STT 弹幕）
│  ├─ LiveMonitor.Plugins.Douyin    # 抖音插件（enter 接口 + Protobuf 弹幕 + sign.js）
│  ├─ LiveMonitor.Danmaku           # 弹幕会话、过滤器、高能检测、设置持久化
│  ├─ LiveMonitor.Media             # 录制（FFmpeg/分段/合并）、剪辑导出、波形、ASR、探针
│  └─ LiveMonitor.Desktop           # Avalonia 桌面壳、视图模型、播放器、服务装配
├─ tests/                           # 6 个 xUnit 测试工程（见 §13）
├─ scripts/                         # 启动器、打包、插件构建/签名、工具下载、PluginSigner
├─ tools/                           # 随包 ffmpeg / mpv（由 scripts/fetch-tools.ps1 准备）
├─ plugins/                         # providers-manifest.json（内置插件清单）
├─ docs/                            # 产品页 docs/index.html、截图、归档 design docs
└─ artifacts/                       # package-release.ps1 的发布产物（不入库）
```

### 工程依赖关系

```
Desktop ──► Media ──► Core
   │          │
   │          ├──► Danmaku ──► Core
   │          └──► Data ──► Core
   ├──► Platforms ──► Platforms.Common ──► Core
   │        │
   │        ├──► Platforms.Bilibili / Platforms.Twitch（内置平台实现）
   │        └──► Plugins.YouTube / Huya / Douyu / Douyin（内置插件实现）
   ├──► Data
   ├──► Danmaku
   └──► Core
```

> `Desktop` 只直接引用 `Platforms`（含四个插件工程），不单独引用各插件实现细节；插件通过 `BuiltInPlatformPlugins.CreateAll()` 统一注册。

### 工程职责一览

| 工程 | 职责要点 |
| --- | --- |
| `LiveMonitor.Core` | 平台/房间/方案/录制/剪辑/ASR 的全部模型与接口；`PlatformType` 数值固定（1=B站 2=抖音 3=斗鱼 4=虎牙 5=Twitch 6=YouTube），`AppSettingKeys` 汇总所有配置键 |
| `LiveMonitor.Data` | `LiveMonitorDatabase` 建库/迁移；9 张表的仓储实现；`CredentialStore` + `DpapiCookieProtector` 凭据读写 |
| `LiveMonitor.Platforms` | `PlatformFactory`（可替换的 Provider 表）、`DanmakuClientFactory`（可替换的弹幕工厂）、插件宿主 `PlatformPluginHost` 与加载/验签/热更新 |
| `LiveMonitor.Platforms.Common` | `PlatformDefaults`（UA/BaseUrl/Cookie 规范化）、`JsonRead`、`YtDlpRunner`（CliWrap）、`CliUpdater`、`ToolLocator`、`PluginVersionInfo/Comparer` |
| `LiveMonitor.Platforms.Bilibili` | 房间信息、播放地址（FLV/DASH/HLS）、WBI 签名、弹幕连接与二进制包解析 |
| `LiveMonitor.Platforms.Twitch` | GQL PlaybackAccessToken + Usher HLS 主列表解析（`embed` 参数去广告）、播放代理模板、yt-dlp 兜底、IRC 弹幕 |
| `LiveMonitor.Plugins.YouTube` | yt-dlp 解析直播/回放、分离音频轨、InnerTube 直播聊天轮询 + ack |
| `LiveMonitor.Plugins.Huya` | 移动 API 解析（含个性域名）、Tars/Wup 最小编解码、wsapi 弹幕、HS 线路优先 + `ratio` 档位画质、Cookie 注入 |
| `LiveMonitor.Plugins.Douyu` | betard 房间信息、Jint 执行下发签名 JS、getH5Play 拉流、TCP STT 弹幕（心跳/组播） |
| `LiveMonitor.Plugins.Douyin` | 首页取 `ttwid`、enter 接口拉流、Protobuf PushFrame 弹幕（gzip + needAck）、运行时加载 `sign.js` 签名 |
| `LiveMonitor.Danmaku` | 多平台弹幕会话管理、关键词/独轮车过滤、高能事件滑动窗口检测、设置持久化与热更新 |
| `LiveMonitor.Media` | 录制管理器与 FFmpeg 进程控制、分段合并去重、剪辑导出（无损/硬编）、波形/探针/硬编探测、云端 ASR 实现 |
| `LiveMonitor.Desktop` | 主窗口壳与四类视图（监控/录制中心/剪辑台/设置）、Dock 布局系统、播放器控件、服务装配与生命周期 |

> **命名契约（冻结）**：`LiveMonitor.*` 是内核与插件共享的程序集契约名（`LiveMonitor.Core`、`LiveMonitor.Platforms.Common` 等），插件 ALC 按简单程序集名共享加载，改名会破坏插件 ABI，v1.0 前不再变动。面向用户的品牌名只体现在外层 `AssemblyName`（`LimoClip`、`LimoClip.PluginSigner`）与发布产物。项目为 Windows 桌面应用（`Data`/`Media`/`Desktop` 为 `net8.0-windows`），如未来需要按平台拆分或做跨平台分析/测试，再按需补 `[SupportedOSPlatform("windows")]`，不引入平台条件编译分支。

---

## 4. 启动与依赖装配

### 4.1 启动链路

```
Program.Main
 └─ App.Initialize / OnFrameworkInitializationCompleted
     └─ MainWindow.Opened
         ├─ 构造 AppServices.CreateAsync(database, ...)          // 全部服务与仓储
         ├─ 构造 MainWindowViewModel(services, ...)
         ├─ DataContext = viewModel
         ├─ 绑定属性映射（Layout→Dock.Layout、视图切换→ApplyShellLayout 等）
         ├─ await viewModel.InitializeAsync()
         │    ├─ ReloadRoomsAsync（房间库）
         │    ├─ 加载/选择 LayoutProfile（无方案则创建默认 2×2）
         │    ├─ DanmakuManager.InitializeAsync（读过滤/高能设置）
         │    ├─ LoadProfileAsync（反序列化 Dock 布局、重建窗格与播放器）
         │    └─ 启动系统指标采样
         └─ 失败时写 startup-error.log 并打开 StartupErrorWindow
```

- 主窗口 `MainWindow.axaml` 固定骨架：`RootGrid` 行为 `48,*,28`（顶栏/内容/状态栏）、列为 `260,*`（侧栏/工作区）。
- 单实例约束由启动器/窗口激活逻辑保证（重复启动会提示已有实例，具体提示见 `scripts/refresh-and-run.ps1`）。
- 数据库打开失败时回退到 `bin/data` 并写 `startup-fallback.log`（仅应急，正常使用默认本地库）。

### 4.2 AppServices 装配顺序

`src/LiveMonitor.Desktop/AppServices.cs` 是唯一的依赖装配点，顺序如下（后创建者可依赖前者）：

1. `LiveMonitorDatabase`（打开/迁移数据库）；
2. `AppSettingsRepository`、`RoomRepository`、`LayoutProfileRepository`、`RecordingTargetRepository`、`RecordingSessionRepository`、`HighlightEventRepository`、`BlocklistRepository`；
3. `CredentialStore`（DPAPI）；
4. `HttpClient`（插件宿主专用，30s 超时，UA `LimoClip-PluginHost/1.0.0`）与 `PlatformFactory.CreateDefault(...)`、`DanmakuClientFactory`；
5. `DanmakuManager`（注入弹幕工厂 + 设置/黑名单仓储 + 高能事件仓储）；
6. `WorkspacePlayerManager`（监控播放器生命周期）、`SystemMetricsService`；
7. `RecordingManager`（录制目标轮询/自动录制/会话落库）与 FFmpeg 相关的 `FfmpegLocator`；
8. `VideoProbeService`、`WaveformService`、`HardwareEncoderDetector`、`ClipExporter`；
9. `AsrSettingsStore` + `OpenAiCompatibleAsrProvider`；
10. `PlatformPluginHost`（注入平台工厂/弹幕工厂/HttpClient/yt-dlp 路径），注册内置插件并加载已安装外部插件；`PluginUpdateService`、`CliUpdater`。

释放顺序与创建**相反**，分两层：`Window.Closing` 先调用 `MainWindowViewModel.Dispose` 做视图态退订（停止计时器与弹幕订阅、解除布局跟踪、销毁子 ViewModel 与播放器控件），随后调用 `AppServices.Dispose`（同步 `IDisposable`，幂等）在组合根内统一释放基础设施：剪辑导出 Job Object 与 ASR → 录制引擎（最多等 5 秒停止录制并 flush）→ 弹幕管理器（最多等 2 秒 flush 高能事件）→ 平台插件宿主与插件 `HttpClient` → 最后 `SqliteConnection.ClearAllPools()`。选择同步接口而非 `IAsyncDisposable`，是因为唯一调用点 `Window.Closing` 是同步事件；两个 `IAsyncDisposable` 依赖以有界等待收尾，超时也继续关闭。`LiveMonitorDatabase` 不持有长连接（每次操作 `OpenConnection()` 创建短生命周期连接、由 Microsoft.Data.Sqlite 连接池托管），因此自身无需实现 `IDisposable`，但退出前仍必须清空连接池才能关闭数据库/WAL 文件句柄（便于升级或备份时替换库文件）。

---

## 5. 数据层（LiveMonitor.Data）

### 5.1 数据库与连接

- SQLite 文件默认位于 `%LocalAppData%\LiveMonitor\livemonitor.db`。
- 每个连接执行：`PRAGMA journal_mode = WAL`（库级一次）+ `PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000`。
- `LiveMonitorDatabase` 负责建表与基于 `PRAGMA user_version` 的增量迁移（当前版本 5；V0.6-A 新增 `LiveRooms.CustomHeadersJson`）；数据库结构变更必须走迁移并递增版本。
- 访问封装：`SqliteValues` 统一读取可空值/时间；仓储实现类位于 `Repositories/`。

### 5.2 表结构

| 表 | 用途 | 关键列 |
| --- | --- | --- |
| `LiveRooms` | 房间库 | `Id, DisplayNumber, Platform, RoomId, RawUrl, Alias, GroupTag, CustomHeadersJson, CreatedAt` |
| `LayoutProfiles` | 窗口方案 | `Id, Name, IsCustomLayout, DockLayoutJson, CreatedAt/UpdatedAt` |
| `LayoutSlots` | 方案中的窗格槽位 | `Id, LayoutProfileId, LiveRoomId, PaneId, DefaultMuted, IsFocusSlot, PreferredQualityId, DefaultDanmakuVisible` |
| `PlatformCredentials` | 平台登录 Cookie | `Platform(PK), EncryptedCookie, IsExpired, UpdatedAt` |
| `DanmakuBlockKeywords` | 弹幕关键词黑名单 | `Id, Keyword, CreatedAt` |
| `AppSettings` | 键值设置 | `Key(PK), Value, UpdatedAt` |
| `HighlightEvents` | 高能事件 | `Id, RecordingSessionId, LiveRoomId, Platform, RoomId, OffsetSeconds, PeakCount, BaselineCount, DensityRatio, Reasons, WordHits, CreatedAt` |
| `RecordingTargets` | 自动录制目标 | `Id, LiveRoomId, OutputDirectory, IsEnabled, UpdatedAt` |
| `RecordingSessions` | 录制历史 | `Id, LiveRoomId, RoomDisplayName, Platform, RoomId, Status, StartedAt, EndedAt, DurationSeconds, FileSizeBytes, DanmakuCount, HighlightCount, VideoFilePath, DanmakuFilePath, ErrorMessage` |

### 5.3 仓储职责

| 仓储 | 关键行为 |
| --- | --- |
| `RoomRepository` | 房间增删改查、按平台/分组过滤、`DisplayNumber` 维护 |
| `LayoutProfileRepository` | 方案列表/加载/保存（含 Slots 全量替换）、自定义布局标记 |
| `RecordingTargetRepository` | 自动录制开关、按房间查询/更新、输出目录 |
| `RecordingSessionRepository` | 会话开始/更新进度/结束、历史分页查询、删除记录 |
| `HighlightEventRepository` | 写入高能事件、按会话查询（`GetByRecordingSessionAsync`） |
| `BlocklistRepository` | 关键词黑名单读写 |
| `AppSettingsRepository` | 设置键值读写（`IAppSettingsRepository`）；JSON 值原样存储 |

### 5.4 凭据安全

- `DpapiCookieProtector`：`ProtectedData.Protect/Unprotect(DataProtectionScope.CurrentUser, entropy)`，entropy 固定为 UTF-8 `"LiveMonitor.V0.1.Cookie"`；密文以 Base64 存库。
- `CredentialStore.GetCookieAsync(platform)`：`IsExpired != 0` 或解密失败返回 `null`；各层（`WorkspacePlayerManager`、`RecordingManager`、`DanmakuManager`、`MainWindowViewModel`）取 Cookie 后作为可选参数传给 Provider/API。
- ASR API Key 同样用该保护器加密，存 `AppSettingKeys.AsrEncryptedApiKey`。

---

## 6. 领域模型与契约（LiveMonitor.Core）

### 6.1 枚举

| 枚举 | 值 | 说明 |
| --- | --- | --- |
| `PlatformType` | 1..6（Bilibili/Douyin/Douyu/Huya/Twitch/YouTube）+ 99（CustomStream 自定义清流） | 数值固定，写库后不可变；99 为后加值，避开既有枚举 |
| `PlayerState` | `Loading / Playing / Offline / Reconnecting / Failed` | `Reconnecting` 表示播放中断后的自动重连态（UI 不可显示为「未开播」） |
| `RecordingStatus` | `Starting / Recording / Completed / Failed` | 与数据库 `RecordingSessions.Status` 对应 |
| `MonitorTier` | 紧凑/常规（按分辨率选择解码策略） | 由播放器尺寸驱动 |

### 6.2 实体

- `LiveRoom`：`Id, Platform, RoomId, RawUrl, DisplayNumber, Alias, GroupTag, CustomHeadersJson, CreatedAt`；`DisplayName` 由「别名或平台名 + #序号」生成；`CustomHeadersJson` 仅自定义清流使用（Referer/User-Agent 等）。
- `LayoutProfile`：`Id, Name, IsCustomLayout, DockLayoutJson, Slots`；`LayoutSlot`：`Id, LayoutProfileId, LiveRoomId, PaneId, DefaultMuted, IsFocusSlot, PreferredQualityId, DefaultDanmakuVisible`。
- `RecordingTarget`：`LiveRoomId, OutputDirectory, IsEnabled`；`RecordingSession`：录制历史（见 §5.2）。
- `PlatformCredential`、`AppSetting`、`DanmakuBlockKeyword`。

### 6.3 关键模型

| 模型 | 关键字段 |
| --- | --- |
| `RoomStatus` | `RoomId, Platform, Title, AnchorName, IsLive, AvailableQualities` |
| `StreamQuality` | `Id, DisplayName, Height, Fps, StreamUrl, RequiredHeaders, AudioStreamUrl/AudioHeaders/HasSeparateAudio` |
| `DanmakuMessage` | `Platform, RoomId, UserName, Text, Emote, Timestamp, IsSticker/IsGift 等` |
| `HighlightEvent` | `OffsetSeconds, PeakCount, BaselineCount, DensityRatio, Reasons, WordHits` |
| `RecordingProgress` | `Duration, FileSizeBytes, Bitrate, StatusText` |
| `VideoMetadata` / `WaveformData` | 剪辑台的源视频信息与波形/弹幕密度数组 |
| `ClipExportRequest/Progress` | 剪辑导出参数（区间、输出目录、文件名、模式、编码器）与进度 |
| `AsrTranscriptResult/AsrTranscriptSegment` | 整段时间轴与逐句结果 |

### 6.4 接口分组

- 平台：`IPlatformProvider`（`CanHandle/ExtractRoomId/GetRoomStatusAsync(roomId, cookie?, ct)`）、`IPlatformPlugin`（平台标识 + Provider/弹幕工厂）、`IPlayerController`。
- 弹幕：`IDanmakuClient`、`IDanmakuClientFactory`、`IDanmakuFilter`、`IHighlightDetector`、`IHighlightEventEnricher`、`IDanmakuAssConverter`。
- 录制：`IFullStreamRecorder`、`IRecordingTargetRepository`、`IRecordingSessionRepository`、`IHighlightEventRepository`。
- 剪辑/媒体：`IClipExporter`、`IVideoProbeService`（`VideoProbeService`）、`IWaveformService`、`IHardwareEncoderDetector`。
- 数据：`IRoomRepository`、`ILayoutProfileRepository`、`IAppSettingsRepository`、`IBlocklistRepository`、`ICredentialStore`。
- ASR：`IAsrProvider`。

### 6.5 配置键（`AppSettingKeys`）

| 键 | 含义 |
| --- | --- |
| `BlockUnicycleEnabled` | 是否开启独轮车过滤 |
| `DanmakuPreviewEnabled` | 监控画面是否显示弹幕 |
| `DanmakuStickerVisible` | 是否渲染纯大表情包弹幕（关闭时仅显示 `[表情名]`） |
| `DanmakuEmoteImagesVisible` | 是否把表情渲染为图片 |
| `HighlightShortWindowSeconds/BaselineWindowSeconds/DensityMultiplier/MinShortWindowCount/CooldownSeconds/BoostKeywords` | 高能检测参数 |
| `RecordingDefaultDirectory` | 录像默认目录（缺省 `%UserProfile%\Videos\LimoClip`） |
| `RecordingPollIntervalSeconds` | 自动录制开播轮询间隔（默认 60s，10~600） |
| `AsrProviderPreset/AsrEndpoint/AsrModel/AsrEncryptedApiKey/AsrLanguage` | 云端 ASR 配置 |
| `TwitchPlaylistProxyUrl` | Twitch 播放列表代理模板（可选，支持 `{channel}` 占位符） |

---

## 7. 平台接入层

### 7.1 工厂与替换机制

- `PlatformFactory`：内部是 `volatile IPlatformProvider[]`；`ReplaceProvider/ReplaceProviders/RemoveProvider` 以「按平台原位替换、后出现覆盖先出现」的方式更新；读取全部走 `Volatile.Read`，保证运行期热替换无锁安全。
- `DanmakuClientFactory`：维护 `PlatformType → Func<roomId, cookie, HttpClient?, IDanmakuClient>` 映射；插件安装后 `RegisterOrReplace` 即可替换弹幕实现。
- 默认启动装配：`PlatformFactory.CreateDefault(httpClient, ytDlpPath, twitchProxyTemplateProvider)` 创建内置 B 站/Twitch Provider；随后 `PlatformPluginHost.RegisterBuiltIn(...)` 为其余四个平台（YouTube/虎牙/斗鱼/抖音）注册 Provider 与弹幕工厂。

### 7.2 各平台实现要点

#### B 站（`LiveMonitor.Platforms.Bilibili`）

- 房间解析：支持 `live.bilibili.com/<房间号>`、纯数字、`b23.tv` 短链、`www.bilibili.com/blackboard/live/...`；短链先解析再归一。
- 房间状态：`room/v1/Room/get_info`（标题/主播/`live_status`）。
- 播放地址：`room/v1/Room/playUrl` 系列接口取 FLV/DASH/HLS；按画质档生成 `StreamQuality`，Cookie 用于高画质与风控。
- 弹幕：`getDanmuInfo` 需要 WBI 签名（`BilibiliWbiSigner`，未签名返回 `-352`）；WebSocket 二进制包按 `BilibiliPacketParser` 解析（头部长度/协议版本/操作码，zlib 压缩弹幕体）。
- 连接前置的纯解析：wbi 图片/子密钥 URL 提取密钥名（`ExtractWbiKey`）、`getDanmuInfo` 候选弹幕服务器选择（`SelectHostUri`，`wss_port` → `ws_port` → `port` 优先级）、wss `/sub` URI 构造（`TryBuildWebSocketUri`）、Cookie 取值（`GetCookieValue`，Ordinal 区分大小写）与 `DedeUserID` 解析（`ParseOwnerUid`）位于 `BilibiliConnectionParsing`，由 `BilibiliConnectionParsingTests` 覆盖（V0.7.5）。
- 房间号与播放地址的纯解析：数字 ID 校验/归一（`IsNumericId`/`NormalizeNumeric`）、链接解析（`ParseRoomId`/`ParseQuery`，支持 `room_id`/`roomid` 查询与 `.html` 末尾）、`g_qn_desc` 画质描述（`media_base_desc` 覆盖 brief）、直链/主机+相对路径/追加 extra 查询的播放地址拼接（`BuildStreamUrl`）、画质描述→高度映射（`ParseHeight`，缺失回退 qn 档位）与短链域名判定（`IsShortLinkHost`）位于 `BilibiliProviderParsing`，由 `BilibiliProviderParsingTests` 覆盖（V0.7.5）；短链 HTTP 解析与接口请求仍留在 `BilibiliProvider`。
- 绘制与统计：弹幕消息转成统一 `DanmakuMessage` 后进入 `DanmakuManager`。

#### Twitch（`LiveMonitor.Platforms.Twitch`）

- 三层降级 `GetRoomStatusAsync`：
  1. **代理层**（可配置 `TwitchPlaylistProxyUrl`）：`{channel}` 替换后直接 GET M3U8，解析主列表；
  2. **原生层**：GQL `PlaybackAccessToken`（persisted query，Client-ID `kimne78kx3ncx6brgo4mv6wki5h1ko`）+ Usher HLS。**`playerType` 固定 `embed`**——实测 `site/popout/mobile_web` 的媒体列表带 `twitch-stitched-ad` 广告段，`embed` 返回纯直播源；有 `auth-token` 时仍带 `Authorization: OAuth <token>` 头；
  3. **yt-dlp 兜底**：`RunJsonAsync("https://www.twitch.tv/<channel>")`，从 `formats` 取 `(height,fps)` 去重后的画质，`http_headers` 原样透传。
- 主列表解析 `ParseMasterM3u8`：扫描 `#EXT-X-STREAM-INF`（RESOLUTION/FRAME-RATE/BANDWIDTH），下一非注释行作为分片列表 URL，按 `(height,fps)` 保留带宽最大的一条，生成 `twitch-{h}p{fps}` 画质。
- 纯解析规则集中在 `TwitchProviderParsing`（internal static，无网络）：域名/频道段识别（`IsTwitchHost`/`TryGetChannelSegment`/`IsChannelName`）、`auth-token` 提取（`ExtractAuthToken`）、Usher 地址拼接（`BuildUsherUrl`）、主列表与属性解析（`ParseMasterM3u8`/`ParseAttributes`/`ParseResolution`）、yt-dlp JSON 解析（`ParseStatus`/`ParseFormats`）与离线判定（`IsOffline`），由 `TwitchProviderParsingTests` 覆盖（V0.7.5）；`TwitchProvider` 只保留 GQL/HTTP/yt-dlp 网络流程。
- 弹幕：`TwitchDanmakuClient` 连接 IRC WebSocket，`PASS oauth:<token>`（无 token 则匿名 `justinfan`），解析 `PRIVMSG` 消息。
- 错误语义：频道离线/不存在时返回 `RoomStatus.Offline`；其他失败抛中文异常并回退下一层。

#### YouTube（`LiveMonitor.Plugins.YouTube`）

- `YouTubeUrlParser` 支持 `watch?v=`、`live/`、`shorts/`、`youtu.be/`、`@handle`、`channel/UC...`，`roomId` 用视频 ID 或 `@handle`/`channel:UC...`。
- `YouTubeProvider` 调 yt-dlp `-j` 取元数据与 `formats`：按 `(height,fps)` 去重保留 `tbr` 最大；有独立音频轨时组装 `audio-file` 播放（libmpv 的 `audio-file` 选项）与录制双输入。
- 弹幕：`YouTubeDanmakuClient` 轮询 InnerTube 的 `live_chat` 延续令牌（无需登录），解析 `text/paid/paidSticker` 消息。

#### 虎牙（`LiveMonitor.Plugins.Huya`）

- 房间解析：`huya.com/<roomId 或个性域名>`；个性域名需抓房间页（手动解 gzip/br）用正则 `"profileRoom"\s*:\s*(\d+)` 反查真实房间号。
- 房间状态：`https://mp.huya.com/cache.php?m=Live&do=profileRoom&showSecret=1&roomid=<id>`，解析 `liveStatus`、主播、`data.stream.flv.multiLine`（AL/TX/HS 三条 CDN 线路）与 `liveData.bitRateInfo` 档位。
- 接口响应与房间页的纯解析：接口 envelope 状态/错误文案（`ReadStatus`，非整数视作 0 走错误分支、`ReadMessage`）、房间页 `"profileRoom":<数字>` 提取（`ExtractProfileRoom`）、FLV 线路（`ParseFlvLines`，跳过非对象与缺 url）、清晰度档位内嵌 JSON（`ParseBitRates`，非法/非数组回空）、页面内容解码（`DecodeContent`，gzip/br/deflate + gzip 魔数兜底）与 JSON 读取（`ReadString`/`ReadInt64`）集中在 `HuyaApiClientParsing`，由 `HuyaApiClientParsingTests` 覆盖（V0.7.5）；`HuyaApiClient` 只保留 HTTP 编排与个性域名缓存。
- 画质：Cookie 注入请求头；以 HS 线路为优先（实测最稳），按 `&ratio=` 档位（500/2000/4000/8000/20000 → 450p/720p/1080p）生成多条 `StreamQuality`，录制取最高档。
- 拉流侧的纯解析：房间号归一（`NormalizeRoomToken`，数字原样/链接交 `HuyaUrlParser`/失败兜底去空白）、FLV 线路稳定性选择（`SelectStableLine`，`StabilityRank` 按 HS→AL→TX→未知 排序、同档按 `webPriorityRate` 降序与 `lineIndex` 升序）、码率→ratio 归一（`NormalizeRatio`）、档位高度（`HeightForRatio`）、`ratio` 追加（`AppendRatio`）、请求头构造（`BuildHeaders`，含 Cookie 清洗）与画质生成（`BuildQualities`）集中在 `HuyaProviderParsing`，由 `HuyaProviderParsingTests` 覆盖（V0.7.5）；`HuyaProvider` 只保留接口/房间页网络流程。
- 弹幕：`wss://wsapi.huya.com/?baseinfo=<base64 Tars>`，自实现 Tars/Wup 最小编解码（`HuyaTars`）；`cmd16` 订阅 `live:<uid>`/`chat:<uid>`，`cmd7` 单条 / `cmd22` 组推送，`uri1400` 为弹幕；`ClientWebSocket` 未请求 permessage-deflate 以避免停推。
- CDN 特性：单连接寿命 3~17s，因此录制采用分段（见 §9.1）。

#### 斗鱼（`LiveMonitor.Plugins.Douyu`）

- 房间信息：`https://www.douyu.com/betard/<roomId>`。
- 拉流：动态签名——GET `swf_api/homeH5Enc?rids=<roomId>` 取 JS，用 **Jint** 在沙箱中执行 `ub98484234` 求签名（prelude 注入 `window/document/navigator/location` 与 `CryptoJS.MD5` 走 .NET MD5）；随后 POST `/lapi/live/getH5Play/<roomId>`（`rate=-1`）取 `rtmp_url` 与 `rtmp_live`；匿名最高「蓝光4M」，Cookie 注入可提升档位。
- 接口响应的纯解析：betard 房间信息（`ParseRoom`，含 `DouyuRoomInfo` 记录、`owner_uid` 数字/字符串兼容、`show_status` 判定）、getH5Play 拉流地址（`BuildQuality`，错误码抛中文异常、缺地址返回 null、`multirates` 选显示名）、CDN 基地址+直播路径拼接（`BuildStreamUrl`）、画质名回退（`ResolveQualityName`）、请求头（`BuildHeaders`）与 Cookie 清洗（`NormalizeCookie`，替换换行）集中在 `DouyuApiParsing`，由 `DouyuApiParsingTests` 覆盖（V0.7.5）；`DouyuApiClient` 只保留签名/HTTP 编排。
- 弹幕：`danmuproxy.douyu.com:8601` 原始 TCP；12 字节头（`len(LE int32)*2 + type(LE int16) + encrypt/reserved`），`loginreq → joingroup(gid=-9999)`，45s `mrkl` 心跳，`chatmsg/dgb` 解析；消息编码为 STT 键值（`@S`→`/`、`@A`→`@`）。
- 拉流注意：HTTPS 输入给 ffmpeg 传 `tools/cacert.crt`（`-ca_file`），http 输入不传（否则 ffmpeg 报 `Option ca_file not found`）。

#### 抖音（`LiveMonitor.Plugins.Douyin`）

- 房间解析：`live.douyin.com/<webRid>`；纯数字不识别（避免与 B 站冲突）。
- 拉流：先 GET `https://live.douyin.com/` 取 `ttwid` Cookie；再 `webcast/room/web/enter/`（aid=6383 等固定参数 + `web_rid`）取 `room.status==2`、`stream_url.flv_pull_url` 与 HLS；Cookie 头 = 登录 Cookie + `ttwid` 合并。
- 弹幕：`wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/`；签名 = 13 个参数按序拼 `k=v,` 后 MD5 十六进制，交给运行时加载的 `sign.js` 的 `get_sign`；协议为 Protobuf（`PushFrame → gzip → Response → Message`），`ChatMessage/Gift/Emoji` 字段号手写读写（`DouyinProto`），5s 心跳 `hb`，needAck 回 `ack`。
- `sign.js` 为 AGPL 第三方，**不随仓库/安装包分发**：按「显式路径 → 环境变量 `LIMOCLIP_DOUYIN_SIGN_JS` → `%LocalAppData%/LiveMonitor/cache/douyin/sign.js` → 应用目录 → 插件目录」查找，缺失时从固定 raw 地址下载缓存。

#### 自定义清流（`LiveMonitor.Platforms/CustomStream`）

- `CustomStreamProvider`：`PlatformType.CustomStream` 的内置 Provider，不依赖任何平台接口：
  - `CanHandle`/`LooksLikeStreamUrl`：`rtmp/rtmps/rtsp/rtsps/srt/mms/mmsh` 绝对地址，或 http(s) 且路径以 `.m3u8/.m3u/.flv/.ts/.mpd` 结尾；
  - `ExtractRoomId`：生成固定短 ID `cs_xxxxxxxx`（真实地址存 `RawUrl`，Token 换链时编辑替换而不重复占位）；
  - `GetRoomStatusAsync(roomId | 完整 URL)`：`roomId` 含 `://` 时直接当作直链（添加阶段不落库即可预览），否则按 `RoomId` 从 `IRoomRepository` 回查 `RawUrl` 与 `CustomHeadersJson`（含 Referer/UA，http(s) 时额外补 Cookie 头）；始终返回 `IsLive=true` 的单档 `StreamQuality("direct","清流原画",1080,60,url,headers)`；
  - 请求头只对 http(s) 注入；rtsp/rtsps 忽略 HTTP 头并由 `FfmpegArgumentBuilder` 强制 `-rtsp_transport tcp`；非 http(s) 不传 `-headers/-user_agent/-ca_file`（ffmpeg 对 rtmp/rtsp 会报错）。
- `NullDanmakuClient`：清流无弹幕服务；`DanmakuClientFactory.Create` 对 `CustomStream` 返回空实现（而非 `null`），避免上层把「无弹幕」当成可重试失败。
- 注册：`AppServices.CreateAsync` 在 `RoomRepository` 创建后 `platforms.ReplaceProvider(new CustomStreamProvider(roomRepository))`；不修改 `IPlatformProvider` 契约。

### 7.3 公共设施（Platforms.Common）

| 类型 | 作用 |
| --- | --- |
| `PlatformDefaults` | 统一 UA、各平台 BaseUrl、Cookie 规范化（禁止 CR/LF，防头注入）、错误截断 |
| `JsonRead` | 大小写不敏感的 JSON 取值辅助（String/Int/Double/Bool） |
| `YtDlpRunner` / `CliWrapYtDlpRunner` | 异步执行 yt-dlp，返回 `YtDlpResult(ExitCode, StdOut, StdErr)` |
| `CliUpdater` | yt-dlp 版本检查与自更新（GitHub Releases） |
| `ToolLocator` | 定位 yt-dlp / 插件根目录 / 应用目录（支持便携与安装版） |
| `PluginVersionInfo` / `PluginVersionComparer` | 从程序集元数据取插件版本、语义化比较 |

### 7.4 插件系统

- **契约**：`IPlatformPlugin { PlatformType Platform; string PluginId; string Version; IPlatformProvider CreateStreamProvider(HttpClient, string ytDlpPath); IDanmakuClient? CreateDanmakuClient(roomId, cookie?, HttpClient?); }`。
- **内置插件**：`BuiltInPlatformPlugins.CreateAll()` 返回 B站/Twitch/YouTube/虎牙/斗鱼/抖音六个实现；`PlatformPluginHost.RegisterBuiltIn` 记录版本，仅在平台尚无 Provider 时注册（避免覆盖 `PlatformFactory.CreateDefault` 已构建的 Twitch/B站 Provider）。
- **安装目录**：`%LocalAppData%\LiveMonitor\plugins\{pluginId}\{version}\*.dll`；宿主校验清单 → 下载 → SHA256 校验 → Ed25519 验签 → 写入磁盘 → 原子切换。
- **加载隔离**：`PlatformPluginLoadContext` 为 `AssemblyDependencyResolver` 的 `isCollectible` 上下文；`LiveMonitor.Core` / `LiveMonitor.Platforms.Common` 由默认上下文共享（返回 `null` 委托默认加载），其余依赖使用内存流加载，避免文件锁；`LoadedPluginHandle` 负责卸载与引用计数，有活跃录制/播放时拒绝替换。
- **信任链**：`PluginTrust` 内嵌公钥（`lYn7fkrnrtAHz4fplCQ3R5yC3pPCrG3QLk5atHiSE24=`）；`Ed25519Verifier` 校验清单签名；`ProvidersManifest.Validate` 校验字段合法性（版本、平台枚举、DLL 文件名、https 下载地址、sha256 64 位 hex、签名字节长度）。
- **清单与镜像**：`plugins/providers-manifest.json`（仓库内）为内置插件清单；外部插件清单主源为 GitHub Raw，镜像源为 Gitee Raw（`PluginTrust.ManifestPrimaryUrl` / `ManifestMirrorUrl`）；每个插件条目可带 `mirrorDownloadUrl`，主地址下载失败自动回退镜像（清单与 DLL 都做了 12s/30s 单次超时）。
- **热更新流程**：设置页「组件与插件更新」→ `PluginUpdateService.CheckAsync`（清单拉取 + 版本比较）→ 用户确认安装 → `InstallAsync`（下载 + 验签 + 落盘 + `PlatformPluginHost` 应用）；失败回滚保留旧版本。
- **发布工具链**：`scripts/build-plugins.ps1`（构建六平台插件 + 签名 + 更新清单）、`scripts/sign-plugin.ps1`（单插件签名，支持 `-MirrorDownloadUrl`）、`scripts/PluginSigner`（net8.0 控制台，`sign`/`keygen` 命令）。私钥不入仓库，需通过 `-PrivateKeyPath` 或环境变量提供。

---

## 8. 弹幕子系统（LiveMonitor.Danmaku）

- 清流平台（`CustomStream`）没有弹幕服务：`DanmakuClientFactory.Create` 返回 `NullDanmakuClient` 空实现，`DanmakuManager` 不会建立弹幕会话，录制也不会创建 JSONL 文件。
- `DanmakuManager`：单例服务，持有各平台弹幕工厂与 `DanmakuSessionHandle` 引用计数表。
  - `InitializeAsync`：读取设置（是否开启过滤/高能检测参数）并启动清理任务；
  - `Acquire(platform, roomId)`：返回 `DanmakuSessionHandle`；监控窗格、录制、剪辑回放分别克隆句柄，最后一个句柄 Dispose 时才真正断开连接；
  - `ReloadSettingsAsync` / `UpdateHighlightRulesAsync` / `SetUnicycleEnabledAsync`：设置页变更后热更新过滤链与检测参数；
  - `GetRecentHighlightEventsAsync`：供 UI 展示最近高能事件。
  - 主窗口窗格订阅同步规则：进入工作区的窗格按 `(平台, 房间号)` 订阅，清流与空房间号不订阅；窗格离开工作区或平台/房间号变化时释放旧句柄后重订阅；设置项（弹幕总闸/贴纸/表情图片）缺省开启、仅显式保存 `false` 才关闭。这些纯规则位于 `ViewModels/DanmakuSubscriptionLogic`（`DanmakuSubscriptionLogicTests` 覆盖），主控制条「全部静音」图标与提示复用 `Player/PlayerChromeLogic`（V0.7.5）。
- 断线重连：会话连接循环按 3s → 10s → 30s → 60s 退避重连、末档固定 60s，连接稳定 ≥30s 后下一次断线重新从 3s 开始；退避梯度选择、索引推进夹取与稳定重置判定为纯逻辑 `Danmaku/DanmakuReconnectPolicy`（`DanmakuReconnectPolicyTests` 覆盖），连接循环只保留客户端创建/事件挂接/清理与日志（V0.7.5）。
- 过滤器链（`IDanmakuFilter`）：
  - `BilibiliUnicycleFilter`：B 站独轮车识别（同用户高频、复读机式复制弹幕、短文本豁免），命中后仅计数不渲染；
  - `KeywordFilter`：`DanmakuBlockKeywords` 黑名单，支持子串匹配。
- 高能检测：`SlidingWindowDetector` 以「短窗计数 / 基线窗计数 / 密度倍数 / 最小条数 / 冷却时间 / 加权关键词」判定高能事件，产出 `HighlightEvent`（峰值、基线、倍数、原因、命中词）；`HighlightSettingsStore` 负责参数持久化。
- 渲染：`DanmakuOverlay` + `DanmakuLaneAllocator`（按时间/高度分配轨道避免重叠）+ `DanmakuRenderTicker`（所有窗格共用一个与合成器 vsync 对齐的帧回调，用 `TopLevel.RequestAnimationFrame` 的时间戳推进滚动，避免 DispatcherTimer 抖动；弹幕空闲时停止循环，带 1s 低频看门狗防止最小化后帧回调丢失导致冻结）；`DanmakuEmoteText` / `DanmakuEmoteImageCache` 处理表情文本与图片缓存（图片渲染默认开启，设置页可关；下载地址先归一化协议相对 URL `//host/...` 为 https，失败按 60s 冷却重试，避免 B 站协议相对地址导致整场表情退回 `[表情名]` 文本）；轨道换算、同屏并发与播放倍率钳制、运动与离屏回收、出生位置与时长/速度推导、表情与超长文本格式化等纯逻辑位于 `Controls/DanmakuOverlayLogic`（`DanmakuOverlayLogicTests` 覆盖），`DanmakuOverlay` 只保留状态与绘制（V0.7.5）。共享帧循环的帧间隔换算（首帧按 60fps、delta 钳制 [0.001, 0.1]）与 1200ms 停滞看门狗判定位于 `Controls/DanmakuRenderTiming`（`DanmakuRenderTimingTests` 覆盖）。实际更新频率受 Avalonia 渲染循环上限约束（Windows 上默认 `SleepRenderTimer` 固定约 60Hz，`IRenderTimer` 标记为不可由用户实现），因此 120/144/200Hz 显示器上弹幕仍以约 60fps 更新：实测弹幕帧循环与 `Render` 均稳定 60fps、单帧绘制 <0.2ms，循环本身不是瓶颈，高刷屏更顺滑需等 Avalonia 上游支持按显示器刷新率驱动渲染循环。
- 落地：录制时 `DanmakuJsonlRecorder` 逐行写 JSONL（含时间戳与高能标记）；剪辑回放由 `DanmakuJsonlReader` + `DanmakuPlaybackCursor` 按时间轴读取，并支持导出 `.ass` 字幕（`DanmakuAssConverter`）。

---

## 9. 媒体子系统（LiveMonitor.Media）

### 9.1 录制（Recording/）

**核心对象与职责**

| 类型 | 职责 |
| --- | --- |
| `IFullStreamRecorder` | 一个实例录一段；事件：首帧锚点、进度、退出；`StartAsync(quality, outputMkvPath, ct)`、`StopGracefullyAsync(timeout)` |
| `FfmpegFullRecorder` | 启动/管理单个 ffmpeg 进程：参数构造、`-progress pipe:1` 解析进度与锚点、`stderr` 环形尾缓存（4000 字）、stdin 写 `q` 优雅停录（超时 Kill 进程树） |
| `FfmpegArgumentBuilder` | 构造参数：`-rw_timeout 15000000`、UA、`-headers`（Referer/Cookie）、仅 https 的 `-ca_file`、可选第二路音频输入（`-map 0:v:0 -map 1:a:0`）、录制输出前追加 `-cluster_time_limit 2000`（细粒度 cluster，录制中 MKV 尾部可被实时读取，支撑边录边剪）、`-c copy -f matroska -progress pipe:1 -y`；刻意不带 `-reconnect`（见下） |
| `ProcessJobGuard` | Windows Job Object，异常退出时确保 ffmpeg 子进程一并结束，避免孤儿进程 |
| `RecordingDirectoryGuard` | 开录前目录可写预检（探针文件）、ffmpeg 失败原因中文翻译（权限/路径/编码器缺失等） |
| `RecordingFileNaming` | 输出文件名规则：`{开始时间}_{房间显示名}_{平台房间号}.mkv`（非法字符替换、长度限制） |
| `RecordingManager` | 录制状态机与调度：自动录制轮询、会话持久化、失败重试、卡死检测、分段续录、合并收尾、历史删除；实现 `ILiveRecordingMonitor`——`GetLiveRecordings()` / `TryGetLiveRecording(sessionId, out LiveRecordingInfo)` 在锁内复制活动会话并 `BuildLiveRecordingInfo`（实时已完成时长、当前分片路径/分片号、累计时长、文件大小、弹幕数、高能数），供剪辑台边录边剪轮询 |
| `SegmentMerger` / `SegmentOverlapPlanner` | 分段录制后的合并与重叠裁剪 |
| `DanmakuJsonlRecorder` | 录制期间弹幕落盘（JSONL） |
| `RecordingHighlightTracker` | 录制期间把高能事件关联到会话（弹幕过滤 + 高能检测的录制侧桥接） |

**录制状态机（RecordingManager）**

1. 轮询（默认 60s）或手动触发 → `StartAsync(room)`：解析 Provider、取 Cookie、`GetRoomStatusAsync`、`ChooseQuality`（最高画质）、目录预检、生成文件名；
2. 创建 `RecordingSession(Status=Starting)` 入库 → 启动 `FfmpegFullRecorder`（第一段写到 `.part001.mkv`）→ `Recording`、启动监控任务；
3. 监控：每 2s 检查 ffmpeg 是否退出/卡死（30s 无进度判卡死）、每 5s 持久化进度；
4. ffmpeg 异常退出（CDN 断流）→ 释放旧录制器、统计本段时长/大小，**续录下一段**（最多连续 6 次启动失败，间隔 2s；每次重新解析房间状态获取新签名地址）；
5. 停止（用户/主播离线/卡死）→ `StopGracefullyAsync(5s)` → `FinalizeOnceAsync`：捕获当前段 → 用 `SegmentMerger` 合并全部 `.partNNN.mkv` → 以合并结果作为**权威时长与文件大小**写回会话 → 状态 Completed/Failed；
6. 会话结束事件通知 UI（录制中心刷新历史/监听提示）。

**清流录制的差异**：`CustomStream` 不参与自动录制轮询（`PollRoomAsync` 直接返回，避免「恒 live」被反复开录）；开录时跳过弹幕记录器、`DanmakuFilePath` 写空串（剪辑台按空路径跳过弹幕轨）；`FfmpegArgumentBuilder` 对非 http(s) 输入不传 `-headers/-user_agent/-ca_file`，rtsp/rtsps 追加 `-rtsp_transport tcp`。

**为什么分段：** 虎牙 CDN 单连接只持续 3~17s，直接 `-reconnect` 续写会让 FLV 时间戳归零，MKV 出现 `Non-monotonic DTS` 并损坏（`duration=N/A`）。因此改为「每段独立 ffmpeg 进程 + 合并」，并对 CDN 在每次连接时重发的少量缓存内容做**重叠裁剪**：

- 每段记录墙钟起点 `StartOffset`（相对整场开始）与实测时长；
- `SegmentOverlapPlanner` 计算相邻段重叠 = 上一段末尾 − 本段起点，用 ffmpeg concat demuxer 的 `inpoint` 裁掉重复部分（保留至少 0.2s，裁剪不足 0.05s 忽略）；
- 合并器先用 ffmpeg 实测每个分片时长（`-map 0:v:0 -c copy -f null` 读取 `out_time_us`），失败则回退录制器进度值；
- 合并成功删除分片；失败保留全部分片并在会话 `ErrorMessage` 中记录，便于人工恢复。

**进度与卡死**：`FfmpegProgressParser` 解析 `progress=continue/end` 与 `out_time_us/size/bitrate`；首帧锚点（`OnFirstFrameAnchored`）用于把弹幕/高能事件对齐到文件时间轴；`StallTimeout=30s` 触发 `Stalled` 并结束当前段。

### 9.2 剪辑与导出（Editing/）

- `VideoProbeService`：ffprobe 读取时长/分辨率/帧率/编码/音轨；
- `WaveformService`：把音轨解码为 PCM，按桶计算峰值/均值包络（`WaveformData`），并计算每桶弹幕密度（剪辑台「双波形」的数据来源）；结果缓存到本地（按文件大小+修改时间）；
- `HardwareEncoderDetector`：探测 NVENC / QSV / AMF / CPU（libx264），返回可用编码器与推荐参数；
- `ClipExporter`：两种导出模式——
  - **无损切割**：`-c copy`，从最近关键帧对齐，秒出、无画质损失（会向前对齐 1~3 秒）；
  - **硬编烧录**：按选择的编码器重编码，并将弹幕按时间轴烧录为画面叠加（`-vf ass=...`），产出「成品版」；
  - 两版可同时输出，进度通过 `ClipExportProgress` 报告；
- `DanmakuAssConverter`：把区段内的弹幕/礼物转成 ASS 字幕（颜色、位置、大小、时间轴偏移）；
- `DanmakuJsonlReader` / `DanmakuPlaybackCursor`：剪辑台预览时按时间轴回放录制时落盘的弹幕。

**边录边剪（V0.7.1）**

录制尚未结束时即可在剪辑台打开「🔴 录制中」场次，对已落盘部分预览、定位、选区与语音识别：

- `ILiveRecordingMonitor`（Core 契约）：`RecordingManager` 实现，提供活动会话的实时信息（见 §9.1）；`LiveRecordingInfo` 含实时已录时长、当前 `.partNNN.mkv` 路径、弹幕/高能计数。
- `LiveWaveformDeltaExtractor`：按「上次已分析到的秒数 → 安全上界」向 ffmpeg 追加一次增量解码（`-ss/-t` + 4kHz 单声道 s16le 输出到 stdout），只产出新增区间的峰值桶；无临时文件；取消时杀整个进程树；分片尾部数据不完整或 `-ss` 落在关键帧之后时按「本次无数据」降级，下次继续。
- `DanmakuJsonlReader.ReadIncrementalAsync(path, fromByteOffset)`：`FileShare.ReadWrite` 打开正在写的 JSONL，按字节偏移增量读取；只在遇到完整换行时消费并推进偏移（半行留待下次），文件被截断/重建（offset > 长度）时从头重读；单次读取上限 16MB。
- `ClipStudioViewModel` live 模式：选中录制中场次后进入「本地预览 + 实时刷新」——10s 定时 `TryGetLiveRecording` 同步进度；安全可剪上界始终钳在「已完成时长 − 安全余量」（分片尾部 cluster 可能尚未写完）；增量波形按 0.5s 桶追加、增量弹幕按偏移追加；`F5`/「同步最新进度」立即刷新并重载播放器；录制结束（`SessionCompleted`）自动切换为完整 MKV（保留当前选区与播放位置）。增量波形的提取窗口对齐（终点向下取整到 0.5s 桶网）、峰值拼接（多裁少补 0）与按已录时长分桶重建（峰值拷贝 + 弹幕密度归一化）等纯计算规则已抽到 `ViewModels/LiveWaveformLogic`，由 `LiveWaveformLogicTests` 覆盖（V0.7.5）。边录边剪会话状态（当前分片路径、实时已录进度、安全可剪截止、增量弹幕字节偏移、波形峰值游标与窗口推进、派生状态条文案）已抽到 `ViewModels/LiveClipSession`（纯状态模型，经 `Start`/`ApplyLatest`/`Reset` 变更），由 `LiveClipSessionTests` 覆盖；`ClipStudioViewModel` 只保留打开/同步/结束编排与属性通知（V0.7.5，5.3 Step A）。
- `LocalClipPlayerView`：live 模式下对 mpv 设 `force-seekable=yes`，`ReloadAtPositionAsync` 以 `loadfile replace` + 精确 seek 重载已写文件；位置推进钳到安全上界（`safe − 100ms`），避免 seek 到尚未 flush 的尾部。位置推进/完成收口/安全上界的纯时间逻辑在 `Player/ClipPlaybackPositionLogic`（边录边剪 EOF 不当作完成、keep-open 末尾 80ms 主动收口、1ms 变化阈值），由 `Player/ClipPlaybackPositionLogicTests` 覆盖（V0.7.5）。播放状态机与守卫规则（已加载态判定、结束态重播/暂停/收尾守卫、`MpvEndFileReason`→动作分类、完成位置、倍速钳制、缺失文件与播放失败文案、解码准备提示）已抽到 `Player/ClipPlaybackStateLogic`，由 `Player/ClipPlaybackStateLogicTests` 覆盖；`LocalClipPlayerView` 只保留 mpv 句柄与事件换算（V0.7.5）。
- `ClipTimelineControl`：自绘双波形 + 选区/播放头/高能标记的时间轴控件。布局与命中几何（缩放上下限、可见时长、视口起点钳制、时间↔像素换算、手柄/播放头/高能命中）在 `Controls/ClipTimelineGeometry`（`ClipTimelineGeometryTests` 覆盖）；缩放锚点与视口换算、缩放到选区、平移拖拽、入/出点手柄钳制、选区/播放头/视口状态收敛、波形包络采样、刻度步长与时间文本在 `Controls/ClipTimelineViewLogic`（`ClipTimelineViewLogicTests` 覆盖）；控件本身只保留属性、绘制与指针事件（V0.7.5）。

### 9.3 云端 ASR（Asr/）

- 契约：`IAsrProvider.TranscribeAsync(audioPath/bytes, options, ct)` → `AsrTranscriptResult(Text, Segments, Language, Provider…)`。
- 实现 `OpenAiCompatibleAsrProvider`：调用 OpenAI 兼容 `POST /v1/audio/transcriptions`（multipart：`file` + `model` + `language` + `response_format=json`），解析 `segments[]`（起止时间 + 文本）与 `text` 两种返回；
- 响应解析与切片估算：verbose_json 的 `segments[]` 直接采用相对时间戳（越界钳制、空文本跳过）；纯文本或单段响应按句末标点拆成多句并按字符数比例估算时间（无标点的长文本按 48 字切块）；旧缓存里只有一整段的结果在读取时按句补拆。这些纯逻辑位于 `Media/Asr/AsrResponseParser`（`AsrResponseParserTests` 覆盖），`OpenAiCompatibleAsrProvider` 只负责 HTTP/FFmpeg 与缓存读写（V0.7.5）。
- 音频提取：FFmpeg 将源 MKV 按区间抽 16kHz 单声道 MP3 后上传；超长区间限制 15 分钟；
- 缓存：按「源文件 + 区间 + 模型/接口」的 SHA256 缓存转写结果（本地文件），避免重复计费；
- 逐句拆分（V0.7.1）：接口只返回整段 `text`（无 `segments[]`）时，`SplitIntoSentences` 按硬标点（`。！？!?；;…` 与换行，`……` 合并不断句）与软标点（`，,、：:`，累计 ≥48 字才断）拆句，无标点时每 48 字强制切块；各句起止时间按字符数比例分配到参考区间（`Relative*` 相对片段起点），最后一句收到区间末尾；旧缓存（只有 1 段）在读取时由 `UpgradeCachedResult` 原地补拆，无需重新请求；
- 错误：HTTP 错误体解析为中文提示（鉴权失败/额度不足/模型不存在/网络超时等）；API Key 由 Desktop 注入（Media 不直接读库）。

---

## 10. 桌面 UI 层（LiveMonitor.Desktop）

### 10.1 主壳与视图切换

- `MainWindow` 四态：**监控工作区**（默认）、**录制中心**（全屏覆盖层）、**剪辑台**（全屏覆盖层）、**设置窗**（独立窗口）；
- `ApplyShellLayout()` 统一计算显示状态：`overlay`（录制/剪辑）、`focused`（单窗格全屏）、`fullscreenShell`（桌面级全屏）、侧栏可见性；据此设置顶栏/侧栏/状态栏可见性与 `RootGrid` 行高列宽；
- **沉浸模式**：焦点模式或桌面全屏下，鼠标空闲 2.5s 自动隐藏各播放器底部控制条、隐藏顶部提示；鼠标/按键活动立即恢复（`_immersiveIdleTimer` + 逐窗格 `SetChromeVisible`）。逐窗格遍历同时覆盖 Dock 与自由画布两个宿主（`Dock` + `FreeformCanvas` 的视觉子树），否则自由布局下控制条不会隐藏；同时把工作区四周 6px 留白置 0（`WorkspaceHost.Padding`），让画面真正铺满整屏；
- 播放器底栏只保留画质与实测 fps（如 `1080P · 60fps`），主播名/编号统一由窗格标题栏展示，避免同一信息重复出现；
- **桌面级全屏**：F11 或顶栏「桌面全屏」按钮；记录进入前的 `WindowState`，进入 `WindowState.FullScreen` 并隐藏外壳，顶部提示仅在鼠标移到屏幕顶部（Y≤90）时出现；Esc 退出（自带 400ms 去抖避免重复触发）；
- 侧栏：房间库搜索、分组药丸（`WrapPanel` 自动换行 + `ScrollViewer` 限高）、房间条目操作（编辑/删除/双击加入工作区）。
- 工作区空闲底色：`DesignTokens.axaml` 的 `BrushBgWorkspace`（`#141A26`，与窗格黑底区分）用于工作区 Border 与自由画布；`MainWindow.Resources` 同时覆盖 Dock 主题的 `DockThemeBackgroundBrush`/`DockThemeControlBackgroundBrush`/`DockSurfaceWorkbenchBrush`/`DockSurfacePanelBrush`，保证平铺模式窗格间隙与无窗格区域同样显示该底色。
- 侧栏开播状态：`MainWindowViewModel` 每 60s 独立轮询主库全部房间（`DispatcherTimer` + 信号量限并发 3、单房间 10s 超时），按 `PlatformType` 取 Provider、带 Cookie 调 `GetRoomStatusAsync`，结果写入 `_knownLiveStatus` 并刷新条目状态点；工作区窗格的实时状态仍由播放器状态同步，两条来源合并后显示，未查询到历史状态的房间保持灰色「状态未知」。顶栏一体化自绘标题栏：空白区域按住拖动窗口、双击最大化/还原，事件源命中按钮/下拉框/输入框等交互控件时直接放行（`IsTopBarInteractiveSource`），避免吞掉首次点击。

### 10.2 布局系统（Dock）

- `WorkspaceDockBuilder`（继承 Dock `Factory`）：生成 `IRootDock` 结构——
  - 网格布局：`workspace-root → workspace-grid(Vertical) → workspace-row-N(Horizontal) → ToolDock(pane-N) → Tool`；
  - 自适应布局 `CreateAutoFit(count, aspect, factory)`：按固定行分布（1~12 路查表）或按宽高比计算行列，逐行均分；
  - **主副屏布局** `CreateHeroAndSideColumn(count, factory, heroProportion=0.7)`：左侧主画面（Proportion 0.3~0.85 clamp），右侧纵向副屏列（`workspace-side-column`），子窗格间自动插入 splitter；
  - 结构操作：`CloneStructure`（复制结构并追加窗格）、`TryApplyProportions`/`TryApplyDock`（结构一致时原地恢复比例）、`EnsureSplitters`（补齐 splitter）、`BindTiles`（反序列化后按 `PaneId` 重新绑定房间）。
- `MainWindowViewModel` 布局职责：
  - 方案（`LayoutProfile`）加载/保存：`DockLayoutJson`（Dock 序列化）+ `Slots`（PaneId ↔ LiveRoomId）；
  - 布局切换差量：`LayoutDiffService.Plan(previousRooms, nextRooms)` → 保留房间的播放器不断流、仅移除/新增/重挂载窗格；
  - 自适应排布按钮：结构变化走重建（`FitRoomsAsync`），仅比例变化则 `ResetProportions` 原地归位；
  - 主副屏按钮：`ApplyHeroLayoutAsync` → `CreateHeroAndSideColumn` 重建 + 记忆 `IsHeroLayout`（模式文本、底栏房间数展示不同）；
  - 拖拽/分割条变化 → `SetCustomLayout(true)`（`LayoutModeText` 显示「自定义比例 / 主副屏 · 自定义」），保存/恢复快照 `Capture/RestoreCustomLayoutAsync`；
  - 窗格增删（双击房间、关闭窗格）与 `AppendPaneAsync`；关闭窗格：自由布局删除文档 placement，Dock 宿主（自动/主副屏/自定义）统一整树重建后起播，避免播放器控件被新旧 `ContentPresenter` 同时引用导致闪退（见 §10.4「移出窗格」）。
- `MainWindowViewModel` 职责拆分（阶段五）：主 VM 从 2865 行降至约 2355 行，保留窗口级命令（工作区/录制中心/剪辑台切换）、`IsFreeformMode` 与自由画布协调、Dock 序列化/布局跟踪、自定义比例快照、焦点/全屏/主静音、弹幕订阅、健康监控与 `DisposePlayers`/`Dispose` 编排；内部协作类 `RoomCatalogCoordinator`（主库增删与选中）、`HealthSummaryCoordinator`（平台健康/资源摘要）、`WorkspaceLayoutCoordinator`（布局差量应用）承担无 UI 的状态编排；`RoomCatalogViewModel`（房间集合/筛选/分组/开播轮询）与 `WorkspaceLayoutProfileViewModel`（方案档案/保存另存/切换 dirty）承接原主 VM 的绑定面，窗口级副作用经 `IRoomCatalogHost`/`IWorkspaceLayoutProfileHost` 回调主 VM；`SystemStatusBarViewModel`/`HighlightFeedViewModel` 为只读子面板（状态栏、高亮事件流），`MainWindow.axaml` 对其使用局部 `x:CompileBindings="True"` + `x:DataType` 编译期绑定。 方案切换完成后的状态栏文案（原地保留 / 结构重建 / 差集变化 / 首次载入四类分支）已抽到 `ViewModels/LayoutLoadStatusLogic`（由 `LayoutLoadStatusLogicTests` 覆盖），`MainWindowViewModel` 只负责传入计数与结果（V0.7.5）。

### 10.3 播放器与监控

- `WorkspacePlayerManager`：监控侧核心服务——
  - `StartAsync(tile)`：解析房间 → 取 Cookie → `provider.GetRoomStatusAsync` → 更新窗格标题/状态 → `QualityPolicy.Choose` 选画质 → `player.PlayAsync(quality)` → 挂弹幕会话；自定义清流会调用 `player.SetDanmakuEnabled(false)` 隐藏窗格弹幕开关；
  - 失败处理：`ReconnectPolicy`（3s/10s/30s 退避，最多 3 次）→ 60s 失败复检；主播离线时定时轮询（60s）；
- 自动降档守护（V0.7.5）：每轮巡检按采样窗口换算丢帧率（帧率缺失/异常按 30 兜底），连续 3 个坏周期（丢帧率 > 5%）后降到当前档位之下最高可用画质（同高度优先高帧率），切档失败退到 15fps 紧凑模式；纯规则在 Services/DropGuardPolicy，由 DropGuardPolicyTests 覆盖，管理器只保留巡检节奏与切档副作用。
  - 播放器状态 → 窗格状态文本（`PlayerStateText`：连接中/直播中/已停止/重连中/播放失败）；同一映射还给出状态遮罩/加载条/重试按钮的可见性与默认标题详情（`Overlay`，由 `PlayerStateTextTests` 覆盖）；
  - 焦点模式：双击窗格进入单窗格全屏（`IsFocused` + 静音策略 + 恢复原布局）、Esc 退出；
  - 锚点/未开播文字与状态同步：`SetDanmakuEnabled` 控制清流弹幕按钮显隐；
  - 底部控制条：音量/静音、弹幕开关、紧凑模式，以及 V0.7 新增的「暂停/继续」（`TogglePause`，同步 mpv `pause` 属性并切换 ⏸/▶ 图标）与「刷新播放」（`RequestRefresh` → `RefreshRequested` → `WorkspacePlayerManager.RefreshTileAsync`，清空退避/离线计时器后立即重新解析地址并起播）；刷新按钮图标使用 `Icons.axaml` 的 `IconRefresh` PathIcon（矢量绘制，避免字体码点被裁切）。
- 播放器句柄创建/复用/销毁归属：上层只依赖 `IWorkspacePlayer` 契约（通过 `View` 取回挂载控件），`WorkspacePlayerManager` 用可注入工厂（默认 `static () => new MpvPlayerView()`）独占创建句柄；`EnsurePlayer` 按窗格复用或创建，`TransferPlayer` 让新句柄经 `IWorkspacePlayer.TryAdoptSessionFrom` 接管旧 mpv 会话（具体类型校验收敛在实现内，迁移失败时释放新句柄并保持旧窗格不变）；销毁统一走 `RemovePlayer`（停止轮询/重试 → 清空 `PlayerView` → Dispose），由 `MainWindowViewModel` 在窗格移除、方案差集销毁、拖拽换位与 `DisposePlayers` 中调用；`Stop` 只停止播放与跟踪（自由画布切换等仍需复用句柄的场景），`Dispose` 兜底释放仍被跟踪的句柄（幂等）。可回放 seek（进度条/seek 轮询计时器）是播放器视图内部 chrome，不纳入契约；其探测值换算（可回放判定）、滑块增量阈值、拖动防抖后的 seek 目标格式与时间文本已抽到 `Player/ReplaySeekLogic`，由 `Player/ReplaySeekLogicTests` 覆盖，视图只保留计时器与控件写回（V0.7.5）。
- `QualityPolicy`：按 Height/Fps 降序；大窗/焦点取最高；紧凑模式上限 480p；普通窗口优先 ≤720p，取第一个满足者。
- `MpvPlayerView`（libmpv 封装）：
  - 初始化选项：`hwdec=auto-safe`、`vo=libmpv`、`gpu-api=opengl`、`cache=yes`、`demuxer-max-bytes`（按分辨率 3M/4M/8M）、`network-timeout=10`、`audio-buffer=0.8`、`audio-stream-silence=yes`、关闭 OSC/默认快捷键（键位由应用处理）；
  - 播放选项：UA → `user-agent`、Referer → `referrer`、`Cookie` → `http-header-fields`、`audio-file`（分离音频）、TLS CA（http 不传）；选项映射（720P 缓存档位/默认 UA/请求头大小写查找与 CR/LF 清理/Cookie 拼装/分离音频）已抽到 `Player/StreamOptionMapper`（由 `StreamOptionMapperTests` 覆盖）。
  - 状态机：`Loading → Playing → Offline/Reconnecting/Failed`；EOF/中断 → `Reconnecting` + 触发 `ReconnectRequested`（UI 显示「直播流中断，正在自动重连…」，而不是「未开播」）；
  - 健康指标：`demuxer-cache-time`、丢帧/解码速率、`container-fps`（解码器实际收到的源帧率，不是渲染帧率）等经 `PlayerHealthMetrics` 上报聚合状态栏；mpv property → `PlayerHealthMetrics` 的采样规则（container-fps 优先、estimated-vf-fps 兜底、缺省归 0、hwdec-current 空/`no` 视为软解）已抽到 `Player/PlayerHealthSampling`（由 `PlayerHealthSamplingTests` 覆盖）；窗格右上角的常驻健康角标已按用户反馈移除（V0.7.1），避免与底栏画质文案的 fps 口径混淆；
  - 底栏画质文案：`PlayerQualityText.Format` 先只显示档位名，拿到 `container-fps` 后补成「1080P · 60fps」；不再显示平台元数据里猜出来的 fps（B 站解析不到高帧率时一律兜底 30，会与实测冲突）；
  - 音频表：通过 FFmpeg `af=astats` 输出到命名管道（`audio-meter`），`AudioLevelMeter` 读取显示；其中 filter 字符串转义、日志尾部 RMS 解析、dB→0~1 映射与表针上升/下降平滑等纯逻辑已抽到 `Player/AudioMeterLogic`（由 `AudioMeterLogicTests` 覆盖），`MpvPlayerView` 仅保留文件读取与 120ms 采样定时器。
  - 窗格控制条/图层 chrome：静音图标与提示（开声/静音）、紧凑档位文案（15fps/60fps）与同屏并发上限、弹幕层可见性（总闸 ∧ 窗格开关）、音量 0~100 钳制等纯规则已抽到 `Player/PlayerChromeLogic`（由 `Player/PlayerChromeLogicTests` 覆盖），视图只负责套用结果（V0.7.5）。
  - 播放器路径解析：CA 证书查找顺序（`LIVEMONITOR_CA_FILE` → 程序目录 `cacert.crt` → `cacert.pem`）与音量表输出目录选择（`%LocalAppData%\LiveMonitor\audio-meter`，LocalAppData 不可用时回退程序目录）已抽到 `Player/PlayerPathResolver`（由 `PlayerPathResolverTests` 覆盖），视图只保留目录创建与文件读取（V0.7.5）。
- `MpvVideoView`：libmpv 渲染目标控件（OpenGL 纹理），尺寸变化触发重绘；`PlayerSmokeWindow` 为无 UI 冒烟用窗口。

### 10.4 录制中心 / 剪辑台 / 设置

- `AddRoomWindow` / `EditRoomWindow`：普通平台按链接识别平台；粘贴清流直链（或勾选「作为自定义清流添加」）时按清流保存，可填 Referer/User-Agent；编辑清流时可直接替换播放地址（保留槽位与录制配置）并触发当前窗格重新拉流。
- `RecordingManager` 职责拆分（阶段五）：由 `RecordingTargetPoller`（流探测与开播轮询：首轮 3s、间隔 10~600s clamp、并发 4、离线重置抑制重试；host 回调接口 `IRecordingTargetPollerHost`）与 `RecordingSessionWorker`（单会话状态机：`ActiveRecording` 同步/启动/收尾、`FfmpegFullRecorder` 分段续录与合并、`DanmakuJsonlRecorder`、`RecordingHighlightTracker` 绑定；线程安全 `BuildProgress`/`BuildLiveRecordingInfo`，`FinalizeAsync` 经 `Interlocked` 闸门恰好一次）组成；`RecordingManager`（约 1123 行）保留 `_gate`/`_startGate`/`_retries`/`_suppressedRooms`、未完成会话恢复与 `ILiveRecordingMonitor` 桥接（`GetLiveRecordings`/`TryGetLiveRecording`/`SessionCompleted` 签名不变）。
- `RecordingCenterViewModel`：目标列表（自动录制开关、目录修改、立即开录/停止、当前录制进度）、历史列表（去剪辑、打开文件夹、删除；删除会同时删视频与 jsonl）。历史区（V0.7.5 起）拆为 `RecordingHistoryViewModel`：负责最近 200 条录像加载（版本号防串写）、资源管理器定位、删除确认与进入剪辑台/边录边剪流程，窗口注入能力经 internal `IRecordingHistoryHost` 回调录制中心；资源管理器启动与失败文案统一到 `Services/ExplorerLauncher`。清流目标不显示「自动录制」开关。目标排序（V0.7.1）：`RecordingTargetOrdering.Rank` 按「正在录制 (0) → 直播中 (1) → 其他 (2)」分组，组内再按显示序号稳定排序，保证正在录制/直播中的房间永远排在最前且同序号相对顺序不变。目标行展示（V0.7.5 起）：状态文案与配色、按钮文案与配色、开播徽标可见性、进度指标文本（时长/码率/文件大小/弹幕数/高能数）等纯规则已抽到 `ViewModels/RecordingTargetDisplayLogic`（由 `RecordingTargetDisplayLogicTests` 覆盖），`RecordingTargetItemViewModel` 只负责套用结果。历史行展示（V0.7.5 起）：房间快照名回退、汇总文案、在录状态判定、删除/剪辑入口可见性与三态悬浮提示、文件名与错误信息判定等纯规则已抽到 `ViewModels/RecordingHistoryDisplayLogic`（由 `RecordingHistoryDisplayLogicTests` 覆盖），`RecordingHistoryItemViewModel` 只负责套用结果。 录制中心摘要与目录归一化（V0.7.5 起）：目标区摘要、整页刷新状态文案、全局/独立保存目录归一化（空白回退默认目录、环境变量展开、非法路径回退原值）等纯规则已抽到 `ViewModels/RecordingCenterDisplayLogic`（由 `RecordingCenterDisplayLogicTests` 覆盖），`RecordingCenterViewModel` 只负责调用并写入状态。
- `ClipStudioViewModel`：录制场次选择 / 打开本地文件、片段播放（`LocalClipPlayerView`）、时间轴（波形+弹幕密度+选区）、导出面板（模式/编码器/目录/文件名/进度）、语音识别面板（转写当前选区、逐句跳转播放、复制文本）。场次下拉顶部追加「🔴 录制中」实时会话（V0.7.1），选中即进入边录边剪模式（安全区、增量波形/弹幕、F5 同步最新进度、录制结束自动切换完整文件，见 §9.2）。子视图模型拆分（V0.7.5 开发中）：`ClipAsrViewModel`（语音识别面板状态/命令/结果行，经 internal `IClipAsrHost` 读取当前视频/选区并写回状态栏与高能卡片可用性）与 `ClipExportViewModel`（导出模式/目录/文件名/导出队列/取消，经 internal `IClipExportHost` 读取视频/选区/边录边剪状态并钳制安全区，自动命名的「用户手改后不再覆盖」语义保留）已从主 VM 抽出；`ClipStudioView.axaml` 对这两个面板使用块级 `x:CompileBindings="True"` + `x:DataType` 编译期绑定。展示层纯函数（视频副标题/边录边剪副标题、伴随弹幕查找、时间钳制、实时状态条、文件大小格式化、高能投影、录制弹幕 → `DanmakuMessage` 转换）已抽到 `ViewModels/ClipStudioPresentationLogic`（由 `ClipStudioPresentationLogicTests` 覆盖）；主 VM 从 2350 行降至约 1589 行；边录边剪增量波形纯计算在 `LiveWaveformLogic`，选区收敛/入出点落入/键盘步进/结尾重播/高能窗口等纯规则在 `ViewModels/ClipSelectionLogic`（分别由 `LiveWaveformLogicTests`、`ClipSelectionLogicTests` 覆盖）。列表行展示规则（场次下拉标题/主副文本、高能卡片速率与悬浮提示、语音转写按钮与摘要截断、秒级时间码）已抽到 `ViewModels/ClipListDisplayLogic`（由 `ClipListDisplayLogicTests` 覆盖）；播放器时间文本/选区时长/弹幕信息行/播放暂停字形/边录边剪标题等展示文本集中在 `ClipStudioPresentationLogic`（V0.7.5），`ClipSessionOption`、`ClipHighlightItemViewModel` 与 `ClipExportTaskViewModel.FormatTime` 只负责套用结果（V0.7.5）。导出队列行展示规则（文件名/模式/时间区间/阶段文案/进度钳制/速度文本/输出文件拼接）已抽到 `ViewModels/ClipExportTaskDisplayLogic`（由 `ClipExportTaskDisplayLogicTests` 覆盖），`ClipExportTaskViewModel` 只负责调用导出器与维护任务状态（V0.7.5）。 默认输出文件名（视频名_入点-出点、超 90 字符截断）、时间码格式（≥1 小时 `h.mm.ss`，否则 `mm.ss`）、文件名非法字符替换与空名回退等纯规则位于 `ViewModels/ClipExportNamingLogic`（由 `ClipExportNamingLogicTests` 覆盖），`ClipExportViewModel` 只负责绑定与调用（V0.7.5）。 边录边剪会话状态已进一步抽到 `ViewModels/LiveClipSession`（分片/进度/安全截止/弹幕偏移/波形游标；`LiveClipSessionTests` 覆盖），主 VM 从约 1589 行降至约 1564 行（V0.7.5，5.3 Step A）。 边录边剪过程提示（分段拒绝/分片缺失/打开与同步失败/同步完成/结束切离线/弹幕与波形失败）、编码器探测文案，以及本地录像加载提示（场次列表/文件选择器/视频与弹幕加载/高能推荐/波形提取/播放失败）等纯文本模板集中在 `ViewModels/ClipStudioStatusText`（由 `ClipStudioStatusTextTests` 覆盖），视图模型只负责赋值（V0.7.5）。 语音识别面板状态与错误提示（选区无效/片段过短/取消/复制/识别中/失败/未打开视频）集中在 `ViewModels/AsrStatusText`，录制目标行向状态栏回报的文案（自动录制开关/启停录制/失败原因）集中在 `ViewModels/RecordingTargetStatusText`（分别由 `AsrStatusTextTests`、`RecordingTargetStatusTextTests` 覆盖，V0.7.5）。
- **时间码 / 时长格式化（V0.7.5）**：界面各处不再各自实现时间码；`ViewModels/TimeCodeFormat` 按显式变体提供 `Clock`（`h:mm:ss` / `mm:ss`）、`ClockWithTenths`（`h:mm:ss.t` / `m:ss.t`）、`DurationZeroPadded`（`HH:mm:ss` / `mm:ss`）、`FileSafeClock`（`h.mm.ss` / `mm.ss`）；`ClipListDisplayLogic`、`ReplaySeekLogic`、`ClipTimelineControl`、`RecordingUiFormat`、`ClipExportNamingLogic` 均委托同一实现，新增 `TimeCodeFormatTests` 覆盖格式化与委托一致性。
- `SettingsWindow`：平台账号（六平台 WebView2 登录/清除，状态显示 Cookie 是否绑定）、弹幕与高能参数、录制默认目录与轮询间隔、ASR 预设/Key、Twitch 去广告代理、组件与插件更新（yt-dlp/插件检查与安装）。
- `WebViewLoginService` + `PlatformLoginProfiles`：按平台加载登录页（Twitch/YouTube 等内存态页），底部「我已登录，采集 Cookie」按钮；命名平台自动轮询关键 Cookie（B 站 `SESSDATA`、Twitch `auth-token`、YouTube `SAPISID/__Secure-3PSID` 等），虎牙/斗鱼/抖音采集全部 Cookie；采集结果经 `CredentialStore.SaveAsync` DPAPI 加密入库；临时 WebView2 用户目录用后删除。

### 10.5 系统指标与状态栏

- `SystemMetricsService`：定时采样 CPU/GPU/内存与窗口 FPS（通过 `MpvPlayerView` 健康指标），状态栏显示「多路并发、解码状态、GPU/CPU 占用、网络延迟」。
- GPU 指标：nvidia-smi CSV 输出解析（首行四列、`N/A`/`[N/A]`/非数字视为缺失、字段不足或空输出不可用）与 GPU/解码/编码/显存文本、进度条值等展示规则已抽到 `Services/GpuMetricsLogic`，由 `GpuMetricsLogicTests` 覆盖；进程启动、2 秒轮询与 UI 线程派发仍留在服务内（V0.7.5）。
- 顶栏布局按钮：`一键自适应排布`、`主副屏布局`、`自由布局`、`画中画预设`、`桌面全屏`、`恢复比例`（仅自定义布局时可见）；右侧为平台健康状态、主静音、布局模式文本（自动适配/自定义比例/主副屏/自由布局）。

### 10.6 自由画布布局（V0.7）

- 目标：在保留平铺（Dock）布局的同时，提供类似导播台的自由画布——窗格可任意拖动/缩放/重叠、自动磁吸、置顶画中画、锁定，并支持一键预设与方案持久化。
- `Services/` 纯逻辑层（可单测）：
  - `FreeformGeometry`：`NormalizedRect(Left/Top/Width/Height)`（0~1 归一化、clamp、像素换算、相交判断）、`ResizeEdge` 八方向枚举、`SnapGuideLine`/`SnapResult`。
  - `FreeformSnapEngine`：拖动 `SnapMove` 与缩放 `SnapResize`，8px 阈值吸附画布边界/中线与其它窗格边缘/中线/中心；支持最小尺寸、Shift 锁 16:9、Ctrl 临时旁路磁吸。
  - `FreeformLayoutDocument`：`{"version":2,"mode":"freeform","tiles":[{paneId,liveRoomId,left,top,width,height,zIndex,isPinned,isLocked}]}`，复用 `LayoutProfiles.DockLayoutJson` 列；`LooksLikeFreeform`/`TryParse` 兼容旧 Dock JSON（旧方案按窗格数自动迁移）。
  - `FreeformLayoutPresets`：`AutoFit`（网格）、`Hero`（主画面 0.72 + 右侧纵列）、`PictureInPicture`（首路铺满 + 2~4 路 0.28×0.28 右下置顶）、`DefaultLanding`（新拖入房间居中错开落位）；`FreeformPreset` 枚举。
- `Controls/` 交互层：
  - `FreeformWorkspaceCanvas : Panel`：按归一化矩形 `Measure/Arrange` 窗格（`ContentControl` + 共享 `RoomTileView` 模板）；指针事件通过 `Tunnel + handledEventsToo` 接管（`RoomTileView` 自身会处理点击/双击，普通冒泡会被吞掉）；按下后移动超过 3px 阈值才进入拖动/缩放（单击/双击语义保留），命中边缘/角 6px 热区进入缩放（角落优先），其余区域进入移动；热区命中、拖动阈值、缩放光标映射、拖动 HUD 文案、拖放落位、贴合 16:9 与画布宽高比等纯几何交互在 `Controls/FreeformInteractionLogic`（`FreeformInteractionLogicTests` 覆盖），控件只负责套用结果（V0.7.5）；右键菜单（置顶画中画/取消置顶、锁定/解锁、置顶/置底、贴合 16:9）、Z 轴普通层 1~999 与置顶层 1000~1999；`LayoutChanged` 事件回写文档（仅在真正发生移动/缩放后触发）。
  - 拖动反馈：拖动中窗格透明度降至 0.92 并临时升至最上层，浮层绘制蓝色落位框 + 跟随指针的尺寸/吸附 HUD（如 `1168 × 812 px · 100% × 100% · 已吸附`），光标切换为移动/缩放光标；拖动中按 `Esc` 取消并恢复按下前位置。
  - `SnapGuidesOverlay`：磁吸参考线 + 落位框 + HUD 浮层（蓝普通线 1.5px / 琥珀虚线中心线 + 端帽，不参与命中测试）。
- `MainWindowViewModel` 集成：`IsFreeformMode`/`FreeformDocument`/`WorkspaceTiles` 事件；`EnterFreeformMode`/`ToggleFreeformMode`/`ApplyFreeformPreset`/`UpdateFreeformDocument`；保存方案时自由布局写 v2 JSON（`SerializeCurrentLayout`），加载时自动识别并进入自由模式；`一键自适应排布` 自动退出自由模式。
- 拖拽放入：左侧列表按下拖动（`DataObject` 键 `limoclip/room-id`，移动 >4px 才算拖拽）→ 画布 `DragOver` 实时显示落位框/HUD → 松手：已在画布则 `SetPlacement`，否则 `AddRoomToWorkspaceAsync` 后落到指针处（默认 0.42×0.42 居中并 clamp），并更新自由布局文档。
- 移出窗格：自由布局删除文档 placement；Dock 宿主（自动适配 / 主副屏 / 自定义比例）统一走「整树重建」：先 `CaptureRoomStates` 保存状态、`DisposePlayers` 解绑并销毁旧播放器、`DetachLayoutTracking`，再按当前模式 `CreateAutoFit` / `CreateHeroAndSideColumn` / `CloneStructure + RemovePane` 生成新树并 `ApplyRebuiltLayoutAsync` 起播，最后 `LayoutStructureChanged` 驱动视图刷新；原因是直接 `RemovePane` 后原地替换 `Dock.Layout` 会让同一个 `MpvPlayerView` 同时被新旧两个 `ContentPresenter` 引用，下一次布局测量抛 `already has a visual parent` 导致白屏后闪退。画布模式同步 `SyncFreeformTiles`，不留空窗格。
- 宿主切换：Dock 与画布互斥显示（`ShowDockWorkspace = !IsWorkspaceEmpty && !IsFreeformMode`）。播放器控件（`MpvPlayerView`）不能同时被两个 `ContentPresenter` 挂载，切换时先 `DetachWorkspacePlayers()`（停止+解绑旧控件）→ 切换宿主并等一轮布局 → `StartWorkspacePlayersAsync()` 重新创建并起播；Dock 在自由模式下保持 `Layout=null`。
- 顶栏：`自由布局`（开/关，显示当前模式提示）、`画中画预设`（未进入自由布局时先进入再应用）；状态栏显示「已进入/退出自由布局」「已应用自由布局预设」。方案中的自由布局结构与平铺布局共用 `LayoutProfile`，保存/重启后可恢复。

### 10.7 视图宿主边界（MainWindow.axaml.cs）

`MainWindow.axaml.cs` 是主壳视图（code-behind），只承担「Avalonia 视觉树与窗口交互」，业务状态与数据流转一律留在 ViewModel / Service。允许的职责：

- **宿主生命周期**：Dock 平铺与 `FreeformWorkspaceCanvas` 两个宿主的挂载/卸载/显隐（`ShowDockWorkspace` 由 VM 决定，View 只执行 `RefreshWorkspaceHost`/`SyncFreeformTiles`）；宿主切换必须先 `DetachWorkspacePlayers()`（停止并解绑旧 `MpvPlayerView`）→ 切换宿主 → 等一轮布局 → `StartWorkspacePlayersAsync()`，因为同一播放器控件不能同时被两个 `ContentPresenter` 挂载（见 §10.6）。
- **窗口外壳与 chrome**：一体化标题栏拖动/双击最大化、`EnsureSplitterTemplate`/`SyncVisualProportions`、沉浸模式空闲计时器、桌面级全屏、F11/Esc 等键位到 VM 命令的映射。
- **系统与平台交互**：`StorageProvider` 文件/目录选择、资源管理器/浏览器打开、确认对话框、`SettingsWindow` 打开；WebView2 登录委托 `WebViewLoginService`。
- **指针手势**：房间列表拖拽的起落判定、窗格双击进入焦点、自由画布拖动/缩放期间的 `Esc` 取消，只把结果回写 VM 命令。
- **事件转发与视图态**：`DataContext`/属性变更订阅只做 UI 呈现（状态点、文案、画刷），不直接改数据库或平台会话。

禁止把以下逻辑放进 code-behind：房间/录制/弹幕状态机、布局方案持久化、平台解析与网络请求、播放器/录制的生命周期决策（这些属 `MainWindowViewModel`、`WorkspacePlayerManager`、`RecordingManager` 或更下层服务）。新增交互时先判断能否用 Bool→Bool 绑定/Behavior 放到 VM；确属视图树时序的才留在本文件，并同步更新本节与 §10.1。

---

## 11. 关键运行链路

1. **添加房间**：`AddRoomWindow` → 平台解析（`PlatformFactory.CanHandle` 遍历）→ `ExtractRoomId` → 入 `LiveRooms` → 分配显示序号。
2. **打开监控**：双击房间或从左侧列表拖拽到工作区指定位置 → `MainWindowViewModel` 找空窗格/在指针处新建（自由布局）/重建布局 → `WorkspacePlayerManager.StartAsync` → 弹幕 `Acquire`。
3. **方案切换**：`LoadProfileAsync` → 反序列化 Dock 布局 → 差量计算 → 保留播放器重挂载（`Dock.Layout` 设置）→ 新增窗格启动 / 移除窗格释放。
4. **手动/自动录制**：录制中心「立即开录」或轮询发现开播 → `RecordingManager.StartAsync` → FFmpeg 分段录制 → 断流续段 → 停止/离线合并 → 会话 Completed；录制期间弹幕写 JSONL、高能事件关联会话。
5. **剪辑导出**：剪辑台选场次 → probe/波形/弹幕加载 → 选区 → 无损切段 + ASS 烧录双版导出 → 进度回显。
6. **ASR 转写**：剪辑台选区 → FFmpeg 抽音频 → `OpenAiCompatibleAsrProvider` 调用云端 → 结果缓存 + 逐句 UI。
7. **插件热更新**：设置页检查更新 → 清单（GitHub Raw → Gitee Raw 回退）→ 版本比较 → 下载（主地址 → `mirrorDownloadUrl` 回退）→ SHA256 + Ed25519 验签 → 安装 → 原子替换 Provider/弹幕工厂。
8. **Twitch 去广告播放**：设置页可选代理模板 → 代理层解析；否则原生 GQL（`playerType=embed`）+ Usher，仍失败回退 yt-dlp。
9. **自定义清流**：添加时勾选/自动识别直链 → `CustomStreamProvider` 直接返回 `StreamQuality`（无弹幕会话）→ 监控/录制复用通用链路；录制产物无 `.jsonl`，剪辑台按空弹幕路径处理。

---

## 12. 配置、日志与运行环境

### 12.1 路径

| 内容 | 位置 |
| --- | --- |
| 数据库 | `%LocalAppData%\LiveMonitor\livemonitor.db`（打开失败回退 `bin\data`，写 `startup-fallback.log`） |
| 插件 | `%LocalAppData%\LiveMonitor\plugins\{pluginId}\{version}\` |
| WebView2 缓存 | `%LocalAppData%\LiveMonitor\`（临时目录，登录后清理） |
| 抖音 sign.js 缓存 | `%LocalAppData%\LiveMonitor\cache\douyin\sign.js` |
| 随包工具 | `tools/ffmpeg/ffmpeg.exe`、`tools/mpv/libmpv-2.dll`（由 `scripts/fetch-tools.ps1` 下载） |
| yt-dlp | `%LocalAppData%\LimoClip\run\yt-dlp.exe`（可由 `CliUpdater` 更新） |
| 日志 | `startup-error.log`、`startup-fallback.log`、`webview-login.log`（应用目录），`%TEMP%\livemonitor-mpv.log`（mpv） |

### 12.2 运行注意事项

- 本机安全软件可能对「位于工作区目录内的可执行文件」施加额外写入限制，导致 ffmpeg 无法写目标盘、资源管理器启动失败等；正式运行请使用 `scripts/refresh-and-run.ps1`（构建 → 复制到工作区外运行目录 → 启动），不要直接运行 `bin\Debug` 下的 exe。
- 构建使用仓库内便携 .NET SDK 时，需把 `DOTNET_ROOT`、`PATH`、`DOTNET_CLI_HOME`、`NUGET_PACKAGES`、`APPDATA/LOCALAPPDATA`、`TMP/TEMP` 指向仓库内目录（`refresh-and-run.ps1` 已处理）。
- GitHub 访问受限时，插件清单/DLL 会自动回退 Gitee 镜像；git push 如遇 DNS 污染可临时把 `140.82.113.4 github.com` 写入 hosts（用完删除）。

---

## 13. 构建、测试与发布

### 13.1 构建

```powershell
dotnet build LiveMonitor.sln --no-restore
```

- 工程目标框架：`Data`、`Media`、`Desktop` 为 `net8.0-windows`，其余类库为 `net8.0`；测试工程对应同目标框架（`Data`/`Media`/`Desktop` 测试为 `net8.0-windows`，`Platforms`/`Danmaku` 测试为 `net8.0`）。
- 需要 `tools/` 下的 ffmpeg/mpv 才能运行应用（`scripts/fetch-tools.ps1` 自动下载并校验）。

### 13.2 测试（当前 2990 个用例）

| 测试工程 | 覆盖范围 | 用例数 |
| --- | --- | --- |
| `tests/LiveMonitor.Data.Tests` | 数据库/仓储/凭据（DPAPI）、清流请求头迁移 | 38 |
| `tests/LiveMonitor.Platforms.Tests` | 六平台解析/API/弹幕协议、插件信任与热更新、镜像回退、自定义清流 Provider、斗鱼流接缝、抖音帧接缝与断线通知、弹幕重连退避、B 站连接解析（`BilibiliConnectionParsing`）、B 站房间号/画质/播放地址解析（`BilibiliProviderParsing`）、Twitch 频道与拉流解析（`TwitchProviderParsing`）、虎牙房间号/线路/档位解析（`HuyaProviderParsing`）、虎牙接口响应与房间页解析（`HuyaApiClientParsing`）、斗鱼接口响应解析（`DouyuApiParsing`）、插件版本比较与版本解析（`PluginVersionComparer`/`PluginVersionInfo`） | 887 |
| `tests/LiveMonitor.Danmaku.Tests` | 过滤器、高能检测、会话管理、重连/释放竞态、断线退避策略（`DanmakuReconnectPolicy`） | 125 |
| `tests/LiveMonitor.Media.Tests` | 录制状态机与并发收尾、轮询器与会话工作器、分段合并、剪辑导出、波形、增量波形/增量弹幕读取、ASR 响应解析与按句拆分（`AsrResponseParser`）、FFmpeg 参数协议分支（取消路径覆盖预取消与首帧进度回调取消） | 261 |
| `tests/LiveMonitor.Desktop.Tests` | ViewModel（主窗口拆分/房间目录/布局方案/状态栏/高亮/录制中心/剪辑台与 ASR/导出子视图模型/设置页 ASR/组件更新/通用与弹幕）、Dock/自由画布布局、时间轴几何（`ClipTimelineGeometry`）与缩放视口/拖拽钳制（`ClipTimelineViewLogic`）、播放器管理器与 `IWorkspacePlayer`、回放 seek 探测/防抖逻辑（`ReplaySeekLogic`）、剪辑预览位置推进与安全上界（`ClipPlaybackPositionLogic`）、剪辑预览播放状态机与守卫（`ClipPlaybackStateLogic`）、剪辑台展示层纯逻辑（`ClipStudioPresentationLogic`）、音量表解析与平滑（`AudioMeterLogic`）、mpv 流选项映射与请求头清理（`StreamOptionMapper`）、AppServices 释放、登录配置、播放状态文本、录制目标排序、自由布局命中与宿主切换、自由画布交互几何（`FreeformInteractionLogic`）、录制中心历史子视图模型（`RecordingHistoryViewModel`）、弹幕渲染层纯逻辑（`DanmakuOverlayLogic`）、窗格控制条 chrome 规则（`PlayerChromeLogic`）、健康采样规则（`PlayerHealthSampling`）、录制目标行展示规则（`RecordingTargetDisplayLogic`）、历史录像行展示规则（`RecordingHistoryDisplayLogic`）、播放器丢帧降档守护（`DropGuardPolicy`）、状态栏 GPU 指标解析与展示（`GpuMetricsLogic`）、边录边剪增量波形纯逻辑（`LiveWaveformLogic`）、剪辑台选区/步进/高能窗口纯规则（`ClipSelectionLogic`）、剪辑台列表行展示规则（`ClipListDisplayLogic`）、导出任务行展示规则（`ClipExportTaskDisplayLogic`）、录制格式化文案（`RecordingUiFormat`）、自由画布磁吸引擎（`FreeformSnapEngine`）、弹幕表情缓存键与重试规则（`DanmakuEmoteCacheLogic`）、录制中心摘要与目录归一化规则（`RecordingCenterDisplayLogic`）、剪辑导出文件名与时间码规则（`ClipExportNamingLogic`）、弹幕渲染帧节拍与停滞判定（`DanmakuRenderTiming`）、播放器 CA 与音量表路径解析（`PlayerPathResolver`）、时间码格式化统一入口（`TimeCodeFormat`）、边录边剪会话状态（`LiveClipSession`）、安全剪辑边界（`LiveClipSafety`）、自由画布预设边缘（`FreeformLayoutPresets`）、主窗口状态栏文案（`MainWindowStatusText`）、平台登录配置表（`PlatformLoginProfiles`）、录制中心与历史录像状态提示文案（`RecordingCenterStatusText`）、录制目标行回报文案（`RecordingTargetStatusText`）、语音识别面板文案（`AsrStatusText`）、窗格状态与重连提示文案（`RoomStatusText`） | 1679 |

合计 **2990** 个用例，五套全部通过（0 失败 0 跳过）。

运行方式（使用已构建的测试 dll，避免重复构建）：

```powershell
dotnet test tests\LiveMonitor.Data.Tests\bin\Debug\net8.0-windows\LiveMonitor.Data.Tests.dll --no-build
dotnet test tests\LiveMonitor.Platforms.Tests\bin\Debug\net8.0\LiveMonitor.Platforms.Tests.dll --no-build
dotnet test tests\LiveMonitor.Danmaku.Tests\bin\Debug\net8.0\LiveMonitor.Danmaku.Tests.dll --no-build
dotnet test tests\LiveMonitor.Media.Tests\bin\Debug\net8.0-windows\LiveMonitor.Media.Tests.dll --no-build
dotnet test tests\LiveMonitor.Desktop.Tests\bin\Debug\net8.0-windows\LiveMonitor.Desktop.Tests.dll --no-build
```

### 13.3 打包与发布

- `scripts/fetch-tools.ps1`：下载并校验 ffmpeg/mpv（或复用已下载文件）。
- `scripts/package-release.ps1 -Version <版本号>`：`dotnet publish` 自包含 win-x64 → 复制 tools/插件 → 生成 `README_FIRST.txt` → 去重 → zip + `SHA256SUMS.txt` → 输出到 `artifacts/`。发布包主程序为 `LimoClip.exe`（`AssemblyName=LimoClip`，`ApplicationIcon=Assets/AppIcon.ico`）。
- `scripts/build-plugins.ps1`：构建四个插件工程 → 用 `PluginSigner` 签名 → 更新 `plugins/providers-manifest.json`。
- `scripts/sign-plugin.ps1`：签名单个插件并输出清单条目（可带 `-MirrorDownloadUrl`）。
- 发布产物与 SHA256 记录见 GitHub Releases 页面（仓库不保留 zip）。

---

## 14. 已知限制与安全边界

**平台侧**

- 各平台存在反爬/风控：请求需带真实 UA/Referer，部分接口需要 Cookie 或动态签名；策略中已通过「带 Cookie + 降级/回退 + 中文错误」提高成功率，但不保证永久可用。
- 虎牙：海外 IP 会被 CDN 限流（单连接更短甚至不可播）；国内网络下仍存在 CDN 单连接寿命短的问题（由 §9.1 分段录制解决）。
- Twitch：`embed` 参数目前可避开流内插播广告，但 Twitch 可能变更策略；可在设置页配置去广告代理模板作为补充。原生通道依赖网络可达性，失败自动回退 yt-dlp。
- YouTube：依赖 yt-dlp；YouTube 接口变动时需更新 yt-dlp（设置页提供检查/更新）。
- 抖音：`sign.js` 为第三方 AGPL 脚本，不随安装包分发，需运行时获取；离线环境可手动放置并设置 `LIMOCLIP_DOUYIN_SIGN_JS`。
- 抖音：`sign.js` 为第三方 AGPL 脚本，不随安装包分发，需运行时获取；离线环境可手动放置并设置 `LIMOCLIP_DOUYIN_SIGN_JS`。
- 斗鱼：拉流地址为动态签名（Jint 执行平台下发 JS），签名逻辑变更时需要更新解析代码。
- 自定义清流：URL 有效性与「能否出流」由目标 CDN 决定，添加时不做真实性探测；防盗链站点需自行填 Referer/UA。清流无弹幕、无自动开录（持续推流恒 live 会触发反复开录），需手动录制。

**功能侧**

- 多路监控的路数上限取决于机器解码能力；超出阈值时可调低画质/帧率（自适应降级），仍可能丢帧。
- 合并录制分片时，concat 只能对齐到关键帧，拼接点可能有极少量重复帧（不影响播放）。
- ASR 走云端接口，需要自备 API Key，按各家计费；长音频有 15 分钟上限。

**安全边界**

- Cookie、ASR Key 一律 DPAPI（CurrentUser）加密落本地库，不经过应用外网络（ASR 只发音频到用户配置的接口）。
- 插件必须通过 Ed25519 验签 + SHA256 校验才会被加载；镜像站点被篡改只会导致验签失败，不会执行恶意代码。
- 录制/剪辑全部本地文件操作，不上传云端（ASR 除外）；删除录制会话会同时删除对应视频与弹幕文件。
