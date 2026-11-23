# 直播监控与自动切片应用 — 技术架构与接口设计文档（v1.6，2026-11-23）

> 版本说明：v1.6 按 V0.4 实施口径修订——剪辑台（本地预览 / 双波形时间轴 / 双模式切片导出）已落地，Core/Media/Desktop 契约与实现以代码为准。v1.5 的 V0.3 修订内容保持不变（**环形缓冲、本地→NAS/云盘异步归档与 `rclone`、手动转 MP4 已确认取消**；录制能力为独立录制中心 + 全程 `.mkv` + 同名伴随 `.jsonl`）。V0.3 / V0.4 的完整规格、API 与验收记录分别以 `limoclip_v0_3.md` / `limoclip_v0_4.md` 与 `HANDOFF.md` 为准；ASR、平台/插件热更新与多轨 NLE 顺延 V0.5。


## 1. 项目定位与核心架构决策

本项目是一个面向 Windows 桌面端的高性能**多平台直播分屏监控、弹幕高能自动切片、语音转写与轻量剪辑一体化应用**，目标覆盖国内（B站、抖音、斗鱼、虎牙）与海外（Twitch、YouTube）六大主流平台，支持 **20~30 路同屏监控**。

基于性能边界与长期维护成本，当前确立以下核心架构决策：

1. **客户端形态与渲染管线**：放弃纯 Web 及浏览器 `<video>` 渲染（规避 JS 解封装 GC 抖动、CORS 拦截、HTTP/1.1 6 路并发上限），采用 **C# (.NET 8/9) + Avalonia UI + `libmpv` (OpenGL Render API)**。通过离屏纹理合成消灭 Windows 句柄空域遮挡问题；遇到极端弹幕海时保留回退至 `libmpv` 内置 `libass` 渲染的通道。
2. **高并发（20~30 路）低开销约束**：非焦点窗口默认静音（音频解码常开 `aid=auto`，仅切 `mute`）、单实例缓存为 `demuxer-max-bytes=4M`（高清 8M / 低清 3M）、`demuxer-max-back-bytes=0`，并在高路数紧凑布局下通过滤镜将非焦点画面压至 `15fps`，结合运行时丢帧率动态降级；若 20~30 路压测证明音频解码是瓶颈，再评估退回 `aid=no`。
3. **预览流、录制流与弹幕展示解耦**：
   * 前端分屏按窗口物理尺寸拉取匹配清晰度（焦点放大时切原画），且多屏默认关闭画面弹幕渲染（支持单窗底部独立开关）。
   * V0.3 起由独立「录制中心」按直播间开启全程录制：录制流独立拉取**原画流**与 Cookie，不受监控清晰度降级影响；同时共享监控侧的单条弹幕 WebSocket（引用计数），将过滤后的弹幕连同相对首帧时间戳写入同名伴随 `.jsonl`。自动切片与剪辑已由 V0.4 剪辑台实现（本地预览 + 双版导出）。
4. **广告处理边界**：YouTube 依赖 `yt-dlp` 直连底层流天然绕过绝大多数网页广告；Twitch 当前保持 **`yt-dlp -j`** 解析 HLS，不剔除服务端 SSAI 广告（V0.1 明确接受该限制）；后续如需去广告，再评估 `streamlink --twitch-disable-ads` 或自定义 M3U8 代理，不在本版范围。支持通过内置 `WebView2` 登录透传账号凭证解锁原画与会员免广告。
5. **本地落盘与录制目录**：全程录制由 FFmpeg 以 `-c copy` 直接写 `.mkv`（崩溃可播），默认目录 `%UserProfile%\Videos\LimoClip`，可按全局或按直播间覆盖；目标目录允许指向 NAS 映射盘，但不做本地→云盘异步归档与 `rclone` 集成（V0.3 已确认取消）。弱网/映射盘抖动由 `-rw_timeout` 与 30 秒无进度判卡死兜底。
6. **主数据与方案解耦**：将“直播间主库”、“窗口布局方案”与“录制偏好（按直播间 `AutoRecordEnabled` / `CustomSaveDirectory`）”拆分为独立实体，配合窗口差量切换（Diff）与弹幕单路复用（引用计数）。
7. **三层热更新抗反爬**：海外平台通过自动更新外部 CLI（`yt-dlp`，Twitch/YouTube 共用）跟进社区修复；国内平台抽离为独立插件 DLL，托管在 GitHub/Gitee 仓库并通过 .NET `AssemblyLoadContext` + **Ed25519 数字签名校验**实现免重装热替换（斗鱼/抖音可辅以 `Jint` 热更新签名 JS）。

