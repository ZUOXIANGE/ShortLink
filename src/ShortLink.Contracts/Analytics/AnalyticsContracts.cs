namespace ShortLink.Contracts.Analytics;

/// <summary>按天的点击趋势。</summary>
public sealed record DailyClickDto(DateOnly Date, long Clicks, long UniqueVisitors);

/// <summary>维度聚合项（来源、设备、浏览器等）。</summary>
public sealed record DimensionItemDto(string Name, long Clicks);

/// <summary>A/B 分支（或条件路由分支）的效果。</summary>
public sealed record VariantStatDto(
    long? RouteId,
    string Name,
    long Clicks,
    long Conversions,
    decimal ConversionValue,
    double Share,
    double ConversionRate);

/// <summary>单条短链的访问统计。</summary>
public sealed record LinkStatsDto(
    long LinkId,
    string Code,
    string DomainHost,
    string ShortUrl,
    string TargetUrl,
    long TotalClicks,
    long UniqueVisitors,
    long TodayClicks,
    int Days,
    long Conversions,
    decimal ConversionValue,
    IReadOnlyList<DailyClickDto> Trend,
    IReadOnlyList<DimensionItemDto> Referers,
    IReadOnlyList<DimensionItemDto> Devices,
    IReadOnlyList<DimensionItemDto> Browsers,
    IReadOnlyList<DimensionItemDto> OperatingSystems,
    IReadOnlyList<DimensionItemDto> Countries,
    IReadOnlyList<VariantStatDto> Variants);

/// <summary>热门短链。</summary>
public sealed record TopLinkDto(
    long Id,
    string Code,
    string DomainHost,
    string? Title,
    string TargetUrl,
    long Clicks);

/// <summary>控制台总览。</summary>
public sealed record DashboardOverviewDto(
    long TotalLinks,
    long EnabledLinks,
    long ExpiredLinks,
    long TotalDomains,
    long TotalClicks,
    long TodayClicks,
    long YesterdayClicks,
    long UniqueVisitorsToday,
    long TotalConversions,
    IReadOnlyList<DailyClickDto> Trend,
    IReadOnlyList<TopLinkDto> TopLinks);

/// <summary>单次点击明细。</summary>
public sealed record ClickLogDto(
    long Id,
    string Code,
    DateTime ClickedAtUtc,
    string? Ip,
    string? Referer,
    string? Device,
    string? Browser,
    string? OperatingSystem,
    string? Country,
    bool IsBot,
    long? RouteId = null);

/// <summary>
/// 转化回调请求。调用方在服务端用短链的转化密钥对 <c>{timestamp}.{原始请求体}</c> 做 HMAC-SHA256，
/// 通过 <c>X-Signature: sha256=...</c> 与 <c>X-Timestamp</c> 头提交，时间戳超过 5 分钟视为重放。
/// </summary>
public sealed class ConversionCallbackRequest
{
    /// <summary>短链 ID 与短码二选一。</summary>
    public long? LinkId { get; set; }

    public string? Code { get; set; }

    /// <summary>幂等键：同一短链下重复提交只计一次。</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>命中的路由分支（A/B 变体）；为空表示兜底分支。</summary>
    public long? RouteId { get; set; }

    /// <summary>转化指标名，默认 <c>conversion</c>。</summary>
    public string Metric { get; set; } = "conversion";

    /// <summary>转化价值，用于计算 ROI。</summary>
    public decimal Value { get; set; }

    /// <summary>Unix 秒级时间戳；不传则以服务端接收时间为准（仅限带签名请求）。</summary>
    public long? Timestamp { get; set; }
}

/// <summary>转化回调结果。<see cref="Duplicate"/> 为 true 表示命中幂等，未重复计数。</summary>
public sealed record ConversionResultDto(long LinkId, string EventId, bool Duplicate, long TotalConversions);
