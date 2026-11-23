# LiveMonitor

多屏直播监控室。实现依据：

- `livemonitor_v0_1.md`：V0.1 施工契约（v1.7）
- `limoclip_v0_2.md`：V0.2 弹幕引擎详细设计实施文档（含实施记录）
- `code_artifact.md`：长期架构全景（v1.4）
- `livemonitor_v0_1.html`：交互 / 视觉基准原型

## V0.2 进度（弹幕引擎与高能空跑，已实现，待真实直播验收）

- [x] Step 1：Core 弹幕契约 + `LiveMonitor.Danmaku` / `LiveMonitor.Danmaku.Tests` 项目（sln 现 12 个工程）
- [x] Step 2：B站弹幕客户端（`getDanmuInfo` → WS 认证 → 30s 心跳 → zlib/brotli 解包 → `DANMU_MSG` / `SEND_GIFT` / `SUPER_CHAT_MESSAGE`）+ 纯函数解包器；`BilibiliWbiSigner` 为 `getDanmuInfo` 做 WBI 签名（2026-11 起未签名返回 `-352`）；表情元数据解析（大表情 `info[0][12]` / 行内表情 `info[0][15].extra`）；36 个测试
- [x] Step 3：Twitch 匿名 IRC 客户端（CAP/PASS/NICK/JOIN、PING/PONG、IRCv3 tags 解析、`@emotes` 表情标签）+ 26 个测试
- [x] Step 4：`KeywordFilter`（包含 + 正则）、`BilibiliUnicycleFilter`（含 `[表情]` 长度折算与大表情豁免）、`SlidingWindowDetector`、`HighlightSettingsStore`、`HighlightEventRepository`（高能事件落盘）与 `DanmakuManager`（按 `(Platform, RoomId)` 引用计数共享连接、退避重连 3/10/30/60s、50ms 批量、热更新）；Danmaku 95 测试、Data 21 测试、Platforms 120 测试
- [x] Step 5：`DanmakuOverlay`（60fps 共享帧循环车道渲染、同屏上限 30 / 紧凑 15、超限丢渲染不丢统计；表情默认渲染：行内 `[doge]` 按需替换为静态小图，纯大表情包默认显示为 `[表情名]`/静态图，可在设置页关闭「显示表情图片」或改为仅统计）；分屏弹幕开关与全局总闸（关渲染保留连接与高能统计）；工作区右侧可折叠「高能事件」面板（内存 200 条，完整历史入库）；状态栏「独轮车已拦」实时计数；设置页开放正则词条与高能参数卡片，关闭设置即热更新
- [~] Step 6（真实网络验证已完成，人工挂机验收待做）：B站 `getDanmuInfo` 补 WBI 签名后游客态真实收弹幕 24 条（房间 21452505）；Twitch 匿名 IRC 真实收弹幕 3 条（ludwig）；GUI 启动冒烟正常（窗口存活、DB 创建、无启动错误）。剩余人工挂机项：6~9 路弹幕海 UI 帧率/内存、独轮车误杀率、高能阈值稳定性、B站登录态（SESSDATA）下 uid 非 0

> 说明：实际实现用 Core 的 `IDanmakuClientFactory`（平台工厂在 `Platforms` 聚合层）取代了原计划的 `IPlatformProvider.CreateDanmakuClient`，使 `LiveMonitor.Danmaku` 只依赖 Core；B站 V0.2 只收弹幕、不发弹幕，游客可连，登录后独轮车单用户识别才完整。

## 当前进度

- [x] **Step 1：9 项目骨架与外部依赖**
  - `LiveMonitor.sln` + `Core` / `Data` / `Platforms.Common` / `Platforms.Bilibili` / `Platforms.Twitch` / `Platforms` / `Desktop`（另含 3 个测试项目）
  - Avalonia 11.3 + Semi.Avalonia + Dock 11.3 + LibMpv + WebView2 依赖
  - `tools/libmpv-2.dll`、`tools/yt-dlp.exe`、`tools/cacert.crt` 构建时自动复制到输出目录
  - 主窗口 48px 工具栏 / 260px 侧栏 / 28px 状态栏骨架，截图 `.cache/app-shell.png`
