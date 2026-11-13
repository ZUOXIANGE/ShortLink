using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShortLink.Domain.Links;

/// <summary>条件路由可选的匹配字段。取值由跳转请求上下文提供，全部为字符串比较。</summary>
public static class RouteFields
{
    public const string Country = "country";

    public const string Province = "province";

    public const string Device = "device";

    public const string Browser = "browser";

    public const string Os = "os";

    public const string Language = "language";

    public const string Referer = "referer";

    public const string Query = "query";

    /// <summary>全部字段，供前端下拉与后端校验复用。</summary>
    public static readonly string[] All =
        [Country, Province, Device, Browser, Os, Language, Referer, Query];

    public static bool IsValid(string? field)
        => !string.IsNullOrWhiteSpace(field)
           && All.Contains(field.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}

/// <summary>条件运算符。</summary>
public static class RouteOperators
{
    public const string Equal = "eq";

    public const string NotEqual = "neq";

    public const string Contains = "contains";

    public const string NotContains = "not_contains";

    public const string Prefix = "prefix";

    public const string In = "in";

    public const string NotIn = "not_in";

    public const string Exists = "exists";

    public const string NotExists = "not_exists";

    public static readonly string[] All =
        [Equal, NotEqual, Contains, NotContains, Prefix, In, NotIn, Exists, NotExists];

    public static bool IsValid(string? op)
        => !string.IsNullOrWhiteSpace(op)
           && All.Contains(op.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}

/// <summary>条件组内多条条件的组合方式。</summary>
public enum RouteMatchMode
{
    /// <summary>全部满足（AND）。</summary>
    All = 0,

    /// <summary>任意满足（OR）。</summary>
    Any = 1,
}

/// <summary>单条匹配条件。<see cref="Value"/> 在 <c>in</c> / <c>not_in</c> 下为逗号分隔的多值。</summary>
public sealed record RouteCondition(string Field, string Op, string Value);

/// <summary>
/// 条件组。以 JSON 存入数据库，避免为条件建立宽表；
/// 解析失败时按「不命中」处理，保证坏数据不会影响跳转。
/// </summary>
public sealed class RouteConditions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public RouteMatchMode Match { get; init; } = RouteMatchMode.All;

    public IReadOnlyList<RouteCondition> Items { get; init; } = [];

