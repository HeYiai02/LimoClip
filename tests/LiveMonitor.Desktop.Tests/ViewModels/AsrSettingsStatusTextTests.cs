using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class AsrSettingsStatusTextTests
{
    [Fact]
    public void LanguageNames_KeepOrderAndWording()
    {
        Assert.Equal(new[] { "自动检测", "中文", "英文", "日文", "韩文" }, AsrSettingsStatusText.LanguageNames);
    }

    [Theory]
    [InlineData(AsrSettingsStatusText.Initial, "尚未读取语音识别设置。")]
    [InlineData(AsrSettingsStatusText.LoadedConfigured, "已读取语音识别设置。")]
    [InlineData(AsrSettingsStatusText.LoadedMissingApiKey, "尚未配置 API Key，请粘贴后点击「保存」。")]
    [InlineData(AsrSettingsStatusText.WatermarkSaved, "已保存，留空保持不变")]
    [InlineData(AsrSettingsStatusText.WatermarkEmpty, "粘贴 API Key")]
    [InlineData(AsrSettingsStatusText.TestButtonIdle, "测试连接")]
    [InlineData(AsrSettingsStatusText.TestButtonRunning, "正在测试…")]
    [InlineData(AsrSettingsStatusText.Testing, "正在测试连接…")]
    [InlineData(AsrSettingsStatusText.Saving, "正在保存语音识别设置…")]
    [InlineData(AsrSettingsStatusText.Saved, "语音识别设置已保存。")]
    [InlineData(AsrSettingsStatusText.CustomPresetHint, "自定义服务需要填写接口地址和模型（OpenAI 兼容）。")]
    [InlineData(AsrSettingsStatusText.InvalidCustomPreset, "自定义服务需要填写接口地址和模型。")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PresetSwitched_IncludesDisplayNameAndSaveHint()
    {
        Assert.Equal("已切换为「Groq」，点击「保存」后生效。", AsrSettingsStatusText.PresetSwitched("Groq"));
    }

    [Fact]
    public void LoadFailed_PrependsOperation()
    {
        Assert.Equal("读取语音识别设置失败：db down", AsrSettingsStatusText.LoadFailed("db down"));
    }

    [Fact]
    public void SaveFailed_PrependsOperation()
    {
        Assert.Equal("保存语音识别设置失败：disk full", AsrSettingsStatusText.SaveFailed("disk full"));
    }

    [Fact]
    public void TestFailed_PrependsOperation()
    {
        Assert.Equal("测试连接失败：timeout", AsrSettingsStatusText.TestFailed("timeout"));
    }

    [Fact]
    public void BusyAndIdleButtonTexts_AreDistinct()
    {
        Assert.NotEqual(AsrSettingsStatusText.TestButtonIdle, AsrSettingsStatusText.TestButtonRunning);
        Assert.NotEqual(AsrSettingsStatusText.WatermarkSaved, AsrSettingsStatusText.WatermarkEmpty);
    }
}
