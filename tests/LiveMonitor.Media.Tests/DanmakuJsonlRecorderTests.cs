using System.Globalization;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class DanmakuJsonlRecorderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "LiveMonitor.Media.Tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task WritesMetaAndDanmakuOffsetsRelativeToAnchor()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var sessionId = Guid.NewGuid();
        var anchor = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

        var recorder = new DanmakuJsonlRecorder(
            path, sessionId, PlatformType.Twitch, "ludwig");
        recorder.UpdateAnchor(anchor);

        Assert.True(recorder.Enqueue(Message("第一条", anchor.AddMilliseconds(1500))));
        Assert.True(recorder.Enqueue(Message("第二条", anchor.AddSeconds(3))));
        // 早于锚点的弹幕（时钟校准回退）偏移量钳到 0。
        Assert.True(recorder.Enqueue(Message("早到的", anchor.AddMilliseconds(-500))));

        await recorder.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(4, lines.Length);

        using var meta = JsonDocument.Parse(lines[0]);
        Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, meta.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(sessionId.ToString("D"), meta.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal((int)PlatformType.Twitch, meta.RootElement.GetProperty("platform").GetInt32());
        Assert.Equal("ludwig", meta.RootElement.GetProperty("roomId").GetString());
        Assert.StartsWith("2026-11-23T08:00:00", meta.RootElement.GetProperty("startedAtUtc").GetString());

        var first = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("dm", first.GetProperty("type").GetString());
        Assert.Equal(1500, first.GetProperty("offsetMs").GetInt64());
        Assert.Equal("第一条", first.GetProperty("text").GetString());
        Assert.Equal("u1", first.GetProperty("uid").GetString());
        Assert.Equal("观众一", first.GetProperty("uname").GetString());
        Assert.False(first.GetProperty("isSticker").GetBoolean());
        Assert.False(first.GetProperty("isGift").GetBoolean());

        Assert.Equal(3000, JsonDocument.Parse(lines[2]).RootElement.GetProperty("offsetMs").GetInt64());
        Assert.Equal(0, JsonDocument.Parse(lines[3]).RootElement.GetProperty("offsetMs").GetInt64());

        Assert.Equal(3, recorder.WrittenCount);
    }

    [Fact]
    public async Task MetaIsWrittenOnce_AndLaterAnchorDoesNotRewriteIt()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var recorder = new DanmakuJsonlRecorder(path, Guid.NewGuid(), PlatformType.Bilibili, "1001");

        var anchor = new DateTimeOffset(2026, 11, 23, 12, 30, 0, TimeSpan.Zero);
        recorder.UpdateAnchor(anchor);
        recorder.UpdateAnchor(anchor.AddMinutes(5));
        recorder.Enqueue(Message("hi", anchor.AddMilliseconds(250)));
        await recorder.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(path);
        using var meta = JsonDocument.Parse(lines[0]);
        Assert.StartsWith("2026-11-23T12:30:00", meta.RootElement.GetProperty("startedAtUtc").GetString());
        Assert.Equal(250, JsonDocument.Parse(lines[1]).RootElement.GetProperty("offsetMs").GetInt64());
    }

    [Fact]
    public async Task DisposeBeforeAnchor_UsesFallbackAnchor_AndStillFlushesQueuedMessages()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var recorder = new DanmakuJsonlRecorder(
            path,
            Guid.NewGuid(),
            PlatformType.Bilibili,
            "1002",
            anchorWaitTimeout: TimeSpan.FromMinutes(10));
        recorder.Enqueue(Message("排队中的弹幕", DateTimeOffset.UtcNow));

        var startedAt = DateTimeOffset.UtcNow;
        await recorder.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("meta", JsonDocument.Parse(lines[0]).RootElement.GetProperty("type").GetString());

        var dm = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("排队中的弹幕", dm.GetProperty("text").GetString());
        var offset = dm.GetProperty("offsetMs").GetInt64();
        Assert.InRange(offset, 0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds + 500);
        Assert.Equal(1, recorder.WrittenCount);
    }

    [Fact]
    public async Task EnqueueAfterDispose_ReturnsFalse_AndIsIdempotent()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var recorder = new DanmakuJsonlRecorder(path, Guid.NewGuid(), PlatformType.Bilibili, "1003");
        recorder.UpdateAnchor(DateTimeOffset.UtcNow);

        await recorder.DisposeAsync();
        await recorder.DisposeAsync();

        Assert.False(recorder.Enqueue(Message("late", DateTimeOffset.UtcNow)));
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
    }

    [Fact]
    public async Task SpecialCharacters_AreEscapedAsValidJson()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var anchor = DateTimeOffset.UtcNow;
        var recorder = new DanmakuJsonlRecorder(path, Guid.NewGuid(), PlatformType.Bilibili, "1004");
        recorder.UpdateAnchor(anchor);

        recorder.Enqueue(Message("含\"引号\"与\\反斜杠\r\n换行", anchor));
        await recorder.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(path);
        var dm = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("含\"引号\"与\\反斜杠\r\n换行", dm.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChineseContent_IsWrittenAsReadableUtf8_NotEscaped()
    {
        var path = Path.Combine(_directory, "room.jsonl");
        var anchor = DateTimeOffset.UtcNow;
        var recorder = new DanmakuJsonlRecorder(path, Guid.NewGuid(), PlatformType.Bilibili, "1006");
        recorder.UpdateAnchor(anchor);

        recorder.Enqueue(Message("赛程", anchor));
        await recorder.DisposeAsync();

        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("\"text\":\"赛程\"", raw);
        Assert.DoesNotContain("\\u8D5B", raw);
    }

    [Fact]
    public async Task Creation_CreatesMissingDirectory()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "room.jsonl");
        var recorder = new DanmakuJsonlRecorder(path, Guid.NewGuid(), PlatformType.Bilibili, "1005");
        recorder.UpdateAnchor(DateTimeOffset.UtcNow);
        await recorder.DisposeAsync();

        Assert.True(File.Exists(path));
    }

    private static DanmakuMessage Message(string text, DateTimeOffset timestamp) => new(
        PlatformType.Twitch,
        "ludwig",
        "u1",
        "观众一",
        text,
        timestamp);
}
