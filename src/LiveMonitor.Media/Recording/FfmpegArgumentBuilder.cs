using System.Text;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 构造 ffmpeg 拉流录制参数。约定（V0.3 强制修正）：
/// ① 自定义请求头必须通过 <c>-headers</c> 放在 <c>-i</c> 之前，格式固定为 <c>"Key: Value\r\n"</c>；
/// ② User-Agent 单独走 <c>-user_agent</c>，不要混进 -headers，否则部分 CDN 会拒绝；
/// ③ 输入侧套接字超时 <c>-rw_timeout 15000000</c>（15 秒），网络抖动时及时失败交给上层重试；
/// ④ 输出统一零重编码 <c>-c copy -f matroska</c>，配合 <c>-progress pipe:1</c> 输出实时进度。
/// </summary>
public static class FfmpegArgumentBuilder
{
    public const string UserAgentHeaderName = "User-Agent";
    public const int ReadWriteTimeoutMicroseconds = 15_000_000;

    /// <summary>
    /// 构造完整参数序列（不含可执行文件名），顺序对 ffmpeg 语义敏感。
    /// 传入 <paramref name="caBundlePath"/> 时追加 <c>-ca_file</c>：静态链接的 ffmpeg 在部分环境
    /// 无法注册 Windows 证书库，直接用 HTTPS 拉流会报 TLS 失败，用随包的 cacert.crt 兜底。
    /// </summary>
    public static IReadOnlyList<string> Build(
        StreamQuality sourceQuality,
        string outputMkvPath,
        string? caBundlePath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceQuality);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMkvPath);

        if (string.IsNullOrWhiteSpace(sourceQuality.StreamUrl))
        {
            throw new ArgumentException("流地址为空，无法构造 ffmpeg 参数。", nameof(sourceQuality));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostats",
            "-loglevel",
            "warning",
            "-rw_timeout",
            ReadWriteTimeoutMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var userAgent = GetUserAgent(sourceQuality.RequiredHeaders);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            arguments.Add("-user_agent");
            arguments.Add(SanitizeHeaderValue(userAgent));
        }

        var headerBlock = BuildHeaderBlock(sourceQuality.RequiredHeaders);
        if (headerBlock.Length > 0)
        {
            arguments.Add("-headers");
            arguments.Add(headerBlock);
        }

        AppendCaBundle(arguments, caBundlePath, sourceQuality.StreamUrl);

        arguments.Add("-i");
        arguments.Add(sourceQuality.StreamUrl.Trim());

        // YouTube 等平台的直播 HLS 音视频分离：视频轨与音频轨要分别拉取，
        // 第二路输入前同样需要 rw_timeout / UA / headers / ca_file。
        if (sourceQuality.HasSeparateAudio)
        {
            arguments.Add("-rw_timeout");
            arguments.Add(ReadWriteTimeoutMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var audioHeaders = sourceQuality.AudioHeaders ?? sourceQuality.RequiredHeaders;
            var audioUserAgent = GetUserAgent(audioHeaders);
            if (!string.IsNullOrWhiteSpace(audioUserAgent))
            {
                arguments.Add("-user_agent");
                arguments.Add(SanitizeHeaderValue(audioUserAgent));
            }

            var audioHeaderBlock = BuildHeaderBlock(audioHeaders);
            if (audioHeaderBlock.Length > 0)
            {
                arguments.Add("-headers");
                arguments.Add(audioHeaderBlock);
            }

            AppendCaBundle(arguments, caBundlePath, sourceQuality.AudioStreamUrl);
            arguments.Add("-i");
            arguments.Add(sourceQuality.AudioStreamUrl!.Trim());

            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-map");
            arguments.Add("1:a:0");
        }

        arguments.AddRange(new[]
        {
            "-c",
            "copy",
            "-f",
            "matroska",
            "-progress",
            "pipe:1",
            "-y",
            outputMkvPath,
        });

        return arguments;
    }

    // -ca_file 只对 TLS 输入有效：给 http:// 输入传该参数时 ffmpeg 会直接报
    // “Option ca_file not found / Error opening input files: Option not found”，
    // 因此仅当地址是 https:// 时才追加。
    private static void AppendCaBundle(List<string> arguments, string? caBundlePath, string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(caBundlePath))
        {
            return;
        }

        if (streamUrl is null || !streamUrl.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        arguments.Add("-ca_file");
        arguments.Add(caBundlePath.Trim());
    }

    /// <summary>
    /// 把请求头拼成 ffmpeg 要求的 <c>"Key: Value\r\n"</c> 文本块。
    /// User-Agent 由 <c>-user_agent</c> 单独传递，这里会跳过；空名/空值/非法换行会被剔除或清洗。
    /// </summary>
    public static string BuildHeaderBlock(StreamQuality sourceQuality)
    {
        ArgumentNullException.ThrowIfNull(sourceQuality);
        return BuildHeaderBlock(sourceQuality.RequiredHeaders);
    }

    /// <inheritdoc cref="BuildHeaderBlock(StreamQuality)"/>
    public static string BuildHeaderBlock(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.Equals(name.Trim(), UserAgentHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 头名里不允许冒号/换行，否则会破坏 ffmpeg 的解析。
            var safeName = SanitizeHeaderName(name);
            if (safeName.Length == 0)
            {
                continue;
            }

            builder.Append(safeName);
            builder.Append(": ");
            builder.Append(SanitizeHeaderValue(value));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>从请求头里取 User-Agent；没有则返回 null。</summary>
    public static string? GetUserAgent(StreamQuality sourceQuality)
    {
        ArgumentNullException.ThrowIfNull(sourceQuality);
        return GetUserAgent(sourceQuality.RequiredHeaders);
    }

    /// <inheritdoc cref="GetUserAgent(StreamQuality)"/>
    public static string? GetUserAgent(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name?.Trim(), UserAgentHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>清洗头名：去掉冒号、控制字符与首尾空白。</summary>
    internal static string SanitizeHeaderName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (ch == ':' || ch == '\r' || ch == '\n' || char.IsControl(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>清洗头值：把 CR/LF/其他控制字符替换为空格，防止参数注入。</summary>
    internal static string SanitizeHeaderValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(ch == '\r' || ch == '\n' || char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString();
    }
}
