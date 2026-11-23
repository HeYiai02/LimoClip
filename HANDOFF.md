# LiveMonitor V0.3 交接（录制中心与全程录制引擎）

## 状态

- 分支 `main`；改动**尚未提交**（详见文末“工作树”，勿误删未跟踪文件）。
- 构建：`dotnet build LiveMonitor.sln --no-restore` → **0 错误**、8 警告（MSB3277/CS9057/WFAC010/AVLN3001/NU1803，均为既有环境告警）。
- 测试：Data 35 / Platforms 120 / Danmaku 98 / Media 95 / Desktop 74 = **422 全绿**（2026-11-23）。
- 验收：GUI 冒烟通过；V0.3 实机录制验收全流程通过（脚本 `.cache/step6-acceptance.ps1`，证据 `.cache/step6-*.png`）。
- 实现依据：`limoclip_v0_3.md`（V0.3 起以此为准；`code_artifact.md` 的 V0.3 段落是早期设想，环形缓冲 / NAS 归档 / 手动转 MP4 已确认砍掉）。

## V0.3 已实现内容

### Core（新增契约）

- `Enums/RecordingStatus.cs`：Starting=0 / Recording=1 / Stopping=2 / Completed=3 / Failed=4。
- `Entities/RecordingTarget.cs`：`LiveRoomId`、`AutoRecordEnabled`、`CustomSaveDirectory?`、`UpdatedAt`。
- `Entities/RecordingSession.cs`：`Id`、`LiveRoomId?`、平台 / 房间快照、状态、视频 / 弹幕路径、开始 / 结束时间、时长、大小、弹幕数、高能数、`ErrorMessage?`。
- `Models/RecordingProgress.cs`：`SessionId`、`Duration`、`FileSizeBytes`、`BitrateKbps`、`DanmakuCount`、`HighlightCount`。
- `Interfaces/IRecordingTargetRepository.cs`、`IRecordingSessionRepository.cs`、`IFullStreamRecorder.cs`、`IHighlightEventEnricher.cs`。
- `Models/HighlightEvent.cs`：新增 init 属性 `RecordingSessionId?`、`RecordingOffsetMs?`。
- `AppSettingKeys.cs`：`RecordingDefaultDirectory`（默认 `%UserProfile%\Videos\LimoClip`）、`RecordingPollIntervalSeconds`（默认 60，范围 10~600）。

### Data

- 新表 `RecordingTargets`（PK = LiveRoomId，FK → LiveRooms `ON DELETE CASCADE`）与 `RecordingSessions`（`LiveRoomId` 可空，FK → LiveRooms **`ON DELETE SET NULL`**，会话独立于直播间生命周期）。
- `HighlightEvents` 幂等迁移：`PRAGMA table_info` 缺列才 `ALTER TABLE` 加 `RecordingSessionId TEXT` / `RecordingOffsetMs INTEGER`；`PRAGMA user_version = 4`。
- `RecordingTargetRepository`（upsert）、`RecordingSessionRepository`（按最近 / 按房间查询、未完成查询）；索引 `IX_RecordingSessions_StartedAt`、`_LiveRoomId`、`_PlatformRoom`。

### Media（新工程 `src/LiveMonitor.Media`，net8.0-windows，依赖 Core + Danmaku）

- `Recording/ProcessJobGuard.cs`：Windows Job Object `KILL_ON_JOB_CLOSE`，宿主退出由内核杀死 ffmpeg。
- `Recording/FfmpegArgumentBuilder.cs`：`-headers` 严格 `Key: Value\r\n` 且在 `-i` 前、UA 单独 `-user_agent`、`-rw_timeout 15000000`、显式 `-ca_file <cacert>`、`-c copy -f matroska -progress pipe:1`。
- `Recording/FfmpegProgressParser.cs`、`Recording/FfmpegLocator.cs`（ffmpeg 与 cacert 的定位，支持从 BaseDirectory 向上 8 层查找）。
- `Recording/FfmpegFullRecorder.cs`：stdin `q` 优雅停录 + 超时 `Kill(entireProcessTree)`；首个 `out_time_us>0` 作为首帧锚点（5s 内未解析回退进程启动时间）；进度事件、退出事件、stderr 尾部摘要。
- `Recording/DanmakuJsonlRecorder.cs`：伴随 `.jsonl`（首行 meta，后续 dm，`offsetMs` 相对首帧）；中文不再被 `\uXXXX` 转义。
- `Recording/RecordingFileNaming.cs`：`{UTC yyyyMMdd_HHmmss}_#{DisplayNumber}_{SafeAlias}_{RoomId}.mkv`，剔除 Windows 非法字符。
- `Recording/RecordingHighlightTracker.cs`：`IHighlightEventEnricher` 实现，把高能事件同步补全 `RecordingSessionId` / `RecordingOffsetMs` 后再广播与落盘（消除竞态）。
- `Recording/RecordingManager.cs`（1688 行）：手动 / 自动录制状态机、原画画质（无 `cookie` 时也走匿名）、断流自动分段与 5~60s 退避重试（最多 5 次）、30s 无进度判卡死、`InitializeAsync` 崩溃恢复（未完成会话标 Failed）、60s 开播轮询（`SemaphoreSlim(4,4)`）、删除记录连文件删除、`DisposeAsync` 停录。

