using System.Text.Json;
using System.Text.Json.Serialization;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 本地插件宿主（V0.5-B）。
/// 目录约定：%LocalAppData%/LiveMonitor/plugins/{pluginId}/plugin.dll + plugin.json（= manifest 条目）。
/// 启动时扫描 → 逐字节验签 → 与内置版本比较 → 注册进 PlatformFactory / DanmakuClientFactory；
/// 安装新版本只在没有活跃录制/弹幕任务时替换（R8），否则提示下次启动生效。
/// </summary>
public sealed class PlatformPluginHost : IDisposable
{
    public const string PluginDllFileName = "plugin.dll";
    public const string PluginEntryFileName = "plugin.json";

    private static readonly JsonSerializerOptions EntryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly string _ytDlpPath;
    private readonly PlatformFactory _platformFactory;
    private readonly DanmakuClientFactory _danmakuFactory;
    private readonly Func<bool> _hasActiveTasks;
    private readonly PlatformPluginLoader _loader;
    private readonly Dictionary<PlatformType, string> _builtInVersions = new();
    private readonly Dictionary<PlatformType, LoadedPluginHandle> _handles = new();
    private readonly Dictionary<string, ProviderPluginEntry> _installedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    public PlatformPluginHost(
        HttpClient httpClient,
        string ytDlpPath,
        PlatformFactory platformFactory,
        DanmakuClientFactory danmakuFactory,
        Func<bool>? hasActiveTasks = null,
        string? pluginsRoot = null,
        PlatformPluginLoader? loader = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ytDlpPath = ytDlpPath ?? string.Empty;
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        _danmakuFactory = danmakuFactory ?? throw new ArgumentNullException(nameof(danmakuFactory));
        _hasActiveTasks = hasActiveTasks ?? (static () => false);
        PluginsRoot = pluginsRoot ?? ToolLocator.PluginsRootDirectory;
        _loader = loader ?? new PlatformPluginLoader();
    }

    public string PluginsRoot { get; }

    /// <summary>已注册到工厂的插件（平台 → 版本）。</summary>
    public IReadOnlyDictionary<PlatformType, string> RegisteredPluginVersions
    {
        get
        {
            lock (_sync)
            {
                return _handles.ToDictionary(pair => pair.Key, pair => pair.Value.Version);
            }
        }
    }

    /// <summary>已安装到本地的插件条目（pluginId → manifest 条目）。</summary>
    public IReadOnlyDictionary<string, ProviderPluginEntry> InstalledPlugins
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, ProviderPluginEntry>(_installedEntries, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>登记内置插件版本，用于判断本地插件是否需要覆盖内置实现。</summary>
    public void RegisterBuiltIn(IPlatformPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_sync)
        {
            _builtInVersions[plugin.Platform] = plugin.Version;

            // 纯插件平台（如 YouTube）没有 Platforms.CreateDefault 内置 Provider：
            // 把内置插件登记进工厂，保证「安装即支持」；B 站/Twitch 已有 Provider，不重复替换。
            if (!HasPlatformProvider(plugin.Platform))
            {
                _platformFactory.ReplaceProvider(plugin.CreateStreamProvider(_httpClient, _ytDlpPath));
                _danmakuFactory.RegisterOrReplace(plugin.Platform, plugin.CreateDanmakuClient);
            }
        }
    }

