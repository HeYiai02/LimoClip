using System.Text;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuSttTests
{
    [Fact]
    public void BuildPacket_WritesHeaderAndTrailingNull()
    {
        var payload = "type@=loginreq/roomid@=9999/\0";
        var packet = DouyuStt.BuildPacket(payload);

        var bodyLength = Encoding.UTF8.GetByteCount(payload);
        Assert.Equal(bodyLength + 12, packet.Length);
        Assert.Equal(bodyLength + 8, BitConverter.ToInt32(packet, 0));
        Assert.Equal(bodyLength + 8, BitConverter.ToInt32(packet, 4));
        Assert.Equal(DouyuStt.ClientTextType, BitConverter.ToInt16(packet, 8));
        Assert.Equal(0, packet[10]);
        Assert.Equal(0, packet[11]);
        Assert.Equal(0, packet[^1]);
        Assert.Equal(payload, Encoding.UTF8.GetString(packet, 12, bodyLength));
    }

    [Fact]
    public void BuildPacket_AppendsTerminatorWhenMissing()
    {
        var packet = DouyuStt.BuildPacket("type@=mrkl/");
        var text = Encoding.UTF8.GetString(packet, 12, packet.Length - 12);
        Assert.Equal("type@=mrkl/\0", text);
    }

    [Fact]
    public void TryParsePacket_RoundTripsPacket()
    {
        var packet = DouyuStt.BuildPacket("type@=chatmsg/nn@=a/txt@=b/\0", DouyuStt.ServerTextType);
        Assert.True(DouyuStt.TryParsePacket(packet, packet.Length, out var type, out var payload));
        Assert.Equal(DouyuStt.ServerTextType, type);
        Assert.Equal("type@=chatmsg/nn@=a/txt@=b/\0", payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    public void TryParsePacket_RejectsTooShortInput(int length)
    {
        var buffer = new byte[64];
        Assert.False(DouyuStt.TryParsePacket(buffer, length, out _, out _));
    }

    [Fact]
    public void TryParsePacket_RejectsInconsistentLength()
    {
        var packet = DouyuStt.BuildPacket("type@=chatmsg/txt@=hi/\0");
        packet[0] = (byte)(packet[0] + 4);
        Assert.False(DouyuStt.TryParsePacket(packet, packet.Length, out _, out _));
    }

    [Fact]
    public void ParseStt_ReadsKeyValuesAndUnescapes()
    {
        var payload = "type@=chatmsg/uid@=42/nn@=a@Sb/txt@=x@Ay/";
        var fields = DouyuStt.ParseStt(payload);

        Assert.Equal("chatmsg", fields["type"]);
        Assert.Equal("42", fields["uid"]);
        Assert.Equal("a/b", fields["nn"]);
        Assert.Equal("x@y", fields["txt"]);
    }

    [Fact]
    public void ParseStt_IgnoresMissingSeparatorAndEmptyValues()
    {
        var fields = DouyuStt.ParseStt("type@=chatmsg/junk/txt@=/");
        Assert.Equal("chatmsg", fields["type"]);
        Assert.Equal(string.Empty, fields["txt"]);
        Assert.Equal(2, fields.Count);
    }

    [Theory]
    [InlineData("a/b", "a@Sb")]
    [InlineData("a@b", "a@Ab")]
    [InlineData("a@S/b", "a@AS@Sb")]
    public void Escape_EscapesSeparators(string plain, string escaped)
    {
        Assert.Equal(escaped, DouyuStt.Escape(plain));
    }
}
