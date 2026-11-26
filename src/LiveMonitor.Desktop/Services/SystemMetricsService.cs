using System.Diagnostics;
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

    public string MemoryText => GpuMetricsLogic.MemoryText(MemoryBytes);

    public string GpuUtilizationText => GpuMetricsLogic.UtilizationText(GpuUtilization);

    public string GpuDecoderText => GpuMetricsLogic.DecoderText(GpuDecoder);

    public string GpuEncoderText => GpuMetricsLogic.EncoderText(GpuEncoder);

    public string GpuMemoryText => GpuMetricsLogic.GpuMemoryText(GpuMemoryMb);

    public double GpuBarValue => GpuMetricsLogic.BarValue(GpuUtilization);

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

    private static GpuMetricsLogic.GpuSample? QueryNvidiaSmi()
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

            return GpuMetricsLogic.ParseSample(process.StandardOutput.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

}
