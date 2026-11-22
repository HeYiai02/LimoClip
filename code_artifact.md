# 直播监控与自动切片应用 — 技术架构与接口设计文档（v1.3，2026-11-22）

## 1. 项目定位与核心架构决策

本项目是一个面向 Windows 桌面端的高性能**多平台直播分屏监控、弹幕高能自动切片、语音转写与轻量剪辑一体化应用**，目标覆盖国内（B站、抖音、斗鱼、虎牙）与海外（Twitch、YouTube）六大主流平台，支持 **20~30 路同屏监控**。

基于性能边界与长期维护成本，当前确立以下核心架构决策：

1. **客户端形态与渲染管线**：放弃纯 Web 及浏览器 `<video>` 渲染（规避 JS 解封装 GC 抖动、CORS 拦截、HTTP/1.1 6 路并发上限），采用 **C# (.NET 8/9) + Avalonia UI + `libmpv` (OpenGL Render API)**。通过离屏纹理合成消灭 Windows 句柄空域遮挡问题；遇到极端弹幕海时保留回退至 `libmpv` 内置 `libass` 渲染的通道。
2. **高并发（20~30 路）低开销约束**：非焦点窗口默认静音（音频解码常开 `aid=auto`，仅切 `mute`）、单实例缓存为 `demuxer-max-bytes=4M`（高清 8M / 低清 3M）、`demuxer-max-back-bytes=0`，并在高路数紧凑布局下通过滤镜将非焦点画面压至 `15fps`，结合运行时丢帧率动态降级；若 20~30 路压测证明音频解码是瓶颈，再评估退回 `aid=no`。
3. **预览流、录制流与弹幕展示解耦**：
   * 前端分屏按窗口物理尺寸拉取匹配清晰度（焦点放大时切原画），且多屏默认关闭画面弹幕渲染（支持单窗底部独立开关）。
   * 后台开启“自动切片”或“全程录制”的直播间独立拉取**原画流**，并强制维持弹幕连接，将过滤后的弹幕连同精确相对时间戳写入伴随文件（`.jsonl` / `.xml`）。
4. **广告处理边界**：YouTube 依赖 `yt-dlp` 直连底层流天然绕过绝大多数网页广告；Twitch 当前保持 **`yt-dlp -j`** 解析 HLS，不剔除服务端 SSAI 广告（V0.1 明确接受该限制）；后续如需去广告，再评估 `streamlink --twitch-disable-ads` 或自定义 M3U8 代理，不在本版范围。支持通过内置 `WebView2` 登录透传账号凭证解锁原画与会员免广告。
5. **本地落盘 + 异步归档存储**：严禁 FFmpeg 将实时录像流直接写入挂载云盘（防止网络抖动导致录屏中断或损坏）。所有实时录制一律先写本地硬盘，录像结束或切片导出后，由后台异步归档服务通过 SMB / WebDAV / `rclone` 上传至用户指定的 NAS 或云盘。
6. **主数据与方案解耦**：将“直播间主库”、“窗口布局方案”与“自动切片/录制任务方案”拆分为独立实体并通过多对多关联，配合窗口差量切换（Diff）与后台单路拉流复用（引用计数）。
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
| **录制与剪辑引擎** | FFmpeg + `CliWrap` | 环形缓冲/全程录制、无损切割（`-c copy`）、硬件加速弹幕烧录（`nvenc/qsv/amf`） |
| **语音识别（ASR）** | `Sherpa-ONNX` (SenseVoiceSmall) + OpenAI 兼容 API | 仅对高能推荐片段或框选区间进行离线/云端语音转文字 |
| **异步存储归档** | SMB / WebDAV / `rclone` (`CliWrap`) | 本地录像与成品切片完成后异步上传至 NAS 或第三方网盘 |
| **本地持久化** | SQLite (`Microsoft.Data.Sqlite`) | 主库、窗口方案、任务方案、高能打点、加密凭证与屏蔽词库（WAL 模式） |

---

## 3. 系统分层架构与解决方案目录结构

核心原则：`Core` 层不依赖任何外部框架；业务模块（录制、弹幕、ASR、归档、存储）不依赖 `Avalonia` UI 层，为未来抽离为无头服务端（NAS/Linux Daemon）保留平滑路径。

