using ShortLink.Domain.Analytics;

namespace ShortLink.Application.Messaging;

/// <summary>
/// 一次跳转的点击消息。
/// <para>
/// 这是平台里唯一的高频消息（正常流量下每秒数十到数千条），也是唯一允许丢失的消息。
/// 两个关键约束写在这里，改动前先读完：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>不持久化</b>：它不进 durable outbox。每条点击都写一行 outbox 再写一行 inbox 再删除，
/// 会让写放大三到四倍，而点击数据的业务定位就是「最终一致、可丢」——
/// 用可靠性换吞吐是反向优化。
/// </item>
/// <item>
/// <b>必须批量消费</b>：单条落库会在每次跳转上多一次事务，批处理才能把 500 条压成一个事务
/// （见 <c>ClickRecordedHandler</c>）。
/// </item>
/// </list>
/// </summary>
public sealed record ClickRecorded(
    Guid ClickId,
    long WorkspaceId,
    long LinkId,
    long DomainId,
    long? RouteId,
    string Code,
    DateTime ClickedAtUtc,
    DateOnly ClickDate,
    string VisitorHash,
    string? Ip,
    string? UserAgent,
    string? Referer,
    string? Country,
    string? Province,
    string? Device,
    string? Browser,
    string? OperatingSystem,
    bool IsBot,
    Guid? QuotaReservationId = null)
{
    /// <summary>从领域实体投影。字段与 <see cref="ClickEvent.Create"/> 的参数严格一一对应。</summary>
    public static ClickRecorded From(ClickEvent click) => new(
        click.EventId,
        click.WorkspaceId,
        click.LinkId,
        click.DomainId,
        click.RouteId,
        click.Code,
        click.ClickedAtUtc,
        click.ClickDate,
        click.VisitorHash,
        click.Ip,
        click.UserAgent,
        click.Referer,
        click.Country,
        click.Province,
        click.Device,
        click.Browser,
        click.OperatingSystem,
        click.IsBot);

    /// <summary>还原为领域实体。校验与截断逻辑仍然只有 <see cref="ClickEvent.Create"/> 一份。</summary>
    public ClickEvent ToEntity() => ClickEvent.Create(
        WorkspaceId,
        LinkId,
        DomainId,
        RouteId,
        Code,
        ClickedAtUtc,
        ClickDate,
        VisitorHash,
        Ip,
        UserAgent,
        Referer,
        Country,
        Province,
        Device,
        Browser,
        OperatingSystem,
        IsBot,
        ClickId);
}

/// <summary>
/// 短链点击量达到上限并已自动停用。
/// <para>
/// 与 <see cref="ClickRecorded"/> 相反，这条消息<b>应该</b>可靠：它是审计证据，
/// 丢了就查不出「谁在什么时候把这条短链打爆了」。
/// </para>
/// </summary>
public sealed record LinkClickCapped(
    long WorkspaceId,
    long LinkId,
    long DomainId,
    string Code,
    long ClickCount,
    long MaxClicks,
    string? Ip,
    string? Country,
    DateTime OccurredAtUtc);

/// <summary>
/// 短链被停用。原因码见 <see cref="LinkDisableReasons"/>。
/// </summary>
public sealed record LinkDisabled(
    long WorkspaceId,
    long LinkId,
    string Code,
    string Reason,
    long? OperatorUserId,
    DateTime OccurredAtUtc);

/// <summary>停用原因码。用字符串而不是枚举，是为了让消息在跨进程序列化后仍然可读、可扩展。</summary>
public static class LinkDisableReasons
{
    /// <summary>用户手动停用。</summary>
    public const string Manual = "manual";

    /// <summary>点击量达到上限自动停用。</summary>
    public const string ClickCapReached = "click_cap_reached";
}
