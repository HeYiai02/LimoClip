namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 剪辑台预览播放器的位置推进纯逻辑（V0.7.5 抽离）：
/// 普通文件按采样位置推进，并在 keep-open 末尾前主动收口为「播放完成」；
/// 边录边剪时文件仍在前端增长，mpv 的 EOF 只代表当前已写入尾部，不能当作完成，
/// 同时把位置钳在「安全可剪截止 - 100ms」内，避免播放头进入尚未安全写入的尾部。
/// 只做时间换算与判定，不读取 mpv、不操作控件，便于单元测试。
/// </summary>
internal static class ClipPlaybackPositionLogic
{
    /// <summary>边录边剪的安全余量：允许到安全截止前 100ms，避免读到尚未写入文件的尾部。</summary>
    public static readonly TimeSpan LiveSafetyMargin = TimeSpan.FromMilliseconds(100);

    /// <summary>位置变化小于 1ms 视为未推进，避免每 100ms 轮询都触发一次界面刷新。</summary>
    public static readonly TimeSpan PositionEpsilon = TimeSpan.FromMilliseconds(1);

    /// <summary>keep-open 到末尾时 mpv 不会自动换状态：距末尾 80ms 内由我们自己收口。</summary>
    public static readonly TimeSpan EndOfFileTolerance = TimeSpan.FromMilliseconds(80);

    /// <summary>安全可剪截止时间为负时归零。</summary>
    public static TimeSpan ClampSafeDuration(TimeSpan safeDuration) =>
        safeDuration < TimeSpan.Zero ? TimeSpan.Zero : safeDuration;

    /// <summary>边录边剪的位置钳制：只允许到达「安全可剪截止 - 余量」。</summary>
    public static TimeSpan ClampLivePosition(TimeSpan position, TimeSpan liveSafeDuration)
    {
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (liveSafeDuration <= TimeSpan.Zero)
        {
            // 安全区尚未建立（录制刚开始）：保持原始位置，不做 live 上界钳制。
            return clamped;
        }

        var maximum = liveSafeDuration - LiveSafetyMargin;
        if (maximum <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return clamped > maximum ? maximum : clamped;
    }

    /// <summary>
    /// Seek / 同步重载时的位置钳制：负数归零；边录边剪且安全区有效时用安全上界；
    /// 否则（普通文件，或安全区尚为 0 的边录边剪）不超过已知总时长。
    /// </summary>
    public static TimeSpan ClampPosition(TimeSpan position, bool isLive, TimeSpan liveSafeDuration, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (isLive && liveSafeDuration > TimeSpan.Zero)
        {
            return ClampLivePosition(position, liveSafeDuration);
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            return duration;
        }

        return position;
    }

    /// <summary>
    /// 根据一次 mpv 位置采样决定：位置是否推进、是否需要向上层发完成事件。
    /// 返回的 <see cref="ClipPositionPollResult.Position"/> 是应写入当前状态的位置。
    /// </summary>
    public static ClipPositionPollResult Evaluate(
        TimeSpan sampled,
        TimeSpan current,
        bool isLive,
        TimeSpan liveSafeDuration,
        TimeSpan duration,
        bool paused)
    {
        if (isLive)
        {
            var clamped = ClampLivePosition(sampled, liveSafeDuration);
            return ShouldUpdate(clamped, current)
                ? new ClipPositionPollResult(clamped, PositionChanged: true, Completed: false)
                : new ClipPositionPollResult(current, PositionChanged: false, Completed: false);
        }

        // keep-open 到末尾时 mpv 不会自动换状态，这里主动收口一次。
        if (!paused && duration > TimeSpan.Zero && sampled >= duration - EndOfFileTolerance)
        {
            return new ClipPositionPollResult(duration, PositionChanged: true, Completed: true);
        }

        return ShouldUpdate(sampled, current)
            ? new ClipPositionPollResult(sampled, PositionChanged: true, Completed: false)
            : new ClipPositionPollResult(current, PositionChanged: false, Completed: false);
    }

    private static bool ShouldUpdate(TimeSpan position, TimeSpan current) =>
        Math.Abs((position - current).TotalMilliseconds) >= PositionEpsilon.TotalMilliseconds;
}

/// <summary>一次位置采样的判定结果。</summary>
internal readonly record struct ClipPositionPollResult(TimeSpan Position, bool PositionChanged, bool Completed);