```text
LiveMonitor.sln
├── src/
│   ├── LiveMonitor.Core/                # [核心域层] 实体模型、枚举、核心接口、全局配置
│   │   ├── Entities/                    # LiveRoom, LayoutProfile, LayoutSlot, ClipTaskProfile 等
│   │   ├── Models/                      # StreamQuality, RoomStatus, DanmakuMessage, HighlightEvent, AsrSegment
│   │   └── Interfaces/                  # IPlatformProvider, IDanmakuClient, IRingBufferRecorder, IAsrProvider 等
│   │
│   ├── LiveMonitor.Platforms.Common/    # 公共解析：PlatformDefaults / JsonRead / YtDlpRunner
│   ├── LiveMonitor.Platforms.Bilibili/  # BilibiliProvider（原生 get_info / getRoomPlayInfo）
│   ├── LiveMonitor.Platforms.Twitch/    # TwitchProvider（yt-dlp -j；V0.1 不去广告）
│   ├── LiveMonitor.Platforms/           # [平台调度层] PlatformFactory 组合各 Provider
│   │   └── PlatformFactory.cs           # URL / PlatformType 路由
│   │
│   ├── LiveMonitor.Danmaku/             # [弹幕管线与分析层] 单例复用、过滤、落盘、高能检测
│   │   ├── Filters/
│   │   │   ├── KeywordFilter.cs         # 本地统一关键词屏蔽
│   │   │   └── UnicycleFilter.cs        # 独轮车自动识别过滤器（豁免 <=6 字短情绪词）
│   │   ├── Analyzers/
│   │   │   └── SlidingWindowDetector.cs # 弹幕密度突增 + 关键词加权检测
│   │   ├── DanmakuRecorder.cs           # 将过滤后弹幕按相对时间戳写入伴随 .jsonl / .xml
│   │   └── DanmakuManager.cs            # 按 LiveRoomId 单例复用连接并分发
│   │
│   ├── LiveMonitor.Media/               # [媒体引擎层] 录制、提取、双模式剪辑、波形与归档
│   │   ├── Recording/
│   │   │   ├── RecordingManager.cs      # 引用计数单路原画流复用调度器
│   │   │   ├── FfmpegRecorder.cs        # 支持 RingBuffer（环形）与 FullRecord（全程）双模式
│   │   │   └── ClipExtractor.cs         # 延迟合并 [T-N, T+M] 分片并切分对应弹幕片段
│   │   ├── Editing/
│   │   │   ├── ClipExporter.cs          # 无弹幕版(-c copy) / 有弹幕版(ASS烧录 + 硬件编码)
│   │   │   └── WaveformService.cs       # 提取音频 PCM 波形 + 弹幕密度热力波形
│   │   ├── Asr/
│   │   │   ├── SherpaOnnxAsrProvider.cs # 本地离线 SenseVoiceSmall 识别
│   │   │   └── OpenAiApiAsrProvider.cs  # 兼容 OpenAI /v1/audio/transcriptions 的免费/云端 API
│   │   └── Storage/
│   │       └── ArchiveUploader.cs       # 本地落盘后异步上传 NAS / 网盘（SMB / WebDAV / rclone）
│   │
│   ├── LiveMonitor.Data/                # [数据持久层] SQLite WAL 初始化与仓储实现
│   │   ├── SqliteConnectionFactory.cs
│   │   └── Repositories/
│   │
│   └── LiveMonitor.Desktop/             # [表现层] Avalonia 桌面端主程序（当前 V0.1 共 9 个项目；V0.4 独立插件化时再把平台项目编译为 Provider.*.dll）
│       ├── Controls/                    # MpvPlayerView, DanmakuOverlay, ClipTimelineView
│       ├── Services/                    # WorkspacePlayerManager (Diff), PerformanceMonitor, WebView2Login
│       ├── ViewModels/
│       └── Views/
│
└── tools/                               # 外部依赖目录（支持独立自动更新）
    ├── cacert.crt                       # mpv 访问 B站/HTTPS 所需 CA bundle
    ├── ffmpeg/
    │   └── ffmpeg.exe
    ├── libmpv-2.dll
    ├── mpv/
    │   └── mpv.exe
    ├── yt-dlp.exe
    └── rclone.exe                       # 可选：用于云盘异步归档
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

### 4.3 录制与自动切片任务方案（Clip Task Profile）
```csharp
namespace LiveMonitor.Core.Entities;

public enum RecordMode { RingBuffer, FullRecord }

public class ClipTaskProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public RecordMode Mode { get; set; } = RecordMode.RingBuffer; // 环形缓冲切片 或 全程录制+高能打点
    public int BeforeSeconds { get; set; } = 120;                 // 高能往前保留秒数 N
    public int AfterSeconds { get; set; } = 60;                   // 高能往后保留秒数 M
    public double DensityMultiplier { get; set; } = 3.0;          // 弹幕密度触发倍数
    public string KeywordsJson { get; set; } = "[]";
    public string LocalStagingDirectory { get; set; } = string.Empty; // 实时录制本地暂存目录（强制本地盘）
    public string? RemoteArchiveTarget { get; set; }              // 可选：异步归档目标（SMB/WebDAV/rclone 路径）
    public bool DeleteLocalAfterArchive { get; set; } = false;
    public List<ClipTaskTarget> Targets { get; set; } = [];
}

