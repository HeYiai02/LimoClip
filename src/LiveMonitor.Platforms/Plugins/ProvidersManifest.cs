using System.Text.Json;
using System.Text.Json.Serialization;
using LiveMonitor.Core.Enums;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// providers-manifest.json 的强类型模型。
/// 该文件由发布方签名后托管在 GitHub Raw / Gitee 镜像；本地更新流程先验签 manifest 再下载插件。
/// </summary>
public sealed class ProvidersManifest
{
    /// <summary>当前客户端支持的 manifest 版本。</summary>
    public const int SupportedManifestVersion = 1;

    public int ManifestVersion { get; init; } = SupportedManifestVersion;

    public DateTimeOffset? UpdatedAt { get; init; }

    public IReadOnlyList<ProviderPluginEntry> Plugins { get; init; } = Array.Empty<ProviderPluginEntry>();

    /// <summary>当前客户端版本（用于 minAppVersion 过滤，可在加载前覆盖）。</summary>
    public string ClientVersion { get; init; } = "0.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString,
    };

    /// <summary>解析 manifest JSON；语法错误抛 <see cref="InvalidDataException"/>。</summary>
    public static ProvidersManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("providers-manifest.json 内容为空。");
        }

        try
        {
            return JsonSerializer.Deserialize<ProvidersManifest>(json, JsonOptions)
                   ?? throw new InvalidDataException("providers-manifest.json 反序列化结果为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"providers-manifest.json 解析失败：{ex.Message}", ex);
        }
    }

    /// <summary>按 pluginId 查找条目。</summary>
    public ProviderPluginEntry? Find(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        return Plugins.FirstOrDefault(p =>
            string.Equals(p.PluginId, pluginId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>校验 manifest 结构与字段；返回人类可读的问题列表（空表示通过）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ManifestVersion != SupportedManifestVersion)
        {
            errors.Add($"不支持的 manifestVersion：{ManifestVersion}（当前支持 {SupportedManifestVersion}）。");
        }

        if (Plugins.Count == 0)
        {
            errors.Add("manifest 中没有任何插件条目。");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Plugins.Count; i++)
        {
            var entry = Plugins[i];
            var label = $"plugins[{i}]";
            if (string.IsNullOrWhiteSpace(entry.PluginId))
            {
                errors.Add($"{label}.pluginId 不能为空。");
            }
            else if (!seenIds.Add(entry.PluginId))
            {
                errors.Add($"{label}.pluginId「{entry.PluginId}」重复。");
            }

            if (!Enum.IsDefined(entry.Platform))
            {
                errors.Add($"{label}.platform「{(int)entry.Platform}」不是合法的平台枚举。");
            }

            if (string.IsNullOrWhiteSpace(entry.Version))
            {
                errors.Add($"{label}.version 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(entry.MinAppVersion))
            {
                errors.Add($"{label}.minAppVersion 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(entry.DllName) ||
                !entry.DllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                entry.DllName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                errors.Add($"{label}.dllName 必须是单个 .dll 文件名。");
            }

            if (!IsAbsoluteHttpsUrl(entry.DownloadUrl))
            {
                errors.Add($"{label}.downloadUrl 必须是 https 绝对地址。");
            }

            if (!IsSha256Hex(entry.Sha256))
            {
                errors.Add($"{label}.sha256 必须是 64 位十六进制哈希。");
            }

            if (!IsBase64Signature(entry.Ed25519Signature))
            {
                errors.Add($"{label}.ed25519Signature 必须是 Base64 的 64 字节 Ed25519 签名。");
            }

            var scriptIndex = 0;
            foreach (var script in entry.ExtraScripts)
            {
                var scriptLabel = $"{label}.extraScripts[{scriptIndex}]";
                if (string.IsNullOrWhiteSpace(script.Name))
                {
                    errors.Add($"{scriptLabel}.name 不能为空。");
                }

                if (!IsAbsoluteHttpsUrl(script.DownloadUrl))
                {
                    errors.Add($"{scriptLabel}.downloadUrl 必须是 https 绝对地址。");
                }

                if (!IsSha256Hex(script.Sha256))
                {
                    errors.Add($"{scriptLabel}.sha256 必须是 64 位十六进制哈希。");
                }

                if (!IsBase64Signature(script.Ed25519Signature))
                {
                    errors.Add($"{scriptLabel}.ed25519Signature 必须是 Base64 的 64 字节 Ed25519 签名。");
                }

                scriptIndex++;
            }
        }

        return errors;
    }

    /// <summary>校验并在有错误时抛出 <see cref="InvalidDataException"/>。</summary>
    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException("providers-manifest.json 校验失败：" + string.Join("；", errors));
        }
    }

    private static bool IsAbsoluteHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsSha256Hex(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 64 &&
           value.All(c => Uri.IsHexDigit(c));

    private static bool IsBase64Signature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value.Trim()).Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>manifest 中的单个平台插件条目。</summary>
public sealed class ProviderPluginEntry
{
    public string PluginId { get; init; } = string.Empty;

    public PlatformType Platform { get; init; }

    public string Version { get; init; } = string.Empty;

    public string MinAppVersion { get; init; } = string.Empty;

    public string DllName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string Ed25519Signature { get; init; } = string.Empty;

    public IReadOnlyList<ProviderManifestExtraScript> ExtraScripts { get; init; } =
        Array.Empty<ProviderManifestExtraScript>();
}

/// <summary>插件附带的脚本文件（如斗鱼签名脚本 .js）。</summary>
public sealed class ProviderManifestExtraScript
{
    public string Name { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string Ed25519Signature { get; init; } = string.Empty;
}
