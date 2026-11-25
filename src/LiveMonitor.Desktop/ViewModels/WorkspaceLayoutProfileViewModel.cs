using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>保存方案时需要主 VM 采集的工作区快照：布局树 JSON、窗格集合与当前布局模式。</summary>
internal sealed record WorkspaceLayoutSnapshot(
    string DockLayoutJson,
    IReadOnlyList<RoomTileViewModel> Tiles,
    bool IsCustomLayout,
    bool IsFreeformMode);

/// <summary>
/// 布局方案子视图模型与窗口宿主的边界：布局树反序列化、播放器重建/复用与自定义比例快照
/// 仍由 <see cref="MainWindowViewModel"/> 负责，子 VM 只编排方案数据与切换流程。
/// </summary>
internal interface IWorkspaceLayoutProfileHost
{
    /// <summary>采集当前工作区快照并同步行列数；Layout 尚未初始化时返回 null。</summary>
    WorkspaceLayoutSnapshot? CaptureSnapshot();

    /// <summary>把方案应用到窗口：反序列化布局树、复用/重建窗格与播放器。</summary>
    Task LoadProfileAsync(LayoutProfile profile);

    /// <summary>切换方案后清理「一键自适应」的自定义比例快照。</summary>
    void ClearCustomLayoutSnapshot();

    /// <summary>写入状态栏文案（主 VM 会同时弹 Toast 并通知状态栏子 VM）。</summary>
    void SetStatus(string message);
}

/// <summary>
/// 顶部布局方案档案子视图模型：Profiles 列表、当前选中方案、保存/另存为，
/// 以及 dirty 方案切换时的「保存并切换 / 放弃修改 / 取消」编排与选中项回滚。
/// 从 <see cref="MainWindowViewModel"/> 抽离后仍由主 VM 持有并通过属性转发对外暴露，
/// 既有绑定路径、代码后置与测试无需改动。本类不引用 Avalonia 类型。
/// </summary>
public sealed class WorkspaceLayoutProfileViewModel : ObservableObject
{
    private readonly ILayoutProfileRepository _profiles;
    private readonly Func<string> _defaultLayoutJsonFactory;
    private readonly IWorkspaceLayoutProfileHost _host;

    private LayoutProfile? _selectedProfile;
    private LayoutProfile? _loadedProfile;
    private bool _suppressProfileLoad;
    private bool _initialized;
    private bool _isDirty;

    internal WorkspaceLayoutProfileViewModel(
        ILayoutProfileRepository profiles,
        Func<string> defaultLayoutJsonFactory,
        IWorkspaceLayoutProfileHost host)
    {
        _profiles = profiles;
        _defaultLayoutJsonFactory = defaultLayoutJsonFactory;
        _host = host;
    }

    public ObservableCollection<LayoutProfile> Profiles { get; } = new();

