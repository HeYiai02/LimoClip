using Avalonia.Controls;
using Avalonia.Threading;
using LiveMonitor.Core.Enums;
using LiveMonitor.Platforms;

namespace LiveMonitor.Desktop.Player;

public partial class PlayerSmokeWindow : Window
{
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "player-smoke.log");
    private readonly DispatcherTimer _timer;
    private int _ticks;

    public PlayerSmokeWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        Opened += OnOpened;
        Closing += OnClosing;

        Player.StateChanged += (_, _) =>
        {
            Log($"STATE {Player.State}");
            SmokeStatus.Text = Player.State.ToString();
        };
        Player.ReconnectRequested += (_, _) => Log("RECONNECT requested");
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Log("OPEN");
        try
        {
            var cookie = Environment.GetEnvironmentVariable("BILIBILI_COOKIE");
            var provider = new BilibiliProvider();
            var status = await provider.GetRoomStatusAsync("7734200", cookie);
            Log($"STATUS live={status.IsLive} room={status.RoomId} title={status.Title} qualities={status.AvailableQualities.Count}");
            Player.RoomTitle = status.Title;

            if (!status.IsLive || status.AvailableQualities.Count == 0)
            {
                Player.ShowOffline("主播未开播");
                Log("OFFLINE");
                return;
            }

            var quality = status.AvailableQualities[0];
            Log($"QUALITY id={quality.Id} name={quality.DisplayName} {quality.Height}p{quality.Fps} url={quality.StreamUrl}");
            await Player.PlayAsync(quality);
            Log("PLAY returned");
            _timer.Start();
        }
        catch (Exception ex)
        {
            Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            Log(ex.ToString());
            Player.ShowFailed(ex.Message);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        var m = Player.GetHealthMetrics();
        Log($"HEALTH state={Player.State} hw={m.HardwareDecoding} hwdec={m.HwdecCurrent} fps={m.FramesPerSecond:0.0} decDrop={m.DecoderDroppedFrames} drop={m.DroppedFrames}");
        SmokeStatus.Text = $"{Player.State} · {m.FramesPerSecond:0}fps · {m.HwdecCurrent ?? "?"}";

        if (_ticks == 8)
        {
            Player.SetMute(true);
            Player.SetVolume(35);
            Player.SetCompactMode(true);
            Log("CONTROLS muted=true volume=35 compact=true");
        }

        if (_ticks == 14)
        {
            Player.SetCompactMode(false);
            Player.SetMute(false);
            Player.SetVolume(100);
            Log("CONTROLS restored");
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _timer.Stop();
        Player.Dispose();
        Log("CLOSING disposed");
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // smoke only
        }
    }
}
