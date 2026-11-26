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
}
