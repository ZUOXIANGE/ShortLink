using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShortLink.Infrastructure.Diagnostics;

/// <summary>
/// 自定义指标。全 200 模式下 HTTP 状态码无法反映业务成败，跳转与点击链路按结果打点。
/// </summary>
public static class ShortLinkDiagnostics
{
    public const string MeterName = "ShortLink";

    public const string ActivitySourceName = "ShortLink.Redirect";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>已入队的点击事件。</summary>
    public static readonly Counter<long> ClicksEnqueued = Meter.CreateCounter<long>(
        "shortlink.clicks.enqueued",
        unit: "{click}",
        description: "进入内存队列的点击事件数");

    /// <summary>队列满被丢弃的点击事件。</summary>
    public static readonly Counter<long> ClicksDropped = Meter.CreateCounter<long>(
        "shortlink.clicks.dropped",
        unit: "{click}",
        description: "队列积压被丢弃的点击事件数");

    /// <summary>批量落库失败次数。</summary>
    public static readonly Counter<long> ClickPersistFailures = Meter.CreateCounter<long>(
        "shortlink.clicks.persist_failures",
        unit: "{failure}",
        description: "点击事件批量落库失败次数");

    /// <summary>跳转结果计数，标签 <c>result</c>：ok / not_found / disabled / expired / password_required / password_invalid。</summary>
    public static readonly Counter<long> Redirects = Meter.CreateCounter<long>(
        "shortlink.redirects",
        unit: "{request}",
        description: "短链跳转结果分布");

    /// <summary>命中点击封顶并自动停用的短链数。</summary>
    public static readonly Counter<long> LinksCapped = Meter.CreateCounter<long>(
        "shortlink.links.capped",
        unit: "{link}",
        description: "点击量达到上限并自动停用的短链数");

    /// <summary>安全拦截计数，标签 <c>reason</c>：ip_blocked / ip_not_allowed / bot_blocked / url_rejected。</summary>
    public static readonly Counter<long> SecurityBlocks = Meter.CreateCounter<long>(
        "shortlink.security.blocks",
        unit: "{request}",
        description: "按原因统计的访问拦截数");

    /// <summary>业务失败计数，标签 <c>code</c>（低基数业务码，适合作为指标维度）。</summary>
    public static readonly Counter<long> BusinessFailures = Meter.CreateCounter<long>(
        "shortlink.business.failures",
        unit: "{request}",
        description: "按业务码统计的失败请求数");

    /// <summary>分布式限流回落到本地窗口的次数（Redis 异常时发生，意味着各实例配额不再共享）。</summary>
    public static readonly Counter<long> RateLimitFallbacks = Meter.CreateCounter<long>(
        "shortlink.ratelimit.fallbacks",
        unit: "{fallback}",
        description: "限流器因分布式后端不可用而回落到本地窗口的次数");

    /// <summary>点击明细清理删除的行数。</summary>
    public static readonly Counter<long> ClicksPurged = Meter.CreateCounter<long>(
        "shortlink.clicks.purged",
        unit: "{click}",
        description: "按保留策略清理的点击明细行数");
}