    /// <summary>无条件：任意请求都命中，用于 A/B 分支与默认分支。</summary>
    public static RouteConditions Empty { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public static RouteConditions Create(RouteMatchMode match, IEnumerable<RouteCondition> items)
        => new() { Match = match, Items = items.ToList() };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析 JSON；空串返回无条件，非法 JSON 返回 null（调用方按不命中处理）。</summary>
    public static RouteConditions? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RouteConditions>(json, JsonOptions);
            return parsed is null ? null : new RouteConditions
            {
                Match = parsed.Match,
                Items = parsed.Items?.Where(i => i is not null).ToList() ?? [],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>跳转时的匹配上下文。由网关侧从请求头、UA、查询串提取。</summary>
public sealed record RouteContext(
    long LinkId,
    string? Country,
    string? Province,
    string? Device,
    string? Browser,
    string? Os,
    string? Language,
    string? Referer,
    IReadOnlyDictionary<string, string> Query,
    string VisitorKey);

/// <summary>路由选择结果。<see cref="RouteId"/> 为空表示走兜底地址。</summary>
public sealed record RouteSelection(long? RouteId, string? RouteName, string TargetUrl, bool IsExperimental)
{
    public static RouteSelection Fallback(string targetUrl) => new(null, null, targetUrl, false);
}

/// <summary>路由规则快照，用于缓存与纯函数式选择（不依赖 EF 跟踪）。</summary>
public sealed record RouteSnapshot(
    long Id,
    string Name,
    int Priority,
    int Weight,
    string TargetUrl,
    RouteConditions Conditions,
    bool IsEnabled)
{
    /// <summary>同一优先级内参与加权分流的分支（权重 &gt; 0 且不是唯一的默认分支）。</summary>
    public bool IsWeighted => Weight > 0;
}

/// <summary>
/// 条件路由选择器：纯函数，便于单元测试与缓存复用。
/// <para>
/// 规则按优先级从高到低求值；同一优先级内多条命中时按权重分流，
/// 分流种子取 <c>访客指纹 + 短链 + 优先级</c> 的哈希，保证同一访客在同一短链上始终落到同一分支
/// （会话一致性），且不需要服务端保存会话状态。
/// </para>
/// </summary>
public static class RouteSelector
{
    public static RouteSelection Select(
        IReadOnlyList<RouteSnapshot> routes,
        RouteContext context,
        string fallbackUrl)
    {
        if (routes.Count == 0)
        {
            return RouteSelection.Fallback(fallbackUrl);
        }

        var ordered = routes
            .Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.TargetUrl))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToList();

        foreach (var group in ordered.GroupBy(r => r.Priority))
        {
            var matched = group.Where(r => Matches(r, context)).ToList();
            if (matched.Count == 0)
            {
                continue;
            }

            var picked = Pick(matched, context);
            return new RouteSelection(picked.Id, picked.Name, picked.TargetUrl, matched.Count > 1);
        }

        return RouteSelection.Fallback(fallbackUrl);
    }

    /// <summary>按权重取一条；权重全为 0 时取排序后的第一条，保证结果稳定可复现。</summary>
    private static RouteSnapshot Pick(List<RouteSnapshot> matched, RouteContext context)
    {
        if (matched.Count == 1)
        {
            return matched[0];
        }

        var totalWeight = matched.Sum(r => Math.Max(r.Weight, 0));
        if (totalWeight <= 0)
        {
            return matched[0];
        }

        var bucket = (int)(DeterministicHash(context.VisitorKey, context.LinkId, matched[0].Priority) % (uint)totalWeight);

        foreach (var route in matched)
        {
            var weight = Math.Max(route.Weight, 0);
            if (bucket < weight)
            {
                return route;
            }

            bucket -= weight;
        }

        return matched[^1];
    }

    private static uint DeterministicHash(string visitorKey, long linkId, int priority)
    {
        var raw = $"{visitorKey}|{linkId}|{priority.ToString(CultureInfo.InvariantCulture)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return BitConverter.ToUInt32(hash, 0);
    }

    private static bool Matches(RouteSnapshot route, RouteContext context)
    {
        if (route.Conditions.IsEmpty)
        {
            // 无条件的规则等价于「全量命中」，仅用于分流与兜底分支。
            return true;
        }

        var results = route.Conditions.Items.Select(item => MatchOne(item, context));
        return route.Conditions.Match == RouteMatchMode.Any
            ? results.Any(r => r)
            : results.All(r => r);
    }

    private static bool MatchOne(RouteCondition condition, RouteContext context)
    {
        var op = condition.Op?.Trim().ToLowerInvariant() ?? RouteOperators.Equal;
        var field = condition.Field?.Trim().ToLowerInvariant() ?? string.Empty;
        var expected = condition.Value ?? string.Empty;

        if (field == RouteFields.Query)
        {
            return MatchQuery(op, expected, context.Query);
        }

        var actual = field switch
        {
            RouteFields.Country => context.Country,
            RouteFields.Province => context.Province,
            RouteFields.Device => context.Device,
            RouteFields.Browser => context.Browser,
            RouteFields.Os => context.Os,
            RouteFields.Language => context.Language,
            RouteFields.Referer => context.Referer,
            _ => null,
        };

        return MatchValue(op, actual, expected);
    }

    /// <summary>
    /// 查询参数条件：<c>exists</c> / <c>not_exists</c> 只看键是否存在；
    /// 其余运算符的 <c>value</c> 写成 <c>key=期望值</c>，或只写 <c>key</c> 表示只看键存在。
    /// </summary>
    private static bool MatchQuery(string op, string expected, IReadOnlyDictionary<string, string> query)
    {
        var separator = expected.IndexOf('=', StringComparison.Ordinal);
        var key = (separator >= 0 ? expected[..separator] : expected).Trim();
        var expectedValue = separator >= 0 ? expected[(separator + 1)..] : null;

        var exists = query.TryGetValue(key, out var actualValue);
        if (op == RouteOperators.Exists)
        {
            return exists;
        }

        if (op == RouteOperators.NotExists)
        {
            return !exists;
        }

        if (expectedValue is null)
        {
            // 只给了键名：按「键存在即命中」处理，兼容 eq / in 等写法。
            return op == RouteOperators.NotEqual ? !exists : exists;
        }

        return MatchValue(op, exists ? actualValue : null, expectedValue);
    }

    private static bool MatchValue(string op, string? actual, string expected)
    {
        var hasActual = !string.IsNullOrEmpty(actual);

        switch (op)
        {
            case RouteOperators.Exists:
                return hasActual;

            case RouteOperators.NotExists:
                return !hasActual;

            case RouteOperators.In:
                return hasActual && SplitValues(expected).Any(v => Same(v, actual));

            case RouteOperators.NotIn:
                return !hasActual || !SplitValues(expected).Any(v => Same(v, actual));
        }

        if (!hasActual)
        {
            // 缺失字段只对「不相等 / 不包含」判为真，其余一律不命中，避免把未配置当成匹配。
            return op is RouteOperators.NotEqual or RouteOperators.NotContains;
        }

        return op switch
        {
            RouteOperators.Equal => Same(expected, actual),
            RouteOperators.NotEqual => !Same(expected, actual),
            RouteOperators.Contains => actual!.Contains(expected, StringComparison.OrdinalIgnoreCase),
            RouteOperators.NotContains => !actual!.Contains(expected, StringComparison.OrdinalIgnoreCase),
            RouteOperators.Prefix => actual!.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string[] SplitValues(string value)
        => value.Split([',', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Same(string a, string? b)
        => string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
