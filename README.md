# LiveMonitor

多屏直播监控 + 弹幕高能录制剪辑。实现依据：

- `livemonitor_v0_1.md`：V0.1 施工契约（v1.7）
- `limoclip_v0_2.md`：V0.2 弹幕引擎详细设计实施文档（含实施记录）
- `limoclip_v0_3.md`：V0.3 独立录制中心与全程录制引擎设计实施文档（含实施记录 §8）
- `limoclip_v0_4.md`：V0.4 剪辑台（本地预览 / 波形时间轴 / 双版切片导出）设计实施文档
- `limoclip_v0_5_asr.md`：V0.5 ASR / 平台插件热更新 / 国内平台补齐设计实施文档（V0.5-A 已实现，B/C 暂缓）
- `code_artifact.md`：长期架构全景（v1.7，已按 V0.5-A 口径更新）
- `HANDOFF.md`：当前版本交接（实现清单、验收证据、偏差与已知限制）
- `livemonitor_v0_1.html`：交互 / 视觉基准原型

## V0.2 进度（弹幕引擎与高能空跑，已实现，待真实直播验收）

- [x] Step 1：Core 弹幕契约 + `LiveMonitor.Danmaku` / `LiveMonitor.Danmaku.Tests` 项目（sln 现 12 个工程）
- [x] Step 2：B站弹幕客户端（`getDanmuInfo` → WS 认证 → 30s 心跳 → zlib/brotli 解包 → `DANMU_MSG` / `SEND_GIFT` / `SUPER_CHAT_MESSAGE`）+ 纯函数解包器；`BilibiliWbiSigner` 为 `getDanmuInfo` 做 WBI 签名（2026-11 起未签名返回 `-352`）；表情元数据解析（大表情 `info[0][12]` / 行内表情 `info[0][15].extra`）；36 个测试
- [x] Step 3：Twitch 匿名 IRC 客户端（CAP/PASS/NICK/JOIN、PING/PONG、IRCv3 tags 解析、`@emotes` 表情标签）+ 26 个测试
- [x] Step 4：`KeywordFilter`（包含 + 正则）、`BilibiliUnicycleFilter`（含 `[表情]` 长度折算与大表情豁免）、`SlidingWindowDetector`、`HighlightSettingsStore`、`HighlightEventRepository`（高能事件落盘）与 `DanmakuManager`（按 `(Platform, RoomId)` 引用计数共享连接、退避重连 3/10/30/60s、50ms 批量、热更新、独轮车计数按被拦截的不同用户数统计）；Danmaku 96 测试、Data 21 测试、Platforms 120 测试
- [x] Step 5：`DanmakuOverlay`（60fps 共享帧循环车道渲染、同屏上限 30 / 紧凑 15、超限丢渲染不丢统计；表情默认渲染：行内 `[doge]` 按需替换为静态小图，纯大表情包默认显示为 `[表情名]`/静态图，可在设置页关闭「显示表情图片」或改为仅统计）；分屏弹幕开关与全局总闸（关渲染保留连接与高能统计）；工作区右侧可折叠「高能事件」面板（内存 200 条，完整历史入库）；状态栏「独轮车已拦: N 人」按被拦截用户数实时计数；设置页开放正则词条与高能参数卡片，关闭设置即热更新
- [~] Step 6（真实网络验证已完成，人工挂机验收待做）：B站 `getDanmuInfo` 补 WBI 签名后游客态真实收弹幕 24 条（房间 21452505）；Twitch 匿名 IRC 真实收弹幕 3 条（ludwig）；GUI 启动冒烟正常（窗口存活、DB 创建、无启动错误）。剩余人工挂机项：独轮车误杀率、高能阈值稳定性、B站登录态（SESSDATA）下 uid 非 0；**性能对比已确认不做**

> 说明：实际实现用 Core 的 `IDanmakuClientFactory`（平台工厂在 `Platforms` 聚合层）取代了原计划的 `IPlatformProvider.CreateDanmakuClient`，使 `LiveMonitor.Danmaku` 只依赖 Core；B站 V0.2 只收弹幕、不发弹幕，游客可连，登录后独轮车单用户识别才完整。

