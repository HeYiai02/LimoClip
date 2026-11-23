using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 展示前的弹幕过滤。返回 false 的消息既不渲染，也不参与高能密度统计。
/// </summary>
public interface IDanmakuFilter
{
    bool ShouldAllow(DanmakuMessage message);
}
