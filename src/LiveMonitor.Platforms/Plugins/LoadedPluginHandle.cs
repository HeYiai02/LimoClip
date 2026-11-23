using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 已通过验签并加载到插件 ALC 的插件句柄。Dispose 时卸载可回收的 AssemblyLoadContext，
/// 之后才能原子替换插件 DLL 文件。
/// </summary>
public sealed class LoadedPluginHandle : IDisposable
{
    private readonly PlatformPluginLoadContext _loadContext;
    private bool _disposed;

    internal LoadedPluginHandle(
        IPlatformPlugin plugin,
        PlatformPluginLoadContext loadContext,
        string? dllPath)
    {
        Plugin = plugin;
        _loadContext = loadContext;
        DllPath = dllPath;
    }

    /// <summary>插件入口实例。</summary>
    public IPlatformPlugin Plugin { get; }

    public string PluginId => Plugin.PluginId;

    public string Version => Plugin.Version;

    public PlatformType Platform => Plugin.Platform;

    /// <summary>插件 DLL 的磁盘路径（内存加载时可能为来源路径或 null）。</summary>
    public string? DllPath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadContext.Unload();
    }
}
