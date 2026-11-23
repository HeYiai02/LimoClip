using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>
/// 斗鱼 H5 播放签名（ub98484234）执行器。
/// 斗鱼的签名函数由 <c>swf_api/homeH5Enc</c> 下发（混淆 JS，运行时 eval 解密），
/// 因此这里用 Jint 执行下发的 JS，并注入 <c>CryptoJS.MD5</c> 的 .NET 实现，
/// 避免额外请求第三方 JS 库。
/// </summary>
public sealed class DouyuSigner : IDisposable
{
    private const string HomeH5EncEndpoint = "https://www.douyu.com/swf_api/homeH5Enc?rids={0}";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task<string?>> _jsFetcher;
    private readonly ConcurrentDictionary<string, string?> _jsCache = new(StringComparer.Ordinal);

    public DouyuSigner()
        : this(null, null)
    {
    }

    public DouyuSigner(HttpClient? httpClient)
        : this(httpClient, null)
    {
    }

    internal DouyuSigner(HttpClient? httpClient, Func<string, CancellationToken, Task<string?>>? jsFetcher)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.douyu.com/");
        _jsFetcher = jsFetcher ?? FetchSignScriptAsync;
    }

    /// <summary>计算 getH5Play 请求所需的签名参数（v/did/tt/sign）。</summary>
    public async Task<IReadOnlyDictionary<string, string>> SignAsync(string roomId, CancellationToken ct = default)
    {
        if (!DouyuUrlParser.IsRoomNumber(roomId))
        {
            throw new ArgumentException($"斗鱼房间号必须为数字：{roomId}", nameof(roomId));
        }

        var script = await GetSignScriptAsync(roomId, ct);
        var did = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var tt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string signature;
        try
        {
            var engine = CreateEngine();
            engine.Execute(script);
            var value = engine.Evaluate($"ub98484234('{roomId}','{did}',{tt})");
            signature = value?.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"斗鱼播放签名执行失败：{ex.Message}", ex);
        }

        var fields = ParseSignature(signature);
        if (fields.Count == 0 || !fields.ContainsKey("sign"))
        {
            throw new InvalidOperationException("斗鱼播放签名结果为空或不包含 sign 字段。");
        }

        return fields;
    }

    internal static Dictionary<string, string> ParseSignature(string signature)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in signature.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            result[pair[..index]] = pair[(index + 1)..];
        }

        return result;
    }

    private static Engine CreateEngine()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.SetValue("__limoclip_md5", new Func<string, string>(value =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()));
        engine.Execute(ScriptPrelude);
        return engine;
    }

    private const string ScriptPrelude = """
        var window = {};
        var document = { cookie: '' };
        var navigator = { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36' };
        var location = { href: 'https://www.douyu.com/' };
        var setTimeout = function (fn, t) { return 0; };
        var clearTimeout = function (id) { };
        var CryptoJS = { MD5: function (x) { var hex = __limoclip_md5(String(x)); return { toString: function () { return hex; }, valueOf: function () { return hex; } }; } };
        """;

    private async Task<string> GetSignScriptAsync(string roomId, CancellationToken ct)
    {
        if (_jsCache.TryGetValue(roomId, out var cached))
        {
            return cached ?? throw new InvalidOperationException("斗鱼签名脚本获取失败（已缓存失败结果）。");
        }

        var script = await _jsFetcher(roomId, ct);
        if (string.IsNullOrWhiteSpace(script))
        {
            _jsCache[roomId] = null;
            throw new InvalidOperationException("无法获取斗鱼播放签名脚本（homeH5Enc 返回为空）。");
        }

        _jsCache[roomId] = script;
        return script;
    }

    private async Task<string?> FetchSignScriptAsync(string roomId, CancellationToken ct)
    {
        try
        {
            var url = string.Format(HomeH5EncEndpoint, Uri.EscapeDataString(roomId));
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("room" + roomId, out var script))
            {
                return null;
            }

            return script.GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"获取斗鱼播放签名脚本失败：{ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
