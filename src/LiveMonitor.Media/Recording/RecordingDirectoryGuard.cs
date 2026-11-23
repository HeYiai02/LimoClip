namespace LiveMonitor.Media.Recording;

/// <summary>
/// 录制输出目录的可写性预检，以及 ffmpeg 写盘失败时的中文诊断文案。
/// 普通用户机器上最常见的原因：目录不存在、目录只读、磁盘满、被安全软件拦截。
/// 预检在开场录制前执行，避免先写数据库会话、几秒后才在历史里看到失败。
/// </summary>
public static class RecordingDirectoryGuard
{
    /// <summary>预检时写入的临时文件名前缀。</summary>
    public const string ProbeFilePrefix = ".livemonitor-write-test-";

    /// <summary>确保目录存在且可写；失败时抛出带建议目录和原因的中文提示。</summary>
    public static void EnsureWritable(string? directory)
    {
        if (!TryEnsureWritable(directory, out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>尝试确保目录存在且可写；返回 false 时 <paramref name="error"/> 为可直接展示的中文原因。</summary>
    public static bool TryEnsureWritable(string? directory, out string? error)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "录制目录为空，请在录制中心「修改目录」里选择一个可写目录。";
            return false;
        }

        var displayPath = directory.Trim();
        string fullPath;
        string probePath;
        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(displayPath));
            Directory.CreateDirectory(fullPath);
            probePath = Path.Combine(fullPath, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        }
        catch (Exception ex) when (IsDirectoryFailure(ex))
        {
            error = BuildWritableFailureMessage(displayPath, ex.Message);
            return false;
        }

        try
        {
            // DeleteOnClose 让文件句柄关闭时自动回收；下面的 TryDelete 只是兜底。
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex) when (IsDirectoryFailure(ex))
        {
            TryDeleteProbe(probePath);
            error = BuildWritableFailureMessage(displayPath, ex.Message);
            return false;
        }

        TryDeleteProbe(probePath);
        error = null;
        return true;
    }

    /// <summary>构造「录制目录不可写」的中文提示，包含原因与建议目录。</summary>
    public static string BuildWritableFailureMessage(string directory, string reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? "目录无法创建或写入" : reason.Trim();
        return $"录制目录不可写：{directory}（{detail}）。" +
               $"请点击「修改目录」换到一个可写目录，例如：{string.Join("、", SuggestDirectories())}。";
    }

    /// <summary>把 ffmpeg 退出信息翻译成用户能照着处理的中文提示；写盘失败时附带建议目录。</summary>
    public static string DescribeFfmpegFailure(int exitCode, string? stderrTail, string? outputDirectory)
    {
        var stderr = string.IsNullOrWhiteSpace(stderrTail) ? "无 stderr 输出" : stderrTail.Trim();
        if (stderr.Length > 300)
        {
            stderr = stderr[^300..];
        }

        if (!IsWritePermissionFailure(exitCode, stderrTail))
        {
            return $"ffmpeg 异常退出（exitCode={exitCode}）：{stderr}";
        }

        var directory = string.IsNullOrWhiteSpace(outputDirectory) ? "录制目录" : outputDirectory.Trim();
        return $"无法写入录制文件：目标目录「{directory}」没有写权限，或写入被安全软件拦截（ffmpeg 退出码 {exitCode}）。" +
               $"请点击「修改目录」换到可写目录（例如：{string.Join("、", SuggestDirectories())}），并检查杀毒/安全软件是否拦截了 ffmpeg.exe。" +
               $"原始错误：{stderr}";
    }

    /// <summary>判断 ffmpeg 退出是否属于「打不开输出文件」类错误（权限/安全软件拦截等）。</summary>
    public static bool IsWritePermissionFailure(int exitCode, string? stderrTail)
    {
        // 成功退出的进程即便 stderr 里有杂音，也不按写盘失败处理。
        if (exitCode == 0)
        {
            return false;
        }

        if (exitCode is -13 or 13)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(stderrTail))
        {
            return false;
        }

        return stderrTail.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
               || stderrTail.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
               || stderrTail.Contains("error opening output", StringComparison.OrdinalIgnoreCase)
               || stderrTail.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从文件路径取出所在目录；非法路径时返回 null。</summary>
    public static string? TryGetDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(filePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>建议目录候选（去重、最多 3 个），供用户一键切换到可写位置。</summary>
    public static IReadOnlyList<string> SuggestDirectories()
    {
        var candidates = new List<string>();
        AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiveMonitor"));
        AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LimoClip"));
        AddCandidate(Path.Combine(Path.GetTempPath(), "LimoClip"));
        return candidates;

        void AddCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Trim().Length == 0)
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(candidate);
                if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 候选路径不合法时忽略即可，另一条候选通常仍然可用。
            }
        }
    }

    private static bool IsDirectoryFailure(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void TryDeleteProbe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 预检文件删不掉不影响录制本身，下一次预检会再尝试。
        }
    }
}
