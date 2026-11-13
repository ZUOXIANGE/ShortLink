using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Options;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;

namespace ShortLink.Application.Links;

/// <summary>主目标、兜底地址与条件路由共用的目标地址安全检查。</summary>
public sealed class TargetUrlSecurityScanner(
    IDomainRepository domains,
    ISecurityRepository securityEvents,
    IUnitOfWork unitOfWork,
    WorkspaceContext workspace,
    IClock clock,
    IOptions<SecurityOptions> securityOptions)
{
    private SecurityOptions Security => securityOptions.Value;

    public async Task EnsureAllowedAsync(string url, long? linkId, CancellationToken cancellationToken)
    {
        if (Security.IsOff)
        {
            return;
        }

        var hosts = (await domains.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(d => ShortDomain.StripPort(d.Host))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = UrlScanner.Scan(url, hosts);
        if (!result.IsBlocked || !Security.IsEnforcing)
        {
            return;
        }

        if (Security.RecordEvents)
        {
            securityEvents.Add(SecurityEvent.Create(
                SecurityEventType.UrlRejected,
                string.Join("；", result.Violations),
                await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false),
                linkId,
                null,
                null,
                null,
                null,
                clock.UtcNow));
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new BusinessException(BizCodes.LinkTargetRejected, string.Join("；", result.Violations));
    }
}
