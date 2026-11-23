using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace LimoClip.PluginSigner;

/// <summary>
/// V0.5-B 平台插件签名工具：读取插件 DLL，计算 SHA256，用 Ed25519 私钥对 DLL 原始字节签名，
/// 输出可写入 plugins/providers-manifest.json 的条目（--manifest 时直接合并更新清单）。
/// 私钥只从命令行/环境变量提供，绝不写入仓库。
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;

    private static readonly Dictionary<string, int> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bilibili"] = 1,
        ["Douyin"] = 2,
        ["Douyu"] = 3,
        ["Huya"] = 4,
        ["Twitch"] = 5,
        ["YouTube"] = 6,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return ExitSuccess;
            }

            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());
            return command switch
            {
                "sign" => Sign(options),
                "keygen" => KeyGen(),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin-signer] 失败：{ex.Message}");
            return ExitFailure;
        }
    }

    private static int Sign(Dictionary<string, string> options)
    {
        var dllPath = Require(options, "dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"插件 DLL 不存在：{dllPath}", dllPath);
        }

        var pluginId = Require(options, "plugin-id").Trim();
        var platform = ResolvePlatform(Require(options, "platform"));
        var version = Require(options, "version").Trim();
        var minAppVersion = Get(options, "min-app-version")?.Trim() ?? "0.0.0";
        var dllName = Get(options, "dll-name")?.Trim();
        if (string.IsNullOrEmpty(dllName))
        {
            dllName = Path.GetFileName(dllPath);
        }

        var downloadUrl = Get(options, "download-url")?.Trim();
        if (string.IsNullOrEmpty(downloadUrl))
        {
            downloadUrl = $"https://github.com/heyiai02/LimoClip/releases/download/plugin-{pluginId}-{version}/{dllName}";
        }

        var dllBytes = File.ReadAllBytes(dllPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(dllBytes)).ToLowerInvariant();
        var keyBase64 = ResolveKeyBase64(options);
        var signatureBase64 = SignBytes(dllBytes, keyBase64);

        var entry = new JsonObject
        {
            ["pluginId"] = pluginId,
            ["platform"] = platform,
            ["version"] = version,
            ["minAppVersion"] = minAppVersion,
            ["dllName"] = dllName,
            ["downloadUrl"] = downloadUrl,
            ["sha256"] = sha256,
            ["ed25519Signature"] = signatureBase64,
        };

        Console.Error.WriteLine($"[plugin-signer] 已签名 {dllName}（{dllBytes.Length} 字节）");
        Console.Error.WriteLine($"[plugin-signer] pluginId={pluginId} platform={platform} version={version} minAppVersion={minAppVersion}");
        Console.Error.WriteLine($"[plugin-signer] sha256={sha256}");
        Console.Error.WriteLine($"[plugin-signer] signature={signatureBase64}");

        var manifestPath = Get(options, "manifest")?.Trim();
        if (!string.IsNullOrEmpty(manifestPath))
        {
            var manifestJson = UpdateManifest(manifestPath, entry);
            Console.Error.WriteLine($"[plugin-signer] 已更新清单：{Path.GetFullPath(manifestPath)}");
            var outFile = Get(options, "out")?.Trim();
            if (!string.IsNullOrEmpty(outFile))
            {
                File.WriteAllText(outFile, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        else
        {
            var outFile = Get(options, "out")?.Trim();
            if (!string.IsNullOrEmpty(outFile))
            {
                File.WriteAllText(outFile, entry.ToJsonString(JsonOptions), new UTF8Encoding(false));
                Console.Error.WriteLine($"[plugin-signer] 已写入条目：{Path.GetFullPath(outFile)}");
            }
        }

        Console.WriteLine(entry.ToJsonString(JsonOptions));
        return ExitSuccess;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"[plugin-signer] 未知命令：{command}（可用命令：sign、keygen）");
        PrintUsage();
        return ExitFailure;
    }

    /// <summary>生成一对新的 Ed25519 私钥/公钥（Base64），输出到标准输出；私钥由调用方离线保存。</summary>
    private static int KeyGen()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = (Ed25519PublicKeyParameters)pair.Public;

        Console.WriteLine($"private={Convert.ToBase64String(privateKey.GetEncoded())}");
        Console.WriteLine($"public={Convert.ToBase64String(publicKey.GetEncoded())}");
        Console.Error.WriteLine("[plugin-signer] 请把 private 值离线保存（不要提交仓库）；public 值写入客户端 PluginTrust.PublicKeyBase64。");
        return ExitSuccess;
    }

    private static string UpdateManifest(string manifestPath, JsonObject entry)
    {
        JsonObject manifest;
        if (File.Exists(manifestPath))
        {
            var text = File.ReadAllText(manifestPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                manifest = NewManifest();
            }
            else
            {
                manifest = JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidDataException($"清单不是 JSON 对象：{manifestPath}");
            }
        }
        else
        {
            manifest = NewManifest();
        }

        if (manifest["manifestVersion"] is null)
        {
            manifest["manifestVersion"] = 1;
        }

        var plugins = manifest["plugins"] as JsonArray;
        if (plugins is null)
        {
            plugins = new JsonArray();
            manifest["plugins"] = plugins;
        }

        var pluginId = (string?)entry["pluginId"] ?? string.Empty;
        var replaced = false;
        for (var i = 0; i < plugins.Count; i++)
        {
            var existingId = plugins[i]?["pluginId"]?.GetValue<string>();
            if (string.Equals(existingId, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                plugins[i] = entry.DeepClone();
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            plugins.Add(entry.DeepClone());
        }

        manifest["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var json = manifest.ToJsonString(JsonOptions);
        File.WriteAllText(manifestPath, json);
        return json;
    }

    private static JsonObject NewManifest() => new()
    {
        ["manifestVersion"] = 1,
        ["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["plugins"] = new JsonArray(),
    };

    private static string ResolveKeyBase64(Dictionary<string, string> options)
    {
        var base64 = Get(options, "key-base64")?.Trim();
        if (!string.IsNullOrEmpty(base64))
        {
            return base64;
        }

        var keyPath = Get(options, "key-path")?.Trim();
        if (!string.IsNullOrEmpty(keyPath))
        {
            if (!File.Exists(keyPath))
            {
                throw new FileNotFoundException($"私钥文件不存在：{keyPath}", keyPath);
            }

            return File.ReadAllText(keyPath).Trim();
        }

        return Environment.GetEnvironmentVariable("LIVEMONITOR_PLUGIN_KEY")?.Trim()
               ?? throw new InvalidOperationException(
                   "缺少签名私钥：请提供 --key-base64、--key-path 或环境变量 LIVEMONITOR_PLUGIN_KEY（Ed25519 私钥 seed 的 Base64）。");
    }

    private static string SignBytes(byte[] payload, string keyBase64)
    {
        byte[] seed;
        try
        {
            seed = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("私钥不是有效的 Base64 字符串。");
        }

        if (seed.Length != 32)
        {
            throw new InvalidOperationException($"私钥长度必须为 32 字节（Ed25519 seed），实际 {seed.Length} 字节。");
        }

        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, privateKey);
        signer.BlockUpdate(payload, 0, payload.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private static int ResolvePlatform(string value)
    {
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var numeric))
        {
            if (numeric is < 1 or > 6)
            {
                throw new ArgumentException($"platform 数字超出范围（1~6）：{trimmed}");
            }

            return numeric;
        }

        if (PlatformIds.TryGetValue(trimmed, out var id))
        {
            return id;
        }

        throw new ArgumentException($"无法识别的 platform：{trimmed}（可用：Bilibili/Douyin/Douyu/Huya/Twitch/YouTube 或 1~6）");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数：{token}");
            }

            var name = token[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 --{name} 缺少取值。");
            }

            options[name] = args[++i];
        }

        return options;
    }

    private static string Require(Dictionary<string, string> options, string name)
    {
        var value = Get(options, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"缺少必需参数 --{name}。");
        }

        return value;
    }

    private static string? Get(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            LimoClip 平台插件签名工具（V0.5-B）

            用法：
              dotnet run --project scripts/PluginSigner -- sign \
                --dll <插件 DLL 路径> \
                --plugin-id <插件标识，如 youtube-dlp> \
                --platform <Bilibili|Douyin|Douyu|Huya|Twitch|YouTube 或 1~6> \
                --version <插件版本，如 1.2.3> \
                [--min-app-version <最低应用版本，默认 0.0.0>] \
                [--dll-name <清单内文件名，默认 DLL 文件名>] \
                [--download-url <下载地址，默认 GitHub Release 模板>] \
                [--key-base64 <私钥 Base64> | --key-path <私钥文件>] \
                [--manifest <要合并更新的 providers-manifest.json>] \
                [--out <输出文件>]

              dotnet run --project scripts/PluginSigner -- keygen
                # 生成新的 Ed25519 密钥对（输出 private= / public= 两行 Base64）

            说明：
              * 签名对象是 DLL 原始字节；清单里同时写入 SHA256 与 Ed25519 签名（Base64，64 字节）。
              * 私钥是 32 字节 Ed25519 seed 的 Base64，可通过 --key-base64、--key-path 或
                环境变量 LIVEMONITOR_PLUGIN_KEY 提供；私钥绝不能提交到仓库。
              * 未配置 --download-url 时生成 GitHub Release 资产模板地址，可用 --download-url 覆盖。
            """);
    }
}
