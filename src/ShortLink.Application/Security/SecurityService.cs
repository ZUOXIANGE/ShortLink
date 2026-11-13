using FluentValidation;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Options;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;

namespace ShortLink.Application.Security;

/// <summary>安全事件查询与滥用举报处理。</summary>
public sealed class SecurityService(
    ISecurityRepository events,
    IShortLinkRepository links,
    IDomainRepository domains,
    ILinkCache cache,
    IClock clock,
    WorkspaceContext workspace,
    IOptions<SecurityOptions> securityOptions,
    IValidator<AbuseReportRequest> abuseValidator)
{
    public const int DefaultDays = 30;

    private SecurityOptions Security => securityOptions.Value;

    public async Task<PagedResult<SecurityEventDto>> QueryAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var days = Math.Clamp(query.Days ?? DefaultDays, 1, 365);
        var from = clock.UtcNow.AddDays(-days);

        var page = await events
            .QueryAsync(query, workspaceId, from, cancellationToken)
            .ConfigureAwait(false);

        var items = page.Items
            .Select(e =>
            {
                var code = SecurityEventTypes.ToCode((int)e.Type);
                return new SecurityEventDto(
                    e.Id,
                    code,
                    SecurityEventTypes.ToName(code),
                    e.Message,
                    e.LinkId,
                    e.Code,
                    e.Ip,
                    e.Country,
                    e.CreatedAtUtc);
            })
            .ToList();

        return new PagedResult<SecurityEventDto>(items, page.Total, page.Page, page.PageSize);
    }

    /// <summary>
    /// 匿名滥用举报。为防刷：同一 IP 一小时内对同一短链的举报超过阈值时静默成功返回
    /// （不告诉举报者是否被采纳，避免变成探测工具）。
    /// </summary>
    public async Task ReportAbuseAsync(
        AbuseReportRequest request,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await abuseValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var code = request.Code.Trim();
        if (!ShortCode.IsValid(code))
        {
            throw new BusinessException(BizCodes.LinkNotFound);
        }

        var domain = await domains.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.DomainNotFound);

        var link = await links.FindByCodeAsync(domain.Id, code, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);

        var now = clock.UtcNow;
        var accepted = await events.TryAddReportWithinQuotaAsync(
            SecurityEvent.Create(
                SecurityEventType.AbuseReported,
                request.Reason,
                link.WorkspaceId,
                link.Id,
                link.Code,
                ip,
                userAgent,
                null,
                now),
            Security.AbuseReportsPerHourPerIp,
            now.AddHours(-1),
            cancellationToken).ConfigureAwait(false);

        if (!accepted || !Security.AbuseAutoDisable)
        {
            return;
        }

        // FindByCodeAsync 返回未跟踪快照；停用必须使用数据库条件更新才能真实生效。
        await links.DisableIfEnabledAsync(link.Id, now, cancellationToken).ConfigureAwait(false);
        await cache.RemoveLinkAsync(link.DomainId, link.Code, cancellationToken).ConfigureAwait(false);
    }
}