- [x] **三个技术 spike（全部通过）**
  - mpv：`hwdec=d3d11va-copy` 硬解成功，GPU decoder 9~10%（非零拷贝，详见下文）
  - WebView2：WinForms 宿主 + HttpOnly Cookie 枚举 + DPAPI 回环成功
  - Dock：无边框 2×2 与 5×6 网格 + `DockLayoutJson` 序列化/反序列化成功
- [x] Step 2：Core 实体与契约接口
  - 实体：`LiveRoom`、`LayoutProfile`、`LayoutSlot`、`PlatformCredential`、`DanmakuBlockKeyword`、`AppSetting`
  - 枚举 / 模型：`PlatformType`、`MonitorTier`、`PlayerState`、`StreamQuality`、`RoomStatus`、`PlayerHealthMetrics`
  - 接口：`IPlatformProvider`、`IPlayerController`、`IRoomRepository`、`ILayoutProfileRepository`、`IBlocklistRepository`、`IAppSettingsRepository`、`ICredentialStore`
- [x] Step 3：SQLite / DPAPI / 仓储
  - 六表 Schema + WAL + `foreign_keys=ON` + `busy_timeout`；`LiveMonitorDatabase.InitializeAsync` 幂等建库
  - `RoomRepository`（DisplayNumber 自动分配、`(Platform, RoomId)` 唯一）、`LayoutProfileRepository`（方案+槽位事务替换、PaneId 去重）、`BlocklistRepository`、`AppSettingsRepository`
  - `CredentialStore` + DPAPI（`CurrentUser`，固定 entropy），Cookie 密文入库
  - `tests/LiveMonitor.Data.Tests`：9 个 xUnit 测试全部通过，覆盖建库/级联/唯一约束/方案往返/同方案房间去重/DPAPI 过期
- [x] Step 4：B站 / Twitch 流解析
  - `BilibiliProvider`：HTTP-FLV / HLS 解析、短链与 short_id 解析、UA / Referer / 可选 Cookie 透传；`GetStreamAsync` 支持按 qn 单独拉档
  - `TwitchProvider`：`CliWrap` 调 `yt-dlp -j`，按 `(height,fps)` 去重取最高码率，离线 stderr 转 `RoomStatus.Offline`
  - `PlatformFactory` 负责 URL / `PlatformType` 路由；`tests/LiveMonitor.Platforms.Tests` 38 个测试通过
  - 平台已拆成独立类库：`LiveMonitor.Platforms.Common`（共享常量、JSON 读取、yt-dlp runner）、`LiveMonitor.Platforms.Bilibili`、`LiveMonitor.Platforms.Twitch`；`LiveMonitor.Platforms` 只保留 `PlatformFactory` 聚合。替换单个平台 DLL 后重启即可生效，运行时热重载暂未实现
  - 实测（真实 Cookie）：房间 7734200 返回 1080P 原画；`qn=10000/400/250/150` 均可单独拉取；游客态请求 10000 才被压到 250。B站真实 .NET 请求在 `danger-full-access` 下已验证通过
  - 实测 Twitch：`eslcs` 直播返回 5 档（1080p60 / 720p60 / 480p30 / 360p30 / 160p30），离线频道走 `RoomStatus.Offline`
  - 限制：B站单次 API 只返回当前一档，`AvailableQualities` 只有一档；Step 8 切档需再调 `GetStreamAsync(qn, cookie)`。验证 Cookie 只通过进程环境变量传入，未写入仓库或数据库
