using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 高能事件落盘与广播前的补全钩子。录制中心用它把事件绑定到当前录制会话与相对偏移块，
/// 且必须在 DanmakuManager 同步广播、异步落盘之前完成，避免落盘行缺少录制关联的竞态。
/// </summary>
public interface IHighlightEventEnricher
{
    /// <summary>补全事件；在消息管线线程同步调用，实现应保持无阻塞、不抛异常。</summary>
    HighlightEvent Enrich(HighlightEvent highlightEvent);
}
