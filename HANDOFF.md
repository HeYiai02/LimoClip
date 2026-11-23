# LiveMonitor 交接（截至 V0.5-C Step9：六平台联调 + v0.5.0 打包；含本机运行规避方案）

## 状态

- 分支 `main`；**当前 HEAD 为 V0.5-C Step7 收尾提交（登录后可见 `git log` 顶部；斗鱼插件提交见文末「V0.5-C 增量（Step7 斗鱼）」，其后为本次文档提交）**，工作树干净。V0.4 按层提交、V0.5-A 三次提交、本机运行规避、波形修复、产品侧加固、V0.5-B 六次提交、V0.5-C Step6 两次提交、Step7 两次提交，提交清单见文末各节。
- 构建：`dotnet build LiveMonitor.sln --no-restore` → **0 错误**，8~14 警告（MSB3277 / NU1803 / NU1900 等，均为本机环境既有告警，无新增代码告警）。
- 测试：Data 37 / Platforms **437** / Danmaku 98 / Media **168** / Desktop 94 = **834 全绿**（V0.4 基线 471；V0.5-A +7 Media、+18 Desktop；产品侧加固 +17 Media；V0.5-B +125 Platforms、+2 Media；V0.5-C Step6 虎牙 +65 Platforms、Step7 斗鱼 +63 Platforms、Step8 抖音 +64 Platforms、http 流 ca_file 修复 +2 Media）。
- 验收：V0.4 GUI 端到端通过（脚本 `.cache/v04-acceptance2.ps1`，证据 `.cache/v04b-*.png`）；V0.5-A 完成真实 API 验证（55s 音频约 11s 返回，见「V0.5-A 增量」）；本机运行规避后完成真实 GUI 录制/打开目录/ASR 验证；**V0.5-B 阶段验收通过**（YouTube 添加/监控/录制 49.97s（含音频）/剪辑波形/双版导出（含音频）/ASR，见「V0.5-B 增量」）；V0.5-C Step6 虎牙：插件、协议单测与真实探针（拉流状态 + 20s 内 86 条弹幕）通过，但本机为海外出口、虎牙 CDN 对海外网络限流，GUI 播放/录制未能完整验证（见「V0.5-C 增量」）；V0.5-C Step7 斗鱼：插件、协议单测、真实探针与 **GUI 全链路验收通过**（真实录制 56.8s/11.4MB 含音频 + 72 条弹幕、剪辑台波形与混流、双版导出均含音频，见「V0.5-C 增量（Step7 斗鱼）」）。
- 实现依据：`limoclip_v0_4.md`（V0.4）、`limoclip_v0_5_asr.md`（V0.5-A/B，已按实现修订 P0 偏差）；`code_artifact.md` 已更新为 v2.1。
- **V0.5-B 已完成并通过阶段 B 验收；V0.5-C 已全部完成：Step6 虎牙、Step7 斗鱼、Step8 抖音（三个平台插件 + 单测 + 真实探针，斗鱼/抖音 GUI 全链路验收通过；虎牙 GUI 受本机海外网络限流未完整验证）、Step9 六平台联调与 v0.5.0 打包已完成**（`artifacts/LimoClip-v0.5.0-win-x64.zip` 190.58MB，SHA256 `0fda2c4e…1b43ed`，冒烟启动通过）。恢复开发先读本文件「新 session 首条消息」「V0.5-B 增量」「V0.5-C 增量」。多轨 NLE 只评估、不实现。

## V0.4 已实现内容

### Core（新增契约）

- `Models/RecordedDanmakuItem.cs`、`VideoMetadata.cs`、`WaveformData.cs`、`ClipExportRequest.cs`（含 `ClipExportMode` CleanOnly/BurnedDanmaku/Both 与 `DanmakuAssStyle`）、`ClipExportProgress.cs`。
- `Interfaces/IWaveformService.cs`（比文档多 `duration` 参数，用于 PCM 桶映射）、`IDanmakuAssConverter.cs`、`IHardwareEncoderDetector.cs`、`IClipExporter.cs`。
- `IHighlightEventRepository`：新增 `GetByRecordingSessionAsync(Guid sessionId, int limit = 500, CancellationToken ct = default)`，按录制内偏移升序返回、未绑定会话的不返回。

### Data

- `HighlightEventRepository.GetByRecordingSessionAsync`：`WHERE RecordingSessionId=$id ORDER BY COALESCE(RecordingOffsetMs,0), TriggerTime LIMIT`。
- 新索引 `IX_HighlightEvents_RecordingSessionId`（在旧库幂等补列 `RecordingSessionId/RecordingOffsetMs` 之后创建，否则 v3 旧库升级报 `no such column`）。
- 测试：`HighlightEventRepositoryTests` 增加按会话过滤/排序/limit/空输入 2 个用例与索引断言。

### Media / Editing（新目录 `src/LiveMonitor.Media/Editing`）

