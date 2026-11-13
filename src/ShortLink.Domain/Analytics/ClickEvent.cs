namespace ShortLink.Domain.Analytics;

/// <summary>一次跳转的访问明细。写入路径为异步批处理，不参与业务事务。</summary>
public sealed class ClickEvent
{
    private ClickEvent()
    {
        Code = string.Empty;
        VisitorHash = string.Empty;
    }

    public long Id { get; private set; }

    /// <summary>一次跳转在采集端生成的稳定标识，用于 Kafka 重投与消费重试去重。</summary>
    public Guid EventId { get; private set; }

    public long WorkspaceId { get; private set; }

    public long LinkId { get; private set; }

    public long DomainId { get; private set; }

    /// <summary>命中的路由 / A/B 分支；为空表示走兜底地址。</summary>
    public long? RouteId { get; private set; }

    public string Code { get; private set; }

    public DateTime ClickedAtUtc { get; private set; }

    /// <summary>
    /// 统计口径下的日期，冗余存储以便按天聚合走索引而不是对时间列做函数计算。
    /// <para>
    /// 它未必等于 <see cref="ClickedAtUtc"/> 的 UTC 日期：日界由部署方配置的统计时区决定
    /// （见 <c>Analytics:TimeZone</c>），写入时由调用方算好传入，领域层不猜时区。
    /// </para>
    /// </summary>
    public DateOnly ClickDate { get; private set; }

    /// <summary>匿名访客指纹（IP + UA + 日期的哈希），用于去重统计，不可反推原始 IP。</summary>
    public string VisitorHash { get; private set; }

    public string? Ip { get; private set; }

    public string? UserAgent { get; private set; }

    public string? Referer { get; private set; }

    public string? Country { get; private set; }

    public string? Province { get; private set; }

    public string? Device { get; private set; }

    public string? Browser { get; private set; }

    public string? OperatingSystem { get; private set; }

    public bool IsBot { get; private set; }

    public static ClickEvent Create(
        long workspaceId,
        long linkId,
        long domainId,
        long? routeId,
        string code,
        DateTime clickedAtUtc,
        DateOnly clickDate,
        string visitorHash,
        string? ip,
        string? userAgent,
        string? referer,
        string? country,
        string? province,
        string? device,
        string? browser,
        string? operatingSystem,
        bool isBot,
        Guid? eventId = null)
        => new()
        {
            EventId = eventId ?? Guid.NewGuid(),
            WorkspaceId = workspaceId,
            LinkId = linkId,
            DomainId = domainId,
            RouteId = routeId,
            Code = code,
            ClickedAtUtc = clickedAtUtc,
            ClickDate = clickDate,
            VisitorHash = visitorHash,
            Ip = Truncate(ip, 64),
            UserAgent = Truncate(userAgent, 512),
            Referer = Truncate(referer, 1024),
            Country = Truncate(country, 64),
            Province = Truncate(province, 64),
            Device = Truncate(device, 32),
            Browser = Truncate(browser, 64),
            OperatingSystem = Truncate(operatingSystem, 64),
            IsBot = isBot,
        };

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
