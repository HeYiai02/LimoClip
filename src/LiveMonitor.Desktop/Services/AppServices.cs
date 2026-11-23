using LiveMonitor.Core.Interfaces;
using LiveMonitor.Danmaku;
using LiveMonitor.Data;
using LiveMonitor.Data.Repositories;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
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
        PlatformFactory platforms,
        IRecordingTargetRepository recordingTargets,
        IRecordingSessionRepository recordingSessions,
        RecordingManager recordings,
        VideoProbeService videoProbe,
        IWaveformService waveforms,
        IHardwareEncoderDetector encoders,
        IClipExporter clips,
        ProcessJobGuard clipJobGuard)
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
        RecordingTargets = recordingTargets;
        RecordingSessions = recordingSessions;
        Recordings = recordings;
        VideoProbe = videoProbe;
        Waveforms = waveforms;
        Encoders = encoders;
        Clips = clips;
        _clipJobGuard = clipJobGuard;
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

    /// <summary>V0.3 录制目标仓储：每个直播间的自动录制开关与独立保存目录。</summary>
    public IRecordingTargetRepository RecordingTargets { get; }

    /// <summary>V0.3 录制会话仓储：录制中心历史录像的数据来源。</summary>
    public IRecordingSessionRepository RecordingSessions { get; }

    /// <summary>V0.3 录制引擎：后台录制、自动录制轮询与实时进度事件。</summary>
    public RecordingManager Recordings { get; }

    /// <summary>V0.4 本地视频探测（ffmpeg -i 解析时长/宽高/帧率/编码）。</summary>
    public VideoProbeService VideoProbe { get; }

    /// <summary>V0.4 时间轴双波形（音频峰值 + 弹幕密度）流式提取与缓存。</summary>
    public IWaveformService Waveforms { get; }

    /// <summary>V0.4 硬件编码器实测探测（nvenc/qsv/amf → libx264 回退）。</summary>
    public IHardwareEncoderDetector Encoders { get; }

    /// <summary>V0.4 双版切片导出器（无损 copy + ASS 弹幕烧录）。</summary>
    public IClipExporter Clips { get; }

    private readonly ProcessJobGuard _clipJobGuard;
    private bool _clipResourcesDisposed;

    /// <summary>窗口关闭时释放导出用的 Job Object（级联结束残留 ffmpeg）。</summary>
    public void DisposeClipExportResources()
    {
        if (_clipResourcesDisposed)
        {
            return;
        }

        _clipResourcesDisposed = true;
        try
        {
            _clipJobGuard.Dispose();
        }
        catch
        {
            // 退出阶段释放失败不阻塞关闭。
        }
    }

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

        var roomRepository = new RoomRepository(database);
        var recordingTargets = new RecordingTargetRepository(database);
        var recordingSessions = new RecordingSessionRepository(database);
        var recordings = new RecordingManager(
            roomRepository,
            recordingTargets,
            recordingSessions,
            settings,
            credentials,
            platform =>
            {
                try
                {
                    return platforms.GetProvider(platform);
                }
                catch (NotSupportedException)
                {
                    // 未注册 Provider 的平台（Douyin/Douyu/Huya/YouTube）仅不支持开录，不阻塞应用启动。
                    return null;
                }
            },
            danmaku);

        // V0.4 剪辑台：ffmpeg 定位失败时仅占位，服务在真正调用时抛出带路径的异常，
        // 不阻塞主界面启动（与录制模块的容错策略一致）。
        var ffmpegPath = FfmpegLocator.TryLocate(out var located)
            ? located
            : Path.Combine(AppContext.BaseDirectory, FfmpegLocator.RelativePath);
        var videoProbe = new VideoProbeService(ffmpegPath);
        var waveforms = new WaveformService(ffmpegPath);
        var encoders = new HardwareEncoderDetector(ffmpegPath);
        var clipJobGuard = new ProcessJobGuard();
        var clips = new ClipExporter(
            ffmpegPath,
            clipJobGuard,
            encoders,
            new DanmakuAssConverter());

        return new AppServices(
            database,
            roomRepository,
            new LayoutProfileRepository(database),
            credentials,
            blocklist,
            settings,
            highlightEvents,
            danmakuFactory,
            danmaku,
            platforms,
            recordingTargets,
            recordingSessions,
            recordings,
            videoProbe,
            waveforms,
            encoders,
            clips,
            clipJobGuard);
    }
}