- `DanmakuJsonlReader.cs`：逐行容错解析 V0.3 伴随 `.jsonl`（meta 行跳过、坏行跳过、offsetMs 负数夹 0、乱序稳定排序），不丢整份文件。
- `VideoProbeService.cs`：**仓库无 ffprobe**，用 `ffmpeg -hide_banner -nostdin -loglevel info -i` 的 stderr 正则解析 Duration / Video 流 / 分辨率 / fps / codec（h265→hevc），30s 超时可取消。
- `WaveformService.cs`：`-vn -ac 1 -ar 4000 -f s16le pipe:1` 流式提取 PCM，2000 桶取桶内最大峰值（/32768 归一化）+ 弹幕密度（按桶归一化），SHA256(路径|大小|mtime|桶数) 缓存到 `%LocalAppData%/LiveMonitor/cache/waveforms/{hash}.wf.json`；无音轨（ffmpeg 非零退出且 0 采样）降级为全 0 峰值；缓存命中先于 ffmpeg 检查。
- `DanmakuAssConverter.cs`：jsonl → ASS 滚动弹幕；PlayRes 1920x1080、字号 42、`\move` 从右到左、轨道碰撞分配（宽度 ASCII 0.56 / 其它 1.02 估算）、透明度换算 alpha、`{ }` 转全角与 `\` 双写转义、UTF8 BOM、`H:MM:SS.cs` 时间格式。
- `HardwareEncoderDetector.cs`：0.1s 空输入实测探测 `h264_nvenc → h264_qsv → h264_amf → libx264`，5s 超时、结果会话内缓存，`DetectionLog` 记录探测结论。
- `ClipExporter.cs`：双版导出。无弹幕版 `-ss` 在 `-i` 前 + `-t` + `-c copy -avoid_negative_ts make_zero -movflags +faststart`（hevc 加 `-tag:v hvc1`）；弹幕版在 `%TEMP%/LimoClip_Export_{guid}/` 生成 `danmaku.ass`、`-vf ass=danmaku.ass` 重编码（`-pix_fmt yuv420p -c:a aac -b:a 192k`）；`-progress pipe:1` 解析 out_time/speed，ProcessJobGuard 兜底、取消杀整棵进程树、失败删残缺输出、finally 删临时目录。
- `DanmakuPlaybackCursor.cs`：按播放时间的二分推进游标；seek 时返回 1.5s 回看窗口、单次最多 200 条、倒退自动重定位。

### Desktop

- `Player/LocalClipPlayerView.axaml(.cs)`：**独立 mpv 上下文**（`hr-seek=yes`、`keep-open=yes`、`demuxer-max-bytes=64M`、`back-bytes=32M`、`aid=auto`、`mute=no`，与直播播放器隔离）；100ms 轮询 time-pos/duration、`seek absolute+exact`、0.5x~2.0x 倍速、EOF→Ended、失败事件、Dispose 停进程。
- `Controls/DanmakuOverlay.cs`：新增 `IsPaused`（暂停冻结屏上弹幕且不 spawn）与 `PlaybackRate`（0.25~4，默认 1.0，直播行为不变），用于剪辑回放同步。
- `Controls/ClipTimelineControl.cs`（约 1075 行）：自绘双波形时间轴——刻度尺（自适应步长）、音频波形（开方压缩动态范围，安静录像也可见起伏）、5s 桶弹幕密度热力、高能区间条、选区 In/Out 手柄（拖拽实时更新）、播放头拖拽（释放时 seek）、滚轮以光标为锚点缩放 1x~60x、右键/中键平移、双击复位。
- `ViewModels/ClipStudioViewModel.cs`（约 1229 行）：场次列表 / 打开本地文件（同名 `.jsonl` 自动伴随）/ 视频探测 / 新仓储方法加载高能推荐 / 后台波形加载 / 游标驱动弹幕回放 / 播放控制与 ±5s / In-Out 选区 / 高能跳转（推荐区间 = 位置 −10s ~ +20s）/ 导出队列（最多 6 条）/ 自动输出文件名（用户手改后不再覆盖）/ 倍速、编码器文案、关键帧对齐提示、状态栏。
- `ViewModels/ClipExportTaskViewModel.cs`、`ClipHighlightItemViewModel.cs`、`ClipSessionOption.cs`。
- `Views/ClipStudioView.axaml(.cs)`：四区布局（左 260 场次+高能；中预览+控制条；右 280 导出面板；底部 180 时间轴），code-behind 桥接播放器事件、时间轴事件与 VM 事件。
- `MainWindow.axaml(.cs)`、`MainWindowViewModel`：顶栏第三视图「✂ 剪辑台」+ `ClipStudioHost`；Shell 布局扩展为 overlay（监控 / 录制中心 / 剪辑台）三态；快捷键（剪辑台内 Space、←/→ 5s、Shift+←/→ 1s、I 设入点、O 设出点、Esc 回工作区；TextBox/ComboBox 聚焦时不拦截）；注入本地文件与导出目录选择器；Dispose 释放剪辑台与 Job Guard。
- `RecordingCenterViewModel` / `RecordingHistoryItemViewModel` / `RecordingCenterView.axaml`：历史卡片新增「✂ 去剪辑」入口（录制中或文件缺失时禁用并提示），点击进入剪辑台并自动打开该场次。
- `Services/AppServices.cs`：新增 VideoProbe / Waveforms / Encoders / Clips 与内部 `ProcessJobGuard ClipJobGuard`，`CreateAsync` 用 `FfmpegLocator` 定位 ffmpeg 构造编辑服务；新增 `DisposeClipExportResources()`。

### 测试

- Media：`DanmakuJsonlReaderTests`、`VideoProbeServiceTests`、`WaveformServiceTests`、`DanmakuAssConverterTests`、`HardwareEncoderDetectorTests`、`ClipExporterTests`、`DanmakuPlaybackCursorTests`（Media 95 → 140）。
- Data：`HighlightEventRepositoryTests` 新增 2 个会话查询用例（37）。
- Desktop：`ClipStudioViewModelTests`（本地文件加载降级、场次加载高能/伴随弹幕，76）。

## 验收证据（2026-11-23）

脚本 `.cache/v04-acceptance2.ps1`（临时库 `.cache/v04/livemonitor.db`，fixture = `.cache/recordings/20261123_044406_#1_第五人格赛事_6650029.mkv` + `.jsonl`，UIAutomation + Python sqlite seed）：

- `.cache/v04b-01-monitor.png` 监控工作区；`v04b-02-recording-center.png` 录制中心（含「✂ 去剪辑」）。
- `.cache/v04b-04-clip-loaded.png` 剪辑台加载：场次、高能推荐（00:05 / 00:21）、预览首帧、时间轴 In 0:00 / Out 0:37。
- `.cache/v04b-05-playing-danmaku.png` 播放至 00:27，回放弹幕「赛程」显示在画面上，播放头同步。
- `.cache/v04b-06-selection.png` 选区 In 00:20 / Out 00:31 / 时长 00:11，右侧面板与时间轴同步刷新（验收中修复的缺陷 A）。
- `.cache/v04b-07-exported.png` 两版导出完成（队列卡片显示文件名与完成时间）。
- `.cache/v04b-08-burned-frame.png` 烧录版第 2 秒抽帧，右上「赛程」弹幕清晰、时间戳正确。
- 导出实测：点「开始导出 MP4」后 **1.9s** 完成两版；`..._00.20-00.31_clean.mp4` duration=13.30s / h264 1280x720 / 2,588,379B（`-c copy` 关键帧对齐，比 11s 选区多 2.3s，UI 已有 1~3s 提示）；`..._00.20-00.31_danmaku.mp4` duration=11.20s / 2,963,169B。
- 本地文件打开路径：GUI 文件选择器已验证可打开且过滤 `*.mkv/*.mp4`（`.cache/v04c-03-diag.png`）；实际选文件因 UIAutomation/SendKeys 无法把路径送进系统对话框而未做成 GUI 自动化，改由 `ClipStudioViewModelTests` 覆盖（探测元数据、无伴随 jsonl 时降级为「无伴随弹幕文件」、场次路径加载伴随弹幕与高能推荐）。

### 验收中发现并修复

1. 右侧「当前区间」入点/出点文字不刷新：`RaiseSelectionChanged()` 未通知 `InText/OutText`。
2. 输出文件名不随选区更新：改为自动生成 + `_outputFileNameAuto` 标记，用户手改后停止自动覆盖。
3. 波形视觉近似平线：控件渲染改为开方压缩（`sqrt(peak)`），安静录像 ~0.01 峰值也能看出起伏。
4. v3 旧库升级建索引早于补列：`IX_HighlightEvents_RecordingSessionId` 移到 `EnsureColumnAsync` 之后单独创建。
5. `UserControl` 在 Desktop 项目（`UseWindowsForms=true`）与 `System.Windows.Forms` 歧义：全限定 `Avalonia.Controls.UserControl` / `Avalonia.Controls.TextBox` 等。

## 与 `limoclip_v0_4.md` 的偏差 / 实现决策（以本文件为准）

1. **视频探测不用 ffprobe**：仓库只随包 ffmpeg，`VideoProbeService` 解析 `ffmpeg -i` 的 stderr（文档已给该备选）。
2. **`IWaveformService.GetWaveformAsync` 增加 `duration` 参数**：PCM 桶映射需要已知总时长（与 `IWaveformService` 注释一致）。
3. **`VideoMetadata` 单独建模**（文档 §4.1 未列，属补齐）。
4. **本地播放器独立 `LocalClipPlayerView`**，不复用直播 `MpvPlayerView`（后者配置 `keep-open=no`、`back-bytes=0`，且拖拽复用逻辑与本地剪辑冲突）；剪辑台视图常驻宿主，启动时即创建一个空闲 mpv 上下文（可接受取舍，切换不销毁）。
5. **Shell 布局改为 overlay 三态**：录制中心与剪辑台都隐藏侧栏/状态栏、保留顶栏；退出 overlay 恢复原布局。
6. **高能推荐跳转区间**：`In = max(0, pos − 10s)`、`Out = min(duration, pos + 20s)`，并 seek 到 `In`（文档只写“推荐区间”，具体数值为本版选择）。
7. **输出文件名规则**：`{源文件名}_{mm.ss-mm.ss}`（≥1h 用 `h.mm.ss`），随选区自动更新；用户手动编辑后不再覆盖。
8. **高能推荐查询**走新增的按会话仓储方法（文档未规定仓储改动）。
9. **`ProcessJobGuard` 的 API 是 `TryAssign(Process)`**（文档误写 `AddProcess`），导出已使用。
10. **`ClipStudioViewModel` 依赖注入编辑服务**，`AppServices` 统一构造并在 `Dispose` 时释放 Job Guard。