## V0.3 进度（录制中心与全程录制引擎，已实现并完成实机验收）

实现依据：`limoclip_v0_3.md`（V0.3 起验收口径以该文档为准；`code_artifact.md` 的 V0.3 段落是早期设想，环形缓冲 / NAS 归档 / 手动转 MP4 已确认砍掉）。

- [x] Step 1：Core 录制契约（`RecordingTarget` / `RecordingSession` / `RecordingStatus` / `RecordingProgress` / 两个仓储接口 / `IFullStreamRecorder` / `IHighlightEventEnricher`）+ `LiveMonitor.Media`、`LiveMonitor.Media.Tests`（sln 现 14 个工程）
- [x] Step 2：Data 新增 `RecordingTargets` / `RecordingSessions` 表、`HighlightEvents` 迁移 `RecordingSessionId` / `RecordingOffsetMs`（`PRAGMA user_version=4`）、两个仓储与迁移测试
- [x] Step 3：`ProcessJobGuard`（Job Object `KILL_ON_JOB_CLOSE`）、`FfmpegArgumentBuilder`、`FfmpegProgressParser`、`FfmpegLocator`、`FfmpegFullRecorder`（`-c copy` .mkv、stdin `q` 优雅停录、`-progress pipe:1` 首帧锚点）、`DanmakuJsonlRecorder`（meta + dm 行）
- [x] Step 4：`RecordingManager`（手动 / 自动录制状态机、原画画质、断流退避重试、30s 卡死判定、崩溃恢复、`RecordingHighlightTracker` 高能绑定 SessionId / Offset、60s 开播轮询、删除记录连文件删除）
- [x] Step 5：录制中心 UI（顶栏「📺 监控工作区 / ⏺ 录制中心」切换不销毁 Dock 与播放器；全局目录、逐房间自动录制、立即开录 / 停止、实时时长 / 码率 / 大小 / 弹幕 / 高能、历史列表、删除确认弹窗）+ `AppServices` 接线 + ffmpeg 打包复制
- [x] Step 6：实机验收（2026-11-23，B站 6650029 / 7734200）：0 路播放时 2 路录制持续写入 mkv + jsonl；停录后 mkv 可播（ffmpeg 解码 exit=0）；强杀宿主后 ffmpeg 0.23s 内全部退出且文件可播；重启后未收尾会话标 Failed 并保留文件大小
- 测试：Data 35 / Platforms 120 / Danmaku 98 / Media 95 / Desktop 74 = **422 全绿**；构建 0 错误
- 实机问题修复：静态 ffmpeg 在 Windows 注册证书库失败导致 HTTPS 拉流 `I/O error` → 显式传 `-ca_file tools\cacert.crt`；录制中心实时指标不刷新（`RecordingCenterViewModel.InitializeAsync` 未被调用）；jsonl 中文被 `\uXXXX` 转义

## V0.4 进度（剪辑台：本地预览 / 波形时间轴 / 双版切片导出，已实现并完成验收）

实现依据：`limoclip_v0_4.md`（V0.4 起验收口径以该文档为准）。

