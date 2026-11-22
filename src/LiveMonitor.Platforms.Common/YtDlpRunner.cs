using CliWrap;
using CliWrap.Buffered;

namespace LiveMonitor.Platforms;

public sealed record YtDlpResult(int ExitCode, string StandardOutput, string StandardError);

public interface IYtDlpRunner
{
    Task<YtDlpResult> RunJsonAsync(string url, string? cookie, CancellationToken ct = default);
}

/// <summary>
/// 调用外部 yt-dlp.exe。TEMP/TMP 显式指向可写目录，避免宿主环境把临时目录设成不可写路径。
/// </summary>
public sealed class CliWrapYtDlpRunner : IYtDlpRunner
{
    private readonly string _executablePath;
    private readonly string _tempDirectory;

    public CliWrapYtDlpRunner(string executablePath, string? tempDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _executablePath = Path.GetFullPath(executablePath);
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "LiveMonitor");
    }

    public async Task<YtDlpResult> RunJsonAsync(string url, string? cookie, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException($"找不到 yt-dlp.exe：{_executablePath}", _executablePath);
        }

        Directory.CreateDirectory(_tempDirectory);

        var args = new List<string>
        {
            "-j",
            "--no-playlist",
            "--no-warnings",
            "--socket-timeout", "15",
            "--retries", "2"
        };

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            args.Add("--add-header");
            args.Add("Cookie: " + cookie);
        }

        args.Add(url);

        var result = await Cli.Wrap(_executablePath)
            .WithArguments(args)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                ["TEMP"] = _tempDirectory,
                ["TMP"] = _tempDirectory
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct)
            .ConfigureAwait(false);

        return new YtDlpResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
