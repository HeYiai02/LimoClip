# LimoClip V0.3（独立录制中心与全程录制引擎）— 详细设计实施文档

> **架构收口说明**：本期完全砍掉环形缓冲（RingBuffer）、异步云盘/NAS 归档以及手动转 MP4 功能。录制模块作为独立视图与监控工作区彻底解耦，仅共享「直播间主库」与 V0.2 的「弹幕连接/过滤/高能检测」基础设施；原始录像统一直接录为抗断电的 **`.mkv`**，并生成同名伴随弹幕 **`.jsonl`**，供 V0.4 剪辑台直接加载并导出 `.mp4` 成品切片。

---

## 1. V0.3 范围边界与验收目标

### 1.1 明确边界（In-Scope / Out-of-Scope）

* **本期包含（In-Scope）**：
  * 新增 `LiveMonitor.Media` 与 `LiveMonitor.Media.Tests` 两个项目，复用 `tools/ffmpeg/ffmpeg.exe`。
  * **主窗口顶级视图切换**：在 `MainWindow` 顶部栏新增 **`[📺 监控工作区]`** 与 **`[⏺ 录制中心]`** 切换按钮（切换视图时，监控区播放器与后台录制互不干扰、不销毁）。
  * **独立录制中心 UI**：
    * 直接读取 `IRoomRepository` 的直播间主库，按主播勾选是否开启「开播自动录制」，支持单行「立即开录 / 停止录制」与「独立目录覆盖」（默认走全局录制目录，用户可在系统资源管理器中直接指定本地盘或已映射的 NAS 盘符）。
    * 实时展示录制中场次的**录制时长、实时码率、已写文件大小、弹幕落盘条数、本次高能打点数**。
    * 展示历史录像列表（状态、时长、文件大小、高能点数、一键在资源管理器中定位文件、删除记录）。
  * **全程 `.mkv` 录制引擎（`FfmpegFullRecorder`）**：调用 `ffmpeg.exe -c copy` 零重编码直录原画流为 `.mkv`，支持 `stdin` 发送 `q` 优雅停录写入索引，并绑定 Windows **Job Object** 彻底消灭主程序异常崩溃时的僵尸 FFmpeg 进程。
  * **伴随弹幕落盘（`DanmakuJsonlRecorder`）**：开录时向 `DanmakuManager` 订阅拿到 `DanmakuSessionHandle`（若监控区正在看同一主播，自动共享单路 WebSocket 连接），将经过 `KeywordFilter` 与 `BilibiliUnicycleFilter` 过滤后的弹幕连同相对录屏起点的毫秒偏移量写入同名 `.jsonl`。
  * **高能事件与录屏场次关联**：录制期间触发的 `HighlightEvent` 自动绑定当前 `RecordingSessionId` 与精确 `RecordingOffsetMs`，并持久化至 SQLite，为 V0.4 时间轴高能标亮铺路。
  * **独立后台开播轮询与断流分段恢复**：后台 60 秒周期轮询所有勾选了「自动录制」但未在录的房间（复用 `SemaphoreSlim(4, 4)` 限流），开播即自动起录；录制中途网络闪断自动收尾当前文件并检测复播新开分段文件，防止破坏已录数据。
* **本期明确不做（Out-of-Scope）**：
  * 环形缓冲录制（已永久移除）。
  * 异步网盘/NAS 归档与 `rclone` 集成（已永久移除，由用户通过系统映射盘符解决）。
  * 历史录像手动转封装为 `.mp4`（已移除，原始录播统一保留 `.mkv`，成品导出留在 V0.4）。
  * 剪辑台、双波形提取、切片导出、ASR 语音转写、抖音/斗鱼/虎牙/YouTube 接入与 DLL 热更新（均属 V0.4）。

### 1.2 验收标准

1. **彻底解耦验证**：
   * 监控工作区关闭全部视频分屏（0 路播放），在录制中心开启 2 个主播的录制，原画 `.mkv` 与伴随 `.jsonl` 正常持续写入。
   * 监控工作区切换窗口方案、拖拽调整大小或关闭某个分屏时，录制中心对应主播的录像进程与文件写入零中断。
