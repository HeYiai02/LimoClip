# LimoClip V0.4（剪辑工作台）— 详细设计实施文档

> **版本定位**：本期直接消费 V0.3 产出的全程 `.mkv` 录像、同名伴随 `.jsonl` 弹幕与 SQLite `HighlightEvents` 高能打点，完成「监控 → 录制 → 双波形挑片段 → 有/无弹幕双版导出 `.mp4`」的核心业务闭环。ASR 语音转写顺延至后续版本，但剪辑台本身的预览、双波形、时间轴缩放、高能跳转与双版导出能力全部完整落地。

---

## 1. V0.4 范围边界与验收目标

### 1.1 明确边界（In-Scope / Out-of-Scope）

* **本期包含（In-Scope）**：
  * **主窗口顶级视图扩展**：在 `MainWindow` 顶部栏新增第三个视图 **`[✂ 剪辑台]`**；同时在「录制中心 → 历史录像」每条已完成记录上增加 **`[去剪辑]`** 快捷入口，一键载入该场次。
  * **独立 `ClipStudioViewModel` 与 `ClipStudioView`**：严禁继续向已达 70KB 的 `MainWindowViewModel` 堆叠业务逻辑；剪辑台作为独立工作区模块运行，仅共享仓储与服务实例。
  * **本地预览播放器与同步弹幕回放**：复用 `libmpv` 封装本地文件播放模式（支持精确拖动 `hr-seek=yes`、暂停/播放、倍速 `0.5x~2.0x`、逐秒微调 `±1s / ±5s`、快捷键 `Space / J / K / L / I / O`），并按当前播放时间戳二分检索 `.jsonl` 同步驱动 `DanmakuOverlay` 回放弹幕（支持开关预览）。
  * **可缩放双波形时间轴（`ClipTimelineControl`）**：
    * **音频 PCM 波形**：流式调用 `ffmpeg.exe` 提取低采样单声道峰值曲线（支持本地缓存，二次打开秒加载）。
    * **弹幕密度热力曲线**：解析伴随 `.jsonl` 按时间桶聚合归一化，与音频波形叠加显示，一眼定位高能波峰。
    * **高能区间高亮与跳转**：读取该 `RecordingSessionId` 关联的所有 `HighlightEvents`，在时间轴上高亮标记推荐区间 `[offset - N, offset + M]`，左侧同步列出高能点列表，点击即可将播放头与入出点一键定位到该区间。
    * **缩放与精确剪裁**：支持鼠标滚轮缩放时间轴（`1x ~ 60x`）、水平滚动平移、拖拽入点（In）/ 出点（Out）滑块及输入框精确微调。
  * **有/无弹幕双版导出 `.mp4`（`ClipExporter`）**：
    * **无弹幕版**：`ffmpeg -c copy` 秒级无损切割导出 `.mp4`（自动处理 `-avoid_negative_ts make_zero` 与 `-movflags +faststart`）。
    * **有弹幕版**：将选定区间 `[Start, End]` 内的 `.jsonl` 弹幕转换为标准 `.ass` 滚动字幕，自动探测本机可用硬件编码器（`h264_nvenc` / `h264_qsv` / `h264_amf`，均不可用时回退 `libx264`）烧录导出 `.mp4`。
    * **一键双版导出与队列**：支持单选「仅无弹幕」「仅有弹幕」或「同时导出两版」，提供实时进度条、剩余耗时预估、取消导出（复用 `ProcessJobGuard` 防残留）与「打开输出目录」。
  * **兼容无弹幕外部视频**：允许用户通过文件选择器打开任意本地 `.mkv` / `.mp4` 文件（无伴随 `.jsonl` 时优雅降级为仅音频波形 + 无弹幕版导出）。
* **本期明确不做（Out-of-Scope，顺延至后续版本）**：
  * 高能片段 ASR 语音转写（`Sherpa-ONNX` / OpenAI 兼容 API）——暂缓接入，在左侧高能列表预留扩展位。
  * YouTube / 抖音 / 斗鱼 / 虎牙平台接入与 `AssemblyLoadContext` + Ed25519 插件热更新（V0.5）。
  * 多轨非线性剪辑（转场特效、贴图、多段拼接、BGM 混音）。

### 1.2 验收标准

