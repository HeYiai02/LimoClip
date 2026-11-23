using System.Reflection;

namespace LiveMonitor.Platforms;

/// <summary>
/// 平台插件版本解析：优先 AssemblyInformationalVersion（去掉 +commit 后缀），回退 AssemblyVersion。
/// </summary>
public static class PluginVersionInfo
{
    public const string FallbackVersion = "0.0.0";

    public static string Resolve(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        return Resolve(pluginType.Assembly);
    }

    public static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var value = informational.Trim();
            var plus = value.IndexOf('+');
            return plus > 0 ? value[..plus] : value;
        }

        var version = assembly.GetName().Version;
        return version is null ? FallbackVersion : version.ToString(3);
    }
}