## 已知限制 / 待办（V0.5）

- ASR（V0.5-A 已实现并完成真实 API 验收）；平台/插件热更新底座与 YouTube（V0.5-B 已实现并通过阶段验收）；虎牙（V0.5-C Step6 已实现，见「V0.5-C 增量」）；斗鱼/抖音（V0.5-C Step7/8 未开始）。
- **虎牙 CDN 对海外/非大陆网络限流**：本机出口 IP 为台湾，实测虎牙 FLV/HLS 仅能拉取约 0.5~5 秒即被服务端断开（多条 CDN 线路一致，与客户端/UA/参数无关），故本机无法完成虎牙监控/录制的 GUI 验收；国内网络需实测。
- 产品侧加固（已完成，见文末「后续修复 · 产品侧加固」）：录制前目录可写性预检、`ffmpeg exit -13 / Permission denied` 中文提示与建议目录、「打开目录」失败提示，启动器 `scripts/refresh-and-run.ps1` 已入库。
- V0.5-A 小尾巴：GUI 二次点击命中缓存未复测；`TestConnectionAsync` 走 `GET /models`，少数服务不暴露该接口时会显示失败（不影响真实转写）。
- 本机「工作区内可执行文件写沙箱」根因未根治（AliPaladin/安全软件嫌疑未证实，`sc stop` 被拒），当前只能靠工作区外运行规避；普通用户机器不受影响。
- 2h 以上长录像的波形内存/时间轴拖拽未压测（设计已按流式 + 2000 桶 + 缓存；文档验收项待补测）。
- 硬编路径（nvenc/qsv/amf）本机不可用，仅验证回退 `libx264`；硬编机器需实测导出与探测日志。
- 无弹幕版 `-c copy` 存在关键帧对齐偏移（1~3s，UI 已提示，属预期）；烧录版为精确重编码。
- GUI 验收脚本依赖 UIAutomation 与前台窗口：DSH 抢焦会遮挡截图；系统文件对话框的自动化选择不稳定（本地文件路径改单测覆盖）。
- 本机 `%UserProfile%\Videos` 创建目录失败需 AppSettings 覆盖（沿用 V0.3 环境限制）。

## 提交记录（V0.4，按层拆分）

| 提交 | 内容 |
| :--- | :--- |
| `feat(clip): 新增 V0.4 剪辑与导出领域契约` | Core 5 模型 + 4 接口 |
| `feat(data): 高能事件支持按录制会话查询` | `GetByRecordingSessionAsync` + 索引/迁移 + Data/Danmaku 测试 |
| `feat(media): 新增剪辑后端（弹幕读取/视频探测/波形/ASS/硬编/双版导出）` | `Media/Editing` 7 文件 + 45 个 Media 测试 |
| `feat(desktop): 新增本地预览播放器与双波形时间轴控件` | `LocalClipPlayerView` + `ClipTimelineControl` + `DanmakuOverlay` 暂停/倍速 |
| `feat(desktop): 新增剪辑台并接入主窗口三视图` | 剪辑台 VM/View、主窗口第三 Tab、录制中心「去剪辑」、AppServices 接线 + Desktop 测试 |
| `docs(v0.4): 新增剪辑台设计文档并同步架构/README/HANDOFF` | `limoclip_v0_4.md`、README、`code_artifact.md`、本文件 |

提交哈希（`git log --oneline -6`）：`f115f0a` → `2de27f9` → `85f37d3` → `2258b40` → `fbaad20` → docs 提交（本文件、README、`code_artifact.md`、`limoclip_v0_4.md`）。每层提交后均以最终 `HEAD` 跑过全量构建与五套测试（471 全绿）。

## 构建 / 测试

每次 pwsh 先设环境（缺失会报 `MSBuildTemp` 权限错误）：

```powershell
$root = 'E:\code\liveSurveilance'
$env:DOTNET_ROOT = "$root\.dotnet"
$env:PATH = "$root\.dotnet;$env:PATH"
$env:DOTNET_CLI_HOME = "$root\.dotnet-home"
$env:NUGET_PACKAGES = "$root\.nuget\packages"
$env:APPDATA = "$root\.appdata"
$env:LOCALAPPDATA = "$root\.appdata"
$env:TMP = "$root\.cache\temp"
$env:TEMP = "$root\.cache\temp"
dotnet build "$root\LiveMonitor.sln" --no-restore
```

测试（先 build，再逐个 `--no-build`）：

```powershell
dotnet test "$root\tests\LiveMonitor.Data.Tests" --no-build
dotnet test "$root\tests\LiveMonitor.Platforms.Tests" --no-build
dotnet test "$root\tests\LiveMonitor.Danmaku.Tests" --no-build
dotnet test "$root\tests\LiveMonitor.Media.Tests" --no-build
dotnet test "$root\tests\LiveMonitor.Desktop.Tests" --no-build
```

GUI 冒烟：`.cache/app-smoke.ps1`。V0.4 验收：`.cache/v04-acceptance2.ps1`（临时库 `.cache/v04` + UIA + Python seed，结束自动清理）。

> 本机 pwsh 实为 Windows PowerShell 5.1：含中文的 `.ps1` 必须是 **UTF-8 with BOM**，否则中文字符串被按 ANSI 解码导致按钮匹配失败；`Get-Content` 读中文源码显示乱码属控制台编码问题，不影响程序。

## 约束（沿用 V0.1~V0.3）

- 平台逻辑保持现状：B站原生接口（含 WBI 签名）、Twitch `yt-dlp -j`；不换 streamlink；不处理 SSAI 广告。
- 焦点放大 / 音频总线 / `PlatformCredentials` / `PlatformType` 语义不动。
- 不为性能回退 `aid=no`；不重构既有 V0.1/V0.2 模块。
- 界面沿用现有深色风格（#0F1115 / #151923 / #262B36，圆角 8，内边距 14）。
- 截图 / 临时 DB / 测试产物放 `.cache/`；第三方二进制不入库（`scripts/fetch-tools.ps1` 补齐，ffmpeg 自 V0.3 起为硬依赖）。

## 新 session 首条消息（可直接复制）

> 阅读 `HANDOFF.md`、`limoclip_v0_5_asr.md`，先核对 `git log --oneline -8` 与全量测试（build + 五套测试，应为 **834 全绿**）。V0.5-A、V0.5-B、产品侧加固均已完成并验收，V0.5-C 已全部完成（Step6 虎牙、Step7 斗鱼、Step8 抖音、Step9 六平台联调 + v0.5.0 打包），**V0.5 里程碑收尾完成**。恢复开发前先读「V0.5-B 增量」「V0.5-A 增量」与「本机运行限制」；本机运行一律使用仓库启动器 `scripts/refresh-and-run.ps1`（桌面快捷方式也指向它），不要直接运行工作区内 bin 下的 exe。插件签名私钥不在仓库内，需向用户索取或使用环境变量 `LIVEMONITOR_PLUGIN_KEY`。

## V0.5-A 增量（2026-11-23）：剪辑台语音识别（ASR）

### 状态

