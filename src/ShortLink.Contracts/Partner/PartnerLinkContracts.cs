namespace ShortLink.Contracts.Partner;

using ShortLink.Contracts.Analytics;

/// <summary>
/// 第三方视角的短链投影。刻意不包含工作区、密钥状态、IP 名单等后台字段：
/// 外部契约只承诺这里出现过的字段，后续后台字段演进不会冲击集成方。
/// </summary>
public sealed record PartnerLinkDto(
    long Id,
    string Code,
    string ShortUrl,
    string TargetUrl,
    string? Title,
    string Status,
    int RedirectType,
    DateTime? StartsAtUtc,
    DateTime? ExpiresAtUtc,
    long ClickCount,
    long? MaxClicks,
    bool HasPassword,
    bool AntiBlockEnabled,
    DateTime CreatedAtUtc,
    DateTime? LastAccessedAtUtc);

/// <summary>
/// 工作区总览投影。刻意去掉后台的域名总数：域名是全局资源，
/// 该数字不属于当前工作区，不应通过第三方接口暴露。
/// </summary>
public sealed record PartnerOverviewDto(
    long TotalLinks,
    long EnabledLinks,
    long ExpiredLinks,
    long TotalClicks,
    long TodayClicks,
    long YesterdayClicks,
    long UniqueVisitorsToday,
    long TotalConversions,
    IReadOnlyList<DailyClickDto> Trend,
    IReadOnlyList<TopLinkDto> TopLinks);
