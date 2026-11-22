using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using LibMpv.Client;

namespace MpvSpike;

public partial class MainWindow : Window
{
    private readonly StringBuilder _log = new();
    private readonly List<string> _samples = new();
    private MpvContext? _mpv;
    private DispatcherTimer? _timer;
    private DateTime _started;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var media = Environment.GetCommandLineArgs().Length > 1
            ? Environment.GetCommandLineArgs()[1]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".cache", "media", "test-1080p60.mp4"));

        try
        {
            Write("step: creating MpvContext");
            LibMpv.Client.LibMpv.UseLibMpv(2);
            _mpv = new MpvContext();
            Write("step: MpvContext created");
            var hwdec = Environment.GetEnvironmentVariable("MPV_HWDEC") ?? "auto-safe";
            var interop = Environment.GetEnvironmentVariable("MPV_INTEROP") ?? "auto";
            Write($"hwdec={hwdec} interop={interop}");
            _mpv.SetOptionString("hwdec", hwdec);
            _mpv.SetOptionString("vo", "libmpv");
            _mpv.SetOptionString("gpu-hwdec-interop", interop);
            _mpv.SetOptionString("gpu-api", "opengl");
            _mpv.SetOptionString("cache", "yes");
            _mpv.SetOptionString("demuxer-max-bytes", "8M");
            _mpv.SetOptionString("demuxer-max-back-bytes", "0");
            _mpv.SetOptionString("network-timeout", "10");
            _mpv.SetOptionString("aid", "no");
            _mpv.SetOptionString("mute", "yes");
            _mpv.SetOptionString("osc", "no");
            _mpv.SetOptionString("osd-level", "0");
            _mpv.SetOptionString("input-default-bindings", "no");
            _mpv.SetOptionString("terminal", "yes");
            Write("step: assigning Video.MpvContext");
            Video.MpvContext = _mpv;
            Write("step: Video.MpvContext assigned");

            Write($"media={media}");
            if (!File.Exists(media))
            {
                Write($"MISSING media file: {media}");
                Status.Text = _log.ToString();
                return;
            }

            DispatcherTimer.RunOnce(() =>
            {
                Write("step: loadfile");
                _mpv.Command("loadfile", media, "replace");
                _started = DateTime.UtcNow;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += OnTick;
                _timer.Start();
            }, TimeSpan.FromMilliseconds(1500));
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            Write($"INIT FAILED: {ex.GetType().Name}: {ex.Message}");
            Write(ex.ToString());
            Status.Text = _log.ToString();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mpv-spike.log"), _log.ToString(), Encoding.UTF8);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(3));
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_mpv is null) return;
        var elapsed = (DateTime.UtcNow - _started).TotalSeconds;
        var line = string.Join(" | ", new[]
        {
            $"t={elapsed:F0}s",
            $"vo={Get("vo-configured")}",
            $"codec={Get("video-codec")}",
            $"size={Get("width")}x{Get("height")}",
            $"fps={Get("container-fps")}/{Get("estimated-vf-fps")}",
            $"hwdec={Get("hwdec-current")}",
            $"interop={Get("hwdec-interop")}",
            $"gpu={Get("gpu-api")}/{Get("gpu-context")}",
            $"drop={Get("decoder-frame-drop-count")}/{Get("frame-drop-count")}",
        });
        _samples.Add(line);
        Write(line);
        Status.Text = string.Join(Environment.NewLine, _samples.Count > 8 ? _samples.GetRange(_samples.Count - 8, 8) : _samples);

        if (elapsed >= 12)
        {
            _timer?.Stop();
            Write("SUMMARY " + line);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mpv-spike.log"), _log.ToString(), Encoding.UTF8);
            Console.WriteLine(_log);
        }
    }

    private string Get(string name)
    {
        try { return _mpv?.GetPropertyString(name) ?? "<null>"; }
        catch (Exception ex) { return "ERR:" + ex.Message; }
    }

    private void Write(string line)
    {
        _log.AppendLine(line);
        Console.WriteLine(line);
    }
}
