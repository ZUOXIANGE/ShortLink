namespace ShortLink.Domain.Links;

/// <summary>
/// 短链路由规则：条件分流（按国家/设备/语言等）与 A/B 加权分流共用一张表。
/// <para>
/// 优先级从高到低求值；同优先级内多条命中按 <see cref="Weight"/> 加权选择。
/// 条件为空的规则等价于「全量命中」，用作 A/B 分支与默认分支。
/// </para>
/// </summary>
public sealed class LinkRoute
{
    public const int MinPriority = -100;

    public const int MaxPriority = 100;

    public const int MaxWeight = 1000;

    private LinkRoute()
    {
        Name = string.Empty;
        TargetUrl = string.Empty;
        ConditionsJson = "{}";
    }

    public long Id { get; private set; }

    public long LinkId { get; private set; }

    /// <summary>分支名称，同时作为 A/B 变体的展示名。</summary>
    public string Name { get; private set; }

    public int Priority { get; private set; }

    /// <summary>同优先级内的分流权重；0 表示不参与加权（仅在唯一命中时生效）。</summary>
    public int Weight { get; private set; }

    public string TargetUrl { get; private set; }

    /// <summary>条件组的 JSON 表示，见 <see cref="RouteConditions"/>。</summary>
    public string ConditionsJson { get; private set; }

    public bool IsEnabled { get; private set; }

    public long ClickCount { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static LinkRoute Create(
        long linkId,
        string name,
        int priority,
        int weight,
        string targetUrl,
        RouteConditions? conditions,
        bool isEnabled,
        DateTime nowUtc)
    {
        var route = new LinkRoute
        {
            LinkId = linkId,
            ClickCount = 0,
            CreatedAtUtc = nowUtc,
        };

        route.Update(name, priority, weight, targetUrl, conditions, isEnabled, nowUtc);
        return route;
    }

    public void Update(
        string name,
        int priority,
        int weight,
        string targetUrl,
        RouteConditions? conditions,
        bool isEnabled,
        DateTime nowUtc)
    {
        Name = Trim(name, 64);
        Priority = Math.Clamp(priority, MinPriority, MaxPriority);
        Weight = Math.Clamp(weight, 0, MaxWeight);
        TargetUrl = targetUrl.Trim();
        ConditionsJson = (conditions ?? RouteConditions.Empty).ToJson();
        IsEnabled = isEnabled;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>复制一份到另一个短链（用于短链复制场景）。</summary>
    public LinkRoute CloneFor(long linkId, DateTime nowUtc)
        => Create(linkId, Name, Priority, Weight, TargetUrl, GetConditions(), IsEnabled, nowUtc);

    public RouteConditions GetConditions() => RouteConditions.TryParse(ConditionsJson) ?? RouteConditions.Empty;

    /// <summary>
    /// 生成缓存快照。条件 JSON 非法时把该规则标记为停用：
    /// 脏数据只影响自己，不会让整条短链无法跳转。
    /// </summary>
    public RouteSnapshot ToSnapshot()
    {
        var parsed = RouteConditions.TryParse(ConditionsJson);
        return new RouteSnapshot(
            Id,
            Name,
            Priority,
            Weight,
            TargetUrl,
            parsed ?? RouteConditions.Empty,
            IsEnabled && parsed is not null);
    }

    private static string Trim(string value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "默认分支";
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