### Danmaku

- `DanmakuManager`：新增 `HighlightEventEnricher` 属性；`OnHighlightDetected` 先同步补全，再用同一实例广播 + 异步落盘。

### Desktop（录制中心 UI）

- 顶栏 `📺 监控工作区` / `⏺ 录制中心` 切换：`IsVisible` 隐藏，不销毁 Dock 与播放器；录制中心隐藏侧栏 / 状态栏、保留顶栏。
- `RecordingCenterViewModel` / `RecordingTargetItemViewModel` / `RecordingHistoryItemViewModel` / `RecordingUiFormat` / `RecordingCenterView.axaml(.cs)` / `ConfirmDeleteRecordingWindow`：
  - 全局录像目录读写、逐房间「开播自动录制」、独立目录覆盖、立即开录 / 停止；
  - 实时指标（时长 / 码率 / 大小 / 弹幕 / 高能）与历史录像列表（打开文件夹 / 删除）；
  - 删除前弹窗确认（连 `.mkv` / `.jsonl` 一起删，不可恢复）；正在录制的会话禁止删除。
- `AppServices`：构造并公开 `RecordingManager Recordings`、`RecordingTargets`、`RecordingSessions`；providerResolver 未注册平台返回 `null`。
- `MainWindowViewModel`：`IsRecordingCenterVisible`、`RecordingCenter`（懒创建）；弹幕与录制引擎初始化后调用 `RecordingCenter.InitializeAsync`（订阅实时进度，**这是指标刷新修复点**）；`DeleteRoomAsync` 在删直播间前先停录（用户决定：删直播间保留录制历史）；`Dispose` 先释放录制中心与录制引擎。
- `MainWindow.axaml(.cs)`：录制中心宿主 Border、`ApplyShellLayout`、Esc 切回监控、注入删除确认窗与目录选择。
- `LiveMonitor.Desktop.csproj`：增加 Media 工程引用；`tools/ffmpeg/ffmpeg.exe` 复制到输出目录（`tools\ffmpeg\ffmpeg.exe`），`cacert.crt` 复制到根目录。

## 实机验收证据（2026-11-23）

脚本 `.cache/step6-acceptance.ps1`（临时库 `.cache/step6/livemonitor.db`，录制目录 `.cache/recordings`，UIA + Python sqlite seed/inspect）：

- 0 路播放时启动 2 路录制（B站 6650029 第五人格赛事、7734200 哔哩哔哩英雄联盟赛事），25s 后 mkv 持续增长（1.8MB / 0.8MB），ffmpeg 2 进程。
- 停录后 mkv 终态 8.2MB / 4.5MB，jsonl 含正确 meta 与 dm 行，`ffmpeg -v error -i <mkv> -f null -` 两文件 `exit=0`（可播、可拖动）。
- 强杀 `LiveMonitor.Desktop.exe`：ffmpeg 在 **0.23s** 内全部消失（Job Object 生效），被杀 mkv 仍可解码（exit=0）。
- 重启 App：未收尾会话被标 `Failed`，`ErrorMessage` = “程序异常退出，录制未正常收尾，文件可能不完整。”，文件大小保留。
- 截图：`.cache/step6-04-recording-25s.png`、`.cache/step6-06-after-crash-recovery.png`；指标刷新复测 `.cache/probe2-recording-12s.png`（录制中显示 时长 / 码率 / 大小）。

## 与 `limoclip_v0_3.md` 的偏差 / 实现决策（以本文件为准）