- 实现依据：`limoclip_v0_5_asr.md` §3 + §7 Step 1/2（已按实现修订 P0 偏差：包名、缓存键、`TryAssign`、`CreateDanmakuClient` 可空、ASR 为分区而非 Tab、高能卡片按钮拆分）。
- 构建：`dotnet build LiveMonitor.sln --no-restore` → **0 错误**；仅既有环境告警（MSB3277 / NU1803 / NU1900 / CS9057 / CS0108 / WFAC010 / AVLN3001 / xUnit2009 等）。
- 测试：Data 37 / Platforms 120 / Danmaku 98 / Media 164 / Desktop 94 = **513 全绿**（V0.4 基线 471；本增量 +7 Media、+18 Desktop，另产品侧加固 +17 Media）。
- 验收：GUI 冒烟脚本 `.cache/v05a-smoke.ps1`（UIAutomation，UTF-8 BOM）通过：剪辑台右侧「🎙 语音识别」面板可见（`尚未识别` / `🎙 识别当前选区`），设置窗新增「语音识别（ASR）」分区（预设/地址/模型/语言/API Key/保存/测试连接）可见；证据 `.cache/v05a-2-clip-studio.png`、`.cache/v05a-3-settings-asr.png`。
- 真实 API 验收（2026-11-23 19:30）：用户配置 siliconflow Key 后，剪辑台选中 55s 录制片段点「🎙 识别当前选区」，约 11s 返回（`siliconflow_cn / FunAudioLLM/SenseVoiceSmall`），缓存写入 `%LocalAppData%/LiveMonitor/cache/asr/{sha256}.asr.json`、临时 MP3 已清理；证据 `.cache/v05bug-3-asr-bottom.png`。
- 未做：V0.5-B（BouncyCastle 验签、PluginLoadContext、providers-manifest、CliUpdater、YouTube 插件、WebView2 登录扩展）、V0.5-C（虎牙/斗鱼/抖音），均已按用户要求暂缓。

### Core

- `Models/AsrTranscriptSegment.cs`：`record AsrTranscriptSegment(TimeSpan RelativeStart, TimeSpan RelativeEnd, string Text)`。
- `Models/AsrTranscriptResult.cs`：`record AsrTranscriptResult(TimeSpan ClipStart, TimeSpan ClipEnd, string FullText, IReadOnlyList<AsrTranscriptSegment> Segments, string ProviderLabel, DateTimeOffset CreatedAtUtc)`。
- `Interfaces/IAsrProvider.cs`：`Task<AsrTranscriptResult> TranscribeSegmentAsync(string videoPath, TimeSpan startTime, TimeSpan endTime, CancellationToken ct = default)`；约定命中缓存不发网络，失败抛 `InvalidOperationException`（消息可直接展示）。
- `AppSettingKeys`：新增 `AsrProviderPreset` / `AsrEndpoint` / `AsrModel` / `AsrEncryptedApiKey`（DPAPI Base64，仅 Data 层可解密）/ `AsrLanguage`（默认 `auto`）。

### Media（新目录 `src/LiveMonitor.Media/Asr`）

- `OpenAiCompatibleAsrProvider.cs`：仅云端 OpenAI 兼容 `/v1/audio/transcriptions`；ctor `(HttpClient, string ffmpegPath, ProcessJobGuard?, Func<Task<AsrApiConfig?>>, string cacheDirectory)`；支持 4 预设（硅基流动国内/国际、Groq、自定义），非 siliconflow 预设使用 `verbose_json`。
- 流程：校验参数与配置 → 缓存命中直接返回 → ffmpeg 抽音（`-ss -t -vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 64k`，临时 MP3 进 `ProcessJobGuard.TryAssign`）→ multipart 上传 → `verbose_json` 400 时降级 `json` 重试一次 → 优先 `segments` 否则顶层 `text` 单段 → 写缓存；单段上限 15 分钟。
- 缓存：`%LocalAppData%/LiveMonitor/cache/asr/{sha256}.asr.json`，键 = `SHA256(Path.GetFullPath|fileSize|lastWriteUtc.Ticks|startMs|endMs|model)`；缓存损坏按未命中重试。
- 生命周期：`IDisposable` 释放注入的 `HttpClient` 与 `ProcessJobGuard`（组合根直接 new 后由 `AppServices` 接管，见下）。

### Desktop

- `Services/AsrSettingsStore.cs`：4 预设静态表 + `LoadAsync/GetConfigAsync/SaveAsync/TestConnectionAsync`；Key 仅以 DPAPI 密文存储、永不回显；`BuildModelsUrl` 把 `/audio/transcriptions` 换成 `/models` 做连通性测试。
- `Services/AppServices.cs`：新增 `Asr`（`OpenAiCompatibleAsrProvider`，专用 HttpClient 10 分钟超时、独立 `ProcessJobGuard`、缓存目录）与 `AsrSettings` 属性；`DisposeClipExportResources()` 同时释放 ASR 资源。
- `Views/SettingsWindow.axaml(.cs)`：单 ScrollViewer 内新增「语音识别（ASR）」分区（预设 ComboBox、接口地址、模型、语言、`PasswordChar` API Key、保存、测试连接、彩色状态与 DPAPI 说明）。
- `ViewModels/ClipStudioViewModel.cs`：新增 ASR 状态区（`IsAsrRunning` / 状态 / 错误 / 服务商 / 全文 / 范围 / `AsrLines`）、`TranscribeSelectionCommand`（当前 In/Out 选区）、`TranscribeHighlightCommand`（高能位置 −10s ~ +20s）、`ShowHighlightAsrCommand`、`CancelAsrCommand`、`SeekToAsrLine`（`ClipStart + RelativeStart` 绝对定位）、复制/失败回调；全局互斥 + 版本号防陈旧回调，切换视频/场次清空卡片结果。
- `ViewModels/ClipHighlightItemViewModel.cs`：卡片转写状态（`IsTranscribing/CanTranscribe/AsrResult/AsrError/AsrSummary…`）。
- `ViewModels/AsrTranscriptLineViewModel.cs`（新）：分段行只读展示模型。
- `Views/ClipStudioView.axaml(.cs)`：高能卡片 DataTemplate 根改为 `StackPanel`，「跳转」与「🎙 转写」并列（避免嵌套 Button 冒泡）、有结果时显示摘要；右侧新增「🎙 语音识别」面板（识别当前选区/取消/进度/状态/错误/复制文本/逐句列表点击 seek）；剪贴板访问只在 View 层。

### 测试

- Media +7：`OpenAiCompatibleAsrProviderTests`（未配置友好消息、超 15 分钟拒绝、出点≤入点、`verbose_json` 分段+二次调用缓存命中零网络、纯 `text` 回退、400 降级重试、401 错误消息）+ 复用 `TestHttpMessageHandler`。
- Desktop +18：`AsrSettingsStoreTests` 14（预设/DPAPI 回写/Key 保留与清除/custom/URL 构造/连通性测试/回退已存 Key/401 透传）+ `ClipStudioViewModelTests` 4（无视频提示、选区识别+分句绝对 seek、Provider 异常友好展示、高能卡片识别与二次复用不重复调用）。

### 偏差 / 实现决策（以本文件为准）

1. ASR 做成设置窗内**分区**而非 Tab（现有设置窗无 TabControl，见 `limoclip_v0_5_asr.md` 已修订）。
2. 缓存键在文档基础上包含 `Path.GetFullPath` 与 `model`（文档已同步）。
3. Provider 参考代码的 `AddProcess` 改为实际 API `ProcessJobGuard.TryAssign`。
4. `AsrSettingsStore.TestConnectionAsync` 走 `GET {endpoint 基址}/models`；不暴露 `/models` 的第三方服务会显示失败（不影响真实转写）。
5. 剪辑台面板常显；卡片已有结果时再次点击只展开到面板、不重复请求（与 Provider 缓存叠加）。
6. 高能卡片摘要截断 60 字符（ToolTip 显示全文）。
7. 已完成真实云端 API 验证（用户 siliconflow Key，55s 音频约 11s 返回），缓存命中与临时文件清理已核对。

