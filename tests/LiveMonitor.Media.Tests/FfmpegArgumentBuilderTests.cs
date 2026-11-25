using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class FfmpegArgumentBuilderTests
{
    private static StreamQuality Quality(
        string url = "https://example.com/live.flv",
        Dictionary<string, string>? headers = null) => new(
        Id: "test",
        DisplayName: "测试",
        Height: 720,
        Fps: 30,
        StreamUrl: url,
        RequiredHeaders: headers ?? new Dictionary<string, string>());

    [Fact]
    public void Build_PutsHeadersAndUserAgentBeforeInput_AndCopiesStream()
    {
        var quality = Quality(headers: new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 Test",
            ["Referer"] = "https://live.example.com/1001",
            ["Cookie"] = "SESSDATA=abc",
        });

        var arguments = FfmpegArgumentBuilder.Build(quality, @"D:\录像\test.mkv");

        var inputIndex = arguments.ToList().IndexOf("-i");
        var headersIndex = arguments.ToList().IndexOf("-headers");
        var userAgentIndex = arguments.ToList().IndexOf("-user_agent");
        var timeoutIndex = arguments.ToList().IndexOf("-rw_timeout");

        Assert.True(inputIndex > 0);
        Assert.InRange(headersIndex, 0, inputIndex - 1);
        Assert.InRange(userAgentIndex, 0, inputIndex - 1);
        Assert.InRange(timeoutIndex, 0, inputIndex - 1);
        Assert.Equal("Mozilla/5.0 Test", arguments[userAgentIndex + 1]);
        Assert.Equal(quality.StreamUrl, arguments[inputIndex + 1]);
        Assert.Equal("15000000", arguments[timeoutIndex + 1]);

        Assert.Equal("copy", arguments[arguments.ToList().IndexOf("-c") + 1]);
        Assert.Equal("matroska", arguments[arguments.ToList().IndexOf("-f") + 1]);
        Assert.Equal("pipe:1", arguments[arguments.ToList().IndexOf("-progress") + 1]);
        Assert.Equal(@"D:\录像\test.mkv", arguments[^1]);
    }

    [Fact]
    public void BuildHeaderBlock_FormatsKeyValueLines_AndSkipsUserAgent()
    {
        var headerBlock = FfmpegArgumentBuilder.BuildHeaderBlock(Quality(headers: new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 Test",
            ["Referer"] = "https://live.example.com/1001",
            ["Cookie"] = "SESSDATA=abc",
        }));

        Assert.DoesNotContain("User-Agent", headerBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Referer: https://live.example.com/1001\r\n", headerBlock);
        Assert.Contains("Cookie: SESSDATA=abc\r\n", headerBlock);
        Assert.EndsWith("\r\n", headerBlock);
    }

    [Fact]
    public void BuildHeaderBlock_SanitizesInjectedNewlines()
    {
        var headerBlock = FfmpegArgumentBuilder.BuildHeaderBlock(Quality(headers: new Dictionary<string, string>
        {
            ["X-Test"] = "abc\r\nX-Injected: bad",
        }));

        // 注入的换行会被压成空格，仍留在同一行头值里，不会变成额外的请求头行。
        Assert.Equal("X-Test: abc  X-Injected: bad\r\n", headerBlock);
        Assert.Single(headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void BuildHeaderBlock_SkipsEmptyValuesAndStripsColonsFromNames()
    {
        var headerBlock = FfmpegArgumentBuilder.BuildHeaderBlock(Quality(headers: new Dictionary<string, string>
        {
            ["Empty"] = "   ",
            ["Bad:Name"] = "value",
            ["Referer"] = "https://live.example.com/1001",
        }));

        Assert.DoesNotContain("Empty", headerBlock);
        Assert.Contains("BadName: value\r\n", headerBlock);
        Assert.Contains("Referer: ", headerBlock);
    }

    [Fact]
    public void Build_WithoutCustomHeaders_OmitsHeadersAndUserAgentOptions()
    {
        var arguments = FfmpegArgumentBuilder.Build(Quality(), "out.mkv");

        Assert.DoesNotContain("-headers", arguments);
        Assert.DoesNotContain("-user_agent", arguments);
    }

    [Fact]
    public void GetUserAgent_FindsCaseInsensitiveHeader()
    {
        var quality = Quality(headers: new Dictionary<string, string>
        {
            ["user-agent"] = "UA-Value",
        });

        Assert.Equal("UA-Value", FfmpegArgumentBuilder.GetUserAgent(quality));
        Assert.Null(FfmpegArgumentBuilder.GetUserAgent(Quality()));
    }

    [Fact]
    public void Build_WithCaBundlePath_AddsCaFileBeforeInput()
    {
        var arguments = FfmpegArgumentBuilder.Build(Quality(), "out.mkv", @"E:\tools\cacert.crt");

        var list = arguments.ToList();
        var caIndex = list.IndexOf("-ca_file");
        var inputIndex = list.IndexOf("-i");

        Assert.True(caIndex > 0);
        Assert.InRange(caIndex, 0, inputIndex - 1);
        Assert.Equal(@"E:\tools\cacert.crt", list[caIndex + 1]);
    }

    [Fact]
    public void Build_WithoutCaBundlePath_OmitsCaFile()
    {
        Assert.DoesNotContain("-ca_file", FfmpegArgumentBuilder.Build(Quality(), "out.mkv"));
        Assert.DoesNotContain(
            "-ca_file",
            FfmpegArgumentBuilder.Build(Quality(), "out.mkv", "   "));
    }

    [Fact]
    public void Build_HttpInput_DoesNotAddCaFile()
    {
        // http:// 输入传 -ca_file 会被 ffmpeg 直接拒绝（Option ca_file not found），
        // 抖音等平台匿名拉流地址就是 http，必须不追加该参数。
        var arguments = FfmpegArgumentBuilder.Build(
            Quality(url: "http://pull-flv.example/live.flv"),
            "out.mkv",
            @"E:\tools\cacert.crt");

        Assert.DoesNotContain("-ca_file", arguments);
    }

    [Fact]
    public void Build_HttpsInput_AddsCaFileBeforeInput()
    {
        var arguments = FfmpegArgumentBuilder.Build(
            Quality(url: "https://pull-flv.example/live.flv"),
            "out.mkv",
            @"E:\tools\cacert.crt").ToList();

        var caIndex = arguments.IndexOf("-ca_file");
        var inputIndex = arguments.IndexOf("-i");
        Assert.InRange(caIndex, 0, inputIndex - 1);
        Assert.Equal(@"E:\tools\cacert.crt", arguments[caIndex + 1]);
    }

    [Fact]
    public void Build_WithSeparateAudio_AddsSecondInputAndMapsAudio()
    {
        var quality = new StreamQuality(
            Id: "youtube-1080p30",
            DisplayName: "1080P30",
            Height: 1080,
            Fps: 30,
            StreamUrl: "https://manifest.example/video.m3u8",
            RequiredHeaders: new Dictionary<string, string> { ["User-Agent"] = "video-ua" },
            AudioStreamUrl: "https://manifest.example/audio.m3u8",
            AudioHeaders: new Dictionary<string, string>
            {
                ["User-Agent"] = "audio-ua",
                ["Referer"] = "https://www.youtube.com/watch?v=x",
            });

        var arguments = FfmpegArgumentBuilder.Build(quality, "out.mkv", @"E:\tools\cacert.crt");
        var list = arguments.ToList();
        var inputIndexes = list
            .Select((value, index) => (Value: value, Index: index))
            .Where(item => item.Value == "-i")
            .Select(item => item.Index)
            .ToArray();

        Assert.Equal(2, inputIndexes.Length);
        Assert.True(quality.HasSeparateAudio);
        Assert.Equal("https://manifest.example/video.m3u8", list[inputIndexes[0] + 1]);
        Assert.Equal("https://manifest.example/audio.m3u8", list[inputIndexes[1] + 1]);
        Assert.Equal(
            new[] { "-map", "0:v:0", "-map", "1:a:0" },
            list.Skip(inputIndexes[1] + 2).Take(4));

        // 第二路输入的 UA/请求头必须在它自己的 -i 之前。
        var audioUserAgentIndex = list.FindLastIndex(value => value == "-user_agent");
        var audioHeadersIndex = list.FindLastIndex(value => value == "-headers");
        Assert.InRange(audioUserAgentIndex, 0, inputIndexes[1] - 1);
        Assert.InRange(audioHeadersIndex, 0, inputIndexes[1] - 1);
        Assert.Equal("audio-ua", list[audioUserAgentIndex + 1]);
        Assert.Contains("Referer: https://www.youtube.com/watch?v=x", list[audioHeadersIndex + 1]);

        // 第二路输入同样要带 socket 超时与 ca_file。
        var secondTimeoutIndex = list.FindLastIndex(value => value == "-rw_timeout");
        Assert.InRange(secondTimeoutIndex, inputIndexes[0] + 1, inputIndexes[1] - 1);
        Assert.Equal(2, list.FindAll(value => value == "-ca_file").Count);
    }

    [Fact]
    public void Build_WithSeparateAudio_AudioWithoutUrl_KeepsSingleInput()
    {
        var quality = Quality() with { AudioStreamUrl = "   " };

        var arguments = FfmpegArgumentBuilder.Build(quality, "out.mkv");

        Assert.False(quality.HasSeparateAudio);
        Assert.Single(arguments, "-i");
        Assert.DoesNotContain("-map", arguments);
    }

    [Fact]
    public void Build_MissingStreamUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentBuilder.Build(Quality(url: "  "), "out.mkv"));
    }

    [Fact]
    public void Build_MissingOutputPath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentBuilder.Build(Quality(), " "));
        Assert.Throws<ArgumentNullException>(() =>
            FfmpegArgumentBuilder.Build(Quality(), null!));
    }

    [Theory]
    [InlineData("rtmp://live.example.com/live/stream")]
    [InlineData("rtmps://live.example.com/live/stream")]
    [InlineData("srt://live.example.com:9000?mode=caller")]
    public void Build_NonHttpInput_DoesNotPassHttpHeadersOrUserAgent(string url)
    {
        var quality = Quality(url: url, headers: new Dictionary<string, string>
        {
            ["User-Agent"] = "UA-1",
            ["Referer"] = "https://live.example.com/",
        });

        var arguments = FfmpegArgumentBuilder.Build(quality, "out.mkv", @"E:\tools\cacert.crt").ToList();

        Assert.DoesNotContain("-headers", arguments);
        Assert.DoesNotContain("-user_agent", arguments);
        Assert.DoesNotContain("-ca_file", arguments);
    }

    [Theory]
    [InlineData("rtsp://192.168.1.10/live")]
    [InlineData("rtsps://192.168.1.10/live")]
    public void Build_RtspInput_ForcesTcpTransport(string url)
    {
        var quality = Quality(url: url, headers: new Dictionary<string, string>
        {
            ["Referer"] = "https://live.example.com/",
        });

        var arguments = FfmpegArgumentBuilder.Build(quality, "out.mkv").ToList();

        var transportIndex = arguments.IndexOf("-rtsp_transport");
        var inputIndex = arguments.IndexOf("-i");
        Assert.InRange(transportIndex, 0, inputIndex - 1);
        Assert.Equal("tcp", arguments[transportIndex + 1]);
        Assert.DoesNotContain("-headers", arguments);
    }

    [Fact]
    public void Build_OutputTail_LimitsClusterDurationForLiveClipping()
    {
        var arguments = FfmpegArgumentBuilder.Build(Quality(), "out.mkv").ToList();

        var clusterIndex = arguments.IndexOf("-cluster_time_limit");
        var copyIndex = arguments.IndexOf("-c");
        var outputIndex = arguments.IndexOf("out.mkv");

        Assert.InRange(clusterIndex, 0, arguments.Count - 2);
        Assert.Equal("2000", arguments[clusterIndex + 1]);
        // 位于输出参数区、-c copy 之前，紧贴输出路径。
        Assert.InRange(clusterIndex, copyIndex - 2, copyIndex - 1);
        Assert.True(outputIndex > clusterIndex);
    }
}
