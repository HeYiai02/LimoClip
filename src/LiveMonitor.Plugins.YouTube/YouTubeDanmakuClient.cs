using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.YouTube;

/// <summary>
/// YouTube 直播间实时弹幕客户端（InnerTube 直播聊天轮询）。
/// <para>
/// 连接流程：解析直播页 HTML 取 <c>INNERTUBE_API_KEY</c>/clientVersion/聊天室 continuation，
/// 之后循环 POST <c>youtubei/v1/live_chat/get_live_chat</c> 并解析弹幕；
/// 单次 <see cref="ConnectAsync"/> 只做一次连接尝试，断线重连由 DanmakuManager 统一负责。
/// </para>
/// <para>
/// 句柄（<c>@handle</c>）或频道 ID（<c>channel:UC…</c>）房间会先用 yt-dlp -j 解析成当前直播的视频 ID。
/// </para>
/// </summary>
public sealed class YouTubeDanmakuClient : IDanmakuClient
{
    private static readonly TimeSpan DisconnectWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly string _roomId;
    private readonly string? _cookie;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IYtDlpRunner? _runner;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    private CancellationTokenSource? _lifeCts;
    private Task? _pumpTask;
    private bool _connected;
    private bool _connectInProgress;
    private int _disposed;
    private int _disconnectedRaised;
    private volatile bool _disconnectRequested;

    /// <param name="roomId">YouTube 视频 ID、<c>@handle</c>、<c>channel:UC…</c> 或可识别的直播链接。</param>
    /// <param name="authCookie">完整 Cookie 头（需已登录 YouTube 时用于人机校验），可为 null。</param>
    /// <param name="httpClient">HTTP 客户端，测试注入；null 时自建 20s 超时客户端并在 Dispose 释放。</param>
    /// <param name="ytDlpRunner">yt-dlp 运行器，仅句柄/频道 ID 房间需要；null 时按 ToolLocator 解析。</param>
    /// <param name="delay">轮询等待函数，测试注入。</param>
    /// <param name="clock">消息时间戳来源，测试注入。</param>
    public YouTubeDanmakuClient(
        string roomId,
        string? authCookie,
        HttpClient? httpClient = null,
        IYtDlpRunner? ytDlpRunner = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("YouTube 房间标识不能为空。", nameof(roomId));
        }

        _roomId = YouTubeUrlParser.ExtractRoomId(roomId);
        _cookie = PlatformDefaults.NormalizeCookie(authCookie);
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _runner = ytDlpRunner;
        _delay = delay ?? (static (timeSpan, token) => Task.Delay(timeSpan, token));
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public PlatformType Platform => PlatformType.YouTube;

    /// <inheritdoc />
    public string RoomId => _roomId;

    /// <inheritdoc />
    public event Action<DanmakuMessage>? MessageReceived;

