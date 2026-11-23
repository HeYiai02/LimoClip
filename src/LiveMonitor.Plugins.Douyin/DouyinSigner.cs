using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// 抖音 WebSocket 弹幕的 signature 计算：签名脚本（sign.js）通过 Jint 在本机执行，
/// 脚本来源按优先级解析，支持「热更新脚本」——把新 sign.js 放到缓存目录即可，无需重新发版。
/// 签名输入 = 对 13 个固定查询参数做 md5 十六进制串，脚本内 get_sign(md5) 返回 signature。
/// </summary>
public sealed class DouyinSigner : IDisposable
{
    /// <summary>签名脚本默认可下载地址（AGPL 参考实现；不随仓库分发，首次使用时下载并缓存到本机）。</summary>
    public const string DefaultScriptUrl =
        "https://raw.githubusercontent.com/saermart/DouyinLiveWebFetcher/main/sign.js";

    /// <summary>签名脚本环境变量覆盖（本地路径）。</summary>
    public const string ScriptPathEnvironmentVariable = "LIMOCLIP_DOUYIN_SIGN_JS";

    private const string SignatureParamOrder =
        "live_id,aid,version_code,webcast_sdk_version," +
        "room_id,sub_room_id,sub_channel_id,did_rule," +
        "user_unique_id,device_platform,device_type,ac," +
        "identity";

    private const string Prelude = """
var window = this;
var globalThis = this;
var self = this;
var navigator = {
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  platform: 'Win32',
  language: 'zh-CN'
};
var location = { href: 'https://live.douyin.com/', protocol: 'https:', host: 'live.douyin.com' };
var document = {
  cookie: '',
  createElement: function () { return { getContext: function () { return null; } }; },
  documentElement: {}
};
var setTimeout = function (fn, ms) { return 0; };
var clearTimeout = function (id) {};
""";

    private static readonly object EngineGate = new();

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _scriptPathOverride;
    private readonly string _cacheDirectory;
    private Engine? _engine;
    private string? _scriptSource;

    /// <param name="httpClient">用于下载签名脚本（为空时自建）。</param>
    /// <param name="scriptPath">显式签名脚本路径（测试/离线场景）。</param>
    /// <param name="cacheDirectory">签名脚本缓存目录，默认 %LocalAppData%/LiveMonitor/cache/douyin。</param>
    public DouyinSigner(HttpClient? httpClient = null, string? scriptPath = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttpClient = httpClient is null;
        _scriptPathOverride = string.IsNullOrWhiteSpace(scriptPath) ? null : scriptPath;
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveMonitor", "cache", "douyin")
            : cacheDirectory;
    }

    /// <summary>本次实际使用的签名脚本来源（文件路径或下载地址），用于诊断提示。</summary>
    public string ScriptSource => _scriptSource ?? string.Empty;

    /// <summary>加载签名脚本（如本地缺失则尝试下载并缓存）。</summary>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (_engine is not null) return;

        var (source, script) = await ResolveScriptAsync(ct).ConfigureAwait(false);

        var engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(60))
            .LimitMemory(256L * 1024 * 1024)
            .MaxStatements(0));
        engine.SetValue("console", new { log = new Action<object>(_ => { }) });
        engine.Execute(Prelude);
        engine.Execute(script);
        var type = engine.Evaluate("typeof get_sign");
        if (!string.Equals(type.ToString(), "function", StringComparison.Ordinal))
            throw new InvalidOperationException("抖音签名脚本无效：未找到 get_sign 函数。");

        lock (EngineGate)
        {
            _engine = engine;
            _scriptSource = source;
        }
    }

    /// <summary>对 WebSocket 地址计算 signature（脚本未加载时抛 InvalidOperationException）。</summary>
    public string Sign(string webSocketUrl)
    {
        if (string.IsNullOrWhiteSpace(webSocketUrl))
            throw new ArgumentException("WebSocket 地址不能为空。", nameof(webSocketUrl));

        var engine = _engine ?? throw new InvalidOperationException(
            "抖音弹幕签名尚未初始化，请先调用 PrepareAsync。");

        var signatureInput = ComputeSignatureInput(webSocketUrl);
        lock (EngineGate)
        {
            var escaped = JsonSerializer.Serialize(signatureInput);
            var result = engine.Evaluate($"get_sign({escaped})");
            var signature = result.ToString();
            if (string.IsNullOrWhiteSpace(signature))
                throw new InvalidOperationException("抖音弹幕签名脚本返回为空。");
            return signature;
        }
    }

    /// <summary>计算 13 个固定参数的 md5 输入串（与网页版一致）。</summary>
    internal static string ComputeSignatureInput(string webSocketUrl)
    {
        var query = ExtractQuery(webSocketUrl);
        var parameters = ParseQuery(query);
        var builder = new StringBuilder();
        var first = true;
        foreach (var name in SignatureParamOrder.Split(','))
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append(name).Append('=');
            builder.Append(parameters.TryGetValue(name, out var value) ? value : string.Empty);
        }

        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(md5).ToLowerInvariant();
    }

    internal static string ExtractQuery(string url)
    {
        var index = url.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? string.Empty : url[(index + 1)..];
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            var name = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            result[name] = value;
        }

        return result;
    }

    private async Task<(string Source, string Script)> ResolveScriptAsync(CancellationToken ct)
    {
        foreach (var candidate in EnumerateLocalCandidates())
        {
            if (!File.Exists(candidate)) continue;
            var content = await File.ReadAllTextAsync(candidate, ct).ConfigureAwait(false);
            if (content.Contains("get_sign", StringComparison.Ordinal))
                return (candidate, content);
        }

        // 本地没有：下载到缓存目录（首次使用）。下载失败给出可操作的中文提示。
        string? downloadError = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DefaultScriptUrl);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var script = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (script.Contains("get_sign", StringComparison.Ordinal))
            {
                var target = Path.Combine(_cacheDirectory, "sign.js");
                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(target, script, ct).ConfigureAwait(false);
                return (DefaultScriptUrl, script);
            }

            downloadError = "下载到的脚本不包含 get_sign 函数";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            downloadError = ex.Message;
        }

        throw new InvalidOperationException(
            "抖音弹幕需要签名脚本 sign.js，但本机没有找到且下载失败。" +
            (downloadError is null ? string.Empty : $"（{downloadError}）") +
            $"请联网后重试，或手动把 sign.js 放到：{Path.Combine(_cacheDirectory, "sign.js")}。" +
            "抖音直播的拉流/录制不受影响。");
    }

    private IEnumerable<string> EnumerateLocalCandidates()
    {
        if (_scriptPathOverride is not null) yield return _scriptPathOverride;

        var fromEnvironment = Environment.GetEnvironmentVariable(ScriptPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

        yield return Path.Combine(_cacheDirectory, "sign.js");
        yield return Path.Combine(AppContext.BaseDirectory, "douyin-sign.js");
        yield return Path.Combine(AppContext.BaseDirectory, "douyin", "sign.js");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveMonitor", "plugins", "douyin", "sign.js");
    }

    public void Dispose()
    {
        _engine = null;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
