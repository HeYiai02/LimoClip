namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕轨道分配器：始终从最上方（lane 0）开始寻找第一条空闲轨道。
/// 旧实现按 <c>_nextLane</c> 循环轮转分配，弹幕稀疏时屏幕上只有连续几条轨道有内容，
/// 这条"滚动带"转到下半屏后就表现为"弹幕只在屏幕下半方，上半方没有"，
/// 看起来不像是弹幕多导致的。改为从上往下找第一条空闲轨道后，
/// 稀疏弹幕会稳定落在上方轨道，密集时再自然向下铺满。
/// </summary>
public static class DanmakuLaneAllocator
{
    /// <summary>
    /// 返回第一条可容纳新弹幕的轨道序号（0 为最上方）；所有轨道都还在"入场区"时返回 -1。
    /// </summary>
    /// <param name="laneCount">轨道总数。</param>
    /// <param name="getLastItemRightEdge">取某条轨道最后一条弹幕的右边缘 X 坐标；该轨道没有弹幕时返回负无穷。</param>
    /// <param name="width">渲染区宽度。</param>
    /// <param name="spawnGap">新弹幕与前一条弹幕之间保留的最小水平间距。</param>
    public static int FindFreeLane(
        int laneCount,
        Func<int, double> getLastItemRightEdge,
        double width,
        double spawnGap)
    {
        ArgumentNullException.ThrowIfNull(getLastItemRightEdge);

        if (laneCount <= 0 || width <= 0)
        {
            return -1;
        }

        var threshold = width - spawnGap;
        for (var lane = 0; lane < laneCount; lane++)
        {
            if (getLastItemRightEdge(lane) <= threshold)
            {
                return lane;
            }
        }

        return -1;
    }
}
