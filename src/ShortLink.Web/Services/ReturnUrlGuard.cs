namespace ShortLink.Web.Services;

/// <summary>登录回跳地址白名单：只允许当前站点内的绝对路径。</summary>
public static class ReturnUrlGuard
{
    public static string GetSafePath(string? returnUrl)
    {
        if (IsUnsafe(returnUrl))
        {
            return "/";
        }

        // 同时校验解码后的形式，避免 %2f、%5c 在规范化时变成站外分隔符。
        var decoded = Uri.UnescapeDataString(returnUrl!);
        return IsUnsafe(decoded) ? "/" : returnUrl!;
    }

    private static bool IsUnsafe(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value[0] != '/'
           || value.StartsWith("//", StringComparison.Ordinal)
           || value.Contains('\\', StringComparison.Ordinal)
           || value.Any(char.IsControl)
           || Uri.TryCreate(value, UriKind.Absolute, out _);
}