1. **删直播间保留历史**：文档原写 `RecordingSessions.LiveRoomId FK ON DELETE CASCADE`，按用户决定改为可空 + `ON DELETE SET NULL`；历史按 `(Platform, RoomId)` 查询，重新添加同一直播间后仍能看到旧录像。
2. **删除历史记录 = 删文件 + 删 DB 行**，且 UI 先弹确认框；正在进行的会话禁止删除。
3. **手动停止自动录制后抑制自动重启**：抑制到该场直播下播或用户再次手动开录；断流自动续录有退避与最大次数。
4. **新增 `-ca_file`**：随包静态 ffmpeg 在本机无法注册 Windows 证书库，HTTPS 输入报 `Failure registering the Windows certificate store` / `error:0A000086` → 显式传 `tools\cacert.crt`（文档 §2 未列，实机验收发现）。
5. **录制中心在启动时初始化**（订阅事件 + 首次刷新），未打开过录制中心也能持续更新指标；`SwitchToRecordingCenter` 再幂等初始化一次兜底。
6. **jsonl 中文不转义**（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`），便于人工与 V0.4 剪辑台阅读，仍是合法 JSON。
7. **高能绑定只在有录制会话时生效**（`RecordingHighlightTracker` 未注册则原样返回），录制中心未启动不影响监控的高能面板。

## 已知限制 / 待办

- 本机实验环境：App 进程无法在 `%UserProfile%\Videos` 下创建目录（沙箱/权限差异），验收用 AppSettings 覆盖目录；真实用户环境预期正常，但发布前建议手动验证默认目录创建失败时的提示文案。
- 自动录制 60s 轮询对 Twitch 每房间一次 `yt-dlp -j`，20~30 路下的开销尚未压测；可考虑按平台 / 错峰。
- B站轮播房（`live_status=2`）会被当作开播并录制（V0.2 语义沿用），未加过滤。
- 自动续录最多 5 次，超过后交回 60s 轮询；长时间断流场景未长时间挂机验证。
- 高能事件 → 录像时间点跳转 / 剪辑台消费属于 V0.4。
- `code_artifact.md` 的 V0.3 段落仍是早期设想（环形缓冲 / NAS 归档 / ClipTask），本版以 `limoclip_v0_3.md` 与本文件为准，后续可一并清理。
- 所有改动未 `git commit`；`limoclip_v0_3.md` 仍未跟踪。

## 工作树（未提交）

修改：`LiveMonitor.sln`、`src/LiveMonitor.Core/AppSettingKeys.cs`、`Models/HighlightEvent.cs`、`Danmaku/DanmakuManager.cs`、`Data/LiveMonitorDatabase.cs`、`Data/Repositories/HighlightEventRepository.cs`、`Desktop/LiveMonitor.Desktop.csproj`、`Desktop/Services/AppServices.cs`、`Desktop/ViewModels/MainWindowViewModel.cs`、`Desktop/Views/MainWindow.axaml(.cs)`、`tests/LiveMonitor.Danmaku.Tests/DanmakuManagerTests.cs`。

新增：`limoclip_v0_3.md`、`src/LiveMonitor.Core` 下 8 个录制契约文件、`src/LiveMonitor.Data/Repositories/Recording*Repository.cs`、`src/LiveMonitor.Media/`（14 个文件）、`src/LiveMonitor.Desktop` 下 9 个录制中心文件、`tests/LiveMonitor.Data.Tests/RecordingRepositoryTests.cs`、`tests/LiveMonitor.Media.Tests/`（8 个测试文件 + csproj）。

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

GUI 冒烟：`.cache/app-smoke.ps1`（启动 → 截图 → UIA 切监控/录制中心 → 停止）。实机验收：`.cache/step6-acceptance.ps1`（临时库 + UIA + Python seed，结束自动清理）。

## 约束（沿用 V0.1/V0.2）

- 平台逻辑保持现状：B站原生接口（含 WBI 签名）、Twitch `yt-dlp -j`；不换 streamlink；不处理 SSAI 广告。
- 焦点放大 / 音频总线 / `PlatformCredentials` / `PlatformType` 语义不动。
- 不为性能回退 `aid=no`；不重构既有 V0.1/V0.2 模块。
- 界面沿用现有深色风格（#0F1115 / #151923 / #262B36，圆角 8，内边距 14）。
- 截图 / 临时 DB / 测试产物放 `.cache/`；第三方二进制不入库（`scripts/fetch-tools.ps1` 补齐，ffmpeg 自 V0.3 起为硬依赖）。

## 新 session 首条消息（可直接复制）

> 阅读 `HANDOFF.md`、`limoclip_v0_3.md` 与 `limoclip_v0_2.md`，先核对 `git status` 与全量测试，再按需开始 V0.4（剪辑台 / 波形 / 切片 / ASR / 平台热更新）。