- [x] Step 5：`MpvPlayerView`
  - 播放链路已通：B站 7734200 720P 硬解（`d3d11va-copy`）、`vo=libmpv`、稳定 30fps、四态层、悬浮控制栏、健康度、`Dispose` 后台释放；截图 `.cache/player-smoke.png`
  - 关键时序：context 必须在 `OnAttachedToVisualTree`（GL 初始化前）挂到 `VideoView.MpvContext`；`Loaded` 太晚，会 `MpvException: not supported`，随后 `vo/libmpv` 报 `No render context set`
  - mpv 参数：`hwdec=auto-safe`、`vo=libmpv`、`gpu-hwdec-interop=auto`、`cache=yes`、`demuxer-max-bytes=2M`、`demuxer-max-back-bytes=0`、`network-timeout=10`、`aid=no`；紧凑模式用命名滤镜 `@compact:fps=15`，恢复时按名移除
  - TLS：mpv/libcurl 不带系统 CA；支持 `LIVEMONITOR_CA_FILE` 或 exe 同目录 `cacert.crt`。当前仓库 `tools/cacert.crt` 为 Git 自带 `ca-bundle.crt`（229,042 B），构建时复制到输出；默认保持 `tls-verify=yes`，干净启动已通过
  - smoke：`$env:LIVEMONITOR_PLAYER_SMOKE=1; dotnet run --project src/LiveMonitor.Desktop`
  - 另观察：窗口被遮挡/切换时 mpv 可能报 `mpv_render_context_render() not being called or stuck`，画面仍继续；前台复测偶发 1 次，Step 6 集成后仍需观察
- [x] Step 6：主界面 / WebView2 登录 / Dock 工作区
  - 48px 工具栏、260px 侧栏、28px 状态栏；工具栏为「模式标签 + 一键自适应排布」，不再有固定 2×2/3×3/4×4/5×6 档位
  - Auto-Fit：按活跃房间数 `N` 优先使用固定行分布：1=[1]、2=[2]、3=[1,2]（上 1 下 2）、4=[2,2]、5=[3,2]、6=[3,3]、7=[3,2,2]、8=[3,3,2]、9=[3,3,3]、10=[4,3,3]、11=[4,4,3]、12=[4,4,4]；13+ 按 `cols=ceil(sqrt(N·W/H·9/16))` 均分；`WorkspaceDockBuilderTests` 已覆盖
  - 自定义比例：`ProportionalDockSplitter` 可拖拽；拖过分割线后 `IsCustomLayout=true`，新增房间只追加/拆分，不重排；一键自适应恢复自动模式，并在工具栏出现「↶ 恢复比例」按钮，可恢复被自适应覆盖前的自定义窗口树与比例。`IsCustomLayout` 已持久化到 `LayoutProfiles`
  - 窗格换位：每格顶部中央 `⣿` 手动拖动条，拖到另一格释放即交换两个房间（停播、交换房间数据、重建两路播放器），不移动 OpenGL 控件；原生 Dock 拖动在 11.3.12.1 + 自定义无边框主题下未生效，已弃用
  - Dock 无边框：`ToolControl` + `ToolChromeControl` 自定义 `ControlTheme`，Pane 无标题栏/pin/close；空窗格占位与播放器铺满；分割线使用 5px `ProportionalStackPanelSplitter` 样式
  - 方案保存/另存为/载入：`DockLayoutJson` 保存窗口树与 `Proportion`，房间归属单独存 `LayoutSlots`；反序列化后按 `PaneId` 重建并 `BindTiles`，启动恢复双流
  - 账号与设置：B站/Twitch 登录窗（WebView2 独立 STA 线程 + CookieManager 轮询 + DPAPI）、屏蔽词库、独轮车开关
  - 测试：`tests/LiveMonitor.Desktop.Tests` 74 个 xUnit 测试通过（全仓库 310 个），覆盖 Auto-Fit 网格/行分布（含 3 路 [1,2]）、30 路、Proportion 克隆/套用、末行追加、比例变化后标自定义比例、房间放入工作区、3 路自动排布、自定义模式满窗格追加、平台徽章配色与在线状态点、弹幕轨道分配（总是优先顶部空闲轨道）、表情文本分段与图片地址解析（`DanmakuEmoteText`：普通文本单段、行内表情三段、连续表情、未闭合括号、大小写匹配等 8 个用例）
  - 实测：B站 7734200 与 Twitch `eslcs` 同屏播放；拖拽分割线后自动切换为 `自定义比例`，再双击加入第 3 路会追加窗格；保存后重启保持 `1×3` 与两条分割线。设置窗添加屏蔽词与独轮车开关持久化成功；B站登录页可正常加载
  - 限制：WebView2 登录成功后的 Cookie 捕获/DPAPI 保存路径尚未用真实账号端到端验证；登录窗关闭/取消会显示未捕获提示。`mpv_render_context_render` 告警仍需在 Step 8 长稳压测中确认；Twitch `yt-dlp` 偶发外部 SSL 瞬断（`UNEXPECTED_EOF_WHILE_READING`），失败态会显示重连按钮