1. **大文件加载与时间轴交互**：
   * 载入一段 $\ge 2$ 小时的 `.mkv` 录像与伴随 `.jsonl`，音频波形流式提取期间 UI 不卡顿且内存增量 $< 100\text{MB}$；关闭后再次打开同一录像，命中缓存秒级呈现双波形。
   * 在 2 小时时间轴上滚轮放大至单屏 3 分钟视野，拖拽入点/出点及播放头顺滑无卡顿；点击左侧任意高能事件，播放头与入出点精准跳转至推荐区间。
2. **预览与弹幕同步**：
   * 播放 `.mkv` 时开启弹幕预览，拖动进度条至任意位置，画面弹幕在 1 秒内与视频内容对齐，无旧位置残影堆积。
3. **双版导出质量与稳定性**：
   * 导出一段 2 分钟的**无弹幕版 `.mp4`**：耗时 $\le 5$ 秒，Windows 自带播放器、剪映、Premiere Pro 均可正常打开，无开头黑屏或音画不同步。
   * 导出同一段 2 分钟的**有弹幕版 `.mp4`**：硬件编码器探测生效（日志确认命中 `nvenc`/`qsv`/`amf` 或回退 `libx264`），画面弹幕清晰流畅、无重叠乱码、时间戳与视频吻合。
   * 导出中途点击「取消」，`ffmpeg.exe` 在 1 秒内终止并自动清理未完成的临时 `.mp4` 与 `.ass` 文件。

---

## 2. V0.4 编码前设计评审修正项（防坑指南）

| 序号 | 潜在隐患 | 强制修正方案 |
| :--- | :--- | :--- |
| **1** | **4 小时录像提取音频波形撑爆内存**：若直接将 48kHz 立体声 PCM 全量读入内存，4 小时录像会占用超过 2.7GB 内存导致 OOM。 | 使用 FFmpeg 流式管道 `-vn -ac 1 -ar 4000 -f s16le pipe:1`，按固定采样步长边读边算每个时间桶的绝对峰值，全程内存占用 $< 2\text{MB}$；结果缓存为 `%LocalAppData%/LiveMonitor/cache/waveforms/{hash}.wf.json`，二次打开秒读。 |
| **2** | **FFmpeg `-vf ass=` Windows 路径转义灾难**：Windows 路径包含盘符冒号 `C:`、反斜杠 `\`、空格与中文（如 `#1 晚风游戏室`），直接拼进 `-vf ass=...` 极易触发滤镜语法解析失败。 | **彻底绕过转义**：生成临时 `.ass` 文件到 `%TEMP%\LimoClip_Export_{guid}\danmaku.ass`，启动导出 FFmpeg 时将 `ProcessStartInfo.WorkingDirectory` 设为该临时目录，滤镜参数直接写死为 `-vf ass=danmaku.ass`，100% 免疫中文、空格与盘符冒号问题。 |
| **3** | **硬件编码器“假支持”导致导出直接报错**：`ffmpeg -encoders` 只列出编译时启用的编码器，不代表当前机器安装了对应显卡或驱动（例如无 N 卡也会列出 `h264_nvenc`）。 | `HardwareEncoderDetector` 按优先级 `h264_nvenc` $\rightarrow$ `h264_qsv` $\rightarrow$ `h264_amf` 执行一次 0.1 秒的空输入实测探测（`-f lavfi -i color=c=black:s=256x256:d=0.1 -frames:v 1 -c:v {enc} -f null -`），将首个返回 `ExitCode == 0` 的编码器缓存至本次会话，全失败则回退 `libx264`。 |
| **4** | **无损切割（`-c copy`）首帧花屏与兼容问题**：`-ss` 放在 `-i` 之前会对齐到最近的关键帧，可能产生负时间戳导致播放器卡死；此外 B站原画常为 HEVC (H.265)，封装进 `.mp4` 若缺 `hvc1` 标签，部分剪辑软件和系统播放器会黑屏无画面。 | ① 无损切割强制加 `-avoid_negative_ts make_zero -movflags +faststart`；② 导出前用 `ffprobe`（或 `ffmpeg -i`）检测原视频编码，若为 `hevc` 则自动追加 `-tag:v hvc1`；③ UI 明确提示“无损切割会按最近关键帧对齐（误差 1~3 秒），极速零画质损失”。 |
| **5** | **长时间录像在时间轴上无法精细操作**：4 小时录像在 1200px 宽的控件上，1 像素代表 12 秒，根本拖不准入出点。 | `ClipTimelineControl` 必须内置**视口缩放（`ZoomFactor` 1x~60x）与水平平移（`ViewportStartRatio`）**，滚轮以鼠标所在时间点为锚点缩放，配合上方或下方细滚动条与 `±1s` 微调按钮。 |
| **6** | **本地剪辑播放与直播监控 `libmpv` 参数冲突**：V0.1 的 `MpvPlayerView` 针对直播流配置了 `demuxer-max-back-bytes=0` 与低延迟，用于本地 `.mkv` 拖动会频繁重新解码卡顿，播完还会直接黑屏。 | 为剪辑台增加本地播放模式配置（或独立轻封装 `LocalVideoPlayerView`）：设置 `hr-seek=yes`（精准定位）、`keep-open=yes`（播到末尾停在最后一帧不黑屏）、`demuxer-max-back-bytes=32M`（向后拖拽秒响应）、`aid=auto`、`mute=no`。 |
| **7** | **编码失败或取消残留垃圾文件**：有弹幕版导出会生成临时 `.ass` 和写了一半的 `.mp4`。 | 导出任务统一挂入 `ProcessJobGuard`，`finally` 块清理临时 `.ass` 目录；若用户取消或非零退出，自动删除未完成的目标 `.mp4`。 |