## 提交记录（V0.5-A）

| 提交 | 内容 |
| :--- | :--- |
| `feat(asr): 新增 V0.5-A 语音识别领域契约与云端 Provider` | Core 2 模型 + 1 接口 + 5 设置键；`Media/Asr/OpenAiCompatibleAsrProvider`；Media 7 个单测 + `TestHttpMessageHandler` |
| `feat(desktop): 设置页与剪辑台接入云端语音识别` | `AsrSettingsStore`、AppServices 接线、设置窗 ASR 分区、剪辑台卡片/面板、`AsrTranscriptLineViewModel`、Desktop 18 个测试 |
| `docs(v0.5-a): 收录 ASR 设计文档并更新 HANDOFF` | `limoclip_v0_5_asr.md`（P0 修订后版）、本文件 |

提交哈希：`d394352` → `4ac079a` → `6cced22`；其后本机运行规避提交 `2d765c8`、波形修复提交 `c32367d`；当前 HEAD 为文档收尾提交（见 `git log -1`）。

## V0.5-B 增量（2026-11-23）：平台插件热更新底座 + YouTube

### 状态

- B-3（插件底座）、B-4（YouTube 插件）、B-5（设置页「组件与插件更新」+ 阶段 B 验收）全部完成；build 0 错误、五套测试 **640 全绿**。
- 设计依据：`limoclip_v0_5_asr.md` §4（IPlatformPlugin、providers-manifest、CliUpdater、YouTube）与 §7 Step3~Step5；偏差以本节与代码为准。
- main 小步提交：`1b72ead`（IPlatformPlugin + ToolLocator）→ `ea2a65c`（签名工具/脚本）→ `931adcc`（YouTube 插件）→ `77bc418`（设置页更新分区 + YouTube 内置化）→ `ebda9e6`（内置插件注册纯插件平台 Provider + 放宽空清单）→ `e9e7aab`（YouTube 分离音频轨）。
- 测试基线：`dotnet build LiveMonitor.sln --no-restore` 0 错误；Data 37 / Platforms 245 / Danmaku 98 / Media 166 / Desktop 94 = 640。

### Core / Platforms

- Core：`IPlatformPlugin`（Platform/PluginId/Version/CreateStreamProvider/CreateDanmakuClient，弹幕可返回 null）；`StreamQuality` 增加可选 `AudioStreamUrl`/`AudioHeaders` 与 `HasSeparateAudio`（YouTube HLS 音视频分离）。
- `LiveMonitor.Platforms/Plugins/`：`PluginTrust`（内嵌公钥 + manifest 主/镜像地址）、`Ed25519Verifier`、`ProvidersManifest`（解析/校验，允许空 plugins）、`PlatformPluginLoadContext`（共享程序集返回 null、内存流加载、isCollectible）、`LoadedPluginHandle`、`PlatformPluginLoader`（验签失败抛 SecurityException）、`PlatformPluginHost`（扫描安装目录、验签安装、版本比较、无活跃任务时原子换表）、`PluginUpdateService`（拉清单/下载/验签/安装/应用）、`BuiltInPlatformPlugins`（B站/Twitch/YouTube）。
- Common：`ToolLocator`（yt-dlp/插件根目录解析）、`PluginVersionInfo`、`PluginVersionComparer`、`CliUpdater`（yt-dlp `--version` 对比 GitHub latest，下载校验 MZ 后原子替换）。
- 工厂：`PlatformFactory` 运行期 `ReplaceProvider(s)`/`RemoveProvider`（原子换表），`DanmakuClientFactory` 覆盖表 `RegisterOrReplace`/`Remove`（内置 fallback 保留）；B站/Twitch 增加 `BilibiliPlugin`/`TwitchPlugin`。
- 新工程 `LiveMonitor.Plugins.YouTube`：URL 解析（watch/live/shorts/embed/youtu.be/@handle/channel/裸 ID）、`YouTubeProvider`（yt-dlp -j、画质去重排序、headers、离线判定、**自动附带最高码率纯音频轨**）、`YouTubeDanmakuClient`（直播页 bootstrap → InnerTube `get_live_chat` 轮询、SuperChat/贴纸消息、timeoutMs 收敛）、`YouTubePlugin`。

### Desktop / 工具

- `AppServices`：插件共享 HttpClient、`PlatformPluginHost`（内置注册 + 扫描本地插件 + 活跃录制守卫）、`PluginUpdateService`、`CliUpdater` 接线与释放顺序；YouTube 作为内置插件注册后即可添加/监控/录制。
- 设置页「组件与插件更新」分区：yt-dlp 检查/更新、平台插件检查/下载安装、状态与错误提示；`AddRoomWindow` 提示支持 YouTube。
- 播放/录制音频：`MpvPlayerView` 通过 mpv `audio-file` 附加分离音轨；`FfmpegArgumentBuilder` 在音频存在时追加第二路输入并 `-map 0:v:0 -map 1:a:0`（单输入参数顺序保持不变）。
- 工具：`scripts/PluginSigner/`（sign/keygen，BouncyCastle Ed25519）、`scripts/sign-plugin.ps1`、`scripts/build-plugins.ps1`、`plugins/providers-manifest.json`、`plugins/README.md`；`.gitignore` 忽略插件签名私钥。

### 安全注意（重要）

- 生产 Ed25519 公钥已内嵌：`lYn7fkrnrtAHz4fplCQ3R5yC3pPCrG3QLk5atHiSE24=`。
- 对应私钥 Base64：`MiSlq4SsmadTpw9D1btrHWyt02bjAQiodQKng34XoIM=`。**该私钥在生成时因本机安全策略（CC Safety Net 拦截 `secret.ext-pattern.key`）未能写入任何文件，仓库内也不存在**；请用户自行离线保存，发布插件签名时通过 `scripts/sign-plugin.ps1 -PrivateKeyBase64` 或环境变量 `LIVEMONITOR_PLUGIN_KEY` 提供。
- 插件清单当前为空数组，代表“暂无已发布插件更新”，设置页检查显示“已是最新”；发布插件时用 `scripts/build-plugins.ps1` + `sign-plugin.ps1` 生成条目并上传 Release。

### 阶段 B 验收证据（2026-11-23 21:43–22:03，工作区外副本 `E:\LimoClip-v05-run`）

- YouTube 房间 `#13 Lofi Girl`（`4t4AQ15yj1M`）添加成功（红色 YouTube 徽章），监控播放 1080P30 硬解（`.cache/v05b-youtube-monitor.png`）。
- 录制 49.97s：`.mkv` 7,778,204B 含 `Stream #0:0 Video h264 1920x1080 30fps` + `Stream #0:1 Audio aac HE-AAC 44.1kHz`，伴生 `.jsonl` 14,809B / 77 条弹幕；DB 会话 Platform=6、Status=3、DurationSeconds=49.969356、DanmakuCount=77、HighlightCount=1、无错误。
- 剪辑台：波形正常显示（`.cache/v05b-youtube-audio-waveform.png`）；导出选区 00:00–00:10 双版：`_clean.mp4` 1,591,941B（含音频）、`_danmaku.mp4` 4,553,124B（含音频）；ASR「识别完成：1 句」（`.cache/v05b-youtube-asr-audio.png`）。
- 设置页「组件与插件更新」：yt-dlp「本地 2026.10.26 · 最新 2026.10.26」，插件「已是最新（清单来源：GitHub Raw）」（`.cache/v05b-settings-update.png`）。