    public LayoutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && !_suppressProfileLoad && _initialized)
            {
                _ = SelectProfileAsync(value);
            }
        }
    }

    /// <summary>由 View 注入：脏方案切换时弹「保存并切换/放弃修改/取消」。</summary>
    public Func<Task<ProfileSwitchChoice>>? ConfirmProfileSwitchAsync { get; set; }

    /// <summary>当前窗口方案是否有未保存修改；写入仅来自本类与主 VM 的布局变更路径。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    /// <summary>读取方案库并填充列表；空库时创建「默认方案」，布局 JSON 由主 VM 提供。</summary>
    internal async Task InitializeAsync(CancellationToken ct = default)
    {
        var profiles = await _profiles.GetAllAsync(ct).ConfigureAwait(true);
        if (profiles.Count == 0)
        {
            var profile = new LayoutProfile { Name = "默认方案" };
            profile.DockLayoutJson = _defaultLayoutJsonFactory();
            await _profiles.SaveAsync(profile, ct).ConfigureAwait(true);
            profiles = await _profiles.GetAllAsync(ct).ConfigureAwait(true);
        }

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        _initialized = true;
    }

    /// <summary>选中第一条方案并交给窗口宿主加载（启动流程的最后一步）。</summary>
    internal async Task LoadInitialProfileAsync(CancellationToken ct = default)
    {
        if (Profiles.Count == 0)
        {
            return;
        }

        _suppressProfileLoad = true;
        SelectedProfile = Profiles[0];
        _suppressProfileLoad = false;
        await _host.LoadProfileAsync(SelectedProfile!).ConfigureAwait(true);
        _loadedProfile = SelectedProfile;
    }

    /// <summary>窗口宿主完成方案加载（原地复用分支）后同步「已加载方案」基准，避免重复加载。</summary>
    internal void NotifyProfileLoaded(LayoutProfile profile) => _loadedProfile = profile;

    internal void MarkDirty() => IsDirty = true;

    internal void MarkClean() => IsDirty = false;

    /// <summary>恢复「一键自适应」前的脏标记（恢复自定义比例后沿用快照值）。</summary>
    internal void RestoreDirty(bool value) => IsDirty = value;

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var snapshot = _host.CaptureSnapshot();
        if (snapshot is null)
        {
            return;
        }

        var profile = SelectedProfile ?? new LayoutProfile { Name = "默认方案" };
        profile.IsCustomLayout = snapshot.IsCustomLayout || snapshot.IsFreeformMode;
        profile.DockLayoutJson = snapshot.DockLayoutJson;
        profile.Slots.Clear();
        profile.Slots.AddRange(WorkspaceLayoutCoordinator.CreateLayoutSlots(snapshot.Tiles, profile.Id));

        await _profiles.SaveAsync(profile, ct).ConfigureAwait(true);
        if (!Profiles.Contains(profile))
        {
            Profiles.Add(profile);
        }

        _suppressProfileLoad = true;
        SelectedProfile = profile;
        _loadedProfile = profile;
        _suppressProfileLoad = false;
        IsDirty = false;
        _host.SetStatus($"已保存方案「{profile.Name}」");
    }

    public async Task SaveAsAsync(string name, CancellationToken ct = default)
    {
        var snapshot = _host.CaptureSnapshot();
        if (snapshot is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("方案名不能为空。", nameof(name));
        }

        var profile = new LayoutProfile
        {
            Name = name.Trim(),
            IsCustomLayout = snapshot.IsCustomLayout || snapshot.IsFreeformMode
        };
        profile.DockLayoutJson = snapshot.DockLayoutJson;
        profile.Slots.AddRange(WorkspaceLayoutCoordinator.CreateLayoutSlots(snapshot.Tiles, profile.Id));

        await _profiles.SaveAsync(profile, ct).ConfigureAwait(true);
        Profiles.Add(profile);
        _suppressProfileLoad = true;
        SelectedProfile = profile;
        _loadedProfile = profile;
        _suppressProfileLoad = false;
        IsDirty = false;
        _host.SetStatus($"已另存方案「{profile.Name}」");
    }

    private async Task SelectProfileAsync(LayoutProfile? profile)
    {
        if (profile is null || ReferenceEquals(profile, _loadedProfile))
        {
            return;
        }

        if (IsDirty && !_suppressProfileLoad)
        {
            var choice = ConfirmProfileSwitchAsync is null
                ? ProfileSwitchChoice.Cancel
                : await ConfirmProfileSwitchAsync().ConfigureAwait(true);

            if (choice == ProfileSwitchChoice.Cancel)
            {
                _suppressProfileLoad = true;
                SetProperty(ref _selectedProfile, _loadedProfile, nameof(SelectedProfile));
                _suppressProfileLoad = false;
                return;
            }

            if (choice == ProfileSwitchChoice.Save)
            {
                await SaveAsync().ConfigureAwait(true);
            }
        }

        await _host.LoadProfileAsync(profile).ConfigureAwait(true);
        _loadedProfile = profile;
        _host.ClearCustomLayoutSnapshot();
    }
}
