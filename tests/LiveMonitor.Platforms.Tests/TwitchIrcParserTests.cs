using LiveMonitor.Core.Enums;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// TwitchIrcParser 的协议层测试：IRCv3 tags 反转义、prefix/command/参数切分、
/// 多行帧拆分、PRIVMSG→DanmakuMessage 映射、空行/畸形行容错与时间戳注入。
/// </summary>
public sealed class TwitchIrcParserTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private const string FullTagsLine =
        """@badge-info=;bits=100;color=#1E90FF;display-name=Alice\sSmith;login=alice;room-id=9;user-id=42 :alice!alice@alice.tmi.twitch.tv PRIVMSG #SomeChannel :hello world""";

    [Fact]
    public void ParseLine_FullTagsPrivmsg_ParsesTagsPrefixCommandAndParameters()
    {
        var message = TwitchIrcParser.ParseLine(FullTagsLine);

        Assert.NotNull(message);
        Assert.Equal("alice!alice@alice.tmi.twitch.tv", message.Prefix);
        Assert.Equal("PRIVMSG", message.Command);
        Assert.Equal(new[] { "#SomeChannel", "hello world" }, message.Parameters);

        Assert.Equal(string.Empty, message.Tags["badge-info"]);
        Assert.Equal("100", message.Tags["bits"]);
        Assert.Equal("#1E90FF", message.Tags["color"]);
        Assert.Equal("Alice Smith", message.Tags["display-name"]);
        Assert.Equal("alice", message.Tags["login"]);
        Assert.Equal("42", message.Tags["user-id"]);

        // Channel 去 # 转小写；Login/DisplayName/UserId/Text 从 tags、prefix、参数统一取出。
        Assert.Equal("somechannel", message.Channel);
        Assert.Equal("alice", message.Login);
        Assert.Equal("Alice Smith", message.DisplayName);
        Assert.Equal("42", message.UserId);
        Assert.Equal("hello world", message.Text);
    }

    [Fact]
    public void ToDanmakuMessage_PrivmsgWithBits_MapsAllFieldsAndGiftFlag()
    {
        var message = TwitchIrcParser.ParseLine(FullTagsLine);
        Assert.NotNull(message);

        var danmaku = TwitchIrcParser.ToDanmakuMessage(message, () => FixedTime);

        Assert.NotNull(danmaku);
        Assert.Equal(PlatformType.Twitch, danmaku.Platform);
        Assert.Equal("somechannel", danmaku.RoomId);
        Assert.Equal("42", danmaku.SenderUid);
        Assert.Equal("Alice Smith", danmaku.SenderName);
        Assert.Equal("hello world", danmaku.Content);
        Assert.Equal(FixedTime, danmaku.Timestamp);
        Assert.True(danmaku.IsGiftOrSuperChat);
    }

    [Fact]
    public void ParseLine_NoTagsPrivmsg_FallsBackToPrefixNickAndAnonymousUid()
    {
        var message = TwitchIrcParser.ParseLine(":bob!bob@bob.tmi.twitch.tv PRIVMSG #chan :hi");

        Assert.NotNull(message);
        Assert.Empty(message.Tags);
        Assert.Equal("bob!bob@bob.tmi.twitch.tv", message.Prefix);
        Assert.Equal("bob", message.Login);
        Assert.Equal("bob", message.DisplayName);
        Assert.Null(message.UserId);
        Assert.Equal("chan", message.Channel);
        Assert.Equal("hi", message.Text);

        var danmaku = TwitchIrcParser.ToDanmakuMessage(message, () => FixedTime);

        Assert.NotNull(danmaku);
        Assert.Equal(string.Empty, danmaku.SenderUid);
        Assert.Equal("bob", danmaku.SenderName);
        Assert.Equal("hi", danmaku.Content);
        Assert.False(danmaku.IsGiftOrSuperChat);
        Assert.True(danmaku.IsAnonymousSender);
    }

    [Fact]
    public void ParseLine_DisplayNameTagEmpty_FallsBackToLogin()
    {
        var message = TwitchIrcParser.ParseLine(
            "@display-name=;login=carol :carol!carol@carol.tmi.twitch.tv PRIVMSG #chan :x");

        Assert.NotNull(message);
        Assert.Equal("carol", message.DisplayName);
    }

    [Fact]
    public void ParseLine_JoinLine_ExposesChannelAndNoText()
    {
        var message = TwitchIrcParser.ParseLine(
            ":justinfan12345!justinfan12345@justinfan12345.tmi.twitch.tv JOIN #SomeChannel");

        Assert.NotNull(message);
        Assert.Equal("JOIN", message.Command);
        Assert.Equal(new[] { "#SomeChannel" }, message.Parameters);
        Assert.Equal("somechannel", message.Channel);
        Assert.Equal("justinfan12345", message.Login);
        Assert.Null(message.Text);
        Assert.Null(TwitchIrcParser.ToDanmakuMessage(message));
    }

    [Fact]
    public void ParseLine_PingLine_HasTrailingParameterButNoChannel()
    {
        var message = TwitchIrcParser.ParseLine("PING :tmi.twitch.tv");

        Assert.NotNull(message);
        Assert.Null(message.Prefix);
        Assert.Empty(message.Tags);
        Assert.Equal("PING", message.Command);
        Assert.Equal(new[] { "tmi.twitch.tv" }, message.Parameters);
        Assert.Null(message.Channel);
        Assert.Null(message.Text);
    }

    [Fact]
    public void ParseLine_WelcomeAndNamesLines_AreParsedWithChannel()
    {
        var welcome = TwitchIrcParser.ParseLine(
            ":tmi.twitch.tv 001 justinfan12345 :Welcome, GLHF!");
        var names = TwitchIrcParser.ParseLine(
            ":tmi.twitch.tv 366 justinfan12345 #chan :End of /NAMES list.");

        Assert.NotNull(welcome);
        Assert.Equal("001", welcome.Command);
        Assert.Equal(new[] { "justinfan12345", "Welcome, GLHF!" }, welcome.Parameters);
        Assert.Null(welcome.Channel);
        Assert.Null(welcome.Text);

        Assert.NotNull(names);
        Assert.Equal("366", names.Command);
        Assert.Equal(new[] { "justinfan12345", "#chan", "End of /NAMES list." }, names.Parameters);
        Assert.Equal("chan", names.Channel);
        Assert.Null(names.Text);
    }

    [Fact]
    public void ParseLine_TrailingParameter_KeepsColonsAndSpaces()
    {
        var message = TwitchIrcParser.ParseLine(
            ":bob!bob@bob.tmi.twitch.tv PRIVMSG #chan :a :b c");

        Assert.NotNull(message);
        Assert.Equal(new[] { "#chan", "a :b c" }, message.Parameters);
        Assert.Equal("a :b c", message.Text);
    }

    [Fact]
    public void ParseLine_UnescapesIrcv3TagsAndTrimsTrailingCarriageReturn()
    {
        var message = TwitchIrcParser.ParseLine(
            """@display-name=foo\sbar\:baz\\qux\r\n PING :tmi.twitch.tv""" + "\r");

        Assert.NotNull(message);
        Assert.Equal("foo bar;baz\\qux\r\n", message.Tags["display-name"]);
        Assert.Equal("PING", message.Command);
    }

    [Fact]
    public void ParseLine_Ircv3UnknownEscape_DropsBackslashAndKeepsCharacter()
    {
        var message = TwitchIrcParser.ParseLine("@display-name=a\\qb PING :tmi.twitch.tv");

        Assert.NotNull(message);
        Assert.Equal("aqb", message.Tags["display-name"]);
    }

    [Fact]
    public void ParseFrame_MultipleLinesAndEmptyLines_ParsesEachNonEmptyLine()
    {
        var frame =
            "PING :tmi.twitch.tv\r\n"
            + "\r\n"
            + ":justinfan1!justinfan1@justinfan1.tmi.twitch.tv JOIN #channel\n"
            + "\n"
            + ":bob!bob@bob.tmi.twitch.tv PRIVMSG #channel :hi\r\n";

        var messages = TwitchIrcParser.ParseFrame(frame);

        Assert.Equal(3, messages.Count);
        Assert.Equal("PING", messages[0].Command);
        Assert.Equal("JOIN", messages[1].Command);
        Assert.Equal("PRIVMSG", messages[2].Command);
        Assert.Equal("hi", messages[2].Text);
    }

    [Fact]
    public void ParseFrame_EmptyOrNull_ReturnsEmptyList()
    {
        Assert.Empty(TwitchIrcParser.ParseFrame(string.Empty));
        Assert.Empty(TwitchIrcParser.ParseFrame(null));
        Assert.Empty(TwitchIrcParser.ParseFrame("\r\n\r\n"));
        Assert.Empty(TwitchIrcParser.ParseFrame("   \n\r\n"));
    }

    [Fact]
    public void ParseLine_EmptyOrMalformed_ReturnsNull()
    {
        Assert.Null(TwitchIrcParser.ParseLine(null));
        Assert.Null(TwitchIrcParser.ParseLine(string.Empty));
        Assert.Null(TwitchIrcParser.ParseLine("   "));
        Assert.Null(TwitchIrcParser.ParseLine("\r"));
        Assert.Null(TwitchIrcParser.ParseLine("@badge-info=;bits="));
        Assert.Null(TwitchIrcParser.ParseLine(":alice!alice@alice.tmi.twitch.tv"));
    }

    [Fact]
    public void ToDanmakuMessage_NonPrivmsgOrMissingChannel_ReturnsNull()
    {
        var ping = TwitchIrcParser.ParseLine("PING :tmi.twitch.tv");
        var noChannel = TwitchIrcParser.ParseLine("PRIVMSG");

        Assert.Null(TwitchIrcParser.ToDanmakuMessage(null));
        Assert.Null(TwitchIrcParser.ToDanmakuMessage(ping));
        Assert.Null(TwitchIrcParser.ToDanmakuMessage(noChannel));
    }
}
