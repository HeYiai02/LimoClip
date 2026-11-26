using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>语音识别「测试连接」文案（`AsrConnectionStatusText`）的直接单测。</summary>
public sealed class AsrConnectionStatusTextTests
{
    [Theory]
    [InlineData(AsrConnectionStatusText.MissingEndpoint, "请先填写转写接口地址。")]
    [InlineData(AsrConnectionStatusText.MissingApiKey, "请先填写 API Key。")]
    [InlineData(AsrConnectionStatusText.Timeout, "连接超时，请检查网络或接口地址。")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Fact]
    public void InvalidEndpoint_EmbedsRawAddress()
    {
        Assert.Equal(
            "接口地址不是合法的 URL：not-a-url",
            AsrConnectionStatusText.InvalidEndpoint("not-a-url"));
    }

    [Fact]
    public void Succeeded_EmbedsStatusCodeAndModel()
    {
        Assert.Equal(
            "连接成功（HTTP 200），准备使用模型：whisper-large-v3-turbo",
            AsrConnectionStatusText.Succeeded(200, "whisper-large-v3-turbo"));
    }

    [Fact]
    public void Failed_EmbedsStatusCodeAndDetail()
    {
        Assert.Equal(
            "连接失败（HTTP 401）：invalid api key",
            AsrConnectionStatusText.Failed(401, "invalid api key"));
    }

    [Fact]
    public void FailedWithMessage_EmbedsReason()
    {
        Assert.Equal(
            "连接失败：No such host is known",
            AsrConnectionStatusText.FailedWithMessage("No such host is known"));
    }

    [Fact]
    public void Messages_AreDistinctAndNonEmpty()
    {
        var messages = new[]
        {
            AsrConnectionStatusText.MissingEndpoint,
            AsrConnectionStatusText.MissingApiKey,
            AsrConnectionStatusText.Timeout,
            AsrConnectionStatusText.InvalidEndpoint("x"),
            AsrConnectionStatusText.Succeeded(200, "m"),
            AsrConnectionStatusText.Failed(500, "boom"),
            AsrConnectionStatusText.FailedWithMessage("boom"),
        };

        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SucceededAndFailed_ShareHttpCodeFormat()
    {
        Assert.Contains("HTTP 204", AsrConnectionStatusText.Succeeded(204, "m"), StringComparison.Ordinal);
        Assert.Contains("HTTP 502", AsrConnectionStatusText.Failed(502, "bad gateway"), StringComparison.Ordinal);
    }
}
