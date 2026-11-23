using System.Net;
using System.Text;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class CliUpdaterTests
{
    private const string LatestReleaseJson = """
        {
          "tag_name": "2026.01.10",
          "name": "yt-dlp 2026.01.10",
          "assets": [
            {
              "name": "yt-dlp.exe",
              "browser_download_url": "https://github.com/yt-dlp/yt-dlp/releases/download/2026.01.10/yt-dlp.exe"
            },
            {
              "name": "SHA2-256SUMS",
              "browser_download_url": "https://github.com/yt-dlp/yt-dlp/releases/download/2026.01.10/SHA2-256SUMS"
            }
          ]
        }
        """;

    [Fact]
    public async Task CheckYtDlp_FileMissing_ReturnsErrorWithoutNetwork()
    {
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var updater = new CliUpdater(
            Path.Combine(Path.GetTempPath(), "limoclip-missing-" + Guid.NewGuid().ToString("N"), "yt-dlp.exe"),
            new HttpClient(handler));

        var result = await updater.CheckYtDlpAsync();

        Assert.False(result.CheckSucceeded);
        Assert.Contains("找不到 yt-dlp.exe", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckYtDlp_NewerRelease_ReturnsUpdateAndDownloadUrl()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(LatestReleaseJson));
        var updater = new CliUpdater(
            executable,
            new HttpClient(handler),
            versionReader: static (_, _) => Task.FromResult<string?>("2025.12.01"));

        var result = await updater.CheckYtDlpAsync();

        Assert.True(result.CheckSucceeded);
        Assert.Equal("2025.12.01", result.LocalVersion);
        Assert.Equal("2026.01.10", result.LatestVersion);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(
            "https://github.com/yt-dlp/yt-dlp/releases/download/2026.01.10/yt-dlp.exe",
            result.DownloadUrl);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("api.github.com", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckYtDlp_SameVersion_NoUpdate()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(LatestReleaseJson));
        var updater = new CliUpdater(
            executable,
            new HttpClient(handler),
            versionReader: static (_, _) => Task.FromResult<string?>("2026.01.10"));

        var result = await updater.CheckYtDlpAsync();

        Assert.True(result.CheckSucceeded);
        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.DownloadUrl);
    }

    [Fact]
    public async Task CheckYtDlp_GitHubError_ReturnsFailure()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var handler = new TestHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var updater = new CliUpdater(executable, new HttpClient(handler), versionReader: VersionReader("2025.01.01"));

        var result = await updater.CheckYtDlpAsync();

        Assert.False(result.CheckSucceeded);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAndInstall_ValidPayload_ReplacesExecutable()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var payload = CreateFakeExecutable(minimumBytes: 2048);
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var updater = new CliUpdater(executable, new HttpClient(handler));
        var check = new CliUpdateCheckResult(
            true, "2025.01.01", "2026.01.10", true,
            "https://github.com/yt-dlp/yt-dlp/releases/download/2026.01.10/yt-dlp.exe", null);

        var result = await updater.DownloadAndInstallAsync(check);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("2026.01.10", result.NewVersion);
        Assert.Equal(payload, await File.ReadAllBytesAsync(executable));
        Assert.False(File.Exists(executable + ".tmp"));
    }

    [Fact]
    public async Task DownloadAndInstall_NonExecutablePayload_Rejected()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var htmlPayload = Encoding.UTF8.GetBytes(new string('<', 4096));
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(htmlPayload),
        });
        var updater = new CliUpdater(executable, new HttpClient(handler));
        var check = new CliUpdateCheckResult(true, "2025.01.01", "2026.01.10", true, "https://example.com/yt-dlp.exe", null);

        var result = await updater.DownloadAndInstallAsync(check);

        Assert.False(result.Success);
        Assert.Contains("MZ", result.ErrorMessage);
        Assert.Equal("MZ-old", await File.ReadAllTextAsync(executable));
    }

    [Fact]
    public async Task DownloadAndInstall_MissingUrl_Rejected()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("yt-dlp.exe", "MZ-old");
        var updater = new CliUpdater(executable, new HttpClient(new TestHttpMessageHandler(
            _ => throw new InvalidOperationException("不应发起网络请求"))));
        var check = new CliUpdateCheckResult(true, "2025.01.01", "2026.01.10", true, null, null);

        var result = await updater.DownloadAndInstallAsync(check);

        Assert.False(result.Success);
        Assert.Contains("更新地址", result.ErrorMessage);
    }

    private static Func<string, CancellationToken, Task<string?>> VersionReader(string version)
        => (_, _) => Task.FromResult<string?>(version);

    private static byte[] CreateFakeExecutable(int minimumBytes)
    {
        var payload = new byte[minimumBytes];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "limoclip-cliupdater-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateFile(string name, string content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响断言结果。
            }
        }
    }
}