- [x] Step 7：方案持久化与差量切换
  - `LayoutDiffService.Plan` 按 `LiveRoomId` 求 `Removed/Retained/Added`；`SelectProfileAsync` 在脏状态弹「保存并切换/放弃修改/取消」，取消会回退方案下拉选中项
  - 结构一致（PaneId 集合相同）时**不替换 Dock 树**：保留原窗格与 `PlayerView`，只按 `LayoutSlots` 换房间/清理空窗格，实现零黑屏；实测 3 路 2×2 → 2 路 2×2 两个保留播放器画面不断流
  - 结构变化（如 1×3 → 2×2）时无法安全搬运 OpenGL 控件：直接移动会 `MPV_ERROR_VO_INIT_FAILED (code -15)`，因此降级为完整重建并显示「布局结构变化，已重建 N 路」；这是当前已明确的限制
  - 测试：`tests/LiveMonitor.Desktop.Tests` 27 个通过（新增 `LayoutDiffServiceTests` 2 个）
- [ ] Step 8：自适应画质 / 丢帧巡检 / 离线轮询 / 压测
  - `QualityPolicy`：窗格高度 >720px 且活跃 <12 路时选最高档；360–720px 选 ≤720P；<360px 或活跃 ≥12 路选 ≤480P 并强制 `vf=fps=15`；全屏浮层按焦点槽拉最高档
  - `WorkspacePlayerManager` 的 `SizeChanged` 500ms 防抖后重新选档；2s 采样 `decoder-frame-drop-count + frame-drop-count`，连续 3 个周期丢帧率 >5% 时降一档，无更低档则切 15fps；未开播每 60s 轮询；断流按 `ReconnectPolicy` 的 3/10/30s 退避重连并重新拉流签名
  - 实测：2×2 窗口从 1456×939 缩到 1456×650，Twitch 从 720P60 切到 480P30 + 15fps，恢复窗口后回到 720P60；B站游客只有 720P 一档，只能叠 15fps、无法降到 480P
  - 测试：`tests/LiveMonitor.Desktop.Tests` 58 个通过（含 `QualityPolicyTests` 11 个、`ReconnectPolicyTests` 6 个、自动布局/比例/状态/手动换位相关测试）
  - 窗格交互：双击窗格或点右上 `⛶` 进入全屏浮层（独立播放器、隐藏工具栏/侧栏/状态栏、只保留全屏声音，双击/Esc/按钮退出；退出提示 2.5 秒后自动隐藏）；`✕` 把房间移出工作区但保留主库记录；音量左侧 16 段 LED 电平条，静音时仍显示波动
  - 顶部工具栏 `🔊` 全部静音：第一次记录每路原静音状态并全部静音，再点按原状态恢复（不强制全部开声）；全局健康灯每 2 秒汇总丢帧率（≥5% 变红）；状态栏显示进程内存、GPU 利用率/解码/编码（`nvidia-smi`，非 N 卡显示 `--`）
  - 左侧分组 pills：`全部` + 当前已有 `LiveRoom.GroupTag` 动态生成；添加直播间时可直接输入分组文本。状态栏 `独轮车已拦: N` 在 V0.2 接入实时计数（V0.1 恒为 0 占位）
  - 平台徽章分色：B站粉 `#FB7299`、Twitch 紫 `#9146FF`、抖音红 `#FE2C55`、斗鱼橙 `#FF6A00`、虎牙黄 `#FFB300`、YouTube 红 `#FF0033`；主库每行右上在线点（绿=直播中、灰=未开播、深灰=未知，来自添加/工作区播放状态）以及铅笔修改名称/分组/序号、红叉确认后从主库删除并清空对应工作区窗格；自动模式下删除/移出后会按剩余房间重排，自定义比例保持原结构
  - 默认布局加载时会补齐 `ProportionalDockSplitter` 节点，**不点「一键自适应排布」也可以直接拖拽分割线**；点击一键自适应：行列数与当前一致且没有空窗格时只把比例归位，不重建播放器，保留每路静音/音量、画面与在线状态；有空格或结构变化才重建（3 路重建为上 1 下 2），重建时先显示「正在重新连接…」并用快照恢复音频、并发起播
  - 限制：20~30 路并发压测、丢帧阈值与 60s 离线轮询的实测尚未完成；B站多档需 Step 8 后续用 `GetStreamAsync(qn, cookie)` 逐档拉取；全屏浮层会额外建立一路播放连接

