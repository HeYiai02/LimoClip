using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LiveMonitor.Platforms.Tests.Douyu;

internal static class DouyuTestData
{
    public const string StubSignScript = """
        function ub98484234(rid, did, ts) {
          return 'v=220120261123&did=' + did + '&tt=' + ts + '&sign=' + CryptoJS.MD5(rid + '_' + did + '_' + ts).toString();
        }
        """;

    public static string BetardJson(bool live, string roomId = "9999")
    {
        var payload = new
        {
            room = new
            {
                room_id = long.Parse(roomId),
                room_name = live ? "陪伴每一天！" : "休息中",
                nickname = "yyfyyf",
                owner_uid = 204389,
                show_status = live ? 1 : 2,
                cate_id = "1",
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string StreamJson(int error = 0, string? message = null) => error == 0
        ? JsonSerializer.Serialize(new
        {
            error = 0,
            msg = "ok",
            data = new
            {
                room_id = 9999,
                rtmp_cdn = "scdncmccgudst",
                rtmp_url = "https://stream.example/live",
                rtmp_live = "abc_4000.flv?wsAuth=x&token=y",
                rate = 4,
                multirates = new object[]
                {
                    new { rate = 0, name = "原画2K60", bit = 20048 },
                    new { rate = 4, name = "蓝光4M", bit = 4000 },
                },
            },
        })
        : JsonSerializer.Serialize(new { error, msg = message ?? "error", data = string.Empty });

    public static string HomeH5EncJson(string roomId = "9999") =>
        JsonSerializer.Serialize(new
        {
            data = new Dictionary<string, string>
            {
                ["room" + roomId] = StubSignScript,
            },
        });

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage GzipJsonResponse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(compressed.ToArray()),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    public static HttpClient CreateClient(TestHttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://www.douyu.com/"),
    };
}
