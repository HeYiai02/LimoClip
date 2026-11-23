using System.Reflection;
using System.Security;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 带 Ed25519 验签的平台插件加载器。
/// 流程：读 DLL 字节 →（可选）比对 SHA256 → Ed25519 验签 → 从内存流加载到可回收 ALC → 反射实例化 IPlatformPlugin。
/// 验签失败抛 <see cref="SecurityException"/>，调用方不得回退为由该 DLL 提供实现。
/// </summary>
public sealed class PlatformPluginLoader
{
    private readonly string _trustedPublicKeyBase64;

    /// <param name="trustedPublicKeyBase64">
    /// 覆盖信任根公钥（测试用）；为空时使用 <see cref="PluginTrust.PublicKeyBase64"/>。
    /// </param>
    public PlatformPluginLoader(string? trustedPublicKeyBase64 = null)
    {
        _trustedPublicKeyBase64 = string.IsNullOrWhiteSpace(trustedPublicKeyBase64)
            ? PluginTrust.PublicKeyBase64
            : trustedPublicKeyBase64!.Trim();
    }

    /// <summary>从磁盘加载并验签插件 DLL。</summary>
    public LoadedPluginHandle LoadVerifiedPlugin(
        string dllPath,
        string? signatureBase64,
        string? expectedSha256Hex = null)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
        {
            throw new ArgumentException("插件 DLL 路径不能为空。", nameof(dllPath));
        }

        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"插件 DLL 不存在：{fullPath}", fullPath);
        }

        var dllBytes = File.ReadAllBytes(fullPath);
        return LoadVerifiedPlugin(dllBytes, signatureBase64, expectedSha256Hex, fullPath);
    }

    /// <summary>从内存字节加载并验签插件（便于测试与后续内存热更新）。</summary>
    public LoadedPluginHandle LoadVerifiedPlugin(
        byte[] dllBytes,
        string? signatureBase64,
        string? expectedSha256Hex = null,
        string? sourcePath = null)
    {
        if (dllBytes is null || dllBytes.Length == 0)
        {
            throw new ArgumentException("插件 DLL 内容为空。", nameof(dllBytes));
        }

        if (!Ed25519Verifier.Verify(dllBytes, signatureBase64, expectedSha256Hex, _trustedPublicKeyBase64))
        {
            throw new SecurityException(
                "插件签名校验失败，已拒绝加载：" + (string.IsNullOrWhiteSpace(sourcePath) ? "<内存插件>" : sourcePath));
        }

        var context = new PlatformPluginLoadContext(
            string.IsNullOrWhiteSpace(sourcePath) ? "in-memory-plugin.dll" : Path.GetFileName(sourcePath)!);
        try
        {
            using var stream = new MemoryStream(dllBytes, writable: false);
            var assembly = context.LoadFromStream(stream);
            var plugin = CreatePluginInstance(assembly);
            return new LoadedPluginHandle(plugin, context, sourcePath);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary>尝试加载：失败返回 false 并给出中文错误，不抛异常（宿主启动路径使用）。</summary>
    public bool TryLoadVerifiedPlugin(
        string dllPath,
        string? signatureBase64,
        string? expectedSha256Hex,
        out LoadedPluginHandle? handle,
        out string? error)
    {
        try
        {
            handle = LoadVerifiedPlugin(dllPath, signatureBase64, expectedSha256Hex);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException
                                       or FileNotFoundException or BadImageFormatException or IOException)
        {
            handle = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>仅校验插件字节的 SHA256 与 Ed25519 签名（不加载、不实例化），用于安装前预检。</summary>
    public bool VerifyPluginPayload(byte[] dllBytes, string? signatureBase64, string? expectedSha256Hex = null)
        => Ed25519Verifier.Verify(dllBytes, signatureBase64, expectedSha256Hex, _trustedPublicKeyBase64);

    private static IPlatformPlugin CreatePluginInstance(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var detail = ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message;
            throw new InvalidOperationException($"插件类型加载失败：{detail}", ex);
        }

        var candidates = types
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IPlatformPlugin).IsAssignableFrom(t))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException("插件未包含 IPlatformPlugin 实现。");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"插件包含 {candidates.Length} 个 IPlatformPlugin 实现，必须恰好一个。");
        }

        var pluginType = candidates[0];
        var ctor = pluginType.GetConstructor(Type.EmptyTypes);
        if (ctor is null)
        {
            throw new InvalidOperationException($"插件类型 {pluginType.FullName} 缺少公开无参构造函数。");
        }

        return ctor.Invoke(null) as IPlatformPlugin
               ?? throw new InvalidOperationException($"插件类型 {pluginType.FullName} 无法实例化为 IPlatformPlugin。");
    }
}
