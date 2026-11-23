# LiveMonitor 交接（截至 V0.5-A：剪辑台云端 ASR；含本机运行规避方案）

## 状态

- 分支 `main`；**当前 HEAD 为文档收尾提交 `docs(v0.5-a): 更新架构/README/交接文档以反映 V0.5-A 收尾`（其前为 `c32367d` 波形修复）**，工作树干净。V0.4 按层提交、V0.5-A 三次提交、本机运行规避与波形修复各一次提交，提交清单见文末各节。
- 构建：`dotnet build LiveMonitor.sln --no-restore` → **0 错误**，8~14 警告（MSB3277 / NU1803 / NU1900 等，均为本机环境既有告警，无新增代码告警）。
- 测试：Data 37 / Platforms 120 / Danmaku 98 / Media 147 / Desktop 94 = **496 全绿**（V0.4 基线 471；V0.5-A 增量 +7 Media、+18 Desktop）。
- 验收：V0.4 GUI 端到端通过（脚本 `.cache/v04-acceptance2.ps1`，证据 `.cache/v04b-*.png`）；V0.5-A 另完成真实 API 验证（55s 音频约 11s 返回，见「V0.5-A 增量」）；本机运行规避后完成真实 GUI 录制/打开目录/ASR 验证（见「本机运行限制」）。
- 实现依据：`limoclip_v0_4.md`（V0.4）、`limoclip_v0_5_asr.md`（V0.5-A，已按实现修订 P0 偏差）；`code_artifact.md` 已更新为 v1.7。
- **V0.5-B（插件热更新底座 + YouTube）与 V0.5-C（虎牙/斗鱼/抖音）未开始，按用户要求暂缓**；恢复时先读本文件「新 session 首条消息」与 V0.5-A 增量节。多轨 NLE 只评估、不实现。

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

- ASR（V0.5-A 已实现并完成真实 API 验收，见下一节）、平台/插件热更新（YouTube/抖音/斗鱼/虎牙录制 + 三层热更新，V0.5-B/C 未开始，**按用户要求暂缓**）。
- 产品侧加固待办（普通用户机器更稳妥，本版未改）：录制前目标目录可写性预检、`ffmpeg exit -13 / Permission denied` 明确中文提示与建议目录、「打开目录」失败提示；并把本机启动器 `refresh-and-run.ps1` 纳入仓库 `scripts/`（当前只存在于 `C:\Users\Administrator\LimoClip`）。
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

> 阅读 `HANDOFF.md`、`limoclip_v0_5_asr.md`，先核对 `git log --oneline -8` 与全量测试（build + 五套测试，应为 496 全绿）。V0.5-A 已完成并真实 API 验收；**V0.5-B（插件热更新底座 + YouTube）与 V0.5-C（虎牙/斗鱼/抖音）按用户要求暂缓，默认不要继续**。恢复开发前先读「V0.5-A 增量」与「本机运行限制」；可选收尾项见「已知限制 / 待办（V0.5）」中的产品侧加固与启动器脚本入库。本机运行一律使用启动器（`C:\Users\Administrator\LimoClip\refresh-and-run.ps1` 或桌面快捷方式），不要直接运行工作区内 bin 下的 exe。

## V0.5-A 增量（2026-11-23）：剪辑台语音识别（ASR）

### 状态

- 实现依据：`limoclip_v0_5_asr.md` §3 + §7 Step 1/2（已按实现修订 P0 偏差：包名、缓存键、`TryAssign`、`CreateDanmakuClient` 可空、ASR 为分区而非 Tab、高能卡片按钮拆分）。
- 构建：`dotnet build LiveMonitor.sln --no-restore` → **0 错误**；仅既有环境告警（MSB3277 / NU1803 / NU1900 / CS9057 / CS0108 / WFAC010 / AVLN3001 / xUnit2009 等）。
- 测试：Data 37 / Platforms 120 / Danmaku 98 / Media 147 / Desktop 94 = **496 全绿**（V0.4 基线 471；本增量 +7 Media、+18 Desktop）。
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

- 一律使用启动器（桌面快捷方式「LiveMonitor（工作区外运行）」或 `C:\Users\Administrator\LimoClip\refresh-and-run.ps1`）：
  1. `dotnet build LiveMonitor.sln --no-restore`；
  2. `robocopy` 构建输出到 **`E:\LimoClip-v05-run`**（工作区外），顺带删除陈旧 `startup-fallback.log`；
  3. 从该目录启动 `LiveMonitor.Desktop.exe`。脚本带「已运行则拒绝」守卫，支持 `-SkipBuild` / `-NoLaunch`。
- 数据迁移：原工作区回退库（含用户房间 7 条、设置 8 项、ASR Key、17 条高能记录）已 `wal_checkpoint(TRUNCATE)` 后迁移到默认位置 `C:\Users\Administrator\AppData\Local\LiveMonitor\livemonitor.db`；原回退库保留未动。

### 规避后验证（2026-11-23 19:28–19:31，真实 GUI）

- 录制：B 站房间 1882958350 真实录制 55.6s 成功，`D:\clipRecord\20261123_112855_#4_...mkv` 46,193,788B、同名 `.jsonl` 6,008B、弹幕 35 条，停止后 DB 状态 `Completed`、无错误。
- 打开目录：正常弹出资源管理器，无 0xc0000142。
- ASR：剪辑台选中该场次后「🎙 识别当前选区」，55s 音频约 11s 返回（siliconflow_cn / FunAudioLLM/SenseVoiceSmall），缓存写入 `%LocalAppData%/LiveMonitor/cache/asr/{sha256}.asr.json`，临时 MP3 已清理。截图 `.cache/v05bug-3-asr-bottom.png`。
- 若在普通用户机器上运行，建议仍记录一条产品侧改进待办：录制前目标目录可写性预检 + `ffmpeg exit -13` 明确提示 + 「打开目录」失败提示（本环境未改代码）。

### 后续修复（2026-11-23 19:4x，提交 `c32367d`）

- 启动器 v3：本机用户环境 `TEMP/TMP=E:\code\Env\Temp`（工作区外），而 `dotnet.exe` 位于工作区内、受同一写沙箱限制，构建时报 `MSB1025 Access to the path 'E:\code\Env\Temp\MSBuildTemp' is denied`，脚本失败后窗口一闪而过。现启动器在构建前把 `DOTNET_ROOT/DOTNET_CLI_HOME/NUGET_PACKAGES/APPDATA/LOCALAPPDATA/TEMP/TMP` 全部指到工作区内（`.cache\temp`、`.appdata`），构建结束在 `finally` 中恢复真实用户环境变量再启动应用；失败时红字报错 + `Read-Host` 暂停，并写 transcript 日志 `.cache/launcher-run.log`。
- 剪辑台时间轴波形修复：`DrawWaveform` 原先用非闭合、`isFilled:false` 的 `StreamGeometry` 线段填充绘制，实际零面积不可见，用户只能看到波形中心线（一条平直蓝线）。改为按像素采样上下包络、拼成闭合多边形填充 + 描边，无可用音频时显示「（该视频没有可用的音频波形）」；`ClipStudioView.axaml` 给 `ClipTimelineControl` 增加 `ToolTip.Tip` 说明各行含义与操作（点击/拖动定位播放头、拖 IN/OUT、滚轮缩放、双击复位）。截图 `.cache/v05bug-8-waveform-crop.png`。