---

## 3. 剪辑台界面布局与交互规范

在 `MainWindow` 顶部栏形成三视图导航：**`[📺 监控工作区]`** | **`[⏺ 录制中心]`** | **`[✂ 剪辑台]`**。

剪辑台采用经典的**「左列表 + 中预览 + 右导出 + 底时间轴」**四区布局：

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ [📺 监控工作区]  [⏺ 录制中心]  [✂ 剪辑台*]                      [⚙ 账号与设置] │
├──────────────────┬───────────────────────────────────┬───────────────────────┤
│ 场次与高能导航    │        本地预览播放器 (libmpv)      │      切片导出面板      │
│ (260px)          │   + 同步 DanmakuOverlay 弹幕回放   │      (280px)          │
│                  │                                   │                       │
│ [切换录像场次 ▼]  │       (16:9 自适应视频预览区)      │  当前区间：            │
│ [打开本地文件...] │                                   │  入点 01:12:05 [设入点]│
│                  │                                   │  出点 01:14:35 [设出点]│
│ ── 高能推荐 (6) ──│                                   │  时长 02:30            │
│ 🔥 00:24:15      │  [◀-5s] [▶/⏸] [+5s▶]  01:12:30   │                       │
│    速率 8.4/s    │  [1.0x▼] [🔊━━━] [💬弹幕回放 ✓]    │  导出模式：            │
│ 🔥 01:12:35  ◀选中│                                   │  ( ) 仅无弹幕 (秒切)   │
│    速率 12.1/s   │                                   │  ( ) 仅有弹幕 (硬编)   │
│                  │                                   │  (●) 同时导出两版      │
│ (预留: V0.5 ASR) │                                   │                       │
│                  │                                   │  编码器: NVENC (自动)  │
│                  │                                   │  输出目录: [...浏览]   │
│                  │                                   │  文件名: [自动填充]    │
│                  │                                   │  [   开始导出 MP4   ] │
│                  │                                   │  ── 导出进度/队列 ──   │
├──────────────────┴───────────────────────────────────┴───────────────────────┤
│ 双波形时间轴 (高度 180px，支持滚轮缩放 1x~60x + 水平拖动)                        │
│  时间刻度:  01:10:00       01:12:00       01:14:00       01:16:00            │
│  高能区间:           [░░░░░🔥高能区░░░░░]                                     │
│  弹幕热力:  ____/\______/‾‾‾‾‾\___/\_______  (半透明暖橙色面积曲线)            │
│  音频波形:  ||||||/||||||\|||||||||||||||    (半透明青蓝色柱状/包络波形)       │
│  选区滑块:            ┃◀═══选定切片═══▶┃  (蓝色半透明选区 + 红色播放头)        │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 快捷键约定（焦点在剪辑台时生效）
* `Space`：播放 / 暂停
* `←` / `→`：后退 / 前进 5 秒（按住 `Shift` 为 1 秒）
* `I`：将当前播放位置设为**入点（In）**
* `O`：将当前播放位置设为**出点（Out）**
* `滚轮`（悬停时间轴）：以光标位置为中心缩放时间轴

