using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站 WBI 签名工具。
/// <para>
/// 直播弹幕接口 <c>getDanmuInfo</c> 自 2025 年起带风控校验，缺少 <c>w_rid</c>/<c>wts</c>
/// 会返回 <c>code=-352</c>。签名算法：取 <c>nav</c> 接口 <c>data.wbi_img</c> 的 img/sub 两个 key，
/// 拼接后按固定置换表取前 32 位得到 mixinKey；请求参数按 key 排序、过滤 <c>!'()*</c>、
/// 追加 <c>wts</c> 后 urlencode 拼接，再对「查询串 + mixinKey」取 MD5 得到 <c>w_rid</c>。
/// </para>
/// </summary>
public static class BilibiliWbiSigner
{
    /// <summary>mixinKey 置换表（B 站 Web 端固定顺序）。</summary>
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    };

    /// <summary>
    /// 由 <c>wbi_img.img_url</c> / <c>sub_url</c> 的文件名 key 计算 mixinKey。
    /// </summary>
    /// <param name="imgKey">img_url 的文件名（不含扩展名），32 位十六进制。</param>
    /// <param name="subKey">sub_url 的文件名（不含扩展名），32 位十六进制。</param>
    public static string CreateMixinKey(string imgKey, string subKey)
    {
        if (string.IsNullOrWhiteSpace(imgKey))
        {
            throw new ArgumentException("imgKey 不能为空。", nameof(imgKey));
        }

        if (string.IsNullOrWhiteSpace(subKey))
        {
            throw new ArgumentException("subKey 不能为空。", nameof(subKey));
        }

        var raw = imgKey.Trim() + subKey.Trim();
        if (raw.Length < 64)
        {
            throw new ArgumentException("WBI 签名密钥长度不足（imgKey/subKey 应为 32 位十六进制）。");
        }

        var builder = new StringBuilder(32);
        for (var i = 0; i < 32; i++)
        {
            builder.Append(raw[MixinKeyEncTab[i]]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 为请求参数生成带 WBI 签名的查询串（含 <c>wts</c> 与 <c>w_rid</c>）。
    /// </summary>
    /// <param name="parameters">业务查询参数（不含 wts/w_rid）。</param>
    /// <param name="mixinKey"><see cref="CreateMixinKey"/> 的结果。</param>
    /// <param name="timestampSeconds">Unix 秒级时间戳（注入时钟便于测试）。</param>
    public static string Sign(
        IEnumerable<KeyValuePair<string, string>> parameters,
        string mixinKey,
        long timestampSeconds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrWhiteSpace(mixinKey))
        {
            throw new ArgumentException("mixinKey 不能为空。", nameof(mixinKey));
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var pair in parameters)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                continue;
            }

            pairs.Add(pair);
        }

        pairs.Add(new KeyValuePair<string, string>(
            "wts",
            timestampSeconds.ToString(CultureInfo.InvariantCulture)));
        pairs.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        var parts = new List<string>(pairs.Count);
        foreach (var pair in pairs)
        {
            parts.Add(string.Concat(
                Uri.EscapeDataString(FilterSpecialChars(pair.Key)),
                "=",
                Uri.EscapeDataString(FilterSpecialChars(pair.Value))));
        }

        var query = string.Join("&", parts);
        return string.Concat(query, "&w_rid=", Md5Hex(string.Concat(query, mixinKey)));
    }

    /// <summary>B 站签名规则要求从参数中去掉 <c>!'()*</c> 这些字符。</summary>
    private static string FilterSpecialChars(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '!' or '\'' or '(' or ')' or '*')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Md5Hex(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
