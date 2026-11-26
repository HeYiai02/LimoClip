using System;
using System.Collections.Generic;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 「边录边剪」会话状态（纯数据，不依赖 Avalonia / 定时器 / 录制器接口）。
/// 保存当前正在写入的分片、实时进度、安全可剪截止与增量弹幕/波形游标；
/// 安全边界复用 <see cref="LiveClipSafety"/>，状态文案复用 <see cref="ClipStudioPresentationLogic"/>，
/// 波形换算复用 <see cref="LiveWaveformLogic"/>。
/// 打开/同步/结束的编排（定时器、播放器重载、UI 通知）由 <see cref="ClipStudioViewModel"/> 负责。
/// </summary>
internal sealed class LiveClipSession
{
    private float[]? _peaks;

    /// <summary>当前录制进度快照；null 表示不在 live 模式。</summary>
    public LiveRecordingInfo? Info { get; private set; }

    /// <summary>正在写入的分片文件（live 时间轴 0 点）。</summary>
    public string? PartPath { get; private set; }

    /// <summary>已读取的弹幕 jsonl 字节偏移。</summary>
    public long DanmakuOffset { get; private set; }

    /// <summary>已录制的实时时长（含仍在前端写入、尚不可安全剪裁的尾部）。</summary>
    public TimeSpan RecordedDuration { get; private set; }

    /// <summary>安全可剪截止（已录时长 - 5 秒安全缓冲）；Zero 表示暂不可剪。</summary>
    public TimeSpan SafeClipDuration { get; private set; }

    /// <summary>录制中状态条文案。</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>已提取音频峰值的截止时间（对齐 0.5s 桶网格）。</summary>
    public TimeSpan PeaksExtractedUntil { get; private set; }

    public bool IsActive => Info is not null;

    /// <summary>进入 live 模式：记录分片并重置增量游标。</summary>
    public void Start(LiveRecordingInfo info, string partPath)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);

        Info = info;
        PartPath = partPath;
        DanmakuOffset = 0;
        _peaks = null;
        PeaksExtractedUntil = TimeSpan.Zero;
        RecordedDuration = Normalize(info.RecordedDuration);
        SafeClipDuration = LiveClipSafety.ComputeSafeDuration(RecordedDuration);
        StatusText = ClipStudioPresentationLogic.BuildLiveStatusText(RecordedDuration, SafeClipDuration);
    }

    /// <summary>应用最新录制进度快照（不重置增量游标）。</summary>
    public void ApplyLatest(LiveRecordingInfo latest, string? partPath)
    {
        ArgumentNullException.ThrowIfNull(latest);

        Info = latest;
        if (!string.IsNullOrWhiteSpace(partPath))
        {
            PartPath = partPath;
        }

        RecordedDuration = Normalize(latest.RecordedDuration);
        SafeClipDuration = LiveClipSafety.ComputeSafeDuration(RecordedDuration);
        StatusText = ClipStudioPresentationLogic.BuildLiveStatusText(RecordedDuration, SafeClipDuration);
    }

    /// <summary>记录弹幕增量读取后的下一字节偏移。</summary>
    public void AdviseDanmakuOffset(long nextByteOffset) => DanmakuOffset = Math.Max(0, nextByteOffset);

    /// <summary>计算下一次安全区段波形提取窗口。</summary>
    public bool TryTakeWaveformWindow(out LiveWaveformLogic.ExtractionWindow window)
        => LiveWaveformLogic.TryComputeExtractionWindow(SafeClipDuration, PeaksExtractedUntil, out window);

    /// <summary>追加增量音频峰值（按窗口期望数量裁剪/补零）。</summary>
    public void AppendPeaks(IReadOnlyList<float> peaks, int expectedCount)
        => _peaks = LiveWaveformLogic.AppendPeaks(_peaks, peaks, expectedCount);

    /// <summary>记录已提取峰值的截止时间（单调前进）。</summary>
    public void MarkPeaksExtracted(TimeSpan until)
    {
        if (until > PeaksExtractedUntil)
        {
            PeaksExtractedUntil = until;
        }
    }

    /// <summary>按已录时长构建固定 0.5s 分辨率波形（音频峰值 + 弹幕密度）；不可用返回 null。</summary>
    public WaveformData? BuildWaveform(IReadOnlyList<RecordedDanmakuItem>? danmakuItems)
        => !IsActive || RecordedDuration <= TimeSpan.Zero
            ? null
            : LiveWaveformLogic.Build(RecordedDuration, _peaks, danmakuItems);

    /// <summary>退出 live 模式并清空全部状态。</summary>
    public void Reset()
    {
        Info = null;
        PartPath = null;
        DanmakuOffset = 0;
        RecordedDuration = TimeSpan.Zero;
        SafeClipDuration = TimeSpan.Zero;
        StatusText = string.Empty;
        PeaksExtractedUntil = TimeSpan.Zero;
        _peaks = null;
    }

    private static TimeSpan Normalize(TimeSpan duration) => duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
}
