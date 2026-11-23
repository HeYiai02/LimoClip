using LiveMonitor.Core.Interfaces;
using LiveMonitor.Danmaku;
using LiveMonitor.Data;
using LiveMonitor.Data.Repositories;
using LiveMonitor.Platforms;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// V0.1 的组合根。Desktop 直接 new 出仓储与平台工厂，避免为一个模块引入完整 DI 容器。
/// </summary>
public sealed class AppServices
{
    private AppServices(
        LiveMonitorDatabase database,
        IRoomRepository rooms,
        ILayoutProfileRepository profiles,
        ICredentialStore credentials,
        IBlocklistRepository blocklist,
        IAppSettingsRepository settings,
        IHighlightEventRepository highlightEvents,
        IDanmakuClientFactory danmakuFactory,
        DanmakuManager danmaku,
        PlatformFactory platforms)
    {
        Database = database;
        Rooms = rooms;
        Profiles = profiles;
        Credentials = credentials;
        Blocklist = blocklist;
        Settings = settings;
        HighlightEvents = highlightEvents;
        DanmakuFactory = danmakuFactory;
        Danmaku = danmaku;
        Platforms = platforms;
    }

    public LiveMonitorDatabase Database { get; }

    public IRoomRepository Rooms { get; }

    public ILayoutProfileRepository Profiles { get; }

    public ICredentialStore Credentials { get; }

    public IBlocklistRepository Blocklist { get; }

    public IAppSettingsRepository Settings { get; }

    /// <summary>V0.2 高能事件落盘仓储。</summary>
    public IHighlightEventRepository HighlightEvents { get; }

    /// <summary>V0.2 弹幕客户端工厂（B 站/Twitch，其余平台返回 null）。</summary>
    public IDanmakuClientFactory DanmakuFactory { get; }

    /// <summary>V0.2 弹幕会话管理器：按 (Platform, RoomId) 引用计数共享连接与过滤管线。</summary>
    public DanmakuManager Danmaku { get; }

    public PlatformFactory Platforms { get; }

    private static async Task<LiveMonitorDatabase> CreateDatabaseAsync(CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return await InitializeDatabaseAsync(configured, ct).ConfigureAwait(false);
        }

        try
        {
            return await InitializeDatabaseAsync(LiveMonitorDatabase.DefaultDatabasePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 开发机/受限账户可能没有 %LOCALAPPDATA%\LiveMonitor 的写权限。
            // 这种情况下退回程序目录下的 data，保证主程序能启动；错误会写入日志。
            var fallback = Path.Combine(AppContext.BaseDirectory, "data", "livemonitor.db");
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "startup-fallback.log"),
                    $"time={DateTimeOffset.Now:O}{Environment.NewLine}" +
                    $"default={LiveMonitorDatabase.DefaultDatabasePath}{Environment.NewLine}" +
                    $"fallback={fallback}{Environment.NewLine}{ex}");
            }
            catch
            {
                // 日志失败不阻塞启动。
            }

            return await InitializeDatabaseAsync(fallback, ct).ConfigureAwait(false);
        }
    }

    private static async Task<LiveMonitorDatabase> InitializeDatabaseAsync(
        string path,
        CancellationToken ct)
    {
        var database = new LiveMonitorDatabase(path);
        await database.InitializeAsync(ct).ConfigureAwait(false);
        return database;
    }

    public static async Task<AppServices> CreateAsync(CancellationToken ct = default)
    {
        var database = await CreateDatabaseAsync(ct).ConfigureAwait(false);

        var platforms = PlatformFactory.CreateDefault();
        var credentials = new CredentialStore(database);
        var blocklist = new BlocklistRepository(database);
        var settings = new AppSettingsRepository(database);
        var highlightEvents = new HighlightEventRepository(database);
        var danmakuFactory = new DanmakuClientFactory();
        var danmaku = new DanmakuManager(
            danmakuFactory,
            credentials,
            blocklist,
            settings,
            highlightEvents);

        return new AppServices(
            database,
            new RoomRepository(database),
            new LayoutProfileRepository(database),
            credentials,
            blocklist,
            settings,
            highlightEvents,
            danmakuFactory,
            danmaku,
            platforms);
    }
}
