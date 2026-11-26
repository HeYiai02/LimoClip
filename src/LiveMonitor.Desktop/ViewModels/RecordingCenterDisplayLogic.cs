using System;
using System.IO;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心的展示与目录归一化规则：目标/刷新摘要文案、以及把用户配置或选择目录
/// 统一为可持久化的绝对路径（空值回落到默认目录）。抽离为纯函数以便直接单测，
/// 视图模型只负责调用并写入 <see cref="RecordingCenterViewModel.StatusText"/>。
/// </summary>
internal static class RecordingCenterDisplayLogic
{
    /// <summary>主库目标区摘要：「共 N 个直播间 · 正在录制 M 路」。</summary>
    public static string TargetSummary(int targetCount, int recordingCount)
        => $"共 {targetCount} 个直播间 · 正在录制 {recordingCount} 路";

    /// <summary>整页刷新后的状态文案：「已刷新：N 个直播间 · M 条历史录像」。</summary>
    public static string RefreshStatusText(int targetCount, int historyCount)
        => $"已刷新：{targetCount} 个直播间 · {historyCount} 条历史录像";

    /// <summary>
    /// 把配置/选择的目录归一化为绝对路径：空白回落到 <paramref name="defaultDirectory"/>，
    /// 展开环境变量并取全路径；非法路径退回去空白后的原值（与历史行为一致）。
    /// </summary>
    public static string NormalizeDirectory(string? raw, string defaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultDirectory;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim()));
        }
        catch
        {
            return raw.Trim();
        }
    }
}