## 构建与运行

要求：Windows x64，.NET 8 SDK。

### 第三方二进制（不入库）

仓库只跟踪源码和 `tools/cacert.crt`。播放与流解析依赖的二进制体积大、再分发还涉及第三方许可，所以都写进 `.gitignore`、不提交：

| 路径 | 用途 | 上游 |
| --- | --- | --- |
| `tools/libmpv-2.dll`（约 116 MB） | mpv 播放内核，`LibMpv.Avalonia` 加载 | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) 的 `mpv-dev-x86_64-*.7z`（别选 `-v3-`，那个要 AVX2） |
| `tools/yt-dlp.exe`（约 22 MB） | Twitch / YouTube 流解析 | [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe) |
| `tools/ffmpeg/`（约 100 MB，可选） | 探测 / 兜底，应用当前未直接调用 | [gyan.dev FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/) |

首次克隆后一条命令补齐（已存在的文件跳过，`-Force` 重新下载）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1             # libmpv-2.dll + yt-dlp.exe + cacert.crt
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1 -WithFfmpeg # 额外下载 ffmpeg
```

脚本会查 GitHub 最新 Release、下载后用 7-Zip 解包（系统没有 7-Zip 时临时下载官方单文件 `7zr.exe`，不安装软件）；`cacert.crt` 为公开 CA 根证书清单，仓库已带，缺失时才从 curl.se 拉取。构建时 `LiveMonitor.Desktop.csproj` 会把 `libmpv-2.dll`、`yt-dlp.exe`、`cacert.crt` 复制到输出目录；缺文件不会让构建失败，但播放/解析会在运行时失败。

发 GitHub Releases 时，把 `tools/` 里的二进制连同 `bin/Release/net8.0-windows/` 的输出一起打进 zip 包即可，仓库本身不必包含它们。

```powershell
dotnet build LiveMonitor.sln
dotnet run --project src/LiveMonitor.Desktop
dotnet test tests/LiveMonitor.Data.Tests/LiveMonitor.Data.Tests.csproj
dotnet test tests/LiveMonitor.Platforms.Tests/LiveMonitor.Platforms.Tests.csproj
dotnet test tests/LiveMonitor.Desktop.Tests/LiveMonitor.Desktop.Tests.csproj
```

`LiveMonitor.Data` 与测试项目目标为 `net8.0-windows`（DPAPI 仅 Windows）；`Core` / `Platforms` 为 `net8.0`，`Desktop` 为 `net8.0-windows`，`LiveMonitor.Platforms.Tests` 为 `net8.0`。

启动失败时主窗口会弹「启动失败」完整异常窗，同时写 `bin/Debug/net8.0-windows/startup-error.log`；若默认数据库目录 `%LOCALAPPDATA%\LiveMonitor` 不可写，会自动改用程序目录下 `data/livemonitor.db`，并写 `startup-fallback.log`。用 VSCode/`dotnet run` 时若仍失败，请把这两个日志和弹窗文本一起反馈。

`global.json` 固定使用 8.0.1xx 及以上 feature band，避免误用旧 SDK。

## 本机沙箱环境（重要）

当前工作区使用便携 SDK `.dotnet/`（8.0.428），系统没有安装 .NET SDK。在 `workspace-write` 沙箱下 Schannel 无法写 `%APPDATA%\Microsoft\Crypto`，会报 `SEC_E_NO_CREDENTIALS`；切到 `danger-full-access` 后 B站真实 HTTPS 请求已通过。NuGet 仍走本地 HTTP 代理（避免依赖 Schannel）：

```powershell
# 代理脚本（需要保持运行）
node .dev/nuget-proxy.mjs

