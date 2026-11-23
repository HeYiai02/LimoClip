using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 高能密度检测器。DanmakuManager 把过滤后的弹幕逐条喂入，检测到高能时触发事件。
/// </summary>
public interface IHighlightDetector
{
    /// <summary>触发一次高能事件（在调用 Ingest 的线程上同步触发）。</summary>
    event Action<HighlightEvent>? HighlightTriggered;

    /// <summary>喂入一条已通过过滤的弹幕。</summary>
    void Ingest(DanmakuMessage message);

    /// <summary>热更新检测参数（不重置已有窗口数据）。</summary>
    void UpdateRules(HighlightRuleOptions options);
}
