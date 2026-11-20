using ShortLink.Domain.Analytics;

namespace ShortLink.Infrastructure.Persistence;

/// <summary>
/// 点击明细归档行。列与 <see cref="ClickEvent"/> 一致，主键沿用原明细的 <c>id</c>，
/// 重复搬运会撞主键而不是产生重复历史；<see cref="ArchivedAtUtc"/> 记录搬运时间。
/// </summary>
public sealed class ClickEventArchive
{
    private ClickEventArchive()
    {
        Code = string.Empty;
        VisitorHash = string.Empty;
    }

    public long Id { get; private set; }

    public Guid EventId { get; private set; }

    public long WorkspaceId { get; private set; }

    public long LinkId { get; private set; }

    public long DomainId { get; private set; }

    public long? RouteId { get; private set; }

    public string Code { get; private set; }

    public DateTime ClickedAtUtc { get; private set; }

    public DateOnly ClickDate { get; private set; }

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

    public DateTime ArchivedAtUtc { get; private set; }

    public static ClickEventArchive From(ClickEvent click, DateTime archivedAtUtc)
        => new()
        {
            Id = click.Id,
            EventId = click.EventId,
            WorkspaceId = click.WorkspaceId,
            LinkId = click.LinkId,
            DomainId = click.DomainId,
            RouteId = click.RouteId,
            Code = click.Code,
            ClickedAtUtc = click.ClickedAtUtc,
            ClickDate = click.ClickDate,
            VisitorHash = click.VisitorHash,
            Ip = click.Ip,
            UserAgent = click.UserAgent,
            Referer = click.Referer,
            Country = click.Country,
            Province = click.Province,
            Device = click.Device,
            Browser = click.Browser,
            OperatingSystem = click.OperatingSystem,
            IsBot = click.IsBot,
            ArchivedAtUtc = archivedAtUtc,
        };
}
