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
}
