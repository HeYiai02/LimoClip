using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class RecordingDirectoryGuardTests : IDisposable
{
    private readonly string _workDir;

    public RecordingDirectoryGuardTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "limoclip-dir-guard-tests", Guid.NewGuid().ToString("N"));
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
    public void EnsureWritable_creates_missing_directory_and_leaves_no_probe_file()
    {
        string target = Path.Combine(_workDir, "recordings", "nested");

        RecordingDirectoryGuard.EnsureWritable(target);

        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(target, RecordingDirectoryGuard.ProbeFilePrefix + "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureWritable_rejects_blank_directory_with_actionable_message(string directory)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RecordingDirectoryGuard.EnsureWritable(directory));

        Assert.Contains("录制目录为空", ex.Message);
        Assert.Contains("修改目录", ex.Message);
    }

    [Fact]
    public void TryEnsureWritable_when_path_is_existing_file_returns_false_with_suggestion()
    {
        string filePath = Path.Combine(_workDir, "not-a-directory");
        File.WriteAllText(filePath, "x");

        bool ok = RecordingDirectoryGuard.TryEnsureWritable(filePath, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("录制目录不可写", error);
        Assert.Contains("修改目录", error);
    }

    [Fact]
    public void SuggestDirectories_returns_writable_candidate_paths()
    {
        var suggestions = RecordingDirectoryGuard.SuggestDirectories();

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, suggestion => Assert.False(string.IsNullOrWhiteSpace(suggestion)));
        Assert.All(suggestions, suggestion => Assert.True(Path.IsPathFullyQualified(suggestion)));
    }

    [Theory]
    [InlineData(-13, "Error opening output file: Permission denied", true)]
    [InlineData(13, "Permission denied", true)]
    [InlineData(1, "Access is denied", true)]
    [InlineData(1, "Error opening output file", true)]
    [InlineData(1, "拒绝访问", true)]
    [InlineData(1, "Error opening input: Connection refused", false)]
    [InlineData(0, "Permission denied", false)]
    public void IsWritePermissionFailure_classifies_ffmpeg_errors(int exitCode, string stderr, bool expected)
    {
        Assert.Equal(expected, RecordingDirectoryGuard.IsWritePermissionFailure(exitCode, stderr));
    }

    [Fact]
    public void DescribeFfmpegFailure_for_permission_error_includes_directory_and_suggestion()
    {
        string directory = Path.Combine(_workDir, "records");
        string message = RecordingDirectoryGuard.DescribeFfmpegFailure(
            -13,
            "Error opening output file D:\\clipRecord\\a.mkv: Permission denied",
            directory);

        Assert.Contains("无法写入录制文件", message);
        Assert.Contains(directory, message);
        Assert.Contains("修改目录", message);
        Assert.Contains("ffmpeg.exe", message);
        Assert.Contains("-13", message);
        Assert.Contains("Permission denied", message);
    }

    [Fact]
    public void DescribeFfmpegFailure_for_other_errors_keeps_exit_code_and_stderr()
    {
        string message = RecordingDirectoryGuard.DescribeFfmpegFailure(
            1,
            "Error opening input: Connection refused",
            Path.Combine(_workDir, "records"));

        Assert.StartsWith("ffmpeg 异常退出（exitCode=1）", message);
        Assert.Contains("Connection refused", message);
        Assert.DoesNotContain("修改目录", message);
    }

    [Fact]
    public void DescribeFfmpegFailure_truncates_long_stderr_tail()
    {
        string stderr = new('x', 400);
        string message = RecordingDirectoryGuard.DescribeFfmpegFailure(2, stderr, _workDir);

        Assert.Contains("exitCode=2", message);
        Assert.True(message.Length < 400, $"message length={message.Length}");
    }

    [Fact]
    public void TryGetDirectory_extracts_parent_and_handles_invalid_input()
    {
        string file = Path.Combine(_workDir, "a", "b.mkv");

        Assert.Equal(Path.Combine(_workDir, "a"), RecordingDirectoryGuard.TryGetDirectory(file));
        Assert.Null(RecordingDirectoryGuard.TryGetDirectory(null));
        Assert.Null(RecordingDirectoryGuard.TryGetDirectory("   "));
    }

    [Fact]
    public void DescribeFfmpegFailure_without_directory_still_mentions_recording_directory()
    {
        string message = RecordingDirectoryGuard.DescribeFfmpegFailure(-13, "Permission denied", null);

        Assert.Contains("录制目录", message);
        Assert.Contains("修改目录", message);
    }
}
