using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using LiveMonitor.Media.Asr;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class OpenAiCompatibleAsrProviderTests : IDisposable
{
    private static readonly string? FfmpegPath;

    private readonly string _workDir;
    private readonly string _cacheDir;

    static OpenAiCompatibleAsrProviderTests()
    {
        FfmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    public OpenAiCompatibleAsrProviderTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "limoclip-asr-tests", Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_workDir, "cache");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch
        {
            // 测试目录清理尽力而为。
        }
    }

    [Fact]
    public async Task Unconfigured_provider_throws_friendly_message()
    {
        string video = CreateDummyFile("clip.mp4");
        using var client = new HttpClient(new TestHttpMessageHandler(
            _ => throw new InvalidOperationException("不应发起网络请求")));
        var provider = new OpenAiCompatibleAsrProvider(
            client,
            FfmpegPath ?? "ffmpeg.exe",
            jobGuard: null,
            () => Task.FromResult<AsrApiConfig?>(null),
            _cacheDir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromSeconds(2)));

        Assert.Contains("尚未配置语音识别 API", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir, "*.asr.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Segment_longer_than_fifteen_minutes_is_rejected()
    {
        string video = CreateDummyFile("clip.mp4");
        var provider = CreateProvider(
            new TestHttpMessageHandler(_ => throw new InvalidOperationException("不应发起网络请求")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromMinutes(15.5)));

        Assert.Contains("15 分钟", ex.Message);
    }

    [Fact]
    public async Task Out_point_must_be_greater_than_in_point()
    {
        string video = CreateDummyFile("clip.mp4");
        var provider = CreateProvider(
            new TestHttpMessageHandler(_ => throw new InvalidOperationException("不应发起网络请求")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranscribeSegmentAsync(video, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Verbose_json_parses_segments_and_writes_cache()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        string video = GenerateAudio("clip.m4a");
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(SegmentsJson(
            fullText: "hello world",
            (0.0, 1.0, "hello"),
            (1.0, 2.0, "world"))));
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.Equal("hello world", result.FullText);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(TimeSpan.Zero, result.Segments[0].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Segments[0].RelativeEnd);
        Assert.Equal("world", result.Segments[1].Text);
        Assert.Equal(TimeSpan.Zero, result.ClipStart);
        Assert.Equal(TimeSpan.FromSeconds(2), result.ClipEnd);
        Assert.Contains("whisper-large-v3-turbo", result.ProviderLabel);
        Assert.Single(Directory.GetFiles(_cacheDir, "*.asr.json"));

        // 第二次调用必须命中缓存：handler 抛出异常也不会被触发。
        var offlineHandler = new TestHttpMessageHandler(
            _ => throw new InvalidOperationException("缓存命中时不应发起网络请求"));
        var cachedProvider = CreateProvider(offlineHandler);
        var cached = await cachedProvider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.Empty(offlineHandler.Requests);
        Assert.Equal("hello world", cached.FullText);
        Assert.Equal(2, cached.Segments.Count);
    }

    [Fact]
    public async Task Plain_text_response_falls_back_to_single_segment()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        string video = GenerateAudio("clip.m4a");
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json("""{"text":" 我是整段文本 "}"""));
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeSegmentAsync(
            video,
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(2.5));

        Assert.Equal("我是整段文本", result.FullText);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimeSpan.Zero, segment.RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.RelativeEnd);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result.ClipStart);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.ClipEnd);
    }

    [Fact]
    public async Task Verbose_json_400_retries_with_plain_json()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        string video = GenerateAudio("clip.m4a");
        string lastRequestBody = string.Empty;
        var handler = new TestHttpMessageHandler(request =>
        {
            string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            lastRequestBody = body;
            return body.Contains("verbose_json", StringComparison.Ordinal)
                ? TestHttpMessageHandler.Json("""{"error":"unsupported response_format"}""", HttpStatusCode.BadRequest)
                : TestHttpMessageHandler.Json("""{"text":"fallback text"}""");
        });
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("fallback text", result.FullText);
        Assert.DoesNotContain("verbose_json", lastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_error_surfaces_upstream_message()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        string video = GenerateAudio("clip.m4a");
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(
            """{"error":{"message":"invalid api key"}}""",
            HttpStatusCode.Unauthorized));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.TranscribeSegmentAsync(video, TimeSpan.Zero, TimeSpan.FromSeconds(2)));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid api key", ex.Message);
    }

    private OpenAiCompatibleAsrProvider CreateProvider(TestHttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            FfmpegPath ?? "ffmpeg.exe",
            jobGuard: null,
            () => Task.FromResult<AsrApiConfig?>(new AsrApiConfig(
                PresetId: "groq",
                Endpoint: "https://api.example.test/openai/v1/audio/transcriptions",
                Model: "whisper-large-v3-turbo",
                ApiKey: "sk-test")),
            _cacheDir);

    private string CreateDummyFile(string fileName)
    {
        string path = Path.Combine(_workDir, fileName);
        File.WriteAllText(path, "not a real media file");
        return path;
    }

    private string GenerateAudio(string fileName)
    {
        string path = Path.Combine(_workDir, fileName);
        RunFfmpeg(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:a", "aac",
            path,
        });
        return path;
    }

    private static string SegmentsJson(
        string fullText,
        params (double Start, double End, string Text)[] segments)
        => JsonSerializer.Serialize(new
        {
            text = fullText,
            segments = segments.Select(s => new { start = s.Start, end = s.End, text = s.Text }).ToArray(),
        });

    private static void RunFfmpeg(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(FfmpegPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg 失败（{process.ExitCode.ToString(CultureInfo.InvariantCulture)}）：{stderr}");
        }
    }
}