    /// <inheritdoc />
    public event Action<Exception?>? Disconnected;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_connected || _connectInProgress)
            {
                throw new InvalidOperationException("YouTube 弹幕连接已建立或正在建立中，请先调用 DisconnectAsync。");
            }

            _connectInProgress = true;
            _disconnectRequested = false;
            _disconnectedRaised = 0;
        }

        try
        {
            var videoId = await ResolveVideoIdAsync(ct).ConfigureAwait(false);
            var bootstrap = await FetchBootstrapAsync(videoId, ct).ConfigureAwait(false);

            lock (_sync)
            {
                ThrowIfDisposed();
                _lifeCts = new CancellationTokenSource();
                _pumpTask = Task.Run(() => PumpAsync(bootstrap, _lifeCts.Token), CancellationToken.None);
                _connected = true;
            }
        }
        finally
        {
            lock (_sync)
            {
                _connectInProgress = false;
            }
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return Task.CompletedTask;
            }

            _disconnectRequested = true;
            _connected = false;
            _lifeCts?.Cancel();
        }

        // 不在事件回调里等待 pump 任务结束（订阅方可能在 MessageReceived 回调中同步调用本方法，等待会自锁）。
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disconnectRequested = true;
        CancellationTokenSource? cts;
        Task? pumpTask;
        lock (_sync)
        {
            _connected = false;
            cts = _lifeCts;
            pumpTask = _pumpTask;
            _lifeCts = null;
            _pumpTask = null;
        }

        cts?.Cancel();
        if (pumpTask is not null && !pumpTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(pumpTask, Task.Delay(DisconnectWaitTimeout)).ConfigureAwait(false);
            }
            catch
            {
                // 关闭阶段的等待失败不影响资源释放。
            }
        }

        cts?.Dispose();

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> ResolveVideoIdAsync(CancellationToken ct)
    {
        if (YouTubeUrlParser.IsVideoId(_roomId))
        {
            return _roomId;
        }

        var runner = _runner ?? new CliWrapYtDlpRunner(ToolLocator.ResolveYtDlpPath());
        YtDlpResult result;
        try
        {
            result = await runner.RunJsonAsync(YouTubeUrlParser.BuildLiveUrl(_roomId), _cookie, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"解析 YouTube 直播 {_roomId} 的视频 ID 失败：{ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"yt-dlp 解析 YouTube 直播 {_roomId} 失败（exit {result.ExitCode}）：{PlatformDefaults.TrimForError(result.StandardError)}");
        }

        var json = result.StandardOutput.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"yt-dlp 未返回 YouTube 直播 {_roomId} 的 JSON。");
        }

        using var document = JsonDocument.Parse(json);
        var videoId = JsonRead(document.RootElement, "id");
        if (string.IsNullOrWhiteSpace(videoId) || !YouTubeUrlParser.IsVideoId(videoId))
        {
            throw new InvalidOperationException($"未能从 yt-dlp 结果中识别 YouTube 视频 ID：{_roomId}");
        }

        return videoId;
    }

    private async Task<YouTubeChatParser.ChatBootstrap> FetchBootstrapAsync(string videoId, CancellationToken ct)
    {
        var url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&hl=en";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        if (_cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        string html;
        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"请求 YouTube 直播页失败（HTTP {(int)response.StatusCode}）{videoId}。");
            }

            html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"请求 YouTube 直播页失败：{ex.Message}", ex);
        }

        var bootstrap = YouTubeChatParser.TryParseBootstrap(html);
        if (bootstrap is null)
        {
            throw new InvalidOperationException(
                "未能获取 YouTube 聊天室信息：该直播可能未开启聊天、需要登录，或直播已结束。");
        }

        return bootstrap;
    }

    private async Task PumpAsync(YouTubeChatParser.ChatBootstrap bootstrap, CancellationToken ct)
    {
        var continuation = bootstrap.Continuation;
        try
        {
            while (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(continuation))
            {
                YouTubeChatParser.ChatPage page;
                try
                {
                    var json = await RequestChatAsync(bootstrap.ApiKey, bootstrap.ClientVersion, continuation, ct).ConfigureAwait(false);
                    page = YouTubeChatParser.ParseChatResponse(json, _roomId, Platform, _clock());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RaiseDisconnected(ex);
                    return;
                }

                foreach (var message in page.Messages)
                {
                    var handler = MessageReceived;
                    if (handler is null)
                    {
                        continue;
                    }

                    try
                    {
                        handler(message);
                    }
                    catch
                    {
                        // 订阅方异常不能中断聊天轮询。
                    }
                }

                continuation = page.NextContinuation;
                if (string.IsNullOrWhiteSpace(continuation))
                {
                    RaiseDisconnected(null);
                    return;
                }

                try
                {
                    await _delay(page.NextInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
        }
    }

    private async Task<string> RequestChatAsync(
        string apiKey,
        string clientVersion,
        string continuation,
        CancellationToken ct)
    {
        var url = $"{YouTubeChatParser.ChatEndpoint}?key={Uri.EscapeDataString(apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("Referer", $"https://www.youtube.com/watch?v={(YouTubeUrlParser.IsVideoId(_roomId) ? _roomId : string.Empty)}");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
        if (_cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        request.Content = new StringContent(
            YouTubeChatParser.BuildChatRequestBody(continuation, clientVersion),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"YouTube 聊天接口返回 HTTP {(int)response.StatusCode}。");
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private void RaiseDisconnected(Exception? error)
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _connected = false;
        }

        if (_disconnectRequested)
        {
            return;
        }

        try
        {
            Disconnected?.Invoke(error);
        }
        catch
        {
            // 订阅方异常不影响连接状态。
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(YouTubeDanmakuClient));
        }
    }

    private static string? JsonRead(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