---

## 2. 技术栈选型清单

| 层级 | 选用组件 / 库 | 核心职责 |
| :--- | :--- | :--- |
| **运行时与语言** | .NET 8 / .NET 9 (C#) | 高性能异步 I/O、插件动态加载（`AssemblyLoadContext`）、成熟桌面生态 |
| **UI 框架与模式** | Avalonia UI + `CommunityToolkit.Mvvm` | 现代桌面 UI、MVVM 源生成器状态管理 |
| **UI 控件与分屏** | `Semi.Avalonia` + `Dock.Avalonia` | 深色监控台风格、平铺式自由拖拽调大小（`ProportionalDock`）、布局序列化 |
| **视频播放引擎** | `LibMpv.Avalonia` (`libmpv`) | D3D11 硬件解码、原生 FLV/HLS 协议解析、独立音轨与画质控制 |
| **平台账号登录** | `WebView2` + Windows DPAPI | 内置官方登录窗捕获 Cookie/Token，本机系统级加密持久化 |
| **多平台流解析** | `yt-dlp` + 国内插件 DLL (+ `Jint`) | Twitch / YouTube 均走 `yt-dlp`；国内四平台走热更新插件；V0.1 不做 Twitch 去广告 |
| **录制与剪辑引擎** | FFmpeg（`Process` 直接调用）+ CliWrap（V0.1 解析） | 全程录制（`-c copy` `.mkv` + 伴随 `.jsonl`）、Windows Job Object 防僵尸；V0.4 已实现视频探测、波形提取、无损切割与硬件烧录（硬件探测失败回退 `libx264`） |
| **语音识别（ASR）** | `Sherpa-ONNX` (SenseVoiceSmall) + OpenAI 兼容 API | 仅对高能推荐片段或框选区间进行离线/云端语音转文字（V0.5） |
| **存储归档（V0.3 起移除）** | — | 原 SMB / WebDAV / `rclone` 异步归档已确认取消；录制目录可直接指向 NAS 映射盘 |
| **本地持久化** | SQLite (`Microsoft.Data.Sqlite`) | 主库、窗口方案、任务方案、高能打点、加密凭证与屏蔽词库（WAL 模式） |

---

## 3. 系统分层架构与解决方案目录结构

核心原则：`Core` 层不依赖任何外部框架；业务模块（录制、弹幕、ASR、归档、存储）不依赖 `Avalonia` UI 层，为未来抽离为无头服务端（NAS/Linux Daemon）保留平滑路径。

```text
LiveMonitor.sln
├── src/
│   ├── LiveMonitor.Core/                # [核心域层] 实体模型、枚举、核心接口、全局配置
│   │   ├── Entities/                    # LiveRoom, LayoutProfile, LayoutSlot, RecordingTarget, RecordingSession
│   │   ├── Models/                      # StreamQuality, DanmakuMessage, HighlightEvent, RecordingProgress,
│   │   │                                # RecordedDanmakuItem, VideoMetadata, WaveformData, ClipExportRequest, ClipExportProgress
│   │   ├── Enums/                       # PlatformType, RecordingStatus 等
│   │   └── Interfaces/                  # IPlatformProvider, IDanmakuClient, IFullStreamRecorder,
│   │                                    # IRecordingTargetRepository / IRecordingSessionRepository, IHighlightEventEnricher,
│   │                                    # IWaveformService, IDanmakuAssConverter, IHardwareEncoderDetector, IClipExporter 等
│   │
│   ├── LiveMonitor.Platforms.Common/    # 公共解析：PlatformDefaults / JsonRead / YtDlpRunner
│   ├── LiveMonitor.Platforms.Bilibili/  # BilibiliProvider（原生 get_info / getRoomPlayInfo）
│   ├── LiveMonitor.Platforms.Twitch/    # TwitchProvider（yt-dlp -j；V0.1 不去广告）
│   ├── LiveMonitor.Platforms/           # [平台调度层] PlatformFactory 组合各 Provider
│   │   └── PlatformFactory.cs           # URL / PlatformType 路由
│   │
│   ├── LiveMonitor.Danmaku/             # [弹幕管线与分析层] 单例复用、过滤、高能检测
│   │   ├── Filters/                     # KeywordFilter, BilibiliUnicycleFilter
│   │   ├── Analyzers/                   # SlidingWindowDetector
│   │   ├── HighlightSettingsStore.cs    # 高能参数持久化
│   │   ├── DanmakuSessionHandle.cs      # 按会话的引用计数句柄（录制中心复用同一连接）
│   │   └── DanmakuManager.cs            # 按 (Platform, RoomId) 单例复用连接并分发
│   │
│   ├── LiveMonitor.Media/               # [媒体引擎层] V0.3 录制 + V0.4 剪辑后端（ASR 属 V0.5）
│   │   ├── Recording/
│   │   │   ├── RecordingManager.cs      # 录制状态机：手动/自动、轮询、断流重试、崩溃恢复
│   │   │   ├── FfmpegFullRecorder.cs    # -c copy 全程 .mkv + stdin q 优雅停录 + 首帧锚点
│   │   │   ├── FfmpegArgumentBuilder.cs # headers/UA/rw_timeout/-ca_file/进度管道参数构造
│   │   │   ├── FfmpegProgressParser.cs  # 解析 -progress pipe:1（out_time_us/total_size/bitrate）
│   │   │   ├── FfmpegLocator.cs         # 定位 ffmpeg 与随包 cacert
│   │   │   ├── DanmakuJsonlRecorder.cs  # 同名伴随 .jsonl（meta + dm，相对首帧偏移）
│   │   │   ├── RecordingFileNaming.cs   # {时间戳}_#{序号}_{主播}_{房间号}
│   │   │   ├── RecordingHighlightTracker.cs # 高能事件绑定 SessionId/Offset（IHighlightEventEnricher）
│   │   │   └── ProcessJobGuard.cs       # Windows Job Object（KILL_ON_JOB_CLOSE）
│   │   └── Editing/                     # V0.4 剪辑后端
│   │       ├── DanmakuJsonlReader.cs    # 伴随 .jsonl 容错读取（坏行/截断跳过、乱序稳定排序）
│   │       ├── VideoProbeService.cs     # ffmpeg -i stderr 探测时长/分辨率/fps/codec（仓库无 ffprobe）
│   │       ├── WaveformService.cs       # 4000Hz 单声道流式 PCM、2000 桶峰值 + 弹幕密度 + SHA256 缓存
│   │       ├── DanmakuAssConverter.cs   # 弹幕 → ASS 滚动轨道与轨道避让
│   │       ├── HardwareEncoderDetector.cs # nvenc/qsv/amf 实测探测，回退 libx264
│   │       ├── ClipExporter.cs          # 无损 clean / 烧录 danmaku 双版导出（Job Object、取消清理）
│   │       └── DanmakuPlaybackCursor.cs # 回放二分推进与 seek 回看
│   │
│   ├── LiveMonitor.Data/                # [数据持久层] SQLite WAL 初始化与仓储实现
│   │   ├── LiveMonitorDatabase.cs
│   │   └── Repositories/                # Room/LayoutProfile/HighlightEvent/RecordingTarget/RecordingSession 等
│   │
│   └── LiveMonitor.Desktop/             # [表现层] Avalonia 桌面端主程序
│       ├── Controls/                    # DanmakuOverlay（直播/回放共用）, ClipTimelineControl（双波形时间轴）
│       ├── Player/                      # MpvPlayerView（直播预览）, LocalClipPlayerView（本地剪辑预览）
│       ├── Services/                    # WorkspacePlayerManager (Diff), PerformanceMonitor, WebView2Login, AppServices
│       ├── ViewModels/                  # MainWindowViewModel, RecordingCenter/Target/History,
│       │                                # ClipStudio / ClipExportTask / ClipHighlightItem / ClipSessionOption
│       └── Views/                       # MainWindow（三视图 Shell）, SettingsWindow, RecordingCenterView,
│                                        # ClipStudioView, 确认弹窗等
│
└── tools/                               # 外部依赖目录（支持独立自动更新）
    ├── cacert.crt                       # 随包 CA bundle（ffmpeg HTTPS 拉流与 mpv 共用）
    ├── ffmpeg/
    │   └── ffmpeg.exe                   # V0.3 起为硬依赖，构建时复制到输出目录
    ├── libmpv-2.dll
    ├── mpv/
    │   └── mpv.exe
    └── yt-dlp.exe
```

---

## 4. 核心领域数据模型设计（SQLite / C# 实体）

### 4.1 直播间主库（全局唯一）
```csharp
namespace LiveMonitor.Core.Entities;

public class LiveRoom
{
    public Guid Id { get; set; }
    public int DisplayNumber { get; set; }                  // 全局递增展示编号（如 1, 2, 3）
    public PlatformType Platform { get; set; }
    public string RoomId { get; set; } = string.Empty;      // 平台房间号，(Platform, RoomId) 唯一
    public string RawUrl { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;       // 主播名，UI 统一展示为 "#{DisplayNumber} {Alias}"
    public string? GroupTag { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### 4.2 窗口方案（Layout Profile）
```csharp
namespace LiveMonitor.Core.Entities;

public class LayoutProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsCustomLayout { get; set; }                // false=按数量自动适配，true=已手动拖拽过比例
    public string? DockLayoutJson { get; set; }             // Dock.Avalonia 序列化窗口树
    public List<LayoutSlot> Slots { get; set; } = [];
}

