using LiveMonitor.Plugins.Huya;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

internal static class HuyaTestData
{
    public const string LiveJson = """
    {
      "status": 200,
      "message": "",
      "data": {
        "liveStatus": "ON",
        "profileInfo": {
          "uid": 1346609715,
          "nick": "虎牙赛事",
          "profileRoom": 660000
        },
        "liveData": {
          "uid": 1346609715,
          "nick": "虎牙赛事",
          "roomName": "S16 赛事直播",
          "profileRoom": 660000,
          "bitRateInfo": "[{\"sDisplayName\":\"蓝光10M\",\"iBitRate\":0},{\"sDisplayName\":\"蓝光4M\",\"iBitRate\":4000}]"
        },
        "stream": {
          "flv": {
            "multiLine": [
              {
                "url": "https://al.flv.huya.com/src/660000-1.flv?wsSecret=abc&wsTime=1",
                "cdnType": "AL",
                "webPriorityRate": 100,
                "lineIndex": 0
              },
              {
                "url": "https://tx.flv.huya.com/src/660000-1.flv?wsSecret=def&wsTime=1",
                "cdnType": "TX",
                "webPriorityRate": 50,
                "lineIndex": 1
              }
            ]
          }
        }
      }
    }
    """;

    public const string OfflineJson = """
    {
      "status": 200,
      "message": "",
      "data": {
        "liveStatus": "OFF",
        "profileInfo": { "uid": 1346609715, "nick": "虎牙赛事", "profileRoom": 660000 },
        "liveData": { "uid": 1346609715, "nick": "虎牙赛事", "roomName": "S16 赛事直播", "profileRoom": 660000 }
      }
    }
    """;

    public const string NotFoundJson = """
    { "status": 422, "message": "该主播不存在" }
    """;

    public const string RoomPageHtml = """
    <html><head><title>虎牙直播</title></head><body>
    <script>
    var hyPlayerConfig = {"gameLiveInfo":{"profileRoom":660000,"uid":1346609715},"gameStreamInfoList":[]};
    </script>
    </body></html>
    """;

    public static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestHttpMessageHandler(responder));

    public static HttpResponseMessage Json(string json)
        => TestHttpMessageHandler.Json(json);

    public static HttpResponseMessage GzipHtml(string html)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            gzip.Write(bytes, 0, bytes.Length);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(buffer.ToArray())
        };
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    public static byte[] BuildChatBody(string uid, string nick, string content)
    {
        var body = new HuyaTarsWriter();
        body.BeginStruct(0);
        body.Write(long.Parse(uid), 0);
        body.Write(nick, 2);
        body.Write(string.Empty, 4);
        body.EndStruct();
        body.Write(content, 3);
        body.BeginStruct(6);
        body.Write(-1, 0);
        body.EndStruct();
        return body.ToArray();
    }

    public static byte[] BuildPushFrame(long uri, byte[] body)
    {
        var payload = new HuyaTarsWriter();
        payload.Write(0, 0);
        payload.Write(uri, 1);
        payload.Write(body, 2);

        var frame = new HuyaTarsWriter();
        frame.Write(7, 0);
        frame.Write(payload.ToArray(), 1);
        return frame.ToArray();
    }

    public static string? Serialize(JsonElement element) => element.GetRawText();
}
