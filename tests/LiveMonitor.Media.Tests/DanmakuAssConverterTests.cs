using LiveMonitor.Core.Models;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class DanmakuAssConverterTests : IDisposable
{
    private readonly string _workDir;

    public DanmakuAssConverterTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "limoclip-ass-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
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
    public async Task Writes_header_and_only_items_in_range()
    {
        var items = new[]
        {
            new RecordedDanmakuItem(1000, "a", "太早了", false, false),
            new RecordedDanmakuItem(3000, "b", "命中一", false, false),
            new RecordedDanmakuItem(4000, "c", "命中二", false, false),
            new RecordedDanmakuItem(9000, "d", "太晚了", false, false),
        };
        var assPath = Path.Combine(_workDir, "range.ass");

        var count = await new DanmakuAssConverter().ConvertToAssAsync(
            items, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), assPath);

        Assert.Equal(2, count);
        var content = await File.ReadAllTextAsync(assPath);
        Assert.Contains("[Script Info]", content);
        Assert.Contains("[Events]", content);

        var dialogues = content.Split('\n').Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, dialogues.Count);
        Assert.Contains(dialogues, line => line.Contains("0:00:01.00", StringComparison.Ordinal)); // 3000ms - 2000ms
        Assert.Contains(dialogues, line => line.Contains("0:00:02.00", StringComparison.Ordinal)); // 4000ms - 2000ms
        Assert.DoesNotContain("太早了", content);
        Assert.DoesNotContain("太晚了", content);
    }

    [Fact]
    public async Task Writes_utf8_bom_and_scroll_layout()
    {
        var items = new[] { new RecordedDanmakuItem(0, "a", "滚动弹幕", false, false) };
        var assPath = Path.Combine(_workDir, "bom.ass");

        await new DanmakuAssConverter().ConvertToAssAsync(items, TimeSpan.Zero, TimeSpan.FromSeconds(10), assPath);

        var bytes = await File.ReadAllBytesAsync(assPath);
        Assert.True(bytes.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var content = await File.ReadAllTextAsync(assPath);
        Assert.Contains(@"{\move(1920,", content);
        Assert.Contains(",0:00:10.00,R2L", content); // 默认滚动时长为 10s
    }

    [Fact]
    public async Task Escapes_ass_control_characters_and_newlines()
    {
        var items = new[] { new RecordedDanmakuItem(0, "a", "带{大括号}\\反斜杠\n换行", false, false) };
        var assPath = Path.Combine(_workDir, "escape.ass");

        await new DanmakuAssConverter().ConvertToAssAsync(items, TimeSpan.Zero, TimeSpan.FromSeconds(10), assPath);
        var content = await File.ReadAllTextAsync(assPath);

        Assert.Contains("带｛大括号｝", content);
        Assert.DoesNotContain("{大括号}", content);
        Assert.Contains("反斜杠 换行", content); // 换行被替换为空格
        Assert.Contains("\\\\", content); // 单个反斜杠被双写成转义形式
    }

    [Fact]
    public async Task Drops_overflow_items_when_tracks_are_full()
    {
        // PlayResY=1080 × 0.25 = 270px 顶部区域；字号 42 + 8 = 50px 行高 → 5 条轨道。
        var items = Enumerable.Range(0, 20)
            .Select(index => new RecordedDanmakuItem(0, "u", $"弹幕{index}", false, false))
            .ToArray();
        var assPath = Path.Combine(_workDir, "overflow.ass");
        var style = new DanmakuAssStyle(DisplayAreaRatio: 0.25);

        var count = await new DanmakuAssConverter().ConvertToAssAsync(
            items, TimeSpan.Zero, TimeSpan.FromSeconds(10), assPath, style);

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task Excludes_sticker_text_when_configured()
    {
        var items = new[]
        {
            new RecordedDanmakuItem(0, "a", "[表情]", true, false),
            new RecordedDanmakuItem(500, "b", "普通文字", false, false),
        };

        var withoutStickerPath = Path.Combine(_workDir, "nosticker.ass");
        var withoutSticker = await new DanmakuAssConverter().ConvertToAssAsync(
            items, TimeSpan.Zero, TimeSpan.FromSeconds(10), withoutStickerPath,
            new DanmakuAssStyle(IncludeStickersAsText: false));
        Assert.Equal(1, withoutSticker);
        Assert.DoesNotContain("[表情]", await File.ReadAllTextAsync(withoutStickerPath));

        var withStickerPath = Path.Combine(_workDir, "sticker.ass");
        var withSticker = await new DanmakuAssConverter().ConvertToAssAsync(
            items, TimeSpan.Zero, TimeSpan.FromSeconds(10), withStickerPath,
            new DanmakuAssStyle(IncludeStickersAsText: true));
        Assert.Equal(2, withSticker);
    }

    [Fact]
    public async Task Rejects_end_not_after_start()
    {
        var converter = new DanmakuAssConverter();
        var assPath = Path.Combine(_workDir, "invalid.ass");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            converter.ConvertToAssAsync([], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), assPath));
    }
}