- [x] Step 1：Core 剪辑契约（`RecordedDanmakuItem` / `VideoMetadata` / `WaveformData` / `ClipExportRequest` / `ClipExportProgress` + `IWaveformService` / `IDanmakuAssConverter` / `IHardwareEncoderDetector` / `IClipExporter`）；Data 新增按录制会话查询高能事件（`GetByRecordingSessionAsync` + 索引）
- [x] Step 2：`WaveformService`（`-vn -ac 1 -ar 4000 -f s16le` 流式 PCM、2000 桶音频峰值 + 弹幕密度、SHA256 缓存，无音轨降级全 0）
- [x] Step 3：`VideoProbeService`（仓库无 ffprobe，解析 `ffmpeg -i` stderr）、`DanmakuAssConverter`（滚动弹幕 + 轨道避让）、`HardwareEncoderDetector`（nvenc→qsv→amf→libx264 实测探测并缓存）、`ClipExporter`（无损 clean + 硬编烧录 danmaku 双版、Job Object、取消/失败清理）
- [x] Step 4：`LocalClipPlayerView`（独立本地 mpv 上下文：hr-seek / keep-open / 32M 回档、100ms 位置轮询、0.5x~2.0x 倍速）；`DanmakuPlaybackCursor` + `DanmakuOverlay` 暂停/倍速实现弹幕回放同步（seek 1.5s 回看补显）
- [x] Step 5：`ClipTimelineControl`（自绘双波形时间轴：自适应刻度、音频波形开方压缩、5s 桶弹幕密度热力、高能区间、In/Out 拖拽、播放头拖拽、滚轮锚点缩放 1x~60x、平移/双击复位）
- [x] Step 6：剪辑台四区视图 + 主窗口第三视图「✂ 剪辑台」+ 录制中心历史卡片「✂ 去剪辑」入口；快捷键 Space / ←→（Shift 1s）/ I / O / Esc
- 测试：Data 37 / Platforms 120 / Danmaku 98 / Media 140 / Desktop 76 = **471 全绿**；构建 0 错误
- 验收：GUI 脚本 `.cache/v04-acceptance2.ps1`（证据 `.cache/v04b-*.png`）——场次加载、弹幕回放、选区联动、双版导出 **1.9s** 完成（clean 13.30s / burned 11.20s），烧录帧弹幕清晰；本地文件 picker 已验证可打开并过滤 `*.mkv/*.mp4`，实际加载逻辑由 Desktop 单测覆盖
- 验收中修复：右侧入点/出点文本不刷新、导出文件名不随选区更新、波形视觉接近平线、旧库迁移索引早于补列、`UseWindowsForms` 下 Avalonia 类型歧义

## V0.5-A 进度（剪辑台云端语音识别 / ASR，已实现并完成真实 API 验收）

实现依据：`limoclip_v0_5_asr.md` §3 + §7 Step 1/2（已按实现修订 P0 偏差：包名、缓存键、`TryAssign`、`CreateDanmakuClient` 可空、ASR 为分区而非 Tab、高能卡片按钮拆分）。

- [x] Step 1：Core `AsrTranscriptSegment` / `AsrTranscriptResult` / `IAsrProvider` + 5 个 `Asr*` AppSettings；`LiveMonitor.Media/Asr/OpenAiCompatibleAsrProvider`（ffmpeg 抽 16kHz MP3 → OpenAI 兼容 `/v1/audio/transcriptions` → `segments`/`text` 双格式解析 → SHA256 本地缓存 → 取消 / Job Object）+ 7 个单测
- [x] Step 2：设置页「语音识别（ASR）」分区（4 预设 + DPAPI 加密 Key + `GET /models` 连通性测试）；剪辑台右侧「🎙 语音识别」面板（识别当前选区 / 取消 / 全文 / 服务商 / 逐句点击 seek / 复制文本）；高能卡片「🎙 转写」按钮与摘要；全局互斥 + 版本号防陈旧回调 + 切换视频清空结果；Desktop +18 测试
- 测试：Data 37 / Platforms 120 / Danmaku 98 / Media 164 / Desktop 94 = **513 全绿**；构建 0 错误
- 产品侧加固（普通用户机器更稳妥）：录制前目录可写性预检（`RecordingDirectoryGuard`，开录/选目录时即校验，失败给出中文原因与建议目录）；`ffmpeg exit -13 / Permission denied` 转为「目标目录没有写权限或被安全软件拦截」提示；「打开目录」失败提示与手动路径；启动器已入库 `scripts/refresh-and-run.ps1`（见「本机沙箱环境」）。新增 17 个 Media 单测
- 实测（真实 siliconflow Key）：55s 录制片段「识别当前选区」约 11s 返回（`siliconflow_cn / FunAudioLLM/SenseVoiceSmall`），缓存写 `%LocalAppData%/LiveMonitor/cache/asr/{sha256}.asr.json`、临时 MP3 已清理；证据 `.cache/v05bug-3-asr-bottom.png`
- 限制：`TestConnectionAsync` 走 `GET {base}/models`，不暴露该接口的服务会显示失败（不影响真实转写）；未接入离线 `Sherpa-ONNX`；V0.5-B（插件热更新 + YouTube）与 C（虎牙/斗鱼/抖音）**暂缓**（恢复入口见 `HANDOFF.md`）

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
  - 测试：`tests/LiveMonitor.Desktop.Tests` 74 个 xUnit 测试通过（全仓库 311 个），覆盖 Auto-Fit 网格/行分布（含 3 路 [1,2]）、30 路、Proportion 克隆/套用、末行追加、比例变化后标自定义比例、房间放入工作区、3 路自动排布、自定义模式满窗格追加、平台徽章配色与在线状态点、弹幕轨道分配（总是优先顶部空闲轨道）、表情文本分段与图片地址解析（`DanmakuEmoteText`：普通文本单段、行内表情三段、连续表情、未闭合括号、大小写匹配等 8 个用例）
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
  - 左侧分组 pills：`全部` + 当前已有 `LiveRoom.GroupTag` 动态生成；添加直播间时可直接输入分组文本。状态栏 `独轮车已拦: N 人` 在 V0.2 接入实时计数（按被拦截的不同用户数统计，V0.1 恒为 0 占位）
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
| `tools/ffmpeg/`（约 100 MB，V0.3 起硬依赖） | 全程录制、视频探测、波形提取、切片导出 | [gyan.dev FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/) |