public class LayoutSlot
{
    public Guid Id { get; set; }
    public Guid LayoutProfileId { get; set; }
    public Guid LiveRoomId { get; set; }
    public string PaneId { get; set; } = string.Empty;
    public bool DefaultMuted { get; set; } = true;
    public bool DefaultDanmakuVisible { get; set; } = false; // 多屏默认关闭画面弹幕渲染
    public bool IsFocusSlot { get; set; } = false;
    public string? PreferredQualityId { get; set; }
}
```

### 4.3 录制偏好与录制会话（V0.3 实际实体）

V0.3 起按直播间保存录制偏好；录制会话独立于直播间生命周期——删除直播间时 `LiveRoomId` 置空、历史与文件保留，重新添加同一 `(Platform, RoomId)` 后仍可回看。原 `ClipTaskProfile` / `RecordMode.RingBuffer` 设计已废弃（切片与剪辑已由 V0.4 剪辑台替代）。

```csharp
namespace LiveMonitor.Core.Entities;

public class RecordingTarget
{
    public Guid LiveRoomId { get; set; }             // 主键，外键 → LiveRooms（级联删除）
    public bool AutoRecordEnabled { get; set; }      // 开播后自动录制
    public string? CustomSaveDirectory { get; set; } // 覆盖全局目录（可指向 NAS 映射盘）
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum RecordingStatus { Starting = 0, Recording = 1, Stopping = 2, Completed = 3, Failed = 4 }

public class RecordingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LiveRoomId { get; set; }            // 删直播间后置空（ON DELETE SET NULL）
    public PlatformType Platform { get; set; }
    public string RoomId { get; set; } = string.Empty;          // 平台原始房间号，用于历史回看
    public string RoomDisplayName { get; set; } = string.Empty; // 快照，如 "#1 晚风游戏室"
    public string StreamTitle { get; set; } = string.Empty;
    public RecordingStatus Status { get; set; } = RecordingStatus.Starting;
    public string VideoFilePath { get; set; } = string.Empty;   // .mkv
    public string DanmakuFilePath { get; set; } = string.Empty; // .jsonl
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;  // 首帧锚点后校准
    public DateTime? EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public int DanmakuCount { get; set; }
    public int HighlightCount { get; set; }
    public string? ErrorMessage { get; set; }
}
```

---

## 5. 核心接口设计（C# 定义）

### 5.1 平台适配接口 (`IPlatformProvider`)
```csharp
namespace LiveMonitor.Core.Interfaces;