---

## 4. 新增目录结构与 Core 层契约

### 4.1 新增文件规划

```text
src/
├── LiveMonitor.Core/
│   ├── Models/
│   │   ├── RecordedDanmakuItem.cs       # 从 .jsonl 反序列化的轻量弹幕项 (OffsetMs, Text, IsSticker, IsGift)
│   │   ├── WaveformData.cs              # 音频峰值数组 + 弹幕密度数组 + 总时长
│   │   ├── ClipExportRequest.cs         # 导出请求（输入、入出点、模式、弹幕样式、输出路径）
│   │   └── ClipExportProgress.cs        # 导出阶段（无弹幕/有弹幕）、百分比、速度、错误信息
│   └── Interfaces/
│       ├── IWaveformService.cs
│       ├── IDanmakuAssConverter.cs
│       ├── IHardwareEncoderDetector.cs
│       └── IClipExporter.cs
│
├── LiveMonitor.Media/
│   └── Editing/                         # [新建目录，复用 Recording 中的 FfmpegLocator / ProcessJobGuard]
│       ├── DanmakuJsonlReader.cs        # 容错逐行读取 .jsonl（忽略截断尾行）
│       ├── WaveformService.cs           # 流式音频 PCM 提取 + 弹幕分桶 + 本地 JSON 缓存
│       ├── DanmakuAssConverter.cs       # .jsonl 区间切片 -> 碰撞分配滚动轨道 -> .ass 字幕文件
│       ├── HardwareEncoderDetector.cs   # 实测探测 nvenc / qsv / amf / libx264 并缓存
│       ├── VideoProbeService.cs         # 检测源视频宽高、帧率、总时长与编码格式 (h264/hevc)
│       └── ClipExporter.cs              # 双版导出调度（无损 -c copy + 硬编烧录 ASS）
│
└── LiveMonitor.Desktop/
    ├── Controls/
    │   └── ClipTimelineControl.cs       # 基于 DrawingContext 自绘的高性能可缩放双波形时间轴
    ├── ViewModels/
    │   ├── ClipStudioViewModel.cs       # 剪辑台主 VM（与 MainWindowViewModel 解耦）
    │   ├── ClipHighlightItemViewModel.cs
    │   └── ClipExportTaskViewModel.cs
    └── Views/
        └── ClipStudioView.axaml
```

### 4.2 Core 层核心契约

```csharp
namespace LiveMonitor.Core.Models;

public enum ClipExportMode
{
    CleanOnly = 0,       // 仅无弹幕版（-c copy 秒级无损）
    BurnedDanmaku = 1,   // 仅有弹幕版（ASS 烧录 + 硬件编码）
    Both = 2             // 同时导出两版（自动附加 _clean / _danmaku 后缀）
}

public record RecordedDanmakuItem(
    long OffsetMs,
    string SenderName,
    string Text,
    bool IsSticker,
    bool IsGift
);

public record VideoMetadata(
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    string VideoCodec    // 如 "h264", "hevc"
);

public record WaveformData(
    TimeSpan Duration,
    float[] AudioPeaks,      // 归一化 [0, 1]，固定 2000~4000 采样点
    float[] DanmakuDensity,  // 归一化 [0, 1]，按桶聚合
    int MaxDanmakuPerBucket  // 原始峰值条数，供悬浮 Tooltip 显示
);

public record DanmakuAssStyle(
    int PlayResX = 1920,
    int PlayResY = 1080,
    string FontName = "Microsoft YaHei",
    int FontSize = 42,
    double ScrollDurationSeconds = 10.0,
    double DisplayAreaRatio = 0.65,   // 弹幕仅占画面顶部 65%，不遮挡底部字幕/UI
    int OpacityPercent = 85,          // 透明度 85%
    bool IncludeStickersAsText = true // 大表情是否以 [表情名] 文本形式烧录
);

public record ClipExportRequest(
    string InputVideoPath,
    string? CompanionDanmakuPath,
    string OutputDirectory,
    string OutputBaseName,
    TimeSpan StartTime,
    TimeSpan EndTime,
    ClipExportMode Mode,
    DanmakuAssStyle? AssStyle = null
);

public record ClipExportProgress(
    string StageLabel,       // 如 "正在无损导出无弹幕版..." / "正在硬件编码烧录弹幕..."
    double Percentage,       // 0 ~ 100
    double? SpeedMultiplier  // 如 4.2x
);
```

