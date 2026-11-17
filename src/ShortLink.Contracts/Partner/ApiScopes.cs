namespace ShortLink.Contracts.Partner;

/// <summary>
/// 第三方 API 的权限范围。scope 与角色是 AND 关系：只读账号即使拿到
/// <see cref="LinksWrite"/> 也不能写入。
/// </summary>
public static class ApiScopes
{
    public const string LinksRead = "links:read";

    public const string LinksWrite = "links:write";

    /// <summary>删除是不可逆操作，单独一个 scope，便于按需下发。</summary>
    public const string LinksDelete = "links:delete";

    public const string AnalyticsRead = "analytics:read";

    public static readonly string[] All = [LinksRead, LinksWrite, LinksDelete, AnalyticsRead];

    /// <summary>空格分隔的规范化 scope 串（去重、排序、去空项）。</summary>
    public static string Normalize(IEnumerable<string>? scopes)
        => string.Join(
            ' ',
            (scopes ?? [])
                .Select(scope => scope.Trim())
                .Where(scope => scope.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(scope => scope, StringComparer.Ordinal));

    public static IReadOnlyList<string> Parse(string? scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsKnown(string? scope)
        => !string.IsNullOrWhiteSpace(scope)
           && All.Contains(scope.Trim(), StringComparer.Ordinal);
}
