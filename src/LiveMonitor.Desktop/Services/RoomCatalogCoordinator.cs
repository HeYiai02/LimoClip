using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 主库房间列表的纯查询/分组计算：搜索词过滤、分组过滤与分组去重排序。
/// 不持有集合、不触发属性通知，由 MainWindowViewModel 负责把结果写回 ObservableCollection。
/// </summary>
internal static class RoomCatalogCoordinator
{
    /// <summary>「不过滤分组」的固定标签，与 MainWindowViewModel 的初始 GroupFilter 保持一致。</summary>
    public const string AllGroupLabel = "全部";

    /// <summary>按分组与搜索词筛选房间，保持传入顺序；搜索词忽略首尾空白与大小写。</summary>
    public static List<RoomListItemViewModel> Filter(
        IEnumerable<RoomListItemViewModel> rooms,
        string searchText,
        string? groupFilter)
    {
        var query = searchText.Trim();
        var result = new List<RoomListItemViewModel>();
        foreach (var room in rooms)
        {
            var groupMatches = groupFilter == AllGroupLabel ||
                               string.Equals(room.GroupTag, groupFilter, StringComparison.OrdinalIgnoreCase);
            if (!groupMatches)
            {
                continue;
            }

            if (query.Length == 0 ||
                room.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(room);
            }
        }

        return result;
    }

    /// <summary>生成分组下拉项：「全部」在前，其余去重（忽略大小写）后按名称排序。</summary>
    public static List<string> BuildGroups(IEnumerable<RoomListItemViewModel> rooms)
    {
        var groups = rooms
            .Select(room => room.GroupTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        groups.Insert(0, AllGroupLabel);
        return groups;
    }

    /// <summary>当前分组是否仍存在（忽略大小写）；不存在时调用方应回退到「全部」。</summary>
    public static bool IsKnownGroup(IEnumerable<string> groups, string? groupFilter) =>
        groups.Any(group => string.Equals(group, groupFilter, StringComparison.OrdinalIgnoreCase));
}