# 还原
dotnet restore LiveMonitor.sln --configfile .dev/NuGet.offline.config -p:RestoreNoHttpCache=true
# 之后可加 --no-restore 构建
dotnet build LiveMonitor.sln --no-restore
```

每次 dotnet 命令还需要设置：

```powershell
$env:DOTNET_ROOT='E:\code\liveSurveilance\.dotnet'
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME='E:\code\liveSurveilance\.dotnet-home'
$env:NUGET_PACKAGES='E:\code\liveSurveilance\.nuget\packages'
$env:APPDATA='E:\code\liveSurveilance\.appdata\Roaming'
$env:LOCALAPPDATA='E:\code\liveSurveilance\.appdata\Local'
$env:TEMP='E:\code\liveSurveilance\.cache\temp'; $env:TMP=$env:TEMP
```

`.dev/`、`.cache/`、`.nuget/`、`.dotnet/` 均为本机辅助目录，已写入 `.gitignore`。在正常机器上直接 `dotnet restore` 即可，不需要代理。

## Spike 结论

### 1. mpv / LibMpv.Avalonia

- 必须调用 `LibMpv.Client.LibMpv.UseLibMpv(2)`，否则默认查找 `libmpv.dll`（实际文件名 `libmpv-2.dll`），所有绑定变成抛 `NotSupportedException` 的惰性桩。
- `VideoView` 的 render context 在 GL 初始化后才建立；`MpvContext` 赋值后要延迟约 1.5s 再 `loadfile`，否则 `[vo/libmpv] No render context set`。
- 实测 `hwdec=d3d11va-copy`、1920×1080@60、`drop=0/1`、GPU decoder 9~10%，硬解成立。
- Avalonia `VideoView` 走 ANGLE OpenGL，**拿不到零拷贝 D3D11 interop**；`gpu-hwdec-interop=d3d11` 不成立，改用 `auto`。12 路以上并发需要补压测。
- `NativeVideoView` 可零拷贝，但创建子 HWND 会有 airspace 问题，弹幕层无法覆盖视频，不采用。

### 2. WebView2

- `Microsoft.Web.WebView2` + WinForms `Form` 在专用 STA 线程可正常创建；`CookieManager.GetCookiesAsync` 能读到 HttpOnly（实测 `lm_httponly=abc123`）。
- DPAPI `Protect`/`Unprotect` 回环成功（`SESSDATA=spike-test`）。
- **DSH 沙箱内 controller 创建失败**（`0x8000FFFF E_UNEXPECTED`），`danger-full-access` 下通过。生产环境不应有该限制；若在受限环境运行，需要允许 Chromium 子进程/命名管道。
- 登录完成后销毁窗口与临时 UserDataFolder；Cookie 用 `DataProtectionScope.CurrentUser` 加密入库。

### 3. Dock.Avalonia

- 默认 `ToolChromeControl`/`ToolTabStrip` 带标题栏与 pin/close，原型要求无边框；必须覆盖 `ToolDockControl` 与 `ToolControl` 的 `ControlTheme`，Style 无法覆盖模板内的本地绑定。
- 自定义主题下 2×2 与 5×6 均能排版；`DockSerializer` 序列化 5×6（30 格）得到约 367KB JSON，反序列化成功。
- **序列化前必须清空 `Tool.Context` / `PlayerView` 等 Avalonia 控件**，否则 `System.Text.Json` 检测到对象环（`$.MdiBounds.X`）。
- 去掉 chrome 后 Dock 自带 pane 拖拽手柄消失，但比例分割线仍由 `ProportionalDockSplitter` 模型驱动。
- Dock 11.3.12.1 默认内容模板未把 `ProportionalDockSplitter` 解析成 `ProportionalStackPanelSplitter`（可视树只出现 0x0 的 `GridSplitter`）。`MainWindow.EnsureSplitterTemplate()` 在 `PART_ContentPresenter.DataTemplates` 插入 `FuncDataTemplate<ProportionalDockSplitter>` 后，分割线渲染为 5px `Thumb`，可拖拽并更新 `Proportion`；`App.axaml` 只保留分割线样式。

## 已采用的默认口径（待确认）

1. `Alias` 存纯主播名，UI 用 `#DisplayNumber + " " + Alias` 拼接；入库前去掉用户输入里的 `#数字`。
2. `IsFocusSlot` 只表示持久化的“主视角”偏好；双击放大是运行时焦点，不写库。槽位音量/15fps 手选状态 V0.1 先不持久化。
3. 网格行列数写入 `DockLayoutJson` 的 profile 元数据（或由 pane 数推导），不新增 SQLite 列。
4. Dock 用自定义无边框主题；拖拽换位由工作区层实现，不依赖 Dock 默认 chrome。
5. `DanmakuBlockKeywords.IsRegex` 在 V0.2 设置页开放勾选；词条保存后关闭设置窗即热更新到运行中的会话。
6. 接口以 `livemonitor_v0_1.md` 为准：`GetRoomStatusAsync(roomId, cookie?, ct)`；弹幕客户端改由 Core `IDanmakuClientFactory` 创建，不再加在 `IPlatformProvider` 上。
7. V0.2 已接入实时弹幕流与渲染：连接只跟「房间是否在工作区」绑定，关闭弹幕开关/全局总闸只停渲染，高能统计与独轮车计数继续。
8. WebView2 用 WinForms 独立登录窗；B站取 `SESSDATA`，Twitch 取 `auth-token`。
9. mpv 用 `gpu-hwdec-interop=auto`；焦点 1080P 的 2M 缓存是否够用，Step 5 实测后再调。

