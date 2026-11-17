namespace ShortLink.Application.Validation;

/// <summary>跨领域复用的 URL 规则。</summary>
internal static class UrlRules
{
    internal static bool IsAbsoluteHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
