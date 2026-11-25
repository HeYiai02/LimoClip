using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 主窗口右侧「实时高能事件」面板的只读子视图模型：条目集合、空状态与面板展开状态。
/// 对外只暴露 getter / 集合 / 事件；条目写入只走 internal 方法，由 <see cref="MainWindowViewModel"/>
/// 在弹幕事件入库、启动回填、清空处调用。
/// 集合写入统一在 UI 线程完成（主 VM 的 AddHighlightEvent / LoadHighlightEvents 已保证
/// Dispatcher 时序），本类不自行切换线程。
/// </summary>
public sealed class HighlightFeedViewModel : ObservableObject
{
    /// <summary>内存里最多保留的高能事件条数；完整历史在数据库。</summary>
    public const int MaxItems = 200;

    /// <summary>高能事件流，最新的在最前面。</summary>
    public ObservableCollection<HighlightEventItem> Items { get; } = new();

    /// <summary>true 表示当前没有任何条目（面板空状态引导用）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>高能事件面板是否展开；只影响主窗口叠加层，不写入方案 / Dock 布局。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    private bool _isVisible;

    /// <summary>新条目插到顶部后请求视图把列表滚回顶部。</summary>
    public event EventHandler? ScrollToTopRequested;

    /// <summary>切换面板展开状态；返回值表示状态是否变化。</summary>
    internal bool UpdateIsVisible(bool value)
    {
        if (_isVisible == value)
        {
            return false;
        }

        IsVisible = value;
        return true;
    }

    /// <summary>插入一条最新事件；超出 <see cref="MaxItems"/> 时丢弃最旧条目（调用方保证 UI 线程）。</summary>
    internal void Insert(HighlightEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Items.Insert(0, item);
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        // 与抽离前一致：每次写入都通知空状态，主 VM 会转发给 IsHighlightEventsEmpty。
        OnPropertyChanged(nameof(IsEmpty));
        ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启动时用数据库回填的条目整体替换当前集合（调用方保证按时间倒序，不回滚视图）。</summary>
    internal void ReplaceAll(IReadOnlyList<HighlightEventItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>清空当前条目（用户点「清空」）。</summary>
    internal void Clear()
    {
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