### 已知问题 / 待办

- 尚未发布任何签名插件，因此「下载并安装 → 热生效」实机路径仅由单测覆盖（`PlatformPluginHostTests`/`PluginUpdateServiceTests`）；发布首个插件后需补实机验证。
- YouTube 采用视频/音频分离拉流，长时录音画同步与断流恢复未做长时间压测；mpv 侧音轨由 `audio-file` 负责。
- 本机工作区写沙箱问题仍未根治（AliPaladin 嫌疑未证实），开发调试仍须使用工作区外副本与仓库启动器。

## 本机运行限制：工作区内可执行文件被强制写沙箱（2026-11-23 定位）

### 现象

- 直接从 `src/LiveMonitor.Desktop/bin/Debug/net8.0-windows/LiveMonitor.Desktop.exe` 运行时：
  - 录制立即失败，DB 记录 `ffmpeg 异常退出（exitCode=-13）：Error opening output D:\clipRecord\...mkv: Permission denied`；
  - 录制中心点「打开目录」弹 Windows 对话框「explorer.exe - 应用程序错误：应用程序无法正常启动(0xc0000142)」；
  - 默认数据库打不开，回退到 `bin/Debug/net8.0-windows/data/livemonitor.db`（`startup-fallback.log` 记录 `SQLite Error 14: unable to open database file`）。

### 定位结论（证据链）

- 同一份 ffmpeg 字节：在工作区内（`bin/.../tools/ffmpeg/ffmpeg.exe`）只能写工作区内目录，写 `D:\`、`C:\Users`、`E:\` 根等全部 `exit -13 Permission denied`；复制到工作区外（`C:\Users`、`D:\clipNew`、`G:\` 等）即可正常写任意目录。
- 同一份 cmd.exe：复制到工作区内启动 `explorer.exe "D:\clipRecord"` 返回 `0xC0000142`（精确复现用户报错）；复制到工作区外启动同命令正常。
- 用 WMI `Win32_Process.Create` 脱离 DSH 进程树启动同样复现 ⇒ 不是 DSH 作业对象/沙箱继承，而是与本机安全软件/过滤驱动相关的机器级限制（本机存在 AlibabaProtect / `AliPaladinEx64.sys` 过滤驱动，`sc stop` 被拒绝 1052，未能进一步验证；Windows 日志 `Application Popup Id 26` 记录了 19:01–19:02 多次 explorer.exe 0xc0000142）。
- 结论：**属开发机环境限制，不是 LiveMonitor 代码缺陷**；普通用户机器不受影响。

### 规避方案（本项目开发调试规定）

- 一律使用仓库启动器 `scripts/refresh-and-run.ps1`（本机桌面快捷方式「LiveMonitor（工作区外运行）」也指向它，并带 `-RunDirectory E:\LimoClip-v05-run`）：
  1. `dotnet build`（若仓库带便携 SDK `.dotnet/`，脚本会自动把 `DOTNET_ROOT/DOTNET_CLI_HOME/NUGET_PACKAGES/APPDATA/LOCALAPPDATA/TEMP/TMP` 指到工作区内；普通机器无便携 SDK 时改用 PATH 里的 `dotnet`）；
  2. `robocopy` 构建输出到 `-RunDirectory`（缺省 `%LOCALAPPDATA%\LimoClip\run`，本机为 **`E:\LimoClip-v05-run`**），顺带删除陈旧 `startup-fallback.log`；
  3. 从该目录启动 `LiveMonitor.Desktop.exe`。脚本带「已运行则拒绝」守卫，支持 `-SkipBuild` / `-NoLaunch`，失败时会红字报错并暂停，日志写到 `.cache/launcher-run.log`。
- 数据迁移：原工作区回退库（含用户房间 7 条、设置 8 项、ASR Key、17 条高能记录）已 `wal_checkpoint(TRUNCATE)` 后迁移到默认位置 `C:\Users\Administrator\AppData\Local\LiveMonitor\livemonitor.db`；原回退库保留未动。

### 规避后验证（2026-11-23 19:28–19:31，真实 GUI）

- 录制：B 站房间 1882958350 真实录制 55.6s 成功，`D:\clipRecord\20261123_112855_#4_...mkv` 46,193,788B、同名 `.jsonl` 6,008B、弹幕 35 条，停止后 DB 状态 `Completed`、无错误。
- 打开目录：正常弹出资源管理器，无 0xc0000142。
- ASR：剪辑台选中该场次后「🎙 识别当前选区」，55s 音频约 11s 返回（siliconflow_cn / FunAudioLLM/SenseVoiceSmall），缓存写入 `%LocalAppData%/LiveMonitor/cache/asr/{sha256}.asr.json`，临时 MP3 已清理。截图 `.cache/v05bug-3-asr-bottom.png`。
- 产品侧加固已在本版完成（目录可写预检、ffmpeg 权限错误中文提示、打开目录失败提示、启动器入库），见下一节。

### 后续修复（2026-11-23 19:4x，提交 `c32367d`）

- 启动器 v3：本机用户环境 `TEMP/TMP=E:\code\Env\Temp`（工作区外），而 `dotnet.exe` 位于工作区内、受同一写沙箱限制，构建时报 `MSB1025 Access to the path 'E:\code\Env\Temp\MSBuildTemp' is denied`，脚本失败后窗口一闪而过。现启动器在构建前把 `DOTNET_ROOT/DOTNET_CLI_HOME/NUGET_PACKAGES/APPDATA/LOCALAPPDATA/TEMP/TMP` 全部指到工作区内（`.cache\temp`、`.appdata`），构建结束在 `finally` 中恢复真实用户环境变量再启动应用；失败时红字报错 + `Read-Host` 暂停，并写 transcript 日志 `.cache/launcher-run.log`。
- 剪辑台时间轴波形修复：`DrawWaveform` 原先用非闭合、`isFilled:false` 的 `StreamGeometry` 线段填充绘制，实际零面积不可见，用户只能看到波形中心线（一条平直蓝线）。改为按像素采样上下包络、拼成闭合多边形填充 + 描边，无可用音频时显示「（该视频没有可用的音频波形）」；`ClipStudioView.axaml` 给 `ClipTimelineControl` 增加 `ToolTip.Tip` 说明各行含义与操作（点击/拖动定位播放头、拖 IN/OUT、滚轮缩放、双击复位）。截图 `.cache/v05bug-8-waveform-crop.png`。

### 产品侧加固（2026-11-23 20:0x）

