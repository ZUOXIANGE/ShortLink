using ShortLink.Application.Partner;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 把 <see cref="HttpRequest"/> 转成第三方签名使用的规范化字符串。
/// 具体规则集中在 <see cref="PartnerSignature"/>，入口层只负责读取路径与 query。
/// </summary>
public static class ApiRequestCanonicalizer
{
    public static string Build(
        HttpRequest request,
        ReadOnlySpan<byte> body,
        string timestamp,
        string nonce)
    {
        var query = request.Query.SelectMany(pair => pair.Value.Select(value =>
            new KeyValuePair<string, string?>(pair.Key, value)));

        return PartnerSignature.Build(
            request.Method,
            request.Path.Value ?? "/",
            query,
            body,
            timestamp,
            nonce);
    }
}
