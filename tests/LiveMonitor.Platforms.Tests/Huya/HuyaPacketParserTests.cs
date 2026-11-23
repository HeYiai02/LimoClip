using LiveMonitor.Plugins.Huya;
using System.Text;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaPacketParserTests
{
    private const ulong PresenterUid = 1199565822350;

    [Fact]
    public void BuildSubscribe_MatchesProtocolGoldenBytes()
    {
        var frame = HuyaPacketParser.BuildSubscribe(PresenterUid);

        var expected = new List<byte>();
        expected.AddRange(new byte[] { 0x00, 0x10, 0x1D, 0x00, 0x00, 0x2D });
        expected.AddRange(new byte[] { 0x09, 0x00, 0x02 });
        AddTarsString(expected, "live:1199565822350");
        AddTarsString(expected, "chat:1199565822350");
        expected.AddRange(new byte[] { 0x16, 0x00 });

        Assert.Equal(expected, frame);
    }

    [Fact]
    public void BuildSocketUrl_ContainsEscapedBaseInfo()
    {
        var guid = "0123456789abcdef0123456789abcdef";

        var url = HuyaPacketParser.BuildSocketUrl(guid);

        const string prefix = "wss://wsapi.huya.com/?baseinfo=";
        Assert.StartsWith(prefix, url);
        var decoded = Convert.FromBase64String(Uri.UnescapeDataString(url[prefix.Length..]));
        Assert.Equal(0x0C, decoded[0]);
        var text = Encoding.UTF8.GetString(decoded);
        Assert.Contains("webh5&2608121011&websocket", text);
        Assert.Contains(guid, text);
    }

    [Fact]
    public void CreateGuid_Is32LowerHexChars()
    {
        var first = HuyaPacketParser.CreateGuid();
        var second = HuyaPacketParser.CreateGuid();

        Assert.Equal(32, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryParse_Cmd7Chat_ReturnsMessage()
    {
        var frame = BuildPushFrame(uri: 1400, BuildChatBody("123", "观众A", "hello"));

        Assert.True(HuyaPacketParser.TryParse(frame, out var messages));
        var message = Assert.Single(messages);
        Assert.Equal("123", message.SenderUid);
        Assert.Equal("观众A", message.SenderName);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public void TryParse_Cmd22GroupPush_ReturnsMessage()
    {
        var chatBody = BuildChatBody("123", "观众A", "group hello");
        var payload = new HuyaTarsWriter();
        payload.Write("chat:1199565822350", 0);
        payload.BeginList(1);
        payload.Write(1, 0);
        payload.BeginStruct(0);
        payload.Write(1400, 0);
        payload.Write(chatBody, 1);
        payload.EndStruct();

        var frame = BuildFrame(command: 22, payload.ToArray());

        Assert.True(HuyaPacketParser.TryParse(frame, out var messages));
        Assert.Equal("group hello", Assert.Single(messages).Content);
    }

    [Fact]
    public void TryParse_NonChatUri_Ignored()
    {
        var frame = BuildPushFrame(uri: 8006, new byte[] { 0x00, 0x64 });

        Assert.True(HuyaPacketParser.TryParse(frame, out var messages));
        Assert.Empty(messages);
    }

    [Fact]
    public void TryParse_EmptyContent_Ignored()
    {
        var frame = BuildPushFrame(uri: 1400, BuildChatBody("123", "观众A", string.Empty));

        Assert.True(HuyaPacketParser.TryParse(frame, out var messages));
        Assert.Empty(messages);
    }

    [Fact]
    public void TryParse_BodyWithoutSender_UsesFallbacks()
    {
        var body = new HuyaTarsWriter();
        body.Write("只看得到内容", 3);
        var frame = BuildPushFrame(uri: 1400, body.ToArray());

        Assert.True(HuyaPacketParser.TryParse(frame, out var messages));
        var message = Assert.Single(messages);
        Assert.Equal("0", message.SenderUid);
        Assert.Equal("未知用户", message.SenderName);
        Assert.Equal("只看得到内容", message.Content);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x00, 0x07, 0x1D, 0x00, 0x00, 0x7F })]
    [InlineData(new byte[0])]
    public void TryParse_MalformedFrame_ReturnsFalse(byte[] frame)
    {
        Assert.False(HuyaPacketParser.TryParse(frame, out _));
    }

    private static byte[] BuildPushFrame(long uri, byte[] body)
    {
        var payload = new HuyaTarsWriter();
        payload.Write(0, 0);
        payload.Write(uri, 1);
        payload.Write(body, 2);
        return BuildFrame(command: 7, payload.ToArray());
    }

    private static byte[] BuildFrame(long command, byte[] payload)
    {
        var frame = new HuyaTarsWriter();
        frame.Write(command, 0);
        frame.Write(payload, 1);
        return frame.ToArray();
    }

    private static byte[] BuildChatBody(string uid, string nick, string content)
    {
        var body = new HuyaTarsWriter();
        body.BeginStruct(0);
        body.Write(long.Parse(uid), 0);
        body.Write(nick, 2);
        body.Write(string.Empty, 4);
        body.EndStruct();
        body.Write(content, 3);
        body.BeginStruct(6);
        body.Write(-1, 0);
        body.EndStruct();
        return body.ToArray();
    }

    private static void AddTarsString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        buffer.Add(0x06);
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }
}