public enum PlatformType
{
    Bilibili = 1,
    Douyin = 2,
    Douyu = 3,
    Huya = 4,
    Twitch = 5,
    YouTube = 6
}

public record StreamQuality(
    string Id,
    string DisplayName,
    int Height,
    int Fps,
    string StreamUrl,
    IReadOnlyDictionary<string, string> RequiredHeaders
);

public record RoomStatus(
    string RoomId,
    PlatformType Platform,
    string Title,
    string AnchorName,
    bool IsLive,
    IReadOnlyList<StreamQuality> AvailableQualities
);

public interface IPlatformProvider
{
    PlatformType Platform { get; }
    bool CanHandle(string roomUrl);
    string ExtractRoomId(string roomUrl);
    Task<RoomStatus> GetRoomStatusAsync(string roomId, string? cookie = null, CancellationToken ct = default);

    // V0.2 已实现：弹幕客户端由 Core 的 IDanmakuClientFactory 创建（Platforms.DanmakuClientFactory 落地），
    // 按 (Platform, RoomId) 由 DanmakuManager 统一分配会话并引用计数复用。
    // B站 getDanmuInfo 自 2026-11 起需要 WBI 签名（BilibiliWbiSigner：nav 密钥 + mixin/w_rid），
    // 未签名会返回 code=-352；签名与密钥缓存封装在 BilibiliDanmakuClient 内。
    // 未在 IPlatformProvider 上增加 CreateDanmakuClient。
}
```

### 5.2 弹幕采集、过滤与高能检测接口
```csharp
namespace LiveMonitor.Core.Interfaces;

