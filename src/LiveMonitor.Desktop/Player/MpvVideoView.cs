using Avalonia.OpenGL;
using LibMpv.Avalonia;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// <see cref="VideoView"/> 的薄封装，只增加一个“OpenGL 渲染上下文已就绪”信号。
/// mpv 的 <c>vo=libmpv</c> 在 render context 建立前执行 <c>loadfile</c> 会报
/// “No render context set”，因此播放器必须等这个信号后再起播。
/// </summary>
public sealed class MpvVideoView : VideoView
{
    private readonly TaskCompletionSource<bool> _renderReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRenderReady => _renderReady.Task.IsCompleted;

    public Task WaitForRenderAsync(CancellationToken ct = default)
        => _renderReady.Task.WaitAsync(ct);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _renderReady.TrySetResult(true);
    }
}
