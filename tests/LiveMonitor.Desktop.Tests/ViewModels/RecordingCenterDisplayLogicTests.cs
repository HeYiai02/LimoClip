using System;
using System.IO;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class RecordingCenterDisplayLogicTests
{
    [Theory]
    [InlineData(0, 0, "共 0 个直播间 · 正在录制 0 路")]
    [InlineData(0, 3, "共 0 个直播间 · 正在录制 3 路")]
    [InlineData(3, 2, "共 3 个直播间 · 正在录制 2 路")]
    [InlineData(12, 12, "共 12 个直播间 · 正在录制 12 路")]
    public void TargetSummary_FormatsCounts(int targetCount, int recordingCount, string expected)
    {
        Assert.Equal(expected, RecordingCenterDisplayLogic.TargetSummary(targetCount, recordingCount));
    }

    [Theory]
    [InlineData(0, 0, "已刷新：0 个直播间 · 0 条历史录像")]
    [InlineData(5, 7, "已刷新：5 个直播间 · 7 条历史录像")]
    public void RefreshStatusText_FormatsCounts(int targetCount, int historyCount, string expected)
    {
        Assert.Equal(expected, RecordingCenterDisplayLogic.RefreshStatusText(targetCount, historyCount));
    }

    [Fact]
    public void NormalizeDirectory_EmptyOrNull_FallsBackToDefault()
    {
        const string fallback = @"C:\default-recordings";

        Assert.Equal(fallback, RecordingCenterDisplayLogic.NormalizeDirectory(null, fallback));
        Assert.Equal(fallback, RecordingCenterDisplayLogic.NormalizeDirectory(string.Empty, fallback));
        Assert.Equal(fallback, RecordingCenterDisplayLogic.NormalizeDirectory("   ", fallback));
    }

    [Fact]
    public void NormalizeDirectory_AbsolutePath_ReturnsFullPath()
    {
        var expected = Path.GetFullPath(Path.GetTempPath());

        Assert.Equal(expected, RecordingCenterDisplayLogic.NormalizeDirectory(Path.GetTempPath(), @"C:\fallback"));
    }

    [Fact]
    public void NormalizeDirectory_TrimsSurroundingWhitespace()
    {
        var expected = Path.GetFullPath(@"C:\Limoclip\Recordings");

        Assert.Equal(expected, RecordingCenterDisplayLogic.NormalizeDirectory(@"   C:\Limoclip\Recordings   ", @"C:\fallback"));
    }

    [Fact]
    public void NormalizeDirectory_ExpandsEnvironmentVariables()
    {
        // 用测试自建的变量而不是 %TEMP%：进程的 TMP/TEMP 可能指向不同目录，
        // 而 Path.GetTempPath() 优先取 TMP，直接比较 %TEMP% 会随环境漂移。
        const string fallback = @"C:\fallback";
        const string variableName = "LIMOCLIP_NORMALIZE_TEST_ROOT";
        var root = Path.Combine(Path.GetTempPath(), "limoclip-normalize-test-root");
        var expected = Path.GetFullPath(Path.Combine(root, "limoclip-normalize-test"));
        Environment.SetEnvironmentVariable(variableName, root);
        try
        {
            var actual = RecordingCenterDisplayLogic.NormalizeDirectory(
                $"%{variableName}%\\limoclip-normalize-test",
                fallback);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void NormalizeDirectory_RelativePath_BecomesAbsolute()
    {
        var expected = Path.GetFullPath("limoclip-subdir");

        Assert.Equal(expected, RecordingCenterDisplayLogic.NormalizeDirectory("limoclip-subdir", @"C:\fallback"));
    }

    [Fact]
    public void NormalizeDirectory_Dot_ResolvesToCurrentDirectory()
    {
        Assert.Equal(Path.GetFullPath("."), RecordingCenterDisplayLogic.NormalizeDirectory(".", @"C:\fallback"));
    }

}