public record DanmakuMessage(
    string RoomId,
    string SenderUid,
    string SenderName,
    string Content,
    DateTimeOffset Timestamp,
    bool IsGiftOrSuperChat = false
);

public record HighlightEvent(
    string RoomId,
    DateTimeOffset TriggerTime,
    double PeakDensity,
    string TriggerReason
);

public interface IDanmakuClient : IAsyncDisposable
{
    event Action<DanmakuMessage>? OnMessageReceived;
    event Action<Exception>? OnDisconnected;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
}

public interface IDanmakuFilter
{
    /// <summary>返回 true 表示放行，false 表示被关键词或独轮车规则拦截</summary>
    bool ShouldAllow(DanmakuMessage message);
}

public interface IHighlightDetector
{
    HighlightEvent? Ingest(DanmakuMessage message);
    void UpdateRules(HighlightRuleOptions options);
}
```

### 5.3 录制引擎接口（V0.3 实际）与剪辑接口（V0.4 实际）

> V0.2 侧实际契约以代码为准：`DanmakuMessage` 含 `Platform` 字段，弹幕客户端事件为 `MessageReceived` / `Disconnected`（非 `OnMessageReceived` / `OnDisconnected`）。`IStorageArchiveService` 随异步归档方案一并移除。V0.3 / V0.4 契约同样以 `LiveMonitor.Core/Interfaces` 代码为准。

```csharp
namespace LiveMonitor.Core.Interfaces;

/// <summary>全程录制器：FfmpegFullRecorder 实现；测试可注入 fake。</summary>
public interface IFullStreamRecorder : IAsyncDisposable
{
    event Action<DateTimeOffset>? OnFirstFrameAnchored;          // 首帧锚点（-progress 首个 out_time_us>0）
    event Action<TimeSpan, long, double>? OnProgressUpdated;      // 时长 / 已写字节 / 码率 kbps
    event Action<int, string?>? OnExited;                         // 退出码 / stderr 尾部摘要
    Task StartAsync(StreamQuality sourceQuality, string outputMkvPath, CancellationToken ct = default);
    Task StopGracefullyAsync(TimeSpan timeout);                   // stdin 'q'，超时 Kill(entireProcessTree)
}

/// <summary>高能事件在广播/落盘前同步补全录制会话绑定（无阻塞、不抛）。</summary>
public interface IHighlightEventEnricher
{
    HighlightEvent Enrich(HighlightEvent highlightEvent);
}

public interface IRecordingTargetRepository
{
    Task<IReadOnlyList<RecordingTarget>> GetAllAsync(CancellationToken ct = default);
    Task<RecordingTarget?> GetAsync(Guid liveRoomId, CancellationToken ct = default);
    Task UpsertAsync(RecordingTarget target, CancellationToken ct = default);
    Task DeleteAsync(Guid liveRoomId, CancellationToken ct = default);
}