---

## 5. 核心 C# 代码实现参考

### 5.1 伴随弹幕转 ASS 字幕转换器 (`LiveMonitor.Media/Editing/DanmakuAssConverter.cs`)

核心要点：严格截取 `[start, end]` 并把时间戳平移至 `0` 起步、采用与桌面端一致的轨道碰撞防重叠算法、自动转义 ASS 控制符（`{}`、`\`、换行）、支持通过 `DisplayAreaRatio` 限制弹幕只在画面上方区域滚动。

```csharp
using System.Globalization;
using System.Text;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

public sealed class DanmakuAssConverter
{
    private readonly record struct TrackState(double FreeAtSec, double LastEndSec, double LastWidthPx);

    /// <summary>
    /// 将指定区间 [start, end] 的弹幕转换为标准 .ass 文件
    /// </summary>
    public async Task<int> ConvertToAssAsync(
        IReadOnlyList<RecordedDanmakuItem> allDanmaku,
        TimeSpan start,
        TimeSpan end,
        string outputAssPath,
        DanmakuAssStyle? style = null,
        CancellationToken ct = default)
    {
        style ??= new DanmakuAssStyle();
        long startMs = (long)start.TotalMilliseconds;
        long endMs = (long)end.TotalMilliseconds;

        var dir = Path.GetDirectoryName(outputAssPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // ASS Alpha 通道：00=完全不透明，FF=完全透明
        int alpha = Math.Clamp((int)Math.Round((100 - style.OpacityPercent) / 100.0 * 255), 0, 255);
        string primaryColor = $"&H{alpha:X2}FFFFFF";
        string outlineColor = $"&H{alpha:X2}000000";

        int lineHeight = style.FontSize + 8;
        int maxTopAreaPx = (int)(style.PlayResY * Math.Clamp(style.DisplayAreaRatio, 0.25, 0.95));
        int trackCount = Math.Max(3, maxTopAreaPx / lineHeight);
        var tracks = new TrackState[trackCount];
        for (int i = 0; i < trackCount; i++)
        {
            tracks[i] = new TrackState(double.NegativeInfinity, double.NegativeInfinity, 0);
        }

        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("[Script Info]");
        sb.AppendLine("Title: LimoClip Exported Danmaku");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {style.PlayResX}");
        sb.AppendLine($"PlayResY: {style.PlayResY}");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine($"Style: R2L,{style.FontName},{style.FontSize},{primaryColor},&H000000FF,{outlineColor},&H80000000,-1,0,0,0,100,100,0,0,1,2,0,7,0,0,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        int written = 0;
        double durationSec = Math.Max(3.0, style.ScrollDurationSeconds);

        foreach (var dm in allDanmaku)
        {
            ct.ThrowIfCancellationRequested();
            if (dm.OffsetMs < startMs || dm.OffsetMs > endMs) continue;
            if (dm.IsSticker && !style.IncludeStickersAsText) continue;

            var cleanText = EscapeAssText(dm.Text);
            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            double relStartSec = (dm.OffsetMs - startMs) / 1000.0;
            double relEndSec = relStartSec + durationSec;

            // 估算文字渲染宽度（中文约 1.0 倍字号，ASCII 约 0.55 倍字号）
            double textWidthPx = EstimateTextWidthPx(cleanText, style.FontSize);
            double totalTravelPx = style.PlayResX + textWidthPx;
            double speedPxPerSec = totalTravelPx / durationSec;

            // 寻找不发生追尾碰撞的最优轨道
            int chosenTrack = FindBestTrack(tracks, relStartSec, relEndSec, textWidthPx, speedPxPerSec, style.PlayResX);
            if (chosenTrack < 0) continue; // 极端弹幕海下丢弃溢出条，防画面糊成一团

            // 轨道在当前弹幕尾部完全进入屏幕（+24px 间距）后才可容纳下一条
            double tailClearTime = relStartSec + (textWidthPx + 24.0) / speedPxPerSec;
            tracks[chosenTrack] = new TrackState(tailClearTime, relEndSec, textWidthPx);

            int y = 12 + chosenTrack * lineHeight;
            int startX = style.PlayResX;
            int endX = -(int)Math.Ceiling(textWidthPx);

            sb.Append("Dialogue: 0,")
              .Append(FormatAssTime(relStartSec)).Append(',')
              .Append(FormatAssTime(relEndSec)).Append(",R2L,,0,0,0,,")
              .Append($@"{{\move({startX},{y},{endX},{y})}}")
              .AppendLine(cleanText);

            written++;
        }

        await File.WriteAllTextAsync(outputAssPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct)
            .ConfigureAwait(false);
        return written;
    }

    private static int FindBestTrack(
        TrackState[] tracks,
        double startSec,
        double endSec,
        double widthPx,
        double speedPxPerSec,
        int playResX)
    {
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            // 条件 1：前一条弹幕尾部已完全离开右边界
            if (startSec < t.FreeAtSec) continue;

            // 条件 2：当前弹幕速度更快时，确保在前一条离场前不会追尾撞上
            double reachLeftEdgeTime = startSec + playResX / speedPxPerSec;
            if (reachLeftEdgeTime < t.LastEndSec) continue;

            return i;
        }
        return -1;
    }

    private static double EstimateTextWidthPx(string text, int fontSize)
    {
        double units = 0;
        foreach (char c in text)
        {
            units += c <= 0x7F ? 0.56 : 1.02;
        }
        return Math.Max(fontSize, units * fontSize);
    }

    private static string EscapeAssText(string raw)
    {
        return raw.Replace("\r", " ")
                  .Replace("\n", " ")
                  .Replace("\\", "\\\\")
                  .Replace("{", "｛")
                  .Replace("}", "｝")
                  .Trim();
    }

    private static string FormatAssTime(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        int cs = Math.Min(99, ts.Milliseconds / 10);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:D2}:{2:D2}.{3:D2}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            cs);
    }
}
```

### 5.2 硬件编码探测器与双版导出器 (`LiveMonitor.Media/Editing/ClipExporter.cs`)

核心要点：实测探测硬件编码器并缓存、无损导出自动兼容 `hevc` (`-tag:v hvc1`) 与 `faststart`、有弹幕版使用 `WorkingDirectory` 彻底避开 Windows ASS 路径转义坑、全程挂入 `ProcessJobGuard` 并在取消时清理残余文件。

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Media.Editing;

public sealed class HardwareEncoderDetector
{
    private readonly string _ffmpegPath;
    private string? _cachedEncoder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly (string Name, string[] ExtraArgs)[] Candidates =
    [
        ("h264_nvenc", ["-preset", "p4", "-cq", "20"]),
        ("h264_qsv",   ["-preset", "medium", "-global_quality", "20"]),
        ("h264_amf",   ["-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20"]),
        ("libx264",    ["-preset", "veryfast", "-crf", "20"])
    ];

    public HardwareEncoderDetector(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    public async Task<(string EncoderName, string[] ExtraArgs)> DetectBestAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedEncoder is not null)
            {
                return Candidates.First(c => c.Name == _cachedEncoder);
            }

            foreach (var candidate in Candidates)
            {
                if (candidate.Name == "libx264")
                {
                    _cachedEncoder = candidate.Name;
                    return candidate;
                }

                if (await ProbeEncoderAsync(candidate.Name, ct).ConfigureAwait(false))
                {
                    _cachedEncoder = candidate.Name;
                    return candidate;
                }
            }

            _cachedEncoder = "libx264";
            return Candidates[^1];
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> ProbeEncoderAsync(string encoderName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("color=c=black:s=256x256:d=0.1");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(encoderName);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ClipExporter
{
    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;
    private readonly HardwareEncoderDetector _encoderDetector;
    private readonly DanmakuAssConverter _assConverter;

    public ClipExporter(
        string ffmpegPath,
        ProcessJobGuard? jobGuard,
        HardwareEncoderDetector encoderDetector,
        DanmakuAssConverter assConverter)
    {
        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
        _encoderDetector = encoderDetector;
        _assConverter = assConverter;
    }

    public async Task<IReadOnlyList<string>> ExportAsync(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
        string sourceVideoCodec,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request.EndTime <= request.StartTime)
            throw new ArgumentException("出点时间必须大于入点时间。");

        Directory.CreateDirectory(request.OutputDirectory);
        var outputs = new List<string>();
        var clipDuration = request.EndTime - request.StartTime;

        bool needClean = request.Mode is ClipExportMode.CleanOnly or ClipExportMode.Both;
        bool needBurned = request.Mode is ClipExportMode.BurnedDanmaku or ClipExportMode.Both;

        if (needClean)
        {
            string suffix = request.Mode == ClipExportMode.Both ? "_clean" : "";
            string outPath = Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{suffix}.mp4");
            await ExportCleanLosslessAsync(request.InputVideoPath, outPath, request.StartTime, clipDuration, sourceVideoCodec, progress, ct)
                .ConfigureAwait(false);
            outputs.Add(outPath);
        }

        if (needBurned)
        {
            string suffix = request.Mode == ClipExportMode.Both ? "_danmaku" : "";
            string outPath = Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{suffix}.mp4");
            await ExportWithBurnedDanmakuAsync(request, danmakuItems ?? [], outPath, clipDuration, progress, ct)
                .ConfigureAwait(false);
            outputs.Add(outPath);
        }

        return outputs;
    }

    private async Task ExportCleanLosslessAsync(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        string sourceCodec,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-ss", FormatSec(start),
            "-i", inputPath,
            "-t", FormatSec(duration),
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart"
        };

        if (sourceCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-tag:v");
            args.Add("hvc1");
        }

        args.AddRange(["-progress", "pipe:1", "-y", outputPath]);

        await RunFfmpegExportAsync(args, null, outputPath, duration, "正在无损导出无弹幕版...", progress, ct)
            .ConfigureAwait(false);
    }

    private async Task ExportWithBurnedDanmakuAsync(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem> danmakuItems,
        string outputPath,
        TimeSpan duration,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"LimoClip_Export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string assPath = Path.Combine(tempDir, "danmaku.ass");

        try
        {
            progress?.Report(new ClipExportProgress("正在生成 ASS 弹幕字幕...", 2, null));
            await _assConverter.ConvertToAssAsync(danmakuItems, request.StartTime, request.EndTime, assPath, request.AssStyle, ct)
                .ConfigureAwait(false);

            var (encoderName, extraArgs) = await _encoderDetector.DetectBestAsync(ct).ConfigureAwait(false);

            var args = new List<string>
            {
                "-hide_banner", "-nostats", "-loglevel", "error",
                "-ss", FormatSec(request.StartTime),
                "-i", request.InputVideoPath,
                "-t", FormatSec(duration),
                // 通过 WorkingDirectory = tempDir，直接传相对文件名，彻底避开 Windows 路径转义问题
                "-vf", "ass=danmaku.ass",
                "-c:v", encoderName
            };
            args.AddRange(extraArgs);
            args.AddRange([
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                "-progress", "pipe:1",
                "-y", outputPath
            ]);

            await RunFfmpegExportAsync(args, tempDir, outputPath, duration, $"正在烧录弹幕导出 ({encoderName})...", progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private async Task RunFfmpegExportAsync(
        List<string> args,
        string? workingDir,
        string outputPath,
        TimeSpan targetDuration,
        string stageLabel,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            WorkingDirectory = workingDir ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException("无法启动 ffmpeg.exe 导出进程。");
        _jobGuard?.AddProcess(proc);

        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            double totalUs = Math.Max(1.0, targetDuration.TotalMicroseconds);
            double? lastSpeed = null;

            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.AsSpan(0, eq).Trim();
                var val = line.AsSpan(eq + 1).Trim();

                if (key.SequenceEqual("speed"))
                {
                    var s = val.ToString().TrimEnd('x', 'X');
                    if (double.TryParse(s, CultureInfo.InvariantCulture, out var spd)) lastSpeed = spd;
                }
                else if (key.SequenceEqual("out_time_us") && long.TryParse(val, out var us) && us >= 0)
                {
                    double pct = Math.Clamp(us / totalUs * 100.0, 0, 99.5);
                    progress?.Report(new ClipExportProgress(stageLabel, pct, lastSpeed));
                }
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var err = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"FFmpeg 导出失败 (ExitCode={proc.ExitCode}): {err}");
            }

            progress?.Report(new ClipExportProgress(stageLabel, 100, lastSpeed));
        }
        catch
        {
            // 取消或报错时清理残缺文件
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw;
        }
    }

    private static string FormatSec(TimeSpan ts)
        => ts.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
```

