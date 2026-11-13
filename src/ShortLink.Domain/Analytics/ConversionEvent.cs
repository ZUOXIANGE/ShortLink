namespace ShortLink.Domain.Analytics;

/// <summary>
/// 转化事件。由广告主服务端在落地页完成目标动作（下单、注册等）后回调写入，
/// 以 <c>(link_id, event_id)</c> 唯一约束保证幂等：同一笔转化重放多次只计一次。
/// </summary>
public sealed class ConversionEvent
{
    private ConversionEvent()
    {
        EventId = string.Empty;
        Metric = string.Empty;
    }

    public long Id { get; private set; }

    public long WorkspaceId { get; private set; }

    public long LinkId { get; private set; }

    /// <summary>命中的路由分支（A/B 变体）；为空表示走兜底地址。</summary>
    public long? RouteId { get; private set; }

    /// <summary>调用方生成的幂等键。</summary>
    public string EventId { get; private set; }

    /// <summary>转化指标名，如 <c>order_paid</c>、<c>signup</c>。</summary>
    public string Metric { get; private set; }

    public decimal Value { get; private set; }

    public string? Ip { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static ConversionEvent Create(
        long workspaceId,
        long linkId,
        long? routeId,
        string eventId,
        string metric,
        decimal value,
        string? ip,
        DateTime nowUtc)
        => new()
        {
            WorkspaceId = workspaceId,
            LinkId = linkId,
            RouteId = routeId,
            EventId = Truncate(eventId, 64),
            Metric = Truncate(metric, 64),
            Value = value,
            Ip = Truncate(ip, 64),
            CreatedAtUtc = nowUtc,
        };

    private static string Truncate(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