public interface IRecordingSessionRepository
{
    Task AddAsync(RecordingSession session, CancellationToken ct = default);
    Task UpdateAsync(RecordingSession session, CancellationToken ct = default);
    Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingSession>> GetRecentAsync(int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingSession>> GetByRoomAsync(PlatformType platform, string roomId, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingSession>> GetUnfinishedAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
```

V0.4 追加（已实现，以下省略 XML 注释）：

```csharp
namespace LiveMonitor.Core.Models;

public sealed record RecordedDanmakuItem(long OffsetMs, string SenderName, string Text, bool IsSticker, bool IsGift);
public sealed record VideoMetadata(TimeSpan Duration, int Width, int Height, double Fps, string VideoCodec);
public sealed record WaveformData(TimeSpan Duration, float[] AudioPeaks, float[] DanmakuDensity, int MaxDanmakuPerBucket);

public enum ClipExportMode { CleanOnly = 0, BurnedDanmaku = 1, Both = 2 }

public sealed record DanmakuAssStyle(
    int PlayResX = 1920, int PlayResY = 1080, string FontName = "Microsoft YaHei",
    int FontSize = 42, double ScrollDurationSeconds = 10.0, double DisplayAreaRatio = 0.65,
    int OpacityPercent = 85, bool IncludeStickersAsText = true);

public sealed record ClipExportRequest(
    string InputVideoPath, string? CompanionDanmakuPath, string OutputDirectory, string OutputBaseName,
    TimeSpan StartTime, TimeSpan EndTime, ClipExportMode Mode, DanmakuAssStyle? AssStyle = null);

public sealed record ClipExportProgress(string StageLabel, double Percentage, double? SpeedMultiplier);

namespace LiveMonitor.Core.Interfaces;

public interface IWaveformService
{
    // duration 用于 PCM 桶映射（比 limoclip_v0_4.md 多一个参数，已按实现口径收敛）
    Task<WaveformData> GetWaveformAsync(string videoPath, TimeSpan duration,
        string? companionDanmakuPath = null, int bucketCount = 2000, CancellationToken ct = default);
}

public interface IDanmakuAssConverter
{
    Task<int> ConvertToAssAsync(IReadOnlyList<RecordedDanmakuItem> allDanmaku, TimeSpan start, TimeSpan end,
        string outputAssPath, DanmakuAssStyle? style = null, CancellationToken ct = default);
}

public interface IHardwareEncoderDetector
{
    Task<(string EncoderName, string[] ExtraArgs)> DetectBestAsync(CancellationToken ct = default);
}

public interface IClipExporter
{
    Task<IReadOnlyList<string>> ExportAsync(ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems, string sourceVideoCodec,
        IProgress<ClipExportProgress>? progress = null, CancellationToken ct = default);
}
```

剪辑台高能推荐按录制会话查询，`IHighlightEventRepository` 追加：

```csharp
Task<IReadOnlyList<HighlightEvent>> GetByRecordingSessionAsync(Guid sessionId, int limit = 500, CancellationToken ct = default);
```

`IAsrProvider`（ASR）保留为 V0.5 预留，本版未实现。

---

## 6. 关键运行机制与工程边界

### 6.1 切换窗口方案时的差量复用（Diff）与平铺自由布局
* **差量复用**：切换方案时对比新旧 `PaneId` 集合；集合一致时直接复用原窗格与 `MpvPlayerView`（不重建、不断流），只替换房间数据。窗格结构变化时无法安全迁移 OpenGL 控件，按新结构重建播放器；**不存在跨窗格搬运 `MpvContext` 的实现**。
* **平铺自由调大小**：使用 `Dock.Avalonia` 的 `ProportionalDock` + `ProportionalDockSplitter`。未手动调过比例时，1~12 路采用固定行分布：1=[1]、2=[2]、3=[1,2]、4=[2,2]、5=[3,2]、6=[3,3]、7=[3,2,2]、8=[3,3,2]、9=[3,3,3]、10=[4,3,3]、11=[4,4,3]、12=[4,4,4]；13 路及以上才按 $\text{cols} = \lceil \sqrt{N \cdot \frac{W}{H} \cdot \frac{9}{16}} \rceil$ 计算。一旦用户手动拖拽分割线，标记 `IsCustomLayout = true` 锁定比例；增删直播间导致窗格数变化时重建，不保留空格，点击 `[⊞ 一键自适应排布]` 可随时归位。

### 6.2 B站独轮车过滤管线与防误伤规则
B站独轮车过滤管线必须前置在 `IHighlightDetector` 与弹幕落盘之前，包含三条判定规则；Twitch 只接弹幕与关键词过滤，不做独轮车判定：
1. **单用户机械复读**：同一 `SenderUid` 60 秒内发送超过 3 条清洗后相似度 $> 80\%$ 的弹幕，或连续 4 条发言间隔标准差 $< 0.5$ 秒，临时静默 10 分钟。
2. **长文本连发**：同一 `SenderUid` 30 秒内连续发送 $\ge 3$ 条长度 $> 15$ 字的弹幕（小说/文案切片特征）。
3. **同账号长句复读**：同一账号在 15 秒全局窗口内发送超过 3 条完全一致的长文本（长度 $> 10$ 字）。
* **硬性豁免**：长度 $\le 6$ 字的短情绪词（如 `666`、`？？？`、`哈哈哈`、`神回`）绝不参与同一账号复读拦截，保护高能切片检测准确度。

### 6.3 弹幕波形、全程录制打点与双模式导出
* **全程录制模式（V0.3 已实现）**：FFmpeg `-c copy -f matroska` 直接写 `.mkv`（不重编码、崩溃可播），通过 stdin `q` 优雅收尾写索引，Windows Job Object 保证宿主退出时无僵尸 ffmpeg；同名 `.jsonl` 写过滤后弹幕（首行 meta，后续 dm，`offsetMs` 相对首帧且误差 ≤2s）。后台高能检测器通过 `IHighlightEventEnricher` 给事件绑定 `RecordingSessionId` / `RecordingOffsetMs` 后入库，供 V0.4 剪辑台定位片段。
* **弹幕密度波形（V0.4 已实现）**：`WaveformService` 用 `ffmpeg -vn -ac 1 -ar 4000 -f s16le pipe:1` 流式读取 PCM，按 2000 桶取桶内峰值（/32768 归一化）并统计同桶弹幕数后归一化；缓存键为 SHA256(路径|大小|mtime|桶数)，落在 `%LocalAppData%/LiveMonitor/cache/waveforms/*.wf.json`，无音轨时降级为全 0。`ClipTimelineControl` 叠加音频波形与 5 秒桶弹幕热力，支持 1x~60x 视口缩放、平移、In/Out 拖拽与高能标记跳转。
* **ASR 触发边界（V0.5 预留）**：仅对高能推荐片段或用户在时间轴手动框选的区间截取音频送入 `IAsrProvider`（离线 `Sherpa-ONNX` SenseVoiceSmall 或用户自配 OpenAI 兼容免费 API），避免对长录播造成算力与额度浪费。
* **双版导出（V0.4 已实现）**：`ClipExporter` 的无弹幕版走 `-ss … -t … -c copy -avoid_negative_ts make_zero -movflags +faststart`（hevc 追加 `-tag:v hvc1`，关键帧对齐误差 1~3s 由 UI 提示）；有弹幕版先把伴随 `.jsonl` 转为 ASS（临时目录 + `-vf ass=danmaku.ass`），再由 `HardwareEncoderDetector` 实测可用的 `h264_nvenc` / `h264_qsv` / `h264_amf` 加速烧录，全部失败回退 `libx264`。导出进程挂 Windows Job Object，取消/失败删除残缺输出与临时目录。

### 6.4 广告处理与三层平台热更新机制
1. **广告策略**：
   * **Twitch**：V0.1 保持 `yt-dlp -j` 解析，不剔除 SSAI 广告分片，也不提供 M3U8 代理设置；后续如需去广告再评估 `streamlink --twitch-disable-ads`，不内置免费公共代理，不手写 `vaft` 反制逻辑。
   * **YouTube**：采用 `yt-dlp` 直连原始流天然跳过网页广告；支持 `WebView2` 透传 Premium/Turbo/Sub 凭证。
2. **三层免重装热更新**：
   * **海外 CLI 层**：客户端定期检查 GitHub Releases（支持镜像加速）自动替换 `tools/` 下的 `yt-dlp.exe`（Twitch/YouTube 共用）。
   * **国内插件 DLL 层**：B站/抖音/斗鱼/虎牙独立编译为 30~80 KB 的 `Provider.*.dll`，由 GitHub/Gitee 上的 `providers-manifest.json` 维护版本与 SHA256；客户端下载后先用内置公钥做 **Ed25519 数字签名校验**，通过后经 `AssemblyLoadContext` 热加载替换。
   * **签名脚本层**：针对斗鱼、抖音高频换壳的前端混淆签名，由插件内置 `Jint` 加载热更新的 `.js` 签名脚本。

### 6.5 存储安全与高并发账号风控边界
* **本地落盘与映射盘**：全程录像直接写入目标目录（默认 `%UserProfile%\Videos\LimoClip`，可全局或按直播间覆盖，允许指向 NAS 映射盘）；不做本地→云盘异步归档与 `rclone` 集成。弱网/映射盘抖动由 `-rw_timeout 15s` + 30 秒无进度判卡死兜底；强杀宿主由 Job Object 保证 ffmpeg 无残留。
* **账号风控分级**：20~30 路高并发监控时，仅焦点原画流与后台原画录制流携带绑定 Cookie，其余低画质监控小窗默认走游客态拉流，降低单账号高频并发触发风控的概率。

---

## 7. 分阶段迭代路线图（已优化顺序）

| 阶段 | 核心交付物 | 关键验证目标 |
| :--- | :--- | :--- |
| **V0.1 多屏监控室** | `Avalonia` + `Dock.Avalonia` + `LibMpv.Avalonia`；B站 + Twitch（`yt-dlp` 解析，V0.1 不去广告）；主库、窗口方案 Diff 切换、平铺自由调大小、`WebView2` 登录、屏蔽词与独轮车开关配置。 | 常规 6~9 路稳定挂机无泄漏；**20~30 路低功耗（`15fps` + 静音常开 `aid=auto`）极限压测**摸清硬解拐点。 |
| **V0.2 弹幕引擎与高能空跑** | 落地 B站 + Twitch 实时弹幕客户端、`DanmakuManager` 单例复用、关键词过滤与 **B站独轮车过滤**（Twitch 不做独轮车）、分屏独立弹幕开关与 `SlidingWindowDetector` 高能打点日志（先不录屏）。 | 验证多路弹幕海下 UI 帧率（评估是否回退 `libass`）、B站独轮车拦截准确率与高能密度阈值调优。 |
| **V0.3 录制引擎与录制中心** | 新增 `LiveMonitor.Media`：`RecordingManager`（手动/自动录制状态机、60s 开播轮询、断流分段退避、崩溃恢复）、`FfmpegFullRecorder`（`-c copy` `.mkv` + stdin `q` 优雅停录 + 首帧锚点）、`DanmakuJsonlRecorder`（同名伴随 `.jsonl`）、`RecordingHighlightTracker`（高能绑定 SessionId/Offset）、Windows Job Object 防僵尸；Desktop 录制中心（全局/独立目录、自动录制、实时指标、历史录像、删除确认）。 | 已验收：0 路播放时多路录制持续写入、停录 `.mkv` 可播（ffmpeg 解码 exit=0）、强杀宿主后 ffmpeg ≤1s 内终止、重启后未收尾会话标 Failed。**环形缓冲 / 异步归档 / 手动 MP4 已确认取消。** |
| **V0.4 剪辑台（已实现）** | Core 剪辑契约与按会话高能查询；Media/Editing：伴随 jsonl 读取、ffmpeg 探测（无 ffprobe）、流式波形 + 缓存、ASS 转换、硬编探测、双版导出、回放游标；Desktop：本地预览播放器、双波形缩放时间轴、剪辑台四区视图、主窗口第三视图「✂ 剪辑台」、录制中心「✂ 去剪辑」入口。 | 已验收：录制中心 → 剪辑台加载 → 弹幕回放 → 选区 → 双版导出（GUI 实测 <2s），471 个单元测试全绿；本地文件打开与无伴随 jsonl 降级由单测覆盖；2h 长录像与硬编真机待补测。 |
| **V0.5 ASR、平台/插件热更新与多轨 NLE** | 高能片段 ASR（`Sherpa-ONNX` + 云端 API）与语音预览；拆出国内独立插件 DLL 热更新体系并补齐抖音、斗鱼、虎牙、YouTube；评估多轨 NLE。 | 完成“监控 → 高能推荐 → 语音预览 → 剪辑导出”全链路闭环与免重装热更新验证。 |