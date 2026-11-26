using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class AsrStatusTextTests
{
    [Fact]
    public void Constants_keep_original_copy()
    {
        Assert.Equal("请先在时间轴上选择有效的入出点区间。", AsrStatusText.SelectionInvalidError);
        Assert.Equal("选区无效", AsrStatusText.SelectionInvalid);
        Assert.Equal("高能片段过短，无法转写。", AsrStatusText.HighlightTooShortError);
        Assert.Equal("片段过短，无法转写", AsrStatusText.HighlightTooShort);
        Assert.Equal("正在取消…", AsrStatusText.Canceling);
        Assert.Equal("已复制到剪贴板", AsrStatusText.Copied);
        Assert.Equal("尚未识别", AsrStatusText.Idle);
        Assert.Equal("正在识别当前选区…", AsrStatusText.RunningSelection);
        Assert.Equal("正在转写高能片段…", AsrStatusText.RunningHighlight);
        Assert.Equal("已取消", AsrStatusText.Canceled);
        Assert.Equal("识别失败", AsrStatusText.Failed);
        Assert.Equal("请先打开本地视频", AsrStatusText.NoVideo);
    }

    [Fact]
    public void CopyFailed_includes_message()
        => Assert.Equal("复制识别文本失败：剪贴板被占用", AsrStatusText.CopyFailed("剪贴板被占用"));

    [Fact]
    public void ExtendedConstants_keep_original_copy()
    {
        Assert.Equal("语音识别失败，请稍后重试。", AsrStatusText.FailedMessage);
        Assert.Equal("未知服务商", AsrStatusText.UnknownProvider);
        Assert.Equal("识别完成：未识别到文本", AsrStatusText.CompletedEmpty);
    }

    [Fact]
    public void FailedWithMessage_prefixes_reason()
        => Assert.Equal("语音识别失败：尚未配置语音识别 API", AsrStatusText.FailedWithMessage("尚未配置语音识别 API"));

    [Fact]
    public void Completed_includes_line_count()
    {
        Assert.Equal("识别完成：2 句", AsrStatusText.Completed(2));
        Assert.Equal("识别完成：1 句", AsrStatusText.Completed(1));
    }
}