### 5.3 流式双波形提取要点 (`LiveMonitor.Media/Editing/WaveformService.cs`)

* **音频波形**：启动 `ffmpeg -hide_banner -loglevel error -i "{videoPath}" -vn -ac 1 -ar 4000 -f s16le pipe:1`，按目标桶数（例如 `2000` 个桶）计算每个桶对应的样本数 `samplesPerBucket = (int)(durationSec * 4000 / bucketCount)`，用 `byte[8192]` 缓冲区循环流式读取，仅记录每个桶的 `maxAbsSample / 32768f`，读完即得归一化 `float[2000]`。
* **弹幕密度曲线**：直接遍历 `DanmakuJsonlReader.ReadAllAsync(danmakuPath)`，按 `bucketIdx = (int)(item.OffsetMs / bucketMs)` 累加计数并除以最大值归一化为 `float[bucketCount]`。
* **缓存策略**：以 `SHA256(filePath + fileSize + lastWriteTimeUtc)` 为键写入 `%LocalAppData%/LiveMonitor/cache/waveforms/`，下次打开同一录像直接秒级还原。

---

## 6. V0.4 6 步开发执行清单

- [ ] **Step 1：补齐 Core 剪辑契约与 `VideoProbeService` / `DanmakuJsonlReader`（1 天）**
  * 在 `Core` 新增 `ClipExportMode`、`RecordedDanmakuItem`、`VideoMetadata`、`WaveformData`、`DanmakuAssStyle`、`ClipExportRequest`、`ClipExportProgress` 及对应接口；在 `LiveMonitor.Media/Editing` 实现容错 `.jsonl` 读取与视频元信息探测，补齐单测。