## 目录

```text
src/
  LiveMonitor.Core/                 实体、接口（Step 2）
  LiveMonitor.Data/                 SQLite、DPAPI、仓储（Step 3）
  LiveMonitor.Platforms.Common/     平台共享常量、JSON 读取、yt-dlp runner
  LiveMonitor.Platforms.Bilibili/   B站 Provider（独立类库）
  LiveMonitor.Platforms.Twitch/     Twitch Provider（独立类库）
  LiveMonitor.Platforms/            PlatformFactory 聚合与默认注册、DanmakuClientFactory
  LiveMonitor.Danmaku/              弹幕会话管理（DanmakuManager）、过滤器、高能检测、参数持久化（V0.2）
  LiveMonitor.Desktop/              Avalonia UI、Dock、mpv、WebView2
tests/
  LiveMonitor.Data.Tests/           Data 层单元测试（xUnit）
  LiveMonitor.Platforms.Tests/      B站 / Twitch 解析与弹幕客户端单元测试（xUnit）
  LiveMonitor.Danmaku.Tests/        弹幕过滤 / 高能检测 / DanmakuManager 单元测试（xUnit，V0.2）
  LiveMonitor.Desktop.Tests/        Dock Auto-Fit / 结构克隆单元测试（xUnit）
tools/                    运行时二进制目录（默认不入库，用 scripts/fetch-tools.ps1 补齐；cacert.crt 例外）
scripts/                  fetch-tools.ps1：一键下载 libmpv / yt-dlp / ffmpeg
spikes/                   三个技术验证 demo（不参与主解决方案）
```