    private bool HasPlatformProvider(PlatformType platform)
    {
        try
        {
            _platformFactory.GetProvider(platform);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>扫描本地插件目录并注册验签通过的插件；单个插件失败不影响其他插件。返回成功注册数量。</summary>
    public int LoadInstalledPlugins()
    {
        ThrowIfDisposed();
        if (!Directory.Exists(PluginsRoot))
        {
            return 0;
        }

        var loaded = 0;
        foreach (var pluginDirectory in Directory.EnumerateDirectories(PluginsRoot))
        {
            var entry = TryReadEntry(pluginDirectory);
            if (entry is null)
            {
                continue;
            }

            lock (_sync)
            {
                _installedEntries[entry.PluginId] = entry;
            }

            if (TryApplyEntry(entry, pluginDirectory, out _))
            {
                loaded++;
            }
        }

        return loaded;
    }

    /// <summary>安装（写入本地目录）一个已验签插件，不立即注册。调用方随后可 ApplyInstalledPlugin。</summary>
    public bool TryInstall(ProviderPluginEntry entry, byte[] dllBytes, out string? error)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(dllBytes);

        error = null;
        if (dllBytes.Length == 0)
        {
            error = "插件 DLL 内容为空。";
            return false;
        }

        if (!_loader.VerifyPluginPayload(dllBytes, entry.Ed25519Signature, entry.Sha256))
        {
            error = "插件签名校验失败，已拒绝安装：" + entry.PluginId;
            return false;
        }

        try
        {
            Directory.CreateDirectory(PluginsRoot);
            var targetDirectory = GetPluginDirectory(entry.PluginId);
            var stagingDirectory = Path.Combine(
                PluginsRoot,
                SafeName(entry.PluginId) + ".staging-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stagingDirectory);
            File.WriteAllBytes(Path.Combine(stagingDirectory, PluginDllFileName), dllBytes);
            File.WriteAllText(
                Path.Combine(stagingDirectory, PluginEntryFileName),
                JsonSerializer.Serialize(entry, EntryJsonOptions));

            var oldDirectory = targetDirectory + ".old-" + Guid.NewGuid().ToString("N")[..8];
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, oldDirectory);
            }

            Directory.Move(stagingDirectory, targetDirectory);
            TryDeleteDirectory(oldDirectory);

            lock (_sync)
            {
                _installedEntries[entry.PluginId] = entry;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"写入插件目录失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 把已安装插件注册进工厂。要求验签通过、版本高于内置、且当前没有活跃任务；
    /// 成功后会卸载同平台的旧插件 ALC（原子换表）。
    /// </summary>
    public bool TryApplyInstalledPlugin(string pluginId, out string? error)
    {
        ThrowIfDisposed();
        error = null;
        ProviderPluginEntry? entry;
        lock (_sync)
        {
            if (!_installedEntries.TryGetValue(pluginId, out entry))
            {
                error = $"未找到已安装插件：{pluginId}";
                return false;
            }
        }

        return TryApplyEntry(entry, GetPluginDirectory(entry.PluginId), out error);
    }

    /// <summary>从磁盘重新读取某插件条目（更新流程用；未安装返回 null）。</summary>
    public ProviderPluginEntry? GetInstalledEntry(string pluginId)
    {
        lock (_sync)
        {
            return _installedEntries.TryGetValue(pluginId, out var entry) ? entry : null;
        }
    }

    /// <summary>插件是否比内置版本更新（内置未知时视为可应用）。</summary>
    public bool IsNewerThanBuiltIn(ProviderPluginEntry entry)
    {
        lock (_sync)
        {
            return !_builtInVersions.TryGetValue(entry.Platform, out var builtIn) ||
                   PluginVersionComparer.IsNewer(entry.Version, builtIn);
        }
    }

    private bool TryApplyEntry(ProviderPluginEntry entry, string pluginDirectory, out string? error)
    {
        error = null;
        if (!IsNewerThanBuiltIn(entry))
        {
            error = $"插件 {entry.PluginId} {entry.Version} 不高于内置版本，保留内置实现。";
            return false;
        }

        if (_hasActiveTasks())
        {
            error = "当前有正在进行的录制或弹幕任务，插件更新将在下次启动时生效。";
            return false;
        }

        var dllPath = Path.Combine(pluginDirectory, PluginDllFileName);
        if (!File.Exists(dllPath))
        {
            error = $"插件 DLL 缺失：{dllPath}";
            return false;
        }

        LoadedPluginHandle? handle = null;
        try
        {
            handle = _loader.LoadVerifiedPlugin(dllPath, entry.Ed25519Signature, entry.Sha256);
            if (!string.Equals(handle.PluginId, entry.PluginId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"插件标识不一致：manifest 为 {entry.PluginId}，DLL 内为 {handle.PluginId}。");
            }

            if (handle.Platform != entry.Platform)
            {
                throw new InvalidOperationException(
                    $"插件平台不一致：manifest 为 {entry.Platform}，DLL 内为 {handle.Platform}。");
            }

            var plugin = handle.Plugin;
            var provider = plugin.CreateStreamProvider(_httpClient, _ytDlpPath);
            _platformFactory.ReplaceProvider(provider);
            _danmakuFactory.RegisterOrReplace(entry.Platform, plugin.CreateDanmakuClient);

            LoadedPluginHandle? previous = null;
            lock (_sync)
            {
                if (_handles.TryGetValue(entry.Platform, out previous))
                {
                    _handles.Remove(entry.Platform);
                }

                _handles[entry.Platform] = handle;
            }

            previous?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException
                                       or ArgumentException or IOException or BadImageFormatException)
        {
            handle?.Dispose();
            error = $"注册插件 {entry.PluginId} 失败：{ex.Message}";
            return false;
        }
    }

    private ProviderPluginEntry? TryReadEntry(string pluginDirectory)
    {
        try
        {
            var entryPath = Path.Combine(pluginDirectory, PluginEntryFileName);
            if (!File.Exists(entryPath) || !File.Exists(Path.Combine(pluginDirectory, PluginDllFileName)))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProviderPluginEntry>(File.ReadAllText(entryPath), EntryJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetPluginDirectory(string pluginId) => Path.Combine(PluginsRoot, SafeName(pluginId));

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 旧版本目录清理失败不影响使用（下次安装时会再次尝试）。
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PlatformPluginHost));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<LoadedPluginHandle> handles;
        lock (_sync)
        {
            handles = _handles.Values.ToList();
            _handles.Clear();
        }

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }
}
