using System.Diagnostics;

namespace LiveMonitor.Desktop.Services;

/// <summary>用系统资源管理器打开目录/定位文件的公共封装（录制中心与历史录像子视图模型共用）。</summary>
/// <summary>打开目录/定位文件的接缝（测试可注入，避免真的拉起资源管理器）。</summary>
internal delegate bool TryLaunch(string target, out string? error);

internal static class ExplorerLauncher
{
    /// <summary>打开目录；失败时返回可展示的失败原因。</summary>
    public static bool TryOpenDirectory(string path, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>在目录中定位并选中文件；失败时返回可展示的失败原因。</summary>
    public static bool TrySelectFile(string filePath, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>打开目录失败时给用户可执行的替代方案（手动打开路径），而不是只显示异常消息。</summary>
    public static string DescribeFailure(string directory, string? error)
        => $"打开目录失败：{error}。请手动在资源管理器中打开：{directory}";
}
