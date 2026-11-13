using System.Security.Cryptography;

namespace ShortLink.Domain.Links;

/// <summary>短码生成与校验。字符集去掉了易混淆字符（0/O/1/l/I）。</summary>
public static class ShortCode
{
    /// <summary>短码字符集。</summary>
    public const string Alphabet = "23456789abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";

    public const int MinLength = 3;

    public const int MaxLength = 32;

    public const int DefaultLength = 6;

    /// <summary>系统保留字，避免与 API、健康检查、静态资源等路由冲突。</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "healthz", "ready", "live", "metrics", "scalar", "openapi",
        "swagger", "admin", "login", "logout", "signin", "signout", "redirect",
        "favicon.ico", "robots.txt", "sitemap.xml", "error", "assets", "static",
        "_framework", "_blazor", "_content", "css", "js", "images", "wwwroot",
    };

    /// <summary>生成随机短码。使用加密随机源，避免可预测的枚举遍历。</summary>
    public static string Generate(int length = DefaultLength)
    {
        if (length is < MinLength or > MaxLength)
        {
            length = DefaultLength;
        }

        return string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
    }

    /// <summary>校验用户自定义短码：长度 3~32，仅字母数字与 <c>_</c> <c>-</c>，且不是保留字。</summary>
    public static bool IsValid(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (code.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (var ch in code)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
            {
                return false;
            }
        }

        return !Reserved.Contains(code);
    }

    public static bool IsReserved(string? code)
        => !string.IsNullOrWhiteSpace(code) && Reserved.Contains(code);

    /// <summary>把任意标题转成可用的短码候选（非 ASCII 字符会被丢弃）。</summary>
    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var buffer = new char[text.Length];
        var length = 0;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer[length++] = ch;
            }
            else if (ch is ' ' or '-' or '_')
            {
                if (length > 0 && buffer[length - 1] != '-')
                {
                    buffer[length++] = '-';
                }
            }
        }

        var slug = new string(buffer, 0, length).Trim('-');
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('-') : slug;
    }
}
