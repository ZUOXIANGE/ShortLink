using System.Security.Cryptography;

namespace ShortLink.Application.Partner;

/// <summary>
/// 第三方签名的规范化字符串。纯函数，便于单测与给接入方提供逐字一致的参考实现。
/// <para>
/// 固定 6 行：<c>METHOD\nPATH\nCANONICAL_QUERY\nTIMESTAMP\nNONCE\nSHA256(body)</c>。
/// query 先按 key 的序数值排序，key/value 解码后统一按 RFC3986 重新编码——
/// 不让接入方直接签原始 query，是因为各语言的表单编码对空格、<c>+</c>、<c>~</c> 处理不一致。
/// </para>
/// </summary>
public static class PartnerSignature
{
    public static string Build(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, string?>> query,
        ReadOnlySpan<byte> body,
        string timestamp,
        string nonce)
    {
        var canonicalQuery = string.Join(
            '&',
            query
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Encode(pair.Key)}={Encode(pair.Value ?? string.Empty)}"));

        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        return $"{method.ToUpperInvariant()}\n{path}\n{canonicalQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
    }

    /// <summary>
    /// RFC3986 编码：空格是 <c>%20</c>，不保留 <c>+</c> 表示空格；
    /// <see cref="Uri.EscapeDataString"/> 的输出为 UTF-8 + 大写十六进制，与文档一致。
    /// </summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);
}