- [ ] **Step 2：落地 `WaveformService`（流式音频波形 + 弹幕密度曲线 + 本地缓存）（1 天）**
  * 实现低内存流式 PCM 峰值提取、弹幕分桶聚合与本地缓存读写；编写单测校验归一化与边界处理（含空弹幕文件降级）。
- [ ] **Step 3：落地 `DanmakuAssConverter`、`HardwareEncoderDetector` 与 `ClipExporter`（1.5 天）**
  * 完成 `.jsonl` → `.ass` 轨道防重叠转换、实测硬件编码探测与无损/烧录双版导出；在 `LiveMonitor.Media.Tests` 补齐 ASS 时间偏移、特殊字符转义及真实 FFmpeg 端到端双版导出测试。
- [ ] **Step 4：封装本地精确预览播放器与同步弹幕回放（1 天）**
  * 为剪辑台配置 `hr-seek=yes` + `keep-open=yes` 本地 `libmpv` 播放控制（播放/暂停/倍速/进度同步），并基于二分查找按 `TimePos` 驱动 `DanmakuOverlay` 实现同步弹幕回放。
- [ ] **Step 5：自绘可缩放双波形时间轴控件 `ClipTimelineControl`（1.5 天）**
  * 基于 `DrawingContext` 绘制时间刻度、高能推荐区间底色、音频波形、弹幕热力面积图、蓝色选区遮罩、入/出点拖拽手柄与红色播放头；实现滚轮锚点缩放（`1x ~ 60x`）与水平拖拽平移。
- [ ] **Step 6：搭建 `ClipStudioView`、接入主窗口导航与端到端验收（1.5 天）**
  * 在 `MainWindow` 接入 `[✂ 剪辑台]` 视图与「录制中心 → 去剪辑」直达按钮；串联左侧高能点跳转、快捷键（`Space / I / O / ← / →`）与右侧双版导出队列；用 V0.3 真实录制的 `.mkv` + `.jsonl` 完成全链路验收。