2. **连接复用与数据一致性**：
   * 同一主播同时在监控区看直播（开弹幕）并在录制中心录制，底层仅维持 1 条 WebSocket 弹幕连接；关闭监控分屏后，录制端弹幕落盘不中断。
   * 停止录制后，生成的 `.mkv` 可被 `libmpv` / VLC / PotPlayer 秒开且拖动进度条正常；同名 `.jsonl` 中的 `offsetMs` 与视频画面时间基本对齐（误差 $\le 2\text{s}$）。
3. **异常与进程安全**：
   * 录制过程中强行通过任务管理器结束 `LiveMonitor.Desktop.exe`，系统在 1 秒内自动终止所有子 `ffmpeg.exe` 进程（无残留僵尸进程），且已落盘的 `.mkv` 仍可正常播放。
   * 勾选「自动录制」的未开播房间，在开播后 60 秒内自动进入录制状态；主播下播后自动收尾并更新历史记录为 `Completed`。

---

## 2. V0.3 编码前设计评审修正项（防坑指南）

| 序号 | 潜在隐患 | 强制修正方案 |
| :--- | :--- | :--- |
| **1** | **崩溃残留僵尸 `ffmpeg.exe` 写爆硬盘**：若主程序异常崩溃或强杀，普通子进程启动的 `ffmpeg.exe` 不会随之退出，会在后台一直录制直到磁盘写满。 | ① 正常停录时向 FFmpeg `StandardInput` 写入 `"q\n"` 等待最多 5 秒让其写完 MKV 索引（Cues）再兜底 `Kill`；② 引入轻量 P/Invoke 封装 Windows **Job Object**（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`），启动 FFmpeg 后立即挂入全局 Job，主进程一旦意外退出，操作系统内核自动强杀所有子 FFmpeg。 |
| **2** | **FFmpeg 防盗链请求头格式陷阱**：FFmpeg 的 `-headers` 参数对格式极其苛刻，缺少尾部 `\r\n` 会导致 B站 返回 `403 Forbidden`，且 `-headers` 必须写在 `-i` **之前**。 | 封装统一的 `FfmpegArgumentBuilder`：将 `User-Agent` 通过 `-user_agent` 单独传入，将 `Referer`、`Cookie` 等按 `Key: Value\r\n` 严格拼接（确保末尾带 `\r\n`）置于 `-i <url>` 之前。 |
| **3** | **弹幕与视频时间轴偏移（T0 对齐）**：启动 FFmpeg 到真正连上 CDN 拿到首个视频关键帧通常有 1~4 秒延迟；若以启动进程的时间作为弹幕 `offsetMs=0`，回看时弹幕会整体提前数秒。 | 开启 FFmpeg `-progress pipe:1`，监听到第一条 `out_time_us`（$> 0$）或 `frame=` 输出时，将此刻减去 `out_time_us` 锚定为录像精确起点 `AnchorUtc`（若 5 秒内未解析到则回退至进程启动时间），以此计算每条弹幕的 `offsetMs`。 |
| **4** | **映射 NAS 盘符抖动导致 FFmpeg 假死**：用户直接将录制目录设为映射的 SMB NAS 盘符时，网络抖动可能导致 FFmpeg 卡死不退出也不写盘。 | 传入 `-rw_timeout 15000000`（15 秒微秒级读写超时）；并在 `RecordingManager` 监控 `-progress` 心跳——若连续 30 秒无任何进度更新，判定拉流/写盘卡死，主动终止当前进程并触发重试分段（如 `_part2.mkv`）。 |
| **5** | **SQLite 升级破坏 V0.2 已有数据库**：`HighlightEvents` 表在 V0.2 已经创建，直接改 `CREATE TABLE` 不会对已有用户数据库生效。 | 在 `LiveMonitorDatabase.InitializeAsync()` 中增加幂等迁移检查：查询 `PRAGMA table_info(HighlightEvents)`，仅当缺少 `RecordingSessionId` / `RecordingOffsetMs` 列时执行 `ALTER TABLE` 补齐。 |
| **6** | **原画流 Cookie 策略**：录制中心属于主动原画录制，不能受监控区 20~30 路“非焦点降级游客态”的限制。 | `RecordingManager` 调用 `IPlatformProvider.GetRoomStatusAsync` 时始终读取 `ICredentialStore` 凭证并挑选 `Height` / `Fps` 最高的原画清晰度。 |

---

## 3. 数据库增量设计（SQLite Schema Migration）

在现有 `livemonitor.db` 中新增两张录制表，并对 V0.2 的 `HighlightEvents` 表做向后兼容的列扩展：

```sql
-- 1. 录制目标配置表（与直播间主库 1:1 关联，删除主播时级联清理）
CREATE TABLE IF NOT EXISTS RecordingTargets (
    LiveRoomId TEXT PRIMARY KEY,
    AutoRecordEnabled INTEGER NOT NULL DEFAULT 0,   -- 0=仅手动录制, 1=开播自动录制
    CustomSaveDirectory TEXT,                       -- 可空；为空时使用 AppSettings 中的全局录制目录
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE CASCADE
);

