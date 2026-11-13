namespace ShortLink.Contracts.Links;

/// <summary>单条匹配条件。<c>value</c> 在 <c>in</c> / <c>not_in</c> 下为逗号分隔多值。</summary>
public sealed record RouteConditionDto
{
    /// <summary><c>country</c> / <c>province</c> / <c>device</c> / <c>browser</c> / <c>os</c> / <c>language</c> / <c>referer</c> / <c>query</c>。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary><c>eq</c> / <c>neq</c> / <c>contains</c> / <c>not_contains</c> / <c>prefix</c> / <c>in</c> / <c>not_in</c> / <c>exists</c> / <c>not_exists</c>。</summary>
    public string Op { get; set; } = "eq";

    public string Value { get; set; } = string.Empty;
}

/// <summary>路由分支（同时是 A/B 变体）。</summary>
public sealed record LinkRouteDto(
    long Id,
    long LinkId,
    string Name,
    int Priority,
    int Weight,
    string TargetUrl,
    string Match,
    IReadOnlyList<RouteConditionDto> Conditions,
    bool IsEnabled,
    long ClickCount);

/// <summary>
/// 保存路由规则。整表覆盖式写入：一次提交即该短链的全部分支，
/// 避免增量改动的删除/新增交错问题。
/// </summary>
public sealed class SaveLinkRoutesRequest
{
    public const int MaxRoutes = 20;

    public List<LinkRouteInput> Routes { get; set; } = [];
}

/// <summary>路由分支输入。</summary>
public sealed class LinkRouteInput
{
    public string Name { get; set; } = string.Empty;

    public int Priority { get; set; }

    /// <summary>同优先级内的分流权重；0 表示不参与加权。</summary>
    public int Weight { get; set; }

    public string TargetUrl { get; set; } = string.Empty;

    /// <summary><c>all</c>（默认）/ <c>any</c>。</summary>
    public string Match { get; set; } = "all";

    public List<RouteConditionDto> Conditions { get; set; } = [];

    public bool IsEnabled { get; set; } = true;
}

/// <summary>单个分支的模拟命中占比。</summary>
public sealed record RouteTestBucket(string Name, long Hits, double Percent);

/// <summary>路由测试结果。</summary>
public sealed record RouteTestResult(
    string? RouteName,
    string? TargetUrl,
    bool IsExperimental,
    string FallbackUrl,
    IReadOnlyList<RouteTestBucket> Distribution);
