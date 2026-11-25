using System.Text;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class DanmakuJsonlReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests.Jsonl",
        Guid.NewGuid().ToString("N"));

    public DanmakuJsonlReaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task ReadAllAsync_SkipsMetaBadLinesAndKeepsValidItemsInOrder()
    {
        var path = Path.Combine(_directory, "recording.jsonl");
        var lines = new[]
        {
            """{"type":"meta","version":1,"sessionId":"57702b6d-383b-4ec3-bcd4-01d2b5c9ede1","platform":1,"roomId":"6650029"}""",
            """{"type":"dm","offsetMs":21095,"uid":"1","uname":"\u5C0F\u6768\u5B63\u5F55\u5458","text":"\u8D5B\u7A0B","isSticker":false,"isGift":false}""",
            """{"type":"unknown","offsetMs":22000}""",
            """{"type":"dm","offsetMs":5000,"uname":"a","text":"later line but earlier offset"}""",
            """{"type":"dm","offsetMs":-50,"uname":"b","text":"negative"}""",
            """{"type":"dm","uname":"c","text":"missing offset"}""",
            """{"type":"dm","offsetMs":30000,"uname":"d","text":"sticker","isSticker":true}""",
            """{"type":"dm","offsetMs":40000,"uname":"e","text":"truncated""",
            "",
            "not json at all"
        };

        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));

        var items = await DanmakuJsonlReader.ReadAllAsync(path);

        Assert.Equal(4, items.Count);
        Assert.Equal(new[] { 0L, 5000L, 21095L, 30000L }, items.Select(i => i.OffsetMs));
        Assert.Equal("b", items[0].SenderName); // 负偏移被夹到 0
        Assert.Equal("小杨季录员", items[2].SenderName);
        Assert.Equal("赛程", items[2].Text);
        Assert.True(items[3].IsSticker);
        Assert.False(items[3].IsGift);
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsEmptyForMetaOnlyFile()
    {
        var path = Path.Combine(_directory, "meta-only.jsonl");
        await File.WriteAllTextAsync(path, """{"type":"meta","version":1}""" + Environment.NewLine);

        var items = await DanmakuJsonlReader.ReadAllAsync(path);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAllAsync_ThrowsWhenFileMissing()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => DanmakuJsonlReader.ReadAllAsync(Path.Combine(_directory, "missing.jsonl")));
    }

    [Fact]
    public async Task ReadIncrementalAsync_ReadsCompleteLinesWhileWriterKeepsFileOpen()
    {
        var path = Path.Combine(_directory, "live.jsonl");
        var halfLine = "{\"type\":\"dm\",\"offsetMs\":7000,\"uname\":\"late\",\"text\":\"cut";
        var halfLineTail = " tail\"}";

        long length = 0;
        await using (var writer = new FileStream(
                         path, FileMode.Create, FileAccess.Write,
                         FileShare.Read, bufferSize: 4096, useAsync: true))
        {
            async Task WriteAsync(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await writer.WriteAsync(bytes);
                await writer.FlushAsync();
                length += bytes.Length;
            }

            // 录制端（DanmakuJsonlRecorder）的典型写状态：meta + 完整 dm 行 + 正在写的半截行。
            await WriteAsync("""{"type":"meta","version":1,"sessionId":"s1"}""" + "\n");
            await WriteAsync("""{"type":"dm","offsetMs":5000,"uname":"a","text":"first"}""" + "\n");
            await WriteAsync(halfLine);

            var first = await DanmakuJsonlReader.ReadIncrementalAsync(path, 0);

            var firstItem = Assert.Single(first.NewItems);
            Assert.Equal(5000, firstItem.OffsetMs);
            Assert.Equal("first", firstItem.Text);
            // 半截行不推进偏移：下次从它开头继续读。
            Assert.Equal(length - Encoding.UTF8.GetByteCount(halfLine), first.NextByteOffset);

            // 录制端补完半截行、再追加新行。
            await WriteAsync(halfLineTail + "\n");
            await WriteAsync("""{"type":"dm","offsetMs":9000,"uname":"b","text":"last"}""" + "\n");

            var second = await DanmakuJsonlReader.ReadIncrementalAsync(path, first.NextByteOffset);

            Assert.Equal(new[] { 7000L, 9000L }, second.NewItems.Select(item => item.OffsetMs));
            Assert.Equal("cut tail", second.NewItems[0].Text);
            Assert.Equal(length, second.NextByteOffset);

            // 没有新数据时返回空且偏移不变。
            var third = await DanmakuJsonlReader.ReadIncrementalAsync(path, second.NextByteOffset);
            Assert.Empty(third.NewItems);
            Assert.Equal(second.NextByteOffset, third.NextByteOffset);
        }

        // 从头重读可拿到全部完整行（含之前半截、后来补完的那条）。
        var all = await DanmakuJsonlReader.ReadIncrementalAsync(path, 0);
        Assert.Equal(new[] { 5000L, 7000L, 9000L }, all.NewItems.Select(item => item.OffsetMs));
        Assert.Equal(length, all.NextByteOffset);
    }

    [Fact]
    public async Task ReadIncrementalAsync_ResetsOffsetWhenFileWasTruncatedOrRewritten()
    {
        var path = Path.Combine(_directory, "rewritten.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"dm","offsetMs":100,"text":"a"}""" + "\n" +
            """{"type":"dm","offsetMs":200,"text":"b"}""" + "\n",
            new UTF8Encoding(false));

        var result = await DanmakuJsonlReader.ReadIncrementalAsync(path, 10_000_000);

        Assert.Equal(new[] { 100L, 200L }, result.NewItems.Select(item => item.OffsetMs));
    }

    [Fact]
    public async Task ReadIncrementalAsync_SkipsBadLinesAndThrowsWhenMissing()
    {
        var path = Path.Combine(_directory, "with-bad.jsonl");
        await File.WriteAllTextAsync(
            path,
            "not json\n" +
            """{"type":"meta"}""" + "\n" +
            """{"type":"dm","offsetMs":300,"text":"good"}""" + "\n" +
            "{\"type\":\"dm\",\"offsetMs\":400,\"text\":\"still writing\"",
            new UTF8Encoding(false));

        var result = await DanmakuJsonlReader.ReadIncrementalAsync(path, 0);

        Assert.Single(result.NewItems);
        Assert.Equal(300, result.NewItems[0].OffsetMs);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => DanmakuJsonlReader.ReadIncrementalAsync(
                Path.Combine(_directory, "missing-live.jsonl"), 0));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("""{"type":"meta"}""", false)]
    [InlineData("""{"type":"dm"}""", false)]
    [InlineData("""{"type":"dm","offsetMs":"12"}""", false)]
    [InlineData("""{"type":"dm","offsetMs":12}""", true)]
    [InlineData("""{"offsetMs":34,"uname":"u","text":"t"}""", true)]
    public void TryParseLine_HandlesMalformedInput(string line, bool expected)
    {
        Assert.Equal(expected, DanmakuJsonlReader.TryParseLine(line, out _));
    }
}
