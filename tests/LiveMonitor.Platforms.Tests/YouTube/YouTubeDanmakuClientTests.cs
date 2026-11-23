using System.Net;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Platforms;
using LiveMonitor.Plugins.YouTube;
using Xunit;

namespace LiveMonitor.Platforms.Tests.YouTube;

public sealed class YouTubeDanmakuClientTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    private const string PageHtml = """
        <html><script>
        var ytcfg = {"INNERTUBE_API_KEY":"TEST_API_KEY","INNERTUBE_CLIENT_VERSION":"2.20250101.00.00"};
        var ytInitialData = {"contents":{"liveChatRenderer":{"continuation":"BOOTSTRAP_CONTINUATION_TOKEN_1234567890"}}};
        </script></html>
        """;

    private const string ChatJson = """
        {
          "continuationContents": {
            "liveChatContinuation": {
              "actions": [
                { "addChatItemAction": { "item": { "liveChatTextMessageRenderer": {
                    "authorName": { "simpleText": "Alice" },
                    "authorExternalChannelId": "UCAlice",
                    "message": { "runs": [ { "text": "hello world" } ] } } } } }
              ],
              "continuations": [
                { "invalidationContinuationData": { "continuation": "NEXT_CONTINUATION_TOKEN", "timeoutMs": 1000 } }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task ConnectAsync_VideoId_FetchesBootstrapAndEmitsMessages()
    {
        var recorder = new RequestRecorder();
        using var httpClient = CreateHttpClient(recorder);
        var client = new YouTubeDanmakuClient(
            VideoId,
            null,
            httpClient,
            delay: static (_, ct) => Task.Delay(10, ct));

        var received = new TaskCompletionSource<LiveMonitor.Core.Models.DanmakuMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);

        try
        {
            await client.ConnectAsync();
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("hello world", message.Content);
            Assert.Equal("Alice", message.SenderName);
            Assert.Equal(PlatformType.YouTube, message.Platform);
            Assert.Equal(VideoId, message.RoomId);

            Assert.Contains(recorder.Requests, request => request.Method == "GET" && request.Url.Contains(VideoId));
            Assert.Contains(recorder.Requests, request => request.Method == "POST" && request.Url.Contains("TEST_API_KEY"));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_PageWithoutChat_Throws()
    {
        var recorder = new RequestRecorder(html: "<html>no chat here</html>");
        using var httpClient = CreateHttpClient(recorder);
        var client = new YouTubeDanmakuClient(VideoId, null, httpClient);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
            Assert.Contains("聊天室", exception.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_HttpFailure_Throws()
    {
        var recorder = new RequestRecorder(pageStatusCode: HttpStatusCode.Forbidden);
        using var httpClient = CreateHttpClient(recorder);
        var client = new YouTubeDanmakuClient(VideoId, null, httpClient);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
            Assert.Contains("403", exception.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_HandleRoom_ResolvesVideoIdWithYtDlp()
    {
        var recorder = new RequestRecorder();
        using var httpClient = CreateHttpClient(recorder);
        var runner = new FakeYtDlpRunner(new YtDlpResult(0, $$"""{ "id": "{{VideoId}}" }""", string.Empty));
        var client = new YouTubeDanmakuClient(
            "@LofiGirl",
            "SID=abc",
            httpClient,
            runner,
            delay: static (_, ct) => Task.Delay(10, ct));

        try
        {
            await client.ConnectAsync();
            Assert.Equal("https://www.youtube.com/@LofiGirl/live", runner.LastUrl);
            Assert.Equal("SID=abc", runner.LastCookie);
            Assert.Contains(recorder.Requests, request => request.Method == "GET" && request.Url.Contains(VideoId));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectAsync_StopsPollingWithoutRaisingError()
    {
        var recorder = new RequestRecorder();
        using var httpClient = CreateHttpClient(recorder);
        var client = new YouTubeDanmakuClient(
            VideoId,
            null,
            httpClient,
            delay: static (_, ct) => Task.Delay(10, ct));
        var errors = new List<Exception?>();
        client.Disconnected += error => errors.Add(error);

        await client.ConnectAsync();
        await Task.Delay(100);
        await client.DisconnectAsync();
        await client.DisposeAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public void Constructor_RejectsEmptyRoomIdAndNewlinesInCookie()
    {
        Assert.Throws<ArgumentException>(() => new YouTubeDanmakuClient(" ", null));
        Assert.Throws<ArgumentException>(() => new YouTubeDanmakuClient(VideoId, "SID=abc\r\nInjected: 1"));
    }

    private static HttpClient CreateHttpClient(RequestRecorder recorder)
        => new(new RecordingHandler(recorder));

    private sealed class FakeYtDlpRunner : IYtDlpRunner
    {
        private readonly YtDlpResult _result;

        public FakeYtDlpRunner(YtDlpResult result)
        {
            _result = result;
        }

        public string? LastUrl { get; private set; }

        public string? LastCookie { get; private set; }

        public Task<YtDlpResult> RunJsonAsync(string url, string? cookie, CancellationToken ct = default)
        {
            LastUrl = url;
            LastCookie = cookie;
            return Task.FromResult(_result);
        }
    }

    private sealed record RecordedRequest(string Method, string Url);

    private sealed class RequestRecorder
    {
        private readonly string _html;
        private readonly HttpStatusCode _pageStatusCode;

        public RequestRecorder(string html = PageHtml, HttpStatusCode pageStatusCode = HttpStatusCode.OK)
        {
            _html = html;
            _pageStatusCode = pageStatusCode;
        }

        public List<RecordedRequest> Requests { get; } = new();

        public void Record(string method, string url)
        {
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(method, url));
            }
        }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(_pageStatusCode)
                {
                    Content = new StringContent(_html, Encoding.UTF8, "text/html")
                };
            }

            return TestHttpMessageHandler.Json(ChatJson, HttpStatusCode.OK);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly RequestRecorder _recorder;

        public RecordingHandler(RequestRecorder recorder)
        {
            _recorder = recorder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            _recorder.Record(request.Method.Method, url);
            var response = _recorder.Respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