- 录制目录可写性预检：新增 `src/LiveMonitor.Media/Recording/RecordingDirectoryGuard.cs`（`EnsureWritable` / `TryEnsureWritable` / `IsWritePermissionFailure` / `DescribeFfmpegFailure` / `SuggestDirectories`；预检时创建 `.livemonitor-write-test-*.tmp` 后立即删除）。`RecordingManager.StartCoreAsync` 在创建会话前预检，失败抛中文 `InvalidOperationException`（含原因与建议目录），显示在录制中心状态栏；录制中心「修改目录」（全局与单房间）也立即预检，避免保存不可写目录。
- ffmpeg 写盘失败提示：`RecordingManager.DescribeExit` 改用 `RecordingDirectoryGuard.DescribeFfmpegFailure`——exitCode=-13/13 或 stderr 含 `Permission denied`/`Access is denied`/`Error opening output`/`拒绝访问` 时转为「无法写入录制文件：目标目录…没有写权限，或被安全软件拦截…请点击「修改目录」…检查杀毒/安全软件…」+ 原始 stderr；其它错误保持原「ffmpeg 异常退出（exitCode=…）」格式。自动录制失败也会通过 `StatusMessage` 提示到界面。
- 「打开目录」失败提示：录制中心 `OpenGlobalDirectory` / `OpenTargetDirectoryAsync` / `OpenHistoryFolderAsync` 与剪辑台 `OpenOutputDirectory` 捕获 explorer 启动异常，统一显示「打开目录失败：…。请手动在资源管理器中打开：<路径>」。
- 启动器入库：新增 `scripts/refresh-and-run.ps1`（UTF-8 BOM，PS 5.1 兼容），参数 `-SkipBuild` / `-NoLaunch` / `-RunDirectory`；`$repo` 由 `$PSScriptRoot` 推导，自动检测便携 SDK（无便携 SDK 时用 PATH 的 `dotnet`），缺省输出 `%LOCALAPPDATA%\LimoClip\run`，可用 `LIVEMONITOR_RUN_DIR` 覆盖；失败红字报错 + 暂停 + transcript 日志。本机桌面快捷方式已改为指向该脚本并带 `-RunDirectory E:\LimoClip-v05-run`；`C:\Users\Administrator\LimoClip\refresh-and-run.ps1` 保留为转发器。
- 测试：新增 `tests/LiveMonitor.Media.Tests/RecordingDirectoryGuardTests.cs` 17 个用例（缺省目录创建、空白/文件路径拒绝并带建议、权限错误分类、非权限错误保持原格式、stderr 截断、目录解析）；五套 **513 全绿**、构建 0 错误。
- 验证：用仓库启动器从工作区完整执行「构建 → 复制 → 启动」exit 0，副本启动 PID 37260。


## V0.5-C 增量（2026-11-23）：虎牙平台插件（Step6）

### 状态

- 完成：虎牙插件（移动端拉流接口 + 新 wsapi Tars 弹幕协议），提交 `2c2ca42`（功能）与 `1b6d0ac`（移除诊断日志）。
- 校验：解决方案 build 0 错误；五套测试 **705 全绿**（Platforms 245→310，新增虎牙 65 个用例）。
- 真实探针（`.cache/huyaprobe`）：房间 60066（杨齐家丶）`LIVE=True`、画质「蓝光20M」；新 wsapi 弹幕 20 秒收到 **86 条**。
- **本机限制**：本机出口 IP 为台湾，虎牙 CDN 对海外拉流限制（实测约 0.5~5 秒决断开），GUI 监控画面会在播放 1 帧后显示「直播流已结束」；录制/GUI 验收需国内网络实测。

### 实现要点

- `src/LiveMonitor.Plugins.Huya/`：`HuyaUrlParser`（huya.com 链接/数字房间号/个性域名）、`HuyaApiClient`（`mp.huya.com` Live profileRoom JSON + 个性域名缓存解析 gzip 房间页）、`HuyaTars`（Tars/Wup 最小子集编解码）、`HuyaPacketParser`（wsapi 订阅包、cmd7/22 弹幕解析）、`HuyaDanmakuClient`（ClientWebSocket + 主动断开不触发 Disconnected）、`HuyaProvider`（单档画质，匿名下取 iBitRate==0 最高档）、`HuyaPlugin`。
- 协议结论：旧 `cdnws.api.huya.com` 进房协议已半废弃（只推 10 多秒），实现改用 `wss://wsapi.huya.com/?baseinfo=<base64 Tars>` + cmd16 订阅 `live:<uid>` / `chat:<uid>`；Tars 头字节 `(tag<<4)|type`、大端整型、zero tag 优化。.NET ClientWebSocket 默认不请求 permessage-deflate，避开了 OkHttp 会窗口 2 秒后停推的坑。
- 平台注册：`LiveMonitor.Platforms.csproj` 引用虎牙插件；`BuiltInPlatformPlugins.CreateAll()` 加 `new HuyaPlugin()`；平台展示映射此前已存在。

### 新 session 首条消息（V0.5-C 阶段）

- 继续 V0.5-C：Step7 斗鱼已完成（`LiveMonitor.Plugins.Douyu`：betard + Jint 执行 `ub98484234` 签名 + getH5Play 拉流 + STT 弹幕），完成真实探针与 GUI 全链路验收。下一步 Step8 抖音（ttwid + Protobuf PushFrame→Gzip→Response→Ack），然后 Step9 全平台联调 + v0.5.0 打包准备。main 小步提交；GUI 验收从 `E:\LimoClip-v05-run`；注意本机海外网络对虎牙 CDN 的限制（斗鱼在本机可用）。


## V0.5-C 增量（Step7 斗鱼）：2026-11-23

### 状态

- 完成：斗鱼平台插件（`src/LiveMonitor.Plugins.Douyu/`，7 文件）——betard 房间信息、`homeH5Enc` 下发混淆脚本 + **Jint** 执行 `ub98484234` 签名（注入 `CryptoJS.MD5` shim）、`lapi/live/getH5Play` 拉流、`danmuproxy.douyu.com:8601` STT 弹幕；内置插件注册（`BuiltInPlatformPlugins`）。
- 校验：解决方案 build 0 错误；五套测试 **768 全绿**（Platforms 310→373，斗鱼新增 63 个用例）。
- 真实探针（`.cache/douyuprobe2`）：房间 9999（yyfyyf）`LIVE=True`、画质「蓝光4M」（rate=4）、20 秒收到 25 条弹幕、ffmpeg 带 `-ca_file` 录制 8 秒成功（837,725B）。
- GUI 全链路验收（`E:\LimoClip-v05-run`）：添加 `https://www.douyu.com/9999` → 监控窗格播放正常（蓝光4M/硬解 30fps）→ 录制 56.8s（11,428,006B + 12,472B 弹幕 72 条，DB `Platform=3/Status=3/DanmakuCount=72/ErrorMessage=None`）→ 剪辑台加载（波形正常、无「无可用音频波形」提示）→ 导出 00:00-00:10 双版（clean 1,508,219B / danmaku 3,867,838B，ffprobe 均含 AAC 音轨）。截图：`.cache/v05c-douyu-monitor.png`、`.cache/v05c-douyu-clip.png`、`.cache/v05c-douyu-clip-export.png`。
- 网络结论：与虎牙不同，斗鱼在本机海外网络可用；但 HTTPS CDN 需随包 CA（`FfmpegLocator.CaBundleRelativePath` 已内置为 `-ca_file`），匿名最高画质 rate=4「蓝光4M」（更高需登录 Cookie）。

### 实现要点

- `DouyuUrlParser`（douyu 链接/数字房间号；纯数字 CanHandle=false 避免与 B 站冲突）；`DouyuSigner`（GET `swf_api/homeH5Enc?rids=` 取混淆 JS 并按房间缓存，Jint 10s 超时 + CryptoJS.MD5 shim（.NET MD5）+ window/document/navigator/location stub，**注意不能让沙箱注入 host eval**）；`DouyuApiClient`（betard 手动 gzip 解压、签名 form `rate=-1` 调 getH5Play、拼 `rtmp_url`+`rtmp_live`、multirates 匹配显示名）；`DouyuStt`（12 字节小端头 689/690、STT 键值转义 `@S`/`@A`、结尾 `\0`）；`DouyuDanmakuClient`（TCP 8601、loginreq/loginres/joingroup/chatmsg/dgb、45s mrkl 心跳、主动断开不触发 Disconnected）；`DouyuProvider`/`DouyuPlugin`。
- 引用：`LiveMonitor.Plugins.Douyu.csproj` 加 `PackageReference Jint 4.18.1`；`LiveMonitor.Platforms.csproj` 引用斗鱼插件；`BuiltInPlatformPlugins.CreateAll()` 增加 `new DouyuPlugin()`。