首次克隆后一条命令补齐（已存在的文件跳过，`-Force` 重新下载）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1             # libmpv-2.dll + yt-dlp.exe + cacert.crt
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1 -WithFfmpeg # 额外下载 ffmpeg
```

脚本会查 GitHub 最新 Release、下载后用 7-Zip 解包（系统没有 7-Zip 时临时下载官方单文件 `7zr.exe`，不安装软件）；`cacert.crt` 为公开 CA 根证书清单，仓库已带，缺失时才从 curl.se 拉取。构建时 `LiveMonitor.Desktop.csproj` 会把 `libmpv-2.dll`、`yt-dlp.exe`、`cacert.crt`、`tools/ffmpeg/ffmpeg.exe` 复制到输出目录；缺文件不会让构建失败，但播放/解析/录制/剪辑导出会在运行时失败。**ffmpeg 自 V0.3 起为录制、V0.4 起为探测/波形/切片导出的硬依赖，首次搭建建议直接带 `-WithFfmpeg` 下载。**

发 GitHub Releases 时，把 `tools/` 里的二进制连同 `bin/Release/net8.0-windows/` 的输出一起打进 zip 包即可，仓库本身不必包含它们。

```powershell
dotnet build LiveMonitor.sln
dotnet run --project src/LiveMonitor.Desktop
dotnet test tests/LiveMonitor.Data.Tests/LiveMonitor.Data.Tests.csproj
dotnet test tests/LiveMonitor.Platforms.Tests/LiveMonitor.Platforms.Tests.csproj
dotnet test tests/LiveMonitor.Danmaku.Tests/LiveMonitor.Danmaku.Tests.csproj
dotnet test tests/LiveMonitor.Media.Tests/LiveMonitor.Media.Tests.csproj
dotnet test tests/LiveMonitor.Desktop.Tests/LiveMonitor.Desktop.Tests.csproj
```

`LiveMonitor.Data` 与测试项目目标为 `net8.0-windows`（DPAPI 仅 Windows）；`Core` / `Platforms` 为 `net8.0`，`Desktop` 为 `net8.0-windows`，`LiveMonitor.Platforms.Tests` 为 `net8.0`。

启动失败时主窗口会弹「启动失败」完整异常窗，同时写 `bin/Debug/net8.0-windows/startup-error.log`；若默认数据库目录 `%LOCALAPPDATA%\LiveMonitor` 不可写，会自动改用程序目录下 `data/livemonitor.db`，并写 `startup-fallback.log`。用 VSCode/`dotnet run` 时若仍失败，请把这两个日志和弹窗文本一起反馈。

`global.json` 固定使用 8.0.1xx 及以上 feature band，避免误用旧 SDK。

## 本机沙箱环境（重要）

当前工作区使用便携 SDK `.dotnet/`（8.0.428），系统没有安装 .NET SDK；NuGet 直连 nuget.org（旧的本地 HTTP 代理方案已废弃，`.dev/nuget-proxy.mjs` 不再需要）。每次 dotnet 命令必须把环境变量指到工作区内，否则本机 `TEMP=E:\code\Env\Temp` 在工作区外，MSBuild 会报 `MSB1025 ... MSBuildTemp is denied`：

```powershell
$root = 'E:\code\liveSurveilance'
$env:DOTNET_ROOT = "$root\.dotnet"
$env:PATH = "$root\.dotnet;$env:PATH"
$env:DOTNET_CLI_HOME = "$root\.dotnet-home"
$env:NUGET_PACKAGES = "$root\.nuget\packages"
$env:APPDATA = "$root\.appdata"
$env:LOCALAPPDATA = "$root\.appdata"
$env:TMP = "$root\.cache\temp"; $env:TEMP = $env:TMP
```

另外本机对「位于工作区 `E:\code\liveSurveilance` 内的可执行文件」有强制写沙箱：工作区内 exe 只能写工作区内目录，连它拉起的子进程 `explorer.exe` 也会以 `0xC0000142` 启动失败。因此**不要直接运行 `src/LiveMonitor.Desktop/bin/.../LiveMonitor.Desktop.exe`**，一律使用仓库内启动器 `scripts/refresh-and-run.ps1`（本机桌面快捷方式「LiveMonitor（工作区外运行）」也指向它）：构建 → 复制到工作区外 `E:\LimoClip-v05-run` → 启动。`-RunDirectory` 可指定输出目录（缺省 `%LOCALAPPDATA%\LimoClip\run`），另有 `-SkipBuild` / `-NoLaunch`。普通机器无便携 SDK 时脚本会自动改用 PATH 里的 `dotnet`。详见 `HANDOFF.md`「本机运行限制：工作区内可执行文件被强制写沙箱」。

`.dev/`、`.cache/`、`.nuget/`、`.dotnet/`、`.appdata/` 均为本机辅助目录，已写入 `.gitignore`。在正常机器上直接 `dotnet restore` / `dotnet run` 即可，不受上述限制。

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
10. V0.4 剪辑台作为顶栏第三视图接入，与录制中心一样以 overlay 方式隐藏侧栏/状态栏、保留顶栏；本地预览用独立 `LocalClipPlayerView`（hr-seek / keep-open），不复用直播播放器配置。
11. 切片导出文件名 `{源文件名}_{mm.ss-mm.ss}` 随选区自动更新、用户手改后锁定；无弹幕版 `-c copy` 存在 1~3s 关键帧对齐误差，UI 已提示。

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
  LiveMonitor.Media/                录制引擎（Recording/，V0.3）、剪辑后端（Editing/，V0.4：jsonl/探测/波形/ASS/硬编/导出）与云端 ASR（Asr/，V0.5-A）
  LiveMonitor.Desktop/              Avalonia UI、Dock、mpv、WebView2、录制中心、剪辑台与语音识别面板（V0.5-A）
tests/
  LiveMonitor.Data.Tests/           Data 层单元测试（xUnit）
  LiveMonitor.Platforms.Tests/      B站 / Twitch 解析与弹幕客户端单元测试（xUnit）
  LiveMonitor.Danmaku.Tests/        弹幕过滤 / 高能检测 / DanmakuManager 单元测试（xUnit，V0.2）
  LiveMonitor.Media.Tests/          录制、剪辑后端与云端 ASR（V0.5-A）单元测试（xUnit，V0.3/V0.4/V0.5-A）
  LiveMonitor.Desktop.Tests/        Dock Auto-Fit / 结构克隆 / 弹幕渲染 / 剪辑台 ViewModel / ASR 设置与剪辑台转写单元测试（xUnit）
tools/                    运行时二进制目录（默认不入库，用 scripts/fetch-tools.ps1 补齐；cacert.crt 例外）
scripts/                  fetch-tools.ps1：一键下载 libmpv / yt-dlp / cacert / ffmpeg
spikes/                   三个技术验证 demo（不参与主解决方案）
```
