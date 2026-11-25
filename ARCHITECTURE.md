# LimoClip 架构与模块实现说明

> 本文面向开发者，描述当前代码**如何工作**：解决方案结构、模块边界、关键数据结构与每个模块的具体实现方式。
> 产品介绍、下载与使用说明见 `README.md`；历史阶段设计文档已归档到 `docs/archive/`（仅用于追溯，不再维护）。
> 运行时以仓库当前代码为准；文中的「模块」均对应 `src/` 下的工程或子目录。
>
> 可视化架构图：[静态 SVG](docs/images/architecture.svg) · [可交互版本](https://heyiai02.github.io/LimoClip/architecture.html)（使用 Archify 生成，源 IR 见 `.archify/architecture-limoclip-20261124-1530/candidate.json`）。

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
| 运行环境 | .NET 8（`net8.0` 类库 / `net8.0-windows` 桌面与媒体层），Windows 10/11 x64 |
| UI | Avalonia 11（Fluent + Semi.Avalonia 主题）、CommunityToolkit.Mvvm、Dock.Avalonia（可拖拽/序列化的布局系统） |
| 播放 | libmpv（LibMpv.Avalonia）进程内渲染，硬件解码自动降级 |
| 录制/转码 | 独立 FFmpeg 进程（随包 `tools/ffmpeg`），`-c copy` 不转码 |
| 弹幕 | B 站/虎牙/抖音 WebSocket；YouTube InnerTube 轮询；斗鱼原始 TCP（皆为自实现协议层） |
| 数据 | SQLite（Microsoft.Data.Sqlite，WAL），本地文件（MKV/JSONL/波形缓存） |
| 凭据 | Windows DPAPI（CurrentUser）加密后入库 |
| WebView 登录 | WebView2（临时用户目录，登录后采集 Cookie） |
| 平台解析 | 各家 HTTP API + Jint（斗鱼签名、抖音 sign.js）；yt-dlp 作为 YouTube/Twitch 兜底 |
| 插件签名 | Ed25519（BouncyCastle）+ SHA256 清单校验 |
| 测试 | xUnit（6 个测试工程，当前共 940 个用例） |

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

释放顺序与创建**相反**（先停录制与弹幕，再停播放器，最后释放数据库），由 `AppServices.DisposeAsync` 与 `MainWindow.Closing` 保证。

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
- 绘制与统计：弹幕消息转成统一 `DanmakuMessage` 后进入 `DanmakuManager`。

#### Twitch（`LiveMonitor.Platforms.Twitch`）

- 三层降级 `GetRoomStatusAsync`：
  1. **代理层**（可配置 `TwitchPlaylistProxyUrl`）：`{channel}` 替换后直接 GET M3U8，解析主列表；
  2. **原生层**：GQL `PlaybackAccessToken`（persisted query，Client-ID `kimne78kx3ncx6brgo4mv6wki5h1ko`）+ Usher HLS。**`playerType` 固定 `embed`**——实测 `site/popout/mobile_web` 的媒体列表带 `twitch-stitched-ad` 广告段，`embed` 返回纯直播源；有 `auth-token` 时仍带 `Authorization: OAuth <token>` 头；
  3. **yt-dlp 兜底**：`RunJsonAsync("https://www.twitch.tv/<channel>")`，从 `formats` 取 `(height,fps)` 去重后的画质，`http_headers` 原样透传。
- 主列表解析 `ParseMasterM3u8`：扫描 `#EXT-X-STREAM-INF`（RESOLUTION/FRAME-RATE/BANDWIDTH），下一非注释行作为分片列表 URL，按 `(height,fps)` 保留带宽最大的一条，生成 `twitch-{h}p{fps}` 画质。
- 弹幕：`TwitchDanmakuClient` 连接 IRC WebSocket，`PASS oauth:<token>`（无 token 则匿名 `justinfan`），解析 `PRIVMSG` 消息。
- 错误语义：频道离线/不存在时返回 `RoomStatus.Offline`；其他失败抛中文异常并回退下一层。

#### YouTube（`LiveMonitor.Plugins.YouTube`）

- `YouTubeUrlParser` 支持 `watch?v=`、`live/`、`shorts/`、`youtu.be/`、`@handle`、`channel/UC...`，`roomId` 用视频 ID 或 `@handle`/`channel:UC...`。
- `YouTubeProvider` 调 yt-dlp `-j` 取元数据与 `formats`：按 `(height,fps)` 去重保留 `tbr` 最大；有独立音频轨时组装 `audio-file` 播放（libmpv 的 `audio-file` 选项）与录制双输入。
- 弹幕：`YouTubeDanmakuClient` 轮询 InnerTube 的 `live_chat` 延续令牌（无需登录），解析 `text/paid/paidSticker` 消息。

#### 虎牙（`LiveMonitor.Plugins.Huya`）

- 房间解析：`huya.com/<roomId 或个性域名>`；个性域名需抓房间页（手动解 gzip/br）用正则 `"profileRoom"\s*:\s*(\d+)` 反查真实房间号。
- 房间状态：`https://mp.huya.com/cache.php?m=Live&do=profileRoom&showSecret=1&roomid=<id>`，解析 `liveStatus`、主播、`data.stream.flv.multiLine`（AL/TX/HS 三条 CDN 线路）与 `liveData.bitRateInfo` 档位。
- 画质：Cookie 注入请求头；以 HS 线路为优先（实测最稳），按 `&ratio=` 档位（500/2000/4000/8000/20000 → 450p/720p/1080p）生成多条 `StreamQuality`，录制取最高档。
- 弹幕：`wss://wsapi.huya.com/?baseinfo=<base64 Tars>`，自实现 Tars/Wup 最小编解码（`HuyaTars`）；`cmd16` 订阅 `live:<uid>`/`chat:<uid>`，`cmd7` 单条 / `cmd22` 组推送，`uri1400` 为弹幕；`ClientWebSocket` 未请求 permessage-deflate 以避免停推。
- CDN 特性：单连接寿命 3~17s，因此录制采用分段（见 §9.1）。

#### 斗鱼（`LiveMonitor.Plugins.Douyu`）

- 房间信息：`https://www.douyu.com/betard/<roomId>`。
- 拉流：动态签名——GET `swf_api/homeH5Enc?rids=<roomId>` 取 JS，用 **Jint** 在沙箱中执行 `ub98484234` 求签名（prelude 注入 `window/document/navigator/location` 与 `CryptoJS.MD5` 走 .NET MD5）；随后 POST `/lapi/live/getH5Play/<roomId>`（`rate=-1`）取 `rtmp_url` 与 `rtmp_live`；匿名最高「蓝光4M」，Cookie 注入可提升档位。
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
- 过滤器链（`IDanmakuFilter`）：
  - `BilibiliUnicycleFilter`：B 站独轮车识别（同用户高频、复读机式复制弹幕、短文本豁免），命中后仅计数不渲染；
  - `KeywordFilter`：`DanmakuBlockKeywords` 黑名单，支持子串匹配。
- 高能检测：`SlidingWindowDetector` 以「短窗计数 / 基线窗计数 / 密度倍数 / 最小条数 / 冷却时间 / 加权关键词」判定高能事件，产出 `HighlightEvent`（峰值、基线、倍数、原因、命中词）；`HighlightSettingsStore` 负责参数持久化。
- 渲染：`DanmakuOverlay` + `DanmakuLaneAllocator`（按时间/高度分配轨道避免重叠）+ `DanmakuRenderTicker`（UI 线程帧驱动滚动）；`DanmakuEmoteText` / `DanmakuEmoteImageCache` 处理表情文本与图片缓存（可选开关）。
- 落地：录制时 `DanmakuJsonlRecorder` 逐行写 JSONL（含时间戳与高能标记）；剪辑回放由 `DanmakuJsonlReader` + `DanmakuPlaybackCursor` 按时间轴读取，并支持导出 `.ass` 字幕（`DanmakuAssConverter`）。

---

## 9. 媒体子系统（LiveMonitor.Media）

### 9.1 录制（Recording/）

**核心对象与职责**

| 类型 | 职责 |
| --- | --- |
| `IFullStreamRecorder` | 一个实例录一段；事件：首帧锚点、进度、退出；`StartAsync(quality, outputMkvPath, ct)`、`StopGracefullyAsync(timeout)` |
| `FfmpegFullRecorder` | 启动/管理单个 ffmpeg 进程：参数构造、`-progress pipe:1` 解析进度与锚点、`stderr` 环形尾缓存（4000 字）、stdin 写 `q` 优雅停录（超时 Kill 进程树） |
| `FfmpegArgumentBuilder` | 构造参数：`-rw_timeout 15000000`、UA、`-headers`（Referer/Cookie）、仅 https 的 `-ca_file`、可选第二路音频输入（`-map 0:v:0 -map 1:a:0`）、`-c copy -f matroska -progress pipe:1 -y`；刻意不带 `-reconnect`（见下） |
| `ProcessJobGuard` | Windows Job Object，异常退出时确保 ffmpeg 子进程一并结束，避免孤儿进程 |
| `RecordingDirectoryGuard` | 开录前目录可写预检（探针文件）、ffmpeg 失败原因中文翻译（权限/路径/编码器缺失等） |
| `RecordingFileNaming` | 输出文件名规则：`{开始时间}_{房间显示名}_{平台房间号}.mkv`（非法字符替换、长度限制） |
| `RecordingManager` | 录制状态机与调度：自动录制轮询、会话持久化、失败重试、卡死检测、分段续录、合并收尾、历史删除 |
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

### 9.3 云端 ASR（Asr/）

- 契约：`IAsrProvider.TranscribeAsync(audioPath/bytes, options, ct)` → `AsrTranscriptResult(Text, Segments, Language, Provider…)`。
- 实现 `OpenAiCompatibleAsrProvider`：调用 OpenAI 兼容 `POST /v1/audio/transcriptions`（multipart：`file` + `model` + `language` + `response_format=json`），解析 `segments[]`（起止时间 + 文本）与 `text` 两种返回；
- 音频提取：FFmpeg 将源 MKV 按区间抽 16kHz 单声道 MP3 后上传；超长区间限制 15 分钟；
- 缓存：按「源文件 + 区间 + 模型/接口」的 SHA256 缓存转写结果（本地文件），避免重复计费；
- 错误：HTTP 错误体解析为中文提示（鉴权失败/额度不足/模型不存在/网络超时等）；API Key 由 Desktop 注入（Media 不直接读库）。

---

## 10. 桌面 UI 层（LiveMonitor.Desktop）

### 10.1 主壳与视图切换

- `MainWindow` 四态：**监控工作区**（默认）、**录制中心**（全屏覆盖层）、**剪辑台**（全屏覆盖层）、**设置窗**（独立窗口）；
- `ApplyShellLayout()` 统一计算显示状态：`overlay`（录制/剪辑）、`focused`（单窗格全屏）、`fullscreenShell`（桌面级全屏）、侧栏可见性；据此设置顶栏/侧栏/状态栏可见性与 `RootGrid` 行高列宽；
- **沉浸模式**：焦点模式或桌面全屏下，鼠标空闲 2.5s 自动隐藏各播放器底部控制条与右上角状态角标、隐藏顶部提示；鼠标/按键活动立即恢复（`_immersiveIdleTimer` + 逐窗格 `SetChromeVisible`）；
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
  - 窗格增删（双击房间、关闭窗格）与 `AppendPaneAsync`；关闭窗格在自由布局/自定义比例下走 `WorkspaceDockBuilder.RemovePane` 从模型中真正移除（不留空窗格，见 §10.6）。

### 10.3 播放器与监控

- `WorkspacePlayerManager`：监控侧核心服务——
  - `StartAsync(tile)`：解析房间 → 取 Cookie → `provider.GetRoomStatusAsync` → 更新窗格标题/状态 → `QualityPolicy.Choose` 选画质 → `MpvPlayerView.PlayAsync(quality)` → 挂弹幕会话；自定义清流会调用 `player.SetDanmakuEnabled(false)` 隐藏窗格弹幕开关；
  - 失败处理：`ReconnectPolicy`（3s/10s/30s 退避，最多 3 次）→ 60s 失败复检；主播离线时定时轮询（60s）；
  - 播放器状态 → 窗格状态文本（`PlayerStateText`：连接中/直播中/已停止/重连中/播放失败）；
  - 焦点模式：双击窗格进入单窗格全屏（`IsFocused` + 静音策略 + 恢复原布局）、Esc 退出；
  - 锚点/未开播文字与状态同步：`SetDanmakuEnabled` 控制清流弹幕按钮显隐；
  - 底部控制条：音量/静音、弹幕开关、紧凑模式，以及 V0.7 新增的「暂停/继续」（`TogglePause`，同步 mpv `pause` 属性并切换 ⏸/▶ 图标）与「刷新播放」（`RequestRefresh` → `RefreshRequested` → `WorkspacePlayerManager.RefreshTileAsync`，清空退避/离线计时器后立即重新解析地址并起播）；刷新按钮图标使用 `Icons.axaml` 的 `IconRefresh` PathIcon（矢量绘制，避免字体码点被裁切）。
- `QualityPolicy`：按 Height/Fps 降序；大窗/焦点取最高；紧凑模式上限 480p；普通窗口优先 ≤720p，取第一个满足者。
- `MpvPlayerView`（libmpv 封装）：
  - 初始化选项：`hwdec=auto-safe`、`vo=libmpv`、`gpu-api=opengl`、`cache=yes`、`demuxer-max-bytes`（按分辨率 3M/4M/8M）、`network-timeout=10`、`audio-buffer=0.8`、`audio-stream-silence=yes`、关闭 OSC/默认快捷键（键位由应用处理）；
  - 播放选项：UA → `user-agent`、Referer → `referrer`、`Cookie` → `http-header-fields`、`audio-file`（分离音频）、TLS CA（http 不传）；
  - 状态机：`Loading → Playing → Offline/Reconnecting/Failed`；EOF/中断 → `Reconnecting` + 触发 `ReconnectRequested`（UI 显示「直播流中断，正在自动重连…」，而不是「未开播」）；
  - 健康指标：`demuxer-cache-time`、丢帧/解码速率等经 `PlayerHealthMetrics` 上报顶栏状态；
  - 音频表：通过 FFmpeg `af=astats` 输出到命名管道（`audio-meter`），`AudioLevelMeter` 读取显示。
- `MpvVideoView`：libmpv 渲染目标控件（OpenGL 纹理），尺寸变化触发重绘；`PlayerSmokeWindow` 为无 UI 冒烟用窗口。

### 10.4 录制中心 / 剪辑台 / 设置

- `AddRoomWindow` / `EditRoomWindow`：普通平台按链接识别平台；粘贴清流直链（或勾选「作为自定义清流添加」）时按清流保存，可填 Referer/User-Agent；编辑清流时可直接替换播放地址（保留槽位与录制配置）并触发当前窗格重新拉流。
- `RecordingCenterViewModel`：目标列表（自动录制开关、目录修改、立即开录/停止、当前录制进度）、历史列表（去剪辑、打开文件夹、删除；删除会同时删视频与 jsonl）。清流目标不显示「自动录制」开关。
- `ClipStudioViewModel`：录制场次选择 / 打开本地文件、片段播放（`LocalClipPlayerView`）、时间轴（波形+弹幕密度+选区）、导出面板（模式/编码器/目录/文件名/进度）、语音识别面板（转写当前选区、逐句跳转播放、复制文本）。
- `SettingsWindow`：平台账号（六平台 WebView2 登录/清除，状态显示 Cookie 是否绑定）、弹幕与高能参数、录制默认目录与轮询间隔、ASR 预设/Key、Twitch 去广告代理、组件与插件更新（yt-dlp/插件检查与安装）。
- `WebViewLoginService` + `PlatformLoginProfiles`：按平台加载登录页（Twitch/YouTube 等内存态页），底部「我已登录，采集 Cookie」按钮；命名平台自动轮询关键 Cookie（B 站 `SESSDATA`、Twitch `auth-token`、YouTube `SAPISID/__Secure-3PSID` 等），虎牙/斗鱼/抖音采集全部 Cookie；采集结果经 `CredentialStore.SaveAsync` DPAPI 加密入库；临时 WebView2 用户目录用后删除。

### 10.5 系统指标与状态栏

- `SystemMetricsService`：定时采样 CPU/GPU/内存与窗口 FPS（通过 `MpvPlayerView` 健康指标），状态栏显示「多路并发、解码状态、GPU/CPU 占用、网络延迟」。
- 顶栏布局按钮：`一键自适应排布`、`主副屏布局`、`自由布局`、`画中画预设`、`桌面全屏`、`恢复比例`（仅自定义布局时可见）；右侧为平台健康状态、主静音、布局模式文本（自动适配/自定义比例/主副屏/自由布局）。

### 10.6 自由画布布局（V0.7）

- 目标：在保留平铺（Dock）布局的同时，提供类似导播台的自由画布——窗格可任意拖动/缩放/重叠、自动磁吸、置顶画中画、锁定，并支持一键预设与方案持久化。
- `Services/` 纯逻辑层（可单测）：
  - `FreeformGeometry`：`NormalizedRect(Left/Top/Width/Height)`（0~1 归一化、clamp、像素换算、相交判断）、`ResizeEdge` 八方向枚举、`SnapGuideLine`/`SnapResult`。
  - `FreeformSnapEngine`：拖动 `SnapMove` 与缩放 `SnapResize`，8px 阈值吸附画布边界/中线与其它窗格边缘/中线/中心；支持最小尺寸、Shift 锁 16:9、Ctrl 临时旁路磁吸。
  - `FreeformLayoutDocument`：`{"version":2,"mode":"freeform","tiles":[{paneId,liveRoomId,left,top,width,height,zIndex,isPinned,isLocked}]}`，复用 `LayoutProfiles.DockLayoutJson` 列；`LooksLikeFreeform`/`TryParse` 兼容旧 Dock JSON（旧方案按窗格数自动迁移）。
  - `FreeformLayoutPresets`：`AutoFit`（网格）、`Hero`（主画面 0.72 + 右侧纵列）、`PictureInPicture`（首路铺满 + 2~4 路 0.28×0.28 右下置顶）、`DefaultLanding`（新拖入房间居中错开落位）；`FreeformPreset` 枚举。
- `Controls/` 交互层：
  - `FreeformWorkspaceCanvas : Panel`：按归一化矩形 `Measure/Arrange` 窗格（`ContentControl` + 共享 `RoomTileView` 模板）；指针事件通过 `Tunnel + handledEventsToo` 接管（`RoomTileView` 自身会处理点击/双击，普通冒泡会被吞掉）；按下后移动超过 3px 阈值才进入拖动/缩放（单击/双击语义保留），命中 6px 边缘 / 10px 角进入缩放，其余区域进入移动；右键菜单（置顶画中画/取消置顶、锁定/解锁、置顶/置底、贴合 16:9）、Z 轴普通层 1~999 与置顶层 1000~1999；`LayoutChanged` 事件回写文档（仅在真正发生移动/缩放后触发）。
  - 拖动反馈：拖动中窗格透明度降至 0.92 并临时升至最上层，浮层绘制蓝色落位框 + 跟随指针的尺寸/吸附 HUD（如 `1168 × 812 px · 100% × 100% · 已吸附`），光标切换为移动/缩放光标；拖动中按 `Esc` 取消并恢复按下前位置。
  - `SnapGuidesOverlay`：磁吸参考线 + 落位框 + HUD 浮层（蓝普通线 1.5px / 琥珀虚线中心线 + 端帽，不参与命中测试）。
- `MainWindowViewModel` 集成：`IsFreeformMode`/`FreeformDocument`/`WorkspaceTiles` 事件；`EnterFreeformMode`/`ToggleFreeformMode`/`ApplyFreeformPreset`/`UpdateFreeformDocument`；保存方案时自由布局写 v2 JSON（`SerializeCurrentLayout`），加载时自动识别并进入自由模式；`一键自适应排布` 自动退出自由模式。
- 拖拽放入：左侧列表按下拖动（`DataObject` 键 `limoclip/room-id`，移动 >4px 才算拖拽）→ 画布 `DragOver` 实时显示落位框/HUD → 松手：已在画布则 `SetPlacement`，否则 `AddRoomToWorkspaceAsync` 后落到指针处（默认 0.42×0.42 居中并 clamp），并更新自由布局文档。
- 移出窗格：自由布局删除文档 placement；自定义比例下 `WorkspaceDockBuilder.RemovePane` 从 Dock 模型移除窗格并剪掉空行/多余分割条；`LayoutStructureChanged` 驱动视图刷新，画布模式同步 `SyncFreeformTiles`，不留空窗格。
- 宿主切换：Dock 与画布互斥显示（`ShowDockWorkspace = !IsWorkspaceEmpty && !IsFreeformMode`）。播放器控件（`MpvPlayerView`）不能同时被两个 `ContentPresenter` 挂载，切换时先 `DetachWorkspacePlayers()`（停止+解绑旧控件）→ 切换宿主并等一轮布局 → `StartWorkspacePlayersAsync()` 重新创建并起播；Dock 在自由模式下保持 `Layout=null`。
- 顶栏：`自由布局`（开/关，显示当前模式提示）、`画中画预设`（未进入自由布局时先进入再应用）；状态栏显示「已进入/退出自由布局」「已应用自由布局预设」。方案中的自由布局结构与平铺布局共用 `LayoutProfile`，保存/重启后可恢复。

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

- 工程目标框架：除 `Desktop`/`Media` 为 `net8.0-windows` 外均为 `net8.0`；测试工程对应同目标框架。
- 需要 `tools/` 下的 ffmpeg/mpv 才能运行应用（`scripts/fetch-tools.ps1` 自动下载并校验）。

### 13.2 测试（当前 940 个用例）

| 测试工程 | 覆盖范围 | 用例数 |
| --- | --- | --- |
| `tests/LiveMonitor.Data.Tests` | 数据库/仓储/凭据（DPAPI）、清流请求头迁移 | 38 |
| `tests/LiveMonitor.Platforms.Tests` | 六平台解析/API/弹幕协议、插件信任与热更新、镜像回退、自定义清流 Provider | 483 |
| `tests/LiveMonitor.Danmaku.Tests` | 过滤器、高能检测、会话管理 | 98 |
| `tests/LiveMonitor.Media.Tests` | 录制状态机、分段合并、剪辑导出、波形、FFmpeg 参数协议分支 | 183 |
| `tests/LiveMonitor.Desktop.Tests` | ViewModel、Dock/自由画布布局、登录配置、播放状态文本、录制目标状态 | 138 |

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