-- 2. 录像场次记录表（每次开录/断流分段生成一条独立记录）
CREATE TABLE IF NOT EXISTS RecordingSessions (
    Id TEXT PRIMARY KEY,
    LiveRoomId TEXT NOT NULL,
    Platform INTEGER NOT NULL,
    RoomId TEXT NOT NULL,
    RoomDisplayName TEXT NOT NULL,                  -- 快照：如 "#1 晚风游戏室"（防止后续改名丢上下文）
    StreamTitle TEXT NOT NULL DEFAULT '',
    Status INTEGER NOT NULL,                        -- 0=Starting, 1=Recording, 2=Stopping, 3=Completed, 4=Failed
    VideoFilePath TEXT NOT NULL,                    -- 完整 .mkv 路径
    DanmakuFilePath TEXT NOT NULL,                  -- 同名伴随 .jsonl 路径
    StartedAt TEXT NOT NULL,                        -- UTC ISO-8601（锚定首帧后的录制起点）
    EndedAt TEXT,
    DurationSeconds REAL NOT NULL DEFAULT 0,
    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
    DanmakuCount INTEGER NOT NULL DEFAULT 0,
    HighlightCount INTEGER NOT NULL DEFAULT 0,
    ErrorMessage TEXT,
    FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_RecordingSessions_StartedAt ON RecordingSessions(StartedAt DESC);
CREATE INDEX IF NOT EXISTS IX_RecordingSessions_RoomId ON RecordingSessions(LiveRoomId);

-- 3. 幂等扩展 V0.2 已有的 HighlightEvents 表（代码检查列不存在后再执行）
-- ALTER TABLE HighlightEvents ADD COLUMN RecordingSessionId TEXT;
-- ALTER TABLE HighlightEvents ADD COLUMN RecordingOffsetMs INTEGER;
```

新增 `AppSettingKeys` 常量：
* `RecordingDefaultDirectory`：默认录制根目录（缺省为 `%UserProfile%\Videos\LimoClip`）。
* `RecordingPollIntervalSeconds`：自动录制开播轮询间隔（默认 `60`）。

---

## 4. 文件命名与伴随弹幕 `.jsonl` 格式规范

### 4.1 文件命名规则
每次录制在目标目录下生成一对同名前缀文件（文件名严格剔除 Windows 非法字符 `\ / : * ? " < > |`）：
* 视频文件：`{yyyyMMdd_HHmmss}_#{DisplayNumber}_{SafeAlias}_{RoomId}.mkv`
* 伴随弹幕：`{yyyyMMdd_HHmmss}_#{DisplayNumber}_{SafeAlias}_{RoomId}.jsonl`

（若同一场直播中途断流自动重连，新分段按新时间戳生成新的一对 `.mkv` + `.jsonl`，绝不覆盖旧文件。）

### 4.2 伴随弹幕 `.jsonl` 格式（每行一条独立 JSON，抗断电不坏文件）
首行写入元数据头（`type: "meta"`），后续每条过滤后的有效弹幕追加一行（`type: "dm"`）：

```json
{"type":"meta","version":1,"sessionId":"8f3a...","platform":1,"roomId":"21452505","startedAtUtc":"2026-11-23T12:00:05.120Z"}
{"type":"dm","offsetMs":14320,"ts":1795435219440,"uid":"123456","uname":"观众A","text":"666这波操作神了","isSticker":false,"isGift":false}
{"type":"dm","offsetMs":15100,"ts":1795435220220,"uid":"789012","uname":"观众B","text":"[打call]","isSticker":true,"isGift":false}
```

> **设计收益**：JSON Lines 格式每追加一行即完整有效，无需维护闭合的 XML 根节点或 JSON 数组括号；哪怕电脑直接拔电源，V0.4 剪辑台读取 `.jsonl` 时也能完整解析到最后一秒的弹幕并生成热度波形。

---

## 5. 新增项目结构与 Core 层契约

### 5.1 新增目录结构

```text
src/
├── LiveMonitor.Core/
│   ├── Entities/
│   │   ├── RecordingTarget.cs
│   │   └── RecordingSession.cs
│   ├── Enums/
│   │   └── RecordingStatus.cs           # Starting, Recording, Stopping, Completed, Failed
│   ├── Models/
│   │   └── RecordingProgress.cs         # 时长、实时码率 kbps、已写字节数、弹幕数、高能数
│   └── Interfaces/
│       ├── IRecordingTargetRepository.cs
│       ├── IRecordingSessionRepository.cs
│       └── IFullStreamRecorder.cs
│
├── LiveMonitor.Media/                   # [新建类库，依赖 Core + Danmaku]
│   ├── Recording/
│   │   ├── ProcessJobGuard.cs           # Windows Job Object P/Invoke（防僵尸 FFmpeg）
│   │   ├── FfmpegArgumentBuilder.cs     # 严格 CRLF Headers + MKV -c copy 参数构造
│   │   ├── FfmpegFullRecorder.cs        # 单路 FFmpeg 进程封装、进度解析与优雅停录
│   │   ├── DanmakuJsonlRecorder.cs      # 伴随 .jsonl 异步缓冲写入与定期 Flush
│   │   └── RecordingManager.cs          # 录制调度、60s 独立轮询、高能关联、断流分段
│   └── LiveMonitor.Media.csproj
│
└── LiveMonitor.Desktop/
    ├── ViewModels/
    │   ├── RecordingCenterViewModel.cs  # 录制中心主 VM（任务列表 + 实时状态 + 历史记录）
    │   └── RecordingTargetItemViewModel.cs
    └── Views/
        └── RecordingCenterView.axaml    # 独立录制中心界面
```

### 5.2 Core 层核心契约定义

```csharp
namespace LiveMonitor.Core.Models;

public record RecordingProgress(
    Guid SessionId,
    TimeSpan Duration,
    long FileSizeBytes,
    double BitrateKbps,
    int DanmakuCount,
    int HighlightCount
);

public interface IFullStreamRecorder : IAsyncDisposable
{
    event Action<DateTimeOffset>? OnFirstFrameAnchored;
    event Action<TimeSpan, long, double>? OnProgressUpdated; // duration, bytes, bitrateKbps
    event Action<int, string?>? OnExited;                    // exitCode, errorSummary

    Task StartAsync(StreamQuality sourceQuality, string outputMkvPath, CancellationToken ct = default);
    Task StopGracefullyAsync(TimeSpan timeout);
}
```

---

## 6. 核心 C# 代码实现参考

### 6.1 单路全程 MKV 录制器 (`LiveMonitor.Media/Recording/FfmpegFullRecorder.cs`)

核心要点：`-c copy` 零重编码、`-headers` 严格 `\r\n` 注入、`-progress pipe:1` 实时解析进度与首帧 `T0` 锚定、向 `stdin` 写 `q` 优雅收尾、以及挂载 `ProcessJobGuard` 防止崩溃残留。

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

public sealed class FfmpegFullRecorder : IFullStreamRecorder
{
    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;

    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _disposed;
    private int _firstFrameAnchored;

    private readonly StringBuilder _lastStderrLines = new();

    public event Action<DateTimeOffset>? OnFirstFrameAnchored;
    public event Action<TimeSpan, long, double>? OnProgressUpdated;
    public event Action<int, string?>? OnExited;

    public FfmpegFullRecorder(string ffmpegPath, ProcessJobGuard? jobGuard = null)
    {
        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
    }

    public Task StartAsync(StreamQuality sourceQuality, string outputMkvPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputMkvPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        BuildArguments(psi.ArgumentList, sourceQuality, outputMkvPath);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += (_, _) =>
        {
            var code = 0;
            try { code = proc.ExitCode; } catch { /* ignore */ }
            OnExited?.Invoke(code, code == 0 ? null : _lastStderrLines.ToString());
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException("无法启动 ffmpeg.exe 进程。");
        }

        // 绑定到 Windows Job Object：主程序崩溃时由内核自动终止 FFmpeg
        _jobGuard?.AddProcess(proc);

        _process = proc;
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stdoutTask = Task.Run(() => ReadProgressLoopAsync(proc.StandardOutput, _readCts.Token), CancellationToken.None);
        _stderrTask = Task.Run(() => DrainStderrLoopAsync(proc.StandardError, _readCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private static void BuildArguments(ICollection<string> args, StreamQuality quality, string outputMkvPath)
    {
        args.Add("-hide_banner");
        args.Add("-nostats");
        args.Add("-loglevel");
        args.Add("warning");

        // 15 秒微秒级网络/读写超时，防止断流或 NAS 抖动导致进程假死
        args.Add("-rw_timeout");
        args.Add("15000000");

        // 注入防盗链请求头（必须位于 -i 之前，且每行严格以 \r\n 结尾）
        var headersSb = new StringBuilder();
        foreach (var kv in quality.RequiredHeaders)
        {
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-user_agent");
                args.Add(kv.Value);
            }
            else if (!string.IsNullOrWhiteSpace(kv.Value))
            {
                headersSb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            }
        }

        if (headersSb.Length > 0)
        {
            args.Add("-headers");
            args.Add(headersSb.ToString());
        }

        args.Add("-i");
        args.Add(quality.StreamUrl);

        // 零重编码直录原画流为抗断电 MKV
        args.Add("-c");
        args.Add("copy");
        args.Add("-f");
        args.Add("matroska");

        // 将机器可读进度输出至 stdout
        args.Add("-progress");
        args.Add("pipe:1");

        args.Add("-y");
        args.Add(outputMkvPath);
    }

    private async Task ReadProgressLoopAsync(StreamReader reader, CancellationToken ct)
    {
        long totalSize = 0;
        long outTimeUs = 0;
        double bitrateKbps = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                var key = line.AsSpan(0, eqIdx).Trim();
                var val = line.AsSpan(eqIdx + 1).Trim();

                if (key.SequenceEqual("total_size") && long.TryParse(val, out var sz))
                {
                    totalSize = sz;
                }
                else if (key.SequenceEqual("out_time_us") && long.TryParse(val, out var us) && us >= 0)
                {
                    outTimeUs = us;
                    if (us > 0 && Interlocked.Exchange(ref _firstFrameAnchored, 1) == 0)
                    {
                        var anchor = DateTimeOffset.UtcNow - TimeSpan.FromMicroseconds(us);
                        OnFirstFrameAnchored?.Invoke(anchor);
                    }
                }
                else if (key.SequenceEqual("bitrate"))
                {
                    var s = val.ToString().Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (double.TryParse(s, CultureInfo.InvariantCulture, out var kbps))
                    {
                        bitrateKbps = kbps;
                    }
                }
                else if (key.SequenceEqual("progress"))
                {
                    OnProgressUpdated?.Invoke(TimeSpan.FromMicroseconds(outTimeUs), totalSize, bitrateKbps);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainStderrLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (_lastStderrLines.Length > 2000) _lastStderrLines.Remove(0, 1000);
                _lastStderrLines.AppendLine(line);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        var proc = _process;
        if (proc is null || proc.HasExited) return;

        try
        {
            // 向 FFmpeg 发送 'q' 指令，使其写完 MKV Cues 索引并正常退出
            await proc.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await StopGracefullyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _readCts?.Cancel();
        if (_stdoutTask is not null) try { await _stdoutTask.ConfigureAwait(false); } catch { }
        if (_stderrTask is not null) try { await _stderrTask.ConfigureAwait(false); } catch { }

        _readCts?.Dispose();
        _process?.Dispose();
        _process = null;
    }
}
```

### 6.2 伴随弹幕 JSONL 落盘器 (`LiveMonitor.Media/Recording/DanmakuJsonlRecorder.cs`)

核心要点：与 `FfmpegFullRecorder` 首帧时间同步锚定、通过无界 `Channel` 异步批量写入避免阻塞 `DanmakuManager` 回调线程、每 2 秒自动 `FlushAsync()` 保证断电不丢数据。

```csharp
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

public sealed class DanmakuJsonlRecorder : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly Channel<DanmakuMessage> _channel = Channel.CreateUnbounded<DanmakuMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private long _anchorTicksUtc;
    private int _writtenCount;
    private int _disposed;

    public int WrittenCount => Volatile.Read(ref _writtenCount);

    public DanmakuJsonlRecorder(
        string filePath,
        Guid sessionId,
        PlatformType platform,
        string roomId,
        DateTimeOffset initialStartUtc)
    {
        _filePath = filePath;
        _anchorTicksUtc = initialStartUtc.UtcTicks;
        _writerTask = Task.Run(() => WriteLoopAsync(sessionId, platform, roomId, initialStartUtc));
    }

    /// <summary>收到 FFmpeg 首帧时间后校准零点</summary>
    public void UpdateAnchor(DateTimeOffset exactStartUtc)
    {
        Interlocked.Exchange(ref _anchorTicksUtc, exactStartUtc.UtcTicks);
    }

    public void Enqueue(DanmakuMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _channel.Writer.TryWrite(message);
    }

    private async Task WriteLoopAsync(Guid sessionId, PlatformType platform, string roomId, DateTimeOffset initialStartUtc)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 写入第 1 行元数据头
        var metaLine = JsonSerializer.Serialize(new
        {
            type = "meta",
            version = 1,
            sessionId = sessionId.ToString("D"),
            platform = (int)platform,
            roomId,
            startedAtUtc = initialStartUtc.ToString("O")
        });
        await writer.WriteLineAsync(metaLine).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var lastFlush = Environment.TickCount64;
        var reader = _channel.Reader;

        try
        {
            await foreach (var msg in reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var anchorTicks = Interlocked.Read(ref _anchorTicksUtc);
                var offsetMs = Math.Max(0L, (msg.Timestamp.UtcTicks - anchorTicks) / TimeSpan.TicksPerMillisecond);

                var line = JsonSerializer.Serialize(new
                {
                    type = "dm",
                    offsetMs,
                    ts = msg.Timestamp.ToUnixTimeMilliseconds(),
                    uid = msg.SenderUid,
                    uname = msg.SenderName,
                    text = msg.Content,
                    isSticker = msg.IsSticker,
                    isGift = msg.IsGiftOrSuperChat
                });

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                Interlocked.Increment(ref _writtenCount);

                // 每 2 秒落盘一次，兼顾磁盘 I/O 与抗断电完整性
                var nowTick = Environment.TickCount64;
                if (nowTick - lastFlush >= 2000)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    lastFlush = nowTick;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _channel.Writer.TryComplete();
        try
        {
            // 等待队列中剩余弹幕写完（最多 3 秒）
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
```

---

## 7. V0.3 6 步开发执行清单

- [x] **Step 1：补齐 Core 实体契约与新建 `LiveMonitor.Media` 项目（0.5 天）**
  * 在 `Core` 中新增 `RecordingTarget`、`RecordingSession`、`RecordingStatus`、`RecordingProgress` 与 `IRecordingTargetRepository` / `IRecordingSessionRepository` / `IFullStreamRecorder`；新建 `LiveMonitor.Media` 与 `LiveMonitor.Media.Tests` 并加入 `LiveMonitor.sln`。
- [x] **Step 2：落地 Data 层录制表与 `HighlightEvents` 幂等迁移（0.5 天）**
  * 在 `LiveMonitorDatabase` 中建齐 `RecordingTargets`、`RecordingSessions` 表，并对旧库执行 `HighlightEvents` 增量加列迁移；实现对应仓储与 `Data.Tests` 单测。
- [x] **Step 3：落地 `ProcessJobGuard`、`FfmpegFullRecorder` 与 `DanmakuJsonlRecorder`（1.5 天）**
  * 封装 Windows Job Object 防僵尸进程；完成 `FfmpegFullRecorder`（CRLF headers、`-c copy` MKV、`-progress` 首帧锚定、优雅停录）与 `DanmakuJsonlRecorder`；编写参数构造、进度解析与 JSONL 写入单测。
- [x] **Step 4：落地 `RecordingManager` 核心调度器（1.5 天）**
  * 实现单房间起录/停录状态机、订阅 `DanmakuManager` 复用弹幕流、将录制期间的 `HighlightEvent` 关联到当前 `RecordingSessionId` 与 `RecordingOffsetMs`、60 秒后台自动录制轮询（`SemaphoreSlim(4)` 限流）与异常断流自动分段恢复。
- [x] **Step 5：搭建主窗口视图切换与独立「录制中心」界面（1.5 天）**
  * 在 `MainWindow` 顶部栏接入 `[📺 监控工作区]` / `[⏺ 录制中心]` 视图切换；实现 `RecordingCenterView`（全局默认保存目录选择、主播主库自动录制勾选与手动开停录、实时录制指标卡片、历史录像列表与「打开所在文件夹」）。
- [x] **Step 6：实机联调与异常验收（1 天）**
  * 联调 B站与 Twitch 原画 `.mkv` + `.jsonl` 同步落盘；测试监控与录制彻底解耦、同房间单路弹幕连接复用、强杀进程无 FFmpeg 残留以及开播自动起录/下播自动收尾。
---

## 8. 实施记录与偏差（2026-11-23）

V0.3 六步已全部落地并通过实机验收（详见 `HANDOFF.md` 与 `README.md` 的 V0.3 章节）。实现与本设计文档的差异（以实际代码 / `HANDOFF.md` 为准）：

1. **`RecordingSessions.LiveRoomId` 改为可空 + `ON DELETE SET NULL`**：原 §3 设计为 `ON DELETE CASCADE`，按产品决定「删除直播间时保留历史录像、重新添加同一直播间后仍可回看」改为 SET NULL；历史查询按 `(Platform, RoomId)`。
2. **删除历史记录 = 删除 `.mkv` / `.jsonl` 文件 + 数据库行**，UI 先弹确认框；正在录制的会话禁止删除（`ConfirmDeleteRecordingWindow`）。
3. **手动停止自动录制后的抑制**：抑制到该场直播下播或用户再次手动开录；断流自动续录最多 5 次，5s 起指数退避、上限 60s；30 秒无进度判卡死（Failed）。
4. **新增 `-ca_file <cacert.crt>`**：随包静态 ffmpeg 在本机无法注册 Windows 证书库（`Failure registering the Windows certificate store` / `error:0A000086`），HTTPS 输入会 `I/O error`；`FfmpegArgumentBuilder` 在 `-headers` 后、`-i` 前显式传 CA，`FfmpegLocator` 负责查找 `tools\cacert.crt`。
5. **录制中心在启动时初始化**：`RecordingCenterViewModel.InitializeAsync` 在弹幕 / 录制引擎初始化后调用（订阅实时进度）；原设计未规定调用时机，漏调用会导致实时指标不刷新。
6. **`.jsonl` 中文不转义**：使用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，便于人工与 V0.4 剪辑台阅读（仍是合法 JSON）。
7. **`h264_nvenc/qsv/amf` 硬件烧录与无损切割**：属 V0.4，V0.3 未实现；`IFullStreamRecorder` 仅覆盖 `-c copy` 全程录制。

### 验收结果（§1.2）

| 验收项 | 结果 |
| :--- | :--- |
| 0 路播放时 2 主播 mkv+jsonl 持续写入 | 通过（25s 已写 1.8MB / 0.8MB，ffmpeg 2 进程） |
| 停录后 `.mkv` 秒开可拖动 | 通过（`ffmpeg -v error -i <mkv> -f null -` exit=0；终态 8.2MB / 4.5MB） |
| 强杀宿主后 1 秒内 ffmpeg 全部终止 | 通过（实测 0.23s，被杀 mkv 仍可解码） |
| 崩溃恢复：未完成会话标 Failed | 通过（`ErrorMessage="程序异常退出，录制未正常收尾，文件可能不完整。"`，时长 / 大小保留） |
| 监控+录制同房间仅 1 条 WS | 单测覆盖（`DanmakuManager` 引用计数：相同 `(Platform,RoomId)` 复用连接） |
| jsonl `offsetMs` 误差 ≤2s | 实测 dm 行 `offsetMs` 与锚点一致（B站 6650029 采样 21095ms） |

测试基线：Data 35 / Platforms 120 / Danmaku 98 / Media 95 / Desktop 74 = **422 全绿**；构建 0 错误。主要验收脚本：`.cache/step6-acceptance.ps1`；截图 `.cache/step6-04-recording-25s.png`、`.cache/step6-06-after-crash-recovery.png`。