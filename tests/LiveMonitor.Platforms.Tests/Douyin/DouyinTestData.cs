using System.Net;
using System.Text;

namespace LiveMonitor.Platforms.Tests.Douyin;

/// <summary>抖音测试夹具：HTTP 桩、protobuf 手工构造、签名桩脚本。</summary>
internal static class DouyinTestData
{
    internal const string LiveJson = """
{
  "status_code": 0,
  "data": {
    "data": [
      {
        "id_str": "7711351763655969576",
        "title": "看海老师的直播",
        "status": 2,
        "owner": { "id_str": "3457305561275172", "nickname": "看海老师" },
        "stream_url": {
          "flv_pull_url": {
            "FULL_HD1": "https://pull-flv.example/uhd.flv?wsSecret=1",
            "HD1": "https://pull-flv.example/hd.flv?wsSecret=2",
            "SD1": "https://pull-flv.example/ld.flv?wsSecret=3",
            "SD2": "https://pull-flv.example/sd.flv?wsSecret=4"
          },
          "hls_pull_url_map": {
            "HD1": "https://pull-hls.example/hd/playlist.m3u8"
          }
        }
      }
    ],
    "enter_room_id": "7711351763655969576",
    "room_status": 0
  }
}
""";

    internal const string OfflineJson = """
{
  "status_code": 0,
  "data": {
    "data": [
      {
        "id_str": "7555682083511913256",
        "title": "已下播",
        "status": 4,
        "owner": { "id_str": "1", "nickname": "某主播" },
        "stream_url": {}
      }
    ]
  }
}
""";

    internal const string RiskJson = """
{ "data": { "message": "Request params error", "prompts": "当前服务繁忙，请稍后重试" }, "status_code": 10011 }
""";

    internal const string EmptyRoomJson = """{ "status_code": 0, "data": { "data": [] } }""";

    internal const string StubSignScript = """
function get_sign(md5) { return 'stub-' + md5; }
""";

    /// <summary>HTTP 桩：记录请求并按 responder 返回；homepage 请求自动带 ttwid Set-Cookie。</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    internal static string CreateStubSignScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "limoclip-douyin-sign-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sign.js");
        File.WriteAllText(path, StubSignScript);
        return path;
    }

    internal static HttpClient CreateHttpClient(StubHandler handler, string ttwid = "test-ttwid")
        => new(handler);

    internal static HttpResponseMessage HomepageResponse(string ttwid = "test-ttwid")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>", Encoding.UTF8, "text/html"),
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", $"ttwid={ttwid}; Path=/; Domain=.douyin.com; HttpOnly");
        return response;
    }

    internal static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>手工构造 protobuf ChatMessage（user=2 / content=3）。</summary>
    internal static byte[] BuildChatPayload(ulong userId, string nickName, string content)
    {
        var user = BuildUser(userId, nickName);
        var buffer = new MemoryStream();
        WriteBytesField(buffer, 2, user);
        WriteStringField(buffer, 3, content);
        return buffer.ToArray();
    }

    internal static byte[] BuildUser(ulong userId, string nickName)
    {
        var buffer = new MemoryStream();
        WriteVarintField(buffer, 1, userId);
        WriteStringField(buffer, 3, nickName);
        return buffer.ToArray();
    }

    internal static byte[] BuildResponse(IReadOnlyList<(string Method, byte[] Payload)> messages, string internalExt, bool needAck)
    {
        var buffer = new MemoryStream();
        foreach (var (method, payload) in messages)
        {
            var message = new MemoryStream();
            WriteStringField(message, 1, method);
            WriteBytesField(message, 2, payload);
            WriteBytesField(buffer, 1, message.ToArray());
        }

        WriteStringField(buffer, 5, internalExt);
        if (needAck)
        {
            WriteTag(buffer, 9, 0);
            WriteVarint(buffer, 1);
        }

        return buffer.ToArray();
    }

    internal static void WriteTag(Stream stream, int field, int wire)
        => WriteVarint(stream, (ulong)((field << 3) | wire));

    internal static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    internal static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteTag(stream, field, 0);
        WriteVarint(stream, value);
    }

    internal static void WriteStringField(Stream stream, int field, string value)
        => WriteBytesField(stream, field, Encoding.UTF8.GetBytes(value));

    internal static void WriteBytesField(Stream stream, int field, byte[] value)
    {
        WriteTag(stream, field, 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    internal static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }
}
