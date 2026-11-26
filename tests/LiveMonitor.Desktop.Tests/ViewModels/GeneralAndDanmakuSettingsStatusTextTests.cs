using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class GeneralAndDanmakuSettingsStatusTextTests
{
    [Theory]
    [InlineData(GeneralAndDanmakuSettingsStatusText.KeywordExists, "这个词已经在屏蔽词库中。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.KeywordAdded, "已添加屏蔽词。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.RegexKeywordAdded, "已添加正则屏蔽词。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.KeywordNotSelected, "请先选中一个屏蔽词。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.KeywordRemoved, "已删除屏蔽词。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.HighlightSaved, "高能参数已保存（对运行中的会话即时生效）。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.HighlightReset, "高能参数已恢复默认值。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.TwitchProxySavedBuiltin, "已保存：Twitch 仅使用内置原生播放列表（GQL + Usher）。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.TwitchProxySavedCustom, "已保存：Twitch 将优先使用自定义去广告代理。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.UnicycleEnabled, "独轮车自动屏蔽已开启。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.UnicycleDisabled, "独轮车自动屏蔽已关闭。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.UnicycleSaveFailedPrefix, "保存开关失败")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.DanmakuPreviewEnabled, "监控画面弹幕预览已开启。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.DanmakuPreviewDisabled, "监控画面弹幕预览已关闭。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.DanmakuPreviewSaveFailedPrefix, "保存弹幕预览开关失败")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.StickerEnabled, "纯表情包会渲染为 [表情名] 文本。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.StickerDisabled, "纯表情包仅参与统计与高能检测，不再渲染。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.StickerSaveFailedPrefix, "保存表情包渲染开关失败")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.EmoteImagesEnabled, "表情图片已开启（仅渲染静态帧，未加载成功的表情先显示 [表情名]）。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.EmoteImagesDisabled, "表情图片已关闭，表情按 [表情] 文本显示。")]
    [InlineData(GeneralAndDanmakuSettingsStatusText.EmoteImagesSaveFailedPrefix, "保存表情图片开关失败")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("读取设置失败：db down", "db down")]
    [InlineData("添加失败：disk full", "disk full")]
    [InlineData("删除失败：not found", "not found")]
    [InlineData("保存高能参数失败：io", "io")]
    [InlineData("恢复默认值失败：io", "io")]
    [InlineData("保存 Twitch 代理失败：timeout", "timeout")]
    public void FailedTemplate_PrependsOperation(string actual, string message)
    {
        Assert.EndsWith(message, actual);
        Assert.Contains("失败", actual);
    }

    [Fact]
    public void LoadFailed_PrependsContext()
    {
        Assert.Equal("读取设置失败：db down", GeneralAndDanmakuSettingsStatusText.LoadFailed("db down"));
    }

    [Fact]
    public void AddKeywordFailed_PrependsContext()
    {
        Assert.Equal("添加失败：disk full", GeneralAndDanmakuSettingsStatusText.AddKeywordFailed("disk full"));
    }

    [Fact]
    public void RemoveKeywordFailed_PrependsContext()
    {
        Assert.Equal("删除失败：not found", GeneralAndDanmakuSettingsStatusText.RemoveKeywordFailed("not found"));
    }

    [Fact]
    public void HighlightFailures_PrependContext()
    {
        Assert.Equal("保存高能参数失败：io", GeneralAndDanmakuSettingsStatusText.HighlightSaveFailed("io"));
        Assert.Equal("恢复默认值失败：io", GeneralAndDanmakuSettingsStatusText.HighlightResetFailed("io"));
    }

    [Fact]
    public void TwitchProxySaveFailed_PrependsContext()
    {
        Assert.Equal("保存 Twitch 代理失败：timeout", GeneralAndDanmakuSettingsStatusText.TwitchProxySaveFailed("timeout"));
    }

    [Fact]
    public void ToggleSaveFailed_JoinsPrefixWithColon()
    {
        Assert.Equal(
            "保存弹幕预览开关失败：拒绝访问",
            GeneralAndDanmakuSettingsStatusText.ToggleSaveFailed(
                GeneralAndDanmakuSettingsStatusText.DanmakuPreviewSaveFailedPrefix,
                "拒绝访问"));
    }

    [Theory]
    [InlineData(true, "已添加正则屏蔽词。")]
    [InlineData(false, "已添加屏蔽词。")]
    public void KeywordAddedFor_ChoosesWordingByRegexFlag(bool isRegex, string expected)
    {
        Assert.Equal(expected, GeneralAndDanmakuSettingsStatusText.KeywordAddedFor(isRegex));
    }

    [Fact]
    public void TogglePairs_AreDistinct()
    {
        Assert.NotEqual(GeneralAndDanmakuSettingsStatusText.UnicycleEnabled, GeneralAndDanmakuSettingsStatusText.UnicycleDisabled);
        Assert.NotEqual(GeneralAndDanmakuSettingsStatusText.DanmakuPreviewEnabled, GeneralAndDanmakuSettingsStatusText.DanmakuPreviewDisabled);
        Assert.NotEqual(GeneralAndDanmakuSettingsStatusText.StickerEnabled, GeneralAndDanmakuSettingsStatusText.StickerDisabled);
        Assert.NotEqual(GeneralAndDanmakuSettingsStatusText.EmoteImagesEnabled, GeneralAndDanmakuSettingsStatusText.EmoteImagesDisabled);
    }
}