public class ClipTaskTarget
{
    public Guid ClipTaskProfileId { get; set; }
    public Guid LiveRoomId { get; set; }
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

    // V0.2 计划：弹幕客户端按 LiveRoomId 单例复用，由 DanmakuManager 统一分发。
    // IDanmakuClient CreateDanmakuClient(string roomId, string? authCookie = null);
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

### 5.3 录制、双模式导出、ASR 与异步归档接口
```csharp
namespace LiveMonitor.Core.Interfaces;

public record ClipExportOptions(
    string InputVideoPath,
    string? CompanionDanmakuPath,
    string OutputVideoPath,
    TimeSpan StartTime,
    TimeSpan EndTime,
    bool BurnDanmaku = false,              // false = 秒级无损(-c copy); true = 烧录 ASS 字幕并启用硬编
    bool PreferHardwareEncoder = true      // 自动探测 nvenc / qsv / amf
);

public record AsrTranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

public interface IMediaEditorService
{
    /// <summary>提取音频 PCM 峰值数组（归一化 0~1）</summary>
    Task<float[]> ExtractAudioWaveformAsync(string videoPath, int sampleCount, CancellationToken ct = default);

    /// <summary>读取伴随弹幕文件，按时间桶聚合生成弹幕密度热力曲线（归一化 0~1）</summary>
    Task<float[]> ExtractDanmakuDensityAsync(string danmakuFilePath, TimeSpan totalDuration, int bucketCount, CancellationToken ct = default);

    /// <summary>导出无弹幕版（无损秒切）或有弹幕版（硬编烧录）</summary>
    Task<string> ExportClipAsync(ClipExportOptions options, IProgress<double>? progress = null, CancellationToken ct = default);
}

public interface IAsrProvider
{
    string ProviderName { get; }
    /// <summary>仅对指定片段音频执行语音转写（禁止对数小时全程录像直接调用）</summary>
    Task<IReadOnlyList<AsrTranscriptSegment>> TranscribeSegmentAsync(
        string videoOrAudioPath,
        TimeSpan startTime,
        TimeSpan endTime,
        CancellationToken ct = default);
}

public interface IStorageArchiveService
{
    /// <summary>录像结束或导出完成后，异步上传至 NAS / 云盘并在校验通过后按需清理本地文件</summary>
    Task ArchiveAsync(string localFilePath, string remoteTarget, bool deleteLocalOnSuccess, CancellationToken ct = default);
}
```

---

## 6. 关键运行机制与工程边界

### 6.1 切换窗口方案时的差量复用（Diff）与平铺自由布局
* **差量复用**：切换方案时对比新旧 `PaneId` 集合；集合一致时直接复用原窗格与 `MpvPlayerView`（不重建、不断流），只替换房间数据。窗格结构变化时无法安全迁移 OpenGL 控件，按新结构重建播放器；**不存在跨窗格搬运 `MpvContext` 的实现**。
* **平铺自由调大小**：使用 `Dock.Avalonia` 的 `ProportionalDock` + `ProportionalDockSplitter`。未手动调过比例时，1~12 路采用固定行分布：1=[1]、2=[2]、3=[1,2]、4=[2,2]、5=[3,2]、6=[3,3]、7=[3,2,2]、8=[3,3,2]、9=[3,3,3]、10=[4,3,3]、11=[4,4,3]、12=[4,4,4]；13 路及以上才按 $\text{cols} = \lceil \sqrt{N \cdot \frac{W}{H} \cdot \frac{9}{16}} \rceil$ 计算。一旦用户手动拖拽分割线，标记 `IsCustomLayout = true` 锁定比例；增删直播间导致窗格数变化时重建，不保留空格，点击 `[⊞ 一键自适应排布]` 可随时归位。

### 6.2 独轮车过滤管线与防误伤规则
过滤管线必须前置在 `IHighlightDetector` 与弹幕落盘之前，包含三条判定规则：
1. **单用户机械复读**：同一 `SenderUid` 60 秒内发送超过 3 条清洗后相似度 $> 80\%$ 的弹幕，或连续 4 条发言间隔标准差 $< 0.5$ 秒，临时静默 10 分钟。
2. **长文本连发**：同一 `SenderUid` 30 秒内连续发送 $\ge 3$ 条长度 $> 15$ 字的弹幕（小说/文案切片特征）。
3. **跨账号长句复读**：15 秒全局窗口内超过 3 个不同账号发送完全一致的长文本（长度 $> 10$ 字）。
* **硬性豁免**：长度 $\le 6$ 字的短情绪词（如 `666`、`？？？`、`哈哈哈`、`神回`）绝不参与跨账号复读拦截，保护高能切片检测准确度。

### 6.3 弹幕波形、全程录制打点与双模式导出
* **弹幕密度波形**：剪辑台将音频波形与 5 秒时间桶的弹幕密度热力曲线叠加显示，即使游戏 BGM 铺满也能一眼定位高能波峰。
* **全程录制模式**：去掉 `-segment_wrap` 滚动覆盖参数，采用 `.ts` 或 `fragmented mp4` / `.mkv` 持续写入防断电损坏；后台高能检测器将 `[T-N, T+M]` 存为标记区间，在工作台时间轴高亮展示。
* **ASR 触发边界**：仅对高能推荐片段或用户在时间轴手动框选的区间截取音频送入 `IAsrProvider`（离线 `Sherpa-ONNX` SenseVoiceSmall 或用户自配 OpenAI 兼容免费 API），避免对长录播造成算力与额度浪费。
* **双版导出**：无弹幕版走 `-c copy` 秒级无损导出；有弹幕版将伴随弹幕转为 `.ass` 后调用 FFmpeg 硬件编码器（`h264_nvenc` / `h264_qsv` / `h264_amf`）加速烧录。

### 6.4 广告处理与三层平台热更新机制
1. **广告策略**：
   * **Twitch**：V0.1 保持 `yt-dlp -j` 解析，不剔除 SSAI 广告分片，也不提供 M3U8 代理设置；后续如需去广告再评估 `streamlink --twitch-disable-ads`，不内置免费公共代理，不手写 `vaft` 反制逻辑。
   * **YouTube**：采用 `yt-dlp` 直连原始流天然跳过网页广告；支持 `WebView2` 透传 Premium/Turbo/Sub 凭证。
2. **三层免重装热更新**：
   * **海外 CLI 层**：客户端定期检查 GitHub Releases（支持镜像加速）自动替换 `tools/` 下的 `yt-dlp.exe`（Twitch/YouTube 共用）。
   * **国内插件 DLL 层**：B站/抖音/斗鱼/虎牙独立编译为 30~80 KB 的 `Provider.*.dll`，由 GitHub/Gitee 上的 `providers-manifest.json` 维护版本与 SHA256；客户端下载后先用内置公钥做 **Ed25519 数字签名校验**，通过后经 `AssemblyLoadContext` 热加载替换。
   * **签名脚本层**：针对斗鱼、抖音高频换壳的前端混淆签名，由插件内置 `Jint` 加载热更新的 `.js` 签名脚本。

### 6.5 存储安全与高并发账号风控边界
* **本地先写、异步归档**：实时录像流强制落盘至本地硬盘（严禁直写挂载云盘），结束后由 `ArchiveUploader` 异步传输至 NAS/云盘。
* **账号风控分级**：20~30 路高并发监控时，仅焦点原画流与后台原画录制流携带绑定 Cookie，其余低画质监控小窗默认走游客态拉流，降低单账号高频并发触发风控的概率。

---

## 7. 分阶段迭代路线图（已优化顺序）

| 阶段 | 核心交付物 | 关键验证目标 |
| :--- | :--- | :--- |
| **V0.1 多屏监控室** | `Avalonia` + `Dock.Avalonia` + `LibMpv.Avalonia`；B站 + Twitch（`yt-dlp` 解析，V0.1 不去广告）；主库、窗口方案 Diff 切换、平铺自由调大小、`WebView2` 登录、屏蔽词与独轮车开关配置。 | 常规 6~9 路稳定挂机无泄漏；**20~30 路低功耗（`15fps` + 静音常开 `aid=auto`）极限压测**摸清硬解拐点。 |
| **V0.2 弹幕引擎与高能空跑** | 落地 B站 + Twitch 实时弹幕客户端、`DanmakuManager` 单例复用、关键词+独轮车过滤管线、分屏独立弹幕开关与 `SlidingWindowDetector` 高能打点日志（先不录屏）。 | 验证多路弹幕海下 UI 帧率（评估是否回退 `libass`）、独轮车拦截准确率与高能密度阈值调优。 |
| **V0.3 录制引擎与异步归档** | 落地 `RecordingManager`（引用计数单路复用）、`FfmpegRecorder`（环形缓冲 + 全程录制）、伴随弹幕文件落盘、高能自动切片提取以及本地→NAS/云盘异步归档。 | 验证预览流与原画录制解耦、全程录制抗断电完整性、磁盘 I/O 与无僵尸进程清理。 |
| **V0.4 剪辑台、ASR 与全平台热更新** | 落地音轨+弹幕密度双波形时间轴、双模式导出（无损无弹幕 / 硬编有弹幕）、高能片段 ASR（`Sherpa-ONNX` + 云端 API）；拆出国内独立插件 DLL 热更新体系并补齐抖音、斗鱼、虎牙、YouTube。 | 完成“监控 → 弹幕高能推荐 → 语音预览 → 双版导出 → 云端归档”全链路闭环与免重装热更新验证。 |