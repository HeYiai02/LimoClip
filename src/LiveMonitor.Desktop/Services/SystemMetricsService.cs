using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 状态栏指标：进程工作集内存 + NVIDIA GPU 利用率/解码/编码。
/// nvidia-smi 不存在或不是 N 卡时 GPU 指标显示为 --，不影响主流程。
/// </summary>
public sealed class SystemMetricsService : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private long _memoryBytes;
    private double? _gpuUtilization;
    private double? _gpuDecoder;
    private double? _gpuEncoder;
    private double? _gpuMemoryMb;
    private bool _started;

    public long MemoryBytes
    {
        get => _memoryBytes;
        private set
        {
            if (SetProperty(ref _memoryBytes, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public double? GpuUtilization
    {
        get => _gpuUtilization;
        private set
        {
            if (SetProperty(ref _gpuUtilization, value))
            {
                OnPropertyChanged(nameof(GpuUtilizationText));
                OnPropertyChanged(nameof(GpuBarValue));
            }
        }
    }

    public double? GpuDecoder
    {
        get => _gpuDecoder;
        private set
        {
            if (SetProperty(ref _gpuDecoder, value))
            {
                OnPropertyChanged(nameof(GpuDecoderText));
            }
        }
    }

    public double? GpuEncoder
    {
        get => _gpuEncoder;
        private set
        {
            if (SetProperty(ref _gpuEncoder, value))
            {
                OnPropertyChanged(nameof(GpuEncoderText));
            }
        }
    }

    public double? GpuMemoryMb
    {
        get => _gpuMemoryMb;
        private set
        {
            if (SetProperty(ref _gpuMemoryMb, value))
            {
                OnPropertyChanged(nameof(GpuMemoryText));
            }
        }
    }

    public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F0} MB";

    public string GpuUtilizationText => GpuUtilization is { } value ? $"GPU {value:F0}%" : "GPU --";

    public string GpuDecoderText => GpuDecoder is { } value ? $"解码 {value:F0}%" : "解码 --";

    public string GpuEncoderText => GpuEncoder is { } value ? $"编码 {value:F0}%" : "编码 --";

    public string GpuMemoryText => GpuMemoryMb is { } value ? $"显存 {value:F0} MB" : string.Empty;

    public double GpuBarValue => GpuUtilization is { } value ? Math.Clamp(value, 0, 100) : 0;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var memory = Process.GetCurrentProcess().WorkingSet64;
                var gpu = await Task.Run(QueryNvidiaSmi, ct).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MemoryBytes = memory;
                    if (gpu is not null)
                    {
                        GpuUtilization = gpu.Value.Utilization;
                        GpuDecoder = gpu.Value.Decoder;
                        GpuEncoder = gpu.Value.Encoder;
                        GpuMemoryMb = gpu.Value.MemoryMb;
                    }
                    else
                    {
                        GpuUtilization = null;
                        GpuDecoder = null;
                        GpuEncoder = null;
                        GpuMemoryMb = null;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 指标面板不能影响播放。
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static GpuSample? QueryNvidiaSmi()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,utilization.decoder,utilization.encoder,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // 不让 nvidia-smi 卡住状态栏刷新。
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var line = process.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
            {
                return null;
            }

            return new GpuSample(
                ParseDouble(parts[0]),
                ParseDouble(parts[1]),
                ParseDouble(parts[2]),
                ParseDouble(parts[3]));
        }
        catch
        {
            return null;
        }
    }

    public static double? ParseDouble(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private readonly record struct GpuSample(double? Utilization, double? Decoder, double? Encoder, double? MemoryMb);
}
