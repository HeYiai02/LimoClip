namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 语音识别「测试连接」的用户可见文案唯一来源（失败原因、成功摘要、超时提示）。
/// 只承载文案与占位拼接；错误码与响应体由调用方提供。
/// </summary>
internal static class AsrConnectionStatusText
{
    /// <summary>未填写转写接口地址。</summary>
    public const string MissingEndpoint = "请先填写转写接口地址。";

    /// <summary>未填写 API Key。</summary>
    public const string MissingApiKey = "请先填写 API Key。";

    /// <summary>请求超时。</summary>
    public const string Timeout = "连接超时，请检查网络或接口地址。";

    /// <summary>接口地址不是合法 URL。</summary>
    public static string InvalidEndpoint(string endpoint) => $"接口地址不是合法的 URL：{endpoint}";

    /// <summary>连接成功摘要。</summary>
    public static string Succeeded(int statusCode, string model) =>
        $"连接成功（HTTP {statusCode}），准备使用模型：{model}";

    /// <summary>HTTP 非成功状态，附带截断后的响应体。</summary>
    public static string Failed(int statusCode, string detail) =>
        $"连接失败（HTTP {statusCode}）：{detail}";

    /// <summary>网络/地址/运行时异常导致的失败。</summary>
    public static string FailedWithMessage(string message) => $"连接失败：{message}";
}
