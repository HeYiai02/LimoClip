using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinSignerTests
{
    private static string CreateStubScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "limoclip-douyin-sign-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sign.js");
        File.WriteAllText(path, DouyinTestData.StubSignScript);
        return path;
    }

    [Fact]
    public void ComputeSignatureInput_ProducesMd5OfFixedParameters()
    {
        const string url =
            "wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/?app_name=douyin_web&live_id=1&aid=6383" +
            "&version_code=180800&webcast_sdk_version=1.0.14-beta.0&room_id=7711351763655969576&did_rule=3" +
            "&user_unique_id=7312851930573448226&device_platform=web&identity=audience";

        var input = DouyinSigner.ComputeSignatureInput(url);
        var expected = ComputeExpectedInput();

        Assert.Equal(expected, input);

        static string ComputeExpectedInput()
        {
            const string param =
                "live_id=1,aid=6383,version_code=180800,webcast_sdk_version=1.0.14-beta.0," +
                "room_id=7711351763655969576,sub_room_id=,sub_channel_id=,did_rule=3," +
                "user_unique_id=7312851930573448226,device_platform=web,device_type=,ac=,identity=audience";
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(param))).ToLowerInvariant();
        }
    }

    [Fact]
    public async Task Sign_ExecutesStubScript()
    {
        var scriptPath = CreateStubScript();
        using var signer = new DouyinSigner(scriptPath: scriptPath, cacheDirectory: Path.GetDirectoryName(scriptPath)!);
        await signer.PrepareAsync();

        const string url = "wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/?room_id=1&identity=audience";
        var signature = signer.Sign(url);
        var input = DouyinSigner.ComputeSignatureInput(url);

        Assert.Equal("stub-" + input, signature);
        Assert.Contains(scriptPath, signer.ScriptSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sign_WithoutPrepare_Throws()
    {
        var scriptPath = CreateStubScript();
        using var signer = new DouyinSigner(scriptPath: scriptPath, cacheDirectory: Path.GetDirectoryName(scriptPath)!);

        var exception = Assert.Throws<InvalidOperationException>(() => signer.Sign("wss://example.com/?room_id=1"));
        Assert.Contains("PrepareAsync", exception.Message);
    }

    [Fact]
    public async Task PrepareAsync_MissingScript_ThrowsChineseError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "limoclip-douyin-sign-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var signer = new DouyinSigner(
            httpClient: new HttpClient(new ThrowingHandler()),
            scriptPath: Path.Combine(directory, "missing.js"),
            cacheDirectory: directory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => signer.PrepareAsync());
        Assert.Contains("sign.js", exception.Message);
        Assert.Contains("拉流/录制不受影响", exception.Message);
    }

    [Fact]
    public void ParseQuery_DecodesValues()
    {
        var query = DouyinSigner.ParseQuery("a=1&b=hello%20world&c=");
        Assert.Equal("1", query["a"]);
        Assert.Equal("hello world", query["b"]);
        Assert.Equal(string.Empty, query["c"]);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
