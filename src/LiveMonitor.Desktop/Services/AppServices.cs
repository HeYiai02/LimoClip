using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Danmaku;
using LiveMonitor.Data;
using LiveMonitor.Data.Repositories;
using LiveMonitor.Media.Asr;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using LiveMonitor.Platforms;
using LiveMonitor.Platforms.Plugins;

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
        IAsrProvider asr,
        AsrSettingsStore asrSettings,
        HttpClient pluginHttpClient,
        PlatformPluginHost pluginHost,
        PluginUpdateService pluginUpdates,
        CliUpdater cliUpdater,
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
        Asr = asr;
        AsrSettings = asrSettings;
        _pluginHttpClient = pluginHttpClient;
        PluginHost = pluginHost;
        PluginUpdates = pluginUpdates;
        CliUpdater = cliUpdater;
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

    /// <summary>V0.5-A 云端语音识别（剪辑台「🎙 转写」的数据来源）。</summary>
    public IAsrProvider Asr { get; }

    /// <summary>V0.5-A ASR 设置读写（预设、DPAPI 加密 Key、连通性测试）。</summary>
    public AsrSettingsStore AsrSettings { get; }

    /// <summary>V0.5-B 平台插件宿主：内置插件版本登记、本地插件验签加载与运行期热替换。</summary>
    public PlatformPluginHost PluginHost { get; }

    /// <summary>V0.5-B 平台插件更新服务：拉取 providers-manifest.json、下载并验签安装。</summary>
    public PluginUpdateService PluginUpdates { get; }

    /// <summary>V0.5-B yt-dlp 更新器：检查 GitHub 最新版本并原子替换 tools 下的 yt-dlp.exe。</summary>
    public CliUpdater CliUpdater { get; }

    private readonly ProcessJobGuard _clipJobGuard;
    private readonly HttpClient _pluginHttpClient;
    private bool _clipResourcesDisposed;
    private bool _pluginResourcesDisposed;

    /// <summary>窗口关闭时释放导出/ASR 用的 Job Object 与网络客户端（级联结束残留 ffmpeg）。</summary>
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

        try
        {
            (Asr as IDisposable)?.Dispose();
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>窗口关闭时卸载所有平台插件并释放插件网络客户端（应在录制/弹幕释放之后调用，避免卸载仍在使用的插件类型）。</summary>
    public void DisposePluginResources()
    {
        if (_pluginResourcesDisposed)
        {
            return;
        }

        _pluginResourcesDisposed = true;
        try
        {
            PluginHost.Dispose();
        }
        catch
        {
            // 退出阶段插件卸载失败不阻塞关闭。
        }

        try
        {
            _pluginHttpClient.Dispose();
        }
        catch
        {
            // 同上。
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

        // V0.5-G Twitch 去广告：平台工厂构造时需要共享 HttpClient 与可选的播放列表代理模板，
        // 因此插件 HttpClient / 设置仓储提前创建；代理模板留空时只走原生 GQL + Usher / yt-dlp。
        var pluginHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        pluginHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LimoClip-PluginHost/1.0.0");
        var settings = new AppSettingsRepository(database);
        var twitchProxyTemplate = await settings
            .GetAsync(AppSettingKeys.TwitchPlaylistProxyUrl, ct)
            .ConfigureAwait(false);
        TwitchAdConfig.PlaylistProxyTemplateProvider = () => twitchProxyTemplate;

        var platforms = PlatformFactory.CreateDefault(
            pluginHttpClient,
            twitchPlaylistProxyTemplateProvider: () => TwitchAdConfig.PlaylistProxyTemplateProvider?.Invoke());
        var credentials = new CredentialStore(database);
        var blocklist = new BlocklistRepository(database);
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

        // V0.5-A 云端 ASR：专用 HttpClient（长片段上传，超时 10 分钟）与独立 Job Object，
        // 生命周期由 Provider 自己接管，窗口关闭时随 DisposeClipExportResources 一并释放。
        var asrSettings = new AsrSettingsStore(settings);
        var asrCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveMonitor",
            "cache",
            "asr");
        var asr = new OpenAiCompatibleAsrProvider(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            ffmpegPath,
            new ProcessJobGuard(),
            () => asrSettings.GetConfigAsync(),
            asrCacheDirectory);

        // V0.5-B 平台插件：共享 HttpClient 供插件清单/下载与 yt-dlp 更新检查使用；
        // 先登记内置 B 站/Twitch 插件版本，再加载本地已安装插件（验签失败自动回退内置实现）。
        var ytDlpPath = ToolLocator.ResolveYtDlpPath();
        var pluginHost = new PlatformPluginHost(
            pluginHttpClient,
            ytDlpPath,
            platforms,
            danmakuFactory,
            hasActiveTasks: () => recordings.GetActiveRecordings().Count > 0);
        foreach (var builtInPlugin in BuiltInPlatformPlugins.CreateAll())
        {
            pluginHost.RegisterBuiltIn(builtInPlugin);
        }

        pluginHost.LoadInstalledPlugins();
        var pluginUpdates = new PluginUpdateService(
            pluginHttpClient,
            pluginHost,
            PluginVersionInfo.Resolve(typeof(AppServices).Assembly));
        var cliUpdater = new CliUpdater(ytDlpPath, pluginHttpClient);

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
            asr,
            asrSettings,
            pluginHttpClient,
            pluginHost,
            pluginUpdates,
            cliUpdater,
            clipJobGuard);
    }
}
