using System.Reflection;
using System.Runtime.Loader;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 插件专用 AssemblyLoadContext。
/// 关键约束：
/// 1. LiveMonitor.Core / LiveMonitor.Platforms.Common 必须落到默认 ALC，否则插件里的
///    IPlatformPlugin 与宿主里的接口不是同一个类型，强转会失败；
/// 2. DLL 从内存流加载（LoadFromStream），避免加载后 DLL 文件被锁、无法热更新；
/// 3. isCollectible=true，插件卸载后可以替换文件。
/// </summary>
internal sealed class PlatformPluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LiveMonitor.Core",
        "LiveMonitor.Platforms.Common",
    };

    private readonly AssemblyDependencyResolver? _resolver;

    public PlatformPluginLoadContext(string pluginPath)
        : base($"LimoClip.Plugin.{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        var depsFile = Path.ChangeExtension(pluginPath, ".deps.json");
        if (File.Exists(depsFile))
        {
            try
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _resolver = null;
            }
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedAssemblyNames.Contains(name))
        {
            return null;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
