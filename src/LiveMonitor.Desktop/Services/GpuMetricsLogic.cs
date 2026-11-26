using System.Globalization;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 状态栏 GPU 指标的解析与展示规则：nvidia-smi CSV 输出解析、数值宽容解析、文本/进度条格式化。
/// 纯函数，方便单测；进程启动与轮询节奏仍留在 <see cref="SystemMetricsService"/>。
/// </summary>
public static class GpuMetricsLogic
{
    /// <summary>nvidia-smi 单条 GPU 采样：任一字段可能缺失（N/A）。</summary>
    public readonly record struct GpuSample(
        double? Utilization,
        double? Decoder,
        double? Encoder,
        double? MemoryMb);

    /// <summary>宽容解析 nvidia-smi 的数值列：空、N/A、[N/A]、非数字一律视为缺失。</summary>
    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// 解析 <c>nvidia-smi --query-gpu=utilization.gpu,utilization.decoder,utilization.encoder,memory.used --format=csv,noheader,nounits</c>
    /// 的首行输出；字段不足 4 个或整行为空时返回 null（表头/异常输出视为不可用）。
    /// </summary>
    public static GpuSample? ParseSample(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var line = output
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
            ParseNumber(parts[0]),
            ParseNumber(parts[1]),
            ParseNumber(parts[2]),
            ParseNumber(parts[3]));
    }

    public static string MemoryText(long memoryBytes)
        => $"{memoryBytes / 1024d / 1024d:F0} MB";

    public static string UtilizationText(double? value) => value is { } utilization ? $"GPU {utilization:F0}%" : "GPU --";

    public static string DecoderText(double? value) => value is { } decoder ? $"解码 {decoder:F0}%" : "解码 --";

    public static string EncoderText(double? value) => value is { } encoder ? $"编码 {encoder:F0}%" : "编码 --";

    public static string GpuMemoryText(double? memoryMb) => memoryMb is { } memory ? $"显存 {memory:F0} MB" : string.Empty;

    public static double BarValue(double? utilization) => utilization is { } value ? Math.Clamp(value, 0, 100) : 0;
}