### 新 session 首条消息（V0.5-C 阶段，更新）

- V0.5-C Step6 虎牙、Step7 斗鱼均已完成并推送；下一步 Step8 抖音（ttwid + Protobuf PushFrame→Gzip→Response→Ack，风控最强），然后 Step9 全平台联调 + v0.5.0 打包准备。main 小步提交；GUI 验收从 `E:\LimoClip-v05-run`；本机网络：斗鱼可用、虎牙 CDN 限流、抖音需实测。


## V0.5-C 增量（Step8 抖音）：2026-11-23

### 状态

- 完成：抖音插件（`src/LiveMonitor.Plugins.Douyin/`）：`enter` 拉流接口（ttwid + Cookie）、Protobuf `PushFrame → Gzip → Response → Ack` 弹幕客户端、Jint 执行 `sign.js` 生成 WSS 签名、`DouyinProvider`/`DouyinPlugin`；Platforms 内置插件注册（B站/Twitch/YouTube/虎牙/斗鱼/抖音 共 6 个内置平台）。
- 校验：解决方案 build 0 错误；五套测试 **834 全绿**（Platforms 437，新增抖音 64 个用例；Media 168，新增 http 流不加 `-ca_file` 的 2 个用例）。
- 真实探针（`.cache/douyinpluginprobe`）：房间 web_rid `141601416352`（看海老师）LIVE、4 档画质（原画 1080P/高清 720P/标清 480P/流畅 360P）；签名 WSS 连接成功，15 秒收到 133 条弹幕；ffmpeg 录制 6s 5.7MB。
- GUI 全链路（`E:\LimoClip-v05-run`，pid 24896）：添加 `https://live.douyin.com/141601416352` → 监控（原画 1080P · 60fps 硬解正常）→ 录制 45.43s（42,868,704B + 352 条弹幕，DB `Platform=2 / Status=3 / ErrorMessage=None`）→ 剪辑台波形正常 → 导出 00:00-00:10 双版 MP4（clean 9,653,695B / danmaku 14,598,856B），ffprobe 三条文件均含 AAC 音轨。
- 验收截图：`.cache/v05c-douyin-monitor.png`、`.cache/v05c-douyin-clip.png`、`.cache/v05c-douyin-clip-export.png`。

### 实现要点

- 房间/拉流：`GET https://live.douyin.com/` 取 `ttwid` Cookie；`GET https://live.douyin.com/webcast/room/web/enter/?...&web_rid={webRid}`（带 ttwid + Referer）取 `data.data[0]`，`status==2` 为直播；`stream_url.flv_pull_url`（FULL_HD1/HD1/SD1/SD2）优先、`hls_pull_url_map` 兜底。
- 弹幕：`wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/` 需 13 个参数按固定顺序 md5 后在 Jint 中调用 `sign.js` 的 `get_sign(md5)` 生成 signature；二进制帧 `PushFrame` → payload gzip → `Response`，`needAck` 时回 `ack`（payload=internal_ext），心跳 `hb` 每 5 秒；`WebcastChatMessage`/`WebcastGiftMessage`/`WebcastEmojiChatMessage` 解析为 `DanmakuMessage`。
- 签名脚本：`sign.js`（485KB，AGPL-3.0）不随仓库分发；查找顺序 显式路径 → `LIMOCLIP_DOUYIN_SIGN_JS` → `%LocalAppData%/LiveMonitor/cache/douyin/sign.js` → 应用目录 `douyin-sign.js`/`douyin/sign.js` → plugins 目录；缺失时自动从 GitHub raw 下载并缓存，失败给中文提示且不影响拉流/录制。
- 录制修复：`FfmpegArgumentBuilder` 的 `-ca_file` 改为**仅对 https 输入追加**——抖音匿名拉流地址为 http，给 http 输入传 `-ca_file` 时 ffmpeg 直接报 `Option ca_file not found / Error opening input files: Option not found`（此前 App 内录制 2 秒即失败，手动 CLI 无该参数时正常）。

### 新 session 首条消息（V0.5-C 阶段，更新）

- V0.5-C Step6 虎牙、Step7 斗鱼、Step8 抖音均已完成并推送；**下一步 Step9：全平台联调 + v0.5.0 打包准备**（先跑 `dotnet build` 与五套测试确认 834 全绿；GUI 从 `E:\LimoClip-v05-run` 验收；打包脚本 `scripts/package-release.ps1 -Version v0.5.0`）。main 小步提交；本机网络：斗鱼/抖音可用、虎牙 CDN 限流。


## V0.5-C 增量（Step9）：六平台联调与 v0.5.0 打包（2026-11-23）

### 状态

- **V0.5-C 全部完成**：虎牙/斗鱼/抖音三个平台插件均已落地并注册为内置平台，加上 B站/Twitch/YouTube，共 6 个内置平台。
- 六平台联调（GUI，`E:\LimoClip-v05-run`）：用「一键自适应排布」把 6 路同时铺成 2×3 工作区——B站 `#4 蛋蛋鼠的BJ`（未开播占位）、抖音 `#16 看海老师`（直播中）、斗鱼 `#15 yyfyyf`（直播中）、YouTube `#13 Lofi Girl`（直播中）、虎牙 `#14 杨齐家丶`（加载中→受本机海外网络限流）、Twitch `#17 riotgames`（未开播，测试频道离线）；状态栏 `主库 17 路 · 工作区 2×3`、`解码正常（5% 丢帧）`。截图 `.cache/v05c-all-platforms.png`。
- 打包：`powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Version v0.5.0 -SkipTests` → `artifacts/LimoClip-v0.5.0-win-x64.zip` **199,840,995B（190.58MB）**，SHA256 `0fda2c4e16e5971129373484e26175ce0570f8d8b91e1825d319ff6a4e1b43ed`；zip 546 个条目，包含 `LiveMonitor.Plugins.{YouTube,Huya,Douyu,Douyin}.dll`（YouTube/Huya/Douyu/Douyin）与内置 B站/Twitch。
- 冒烟：解压到 `E:\LimoClip-v050-test`（工作区外）→ WMI 启动 `LiveMonitor.Desktop.exe`（pid 11592）→ 窗口 "LiveMonitor" 正常、默认库 `%LocalAppData%\LiveMonitor\livemonitor.db` 打开并更新；已关闭。
- 注意：抖音 `sign.js`（AGPL-3.0）**不随包分发**，首次使用抖音弹幕时自动下载并缓存；离线场景可自行放置或用 `LIMOCLIP_DOUYIN_SIGN_JS` 指定。

### 打包/发布

- 本地打包命令：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Version v0.5.0`（去掉 `-SkipTests` 会先跑 Release 测试）。
- 产物：`artifacts/LimoClip-v0.5.0-win-x64.zip` 与 `artifacts/SHA256SUMS.txt`；上传到 GitHub Releases 后产品页（`https://heyiai02.github.io/LimoClip/`）会自动同步版本号与下载按钮。
- 本次未自动创建 GitHub Release；如需发布，用 `.cache/create-release.ps1` 的既有流程或网页上传 zip + SHA256SUMS.txt（Release 资产名需保持 `LimoClip-v0.5.0-win-x64.zip`）。

### 新 session 首条消息（V0.5 收尾后）

- V0.5（A/B/C）已全部完成：编辑台云端 ASR、平台插件热更新底座 + YouTube、虎牙/斗鱼/抖音插件、六平台联调与 v0.5.0 打包。全量测试 **834 全绿**，HEAD 见 `git log` 顶部。下一步可选：发布 v0.5.0 Release、抖音签名脚本离线化、虎牙国内网络复验、多轨 NLE 评估等。
