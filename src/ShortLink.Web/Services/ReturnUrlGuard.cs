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

    // 不用 Uri.TryCreate(Absolute) 判绝对地址：Unix 上 "/links" 会被解析成绝对的 file:// 路径，
    // 把站内路径误判成站外地址（同一份代码在 Windows 上返回 false，表现为只在 CI 失败）。
    // 真正的绝对 URI（https://、javascript: 等）首字符不是 '/'，上面的判断已经拒绝。
    private static bool IsUnsafe(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value[0] != '/'
           || value.StartsWith("//", StringComparison.Ordinal)
           || value.Contains('\\', StringComparison.Ordinal)
           || value.Any(char.IsControl);
}
