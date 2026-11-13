using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;

namespace ShortLink.Application.Redirection;

/// <summary>解析成功后的跳转指令。</summary>
public sealed record RedirectOutcome(
    long LinkId,
    long WorkspaceId,
    long DomainId,
    string Code,
    string TargetUrl,
    int StatusCode,
    long? RouteId,
    string? RouteName,
    bool CountClick,
    bool Interstitial,
    ClickQuotaReservation? QuotaReservation = null);

/// <summary>跳转请求上下文：由宿主从请求头与 UA 提取，避免应用层依赖 ASP.NET Core 类型。</summary>
public sealed record RedirectRequestContext(
    string? Ip,
    string? UserAgent,
    string? Referer,
    string? Language,
    string? Country,
    string? Province,
    string? Device,
    string? Browser,
    string? Os,
    bool IsBot,
    IReadOnlyDictionary<string, string> Query,
    string VisitorKey);

/// <summary>
/// 跳转解析：Host + Code → 目标地址。
/// <para>
/// 顺序为「域名 → 短链（缓存） → 生命周期 → 安全策略 → 访问密码 → 条件路由 → 防红判定」，
/// 把最便宜的判断放前面，命中拦截时不再继续后续步骤；
/// 安全事件写入失败不影响跳转本身（catch 后仅记日志）。
/// </para>
/// </summary>
public sealed class RedirectService(
    IShortLinkRepository links,
    IDomainRepository domains,
    IRouteRepository routes,
    ILinkCache cache,
    IPasswordHasher passwordHasher,
    ISecurityRepository securityEvents,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<RedirectOptions> redirectOptions,
    IOptions<SecurityOptions> securityOptions,
    IClickQuota clickQuota,
    ILogger<RedirectService> logger)
{
    private RedirectOptions Redirect => redirectOptions.Value;

    private SecurityOptions Security => securityOptions.Value;

    public async Task<RedirectOutcome> ResolveAsync(
        string host,
        string code,
        string? password,
        RedirectRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!ShortCode.IsValid(code))
        {
            // 保留字与非法短码统一按不存在处理，避免暴露系统路由。
            throw new BusinessException(BizCodes.LinkNotFound);
        }

        var domainId = await ResolveDomainIdAsync(host, cancellationToken).ConfigureAwait(false);
        var link = await ResolveLinkAsync(domainId, code, cancellationToken).ConfigureAwait(false);
        if (link.IsMissing)
        {
            throw new BusinessException(BizCodes.LinkNotFound);
        }

        var now = clock.UtcNow;

        // 封顶判定放在状态判定之前：封顶会把短链自动置为停用，
        // 先判封顶才能给出「已达上限」而不是笼统的「已停用」。
        if (link.MaxClicks is { } maxClicks && link.ClickCount >= maxClicks)
        {
            throw new BusinessException(BizCodes.LinkClickCapped);
        }

        if (link.Status != (int)LinkStatus.Enabled)
        {
            throw new BusinessException(BizCodes.LinkDisabled);
        }

        if (link.StartsAtUtc is { } startsAt && startsAt > now)
        {
            throw new BusinessException(BizCodes.LinkNotStarted);
        }

        if (link.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
        {
            throw new BusinessException(BizCodes.LinkExpired);
        }

        var decision = AccessPolicy.Evaluate(link, context.Ip, context.IsBot);
        if (decision is not (AccessDecision.Allow or AccessDecision.IgnoreCount))
        {
            await RecordSecurityEventAsync(link, decision, context, cancellationToken).ConfigureAwait(false);
            throw new BusinessException(
                decision == AccessDecision.BlockBot ? BizCodes.LinkBotBlocked : BizCodes.LinkIpNotAllowed);
        }

        if (!string.IsNullOrEmpty(link.PasswordHash))
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new BusinessException(BizCodes.LinkPasswordRequired);
            }

            if (!passwordHasher.Verify(link.PasswordHash, password))
            {
                throw new BusinessException(BizCodes.LinkPasswordIncorrect);
            }
        }

        var selection = RouteSelector.Select(
            link.Routes,
            new RouteContext(
                link.Id,
                context.Country,
                context.Province,
                context.Device,
                context.Browser,
                context.Os,
                context.Language,
                context.Referer,
                context.Query,
                context.VisitorKey),
            link.EffectiveFallbackUrl);

        var interstitial = link.AntiBlockEnabled
                           && !string.Equals(Redirect.WeChatInterstitial, "off", StringComparison.OrdinalIgnoreCase)
                           && AccessPolicy.IsWeChatOrQQ(context.UserAgent);

        ClickQuotaReservation? reservation = null;
        if (decision != AccessDecision.IgnoreCount && link.MaxClicks is not null)
        {
            // 只在这里做原子预留；上面的缓存计数检查仅是提前拒绝，不能作为并发硬限制。
            reservation = await clickQuota.TryReserveAsync(link, cancellationToken).ConfigureAwait(false);
            if (reservation is null)
            {
                throw new BusinessException(BizCodes.LinkClickCapped);
            }
        }

        return new RedirectOutcome(
            link.Id,
            link.WorkspaceId,
            link.DomainId,
            link.Code,
            selection.TargetUrl,
            link.RedirectType,
            selection.RouteId,
            selection.RouteName,
            decision != AccessDecision.IgnoreCount,
            interstitial,
            reservation);
    }

    /// <summary>消息未成功交给总线时释放预留；补偿失败只记日志，不影响正常重定向。</summary>
    public async Task ReleaseQuotaAsync(RedirectOutcome outcome)
    {
        if (outcome.QuotaReservation is null)
        {
            return;
        }

        try
        {
            await clickQuota.ReleaseAsync(outcome.QuotaReservation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "点击配额补偿失败：link={LinkId}", outcome.LinkId);
        }
    }

    private async Task RecordSecurityEventAsync(
        CachedLink link,
        AccessDecision decision,
        RedirectRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!Security.RecordEvents)
        {
            return;
        }

        var (type, message) = decision switch
        {
            AccessDecision.BlockIp => (SecurityEventType.IpBlocked, $"IP {context.Ip} 命中黑名单"),
            AccessDecision.IpNotAllowed => (SecurityEventType.IpNotAllowed, $"IP {context.Ip} 不在白名单内"),
            _ => (SecurityEventType.BotBlocked, "命中机器人拦截策略"),
        };

        try
        {
            securityEvents.Add(SecurityEvent.Create(
                type,
                message,
                link.WorkspaceId,
                link.Id,
                link.Code,
                context.Ip,
                context.UserAgent,
                context.Country,
                clock.UtcNow));

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 安全事件是旁路数据：写失败只记日志，绝不把跳转请求打挂。
            logger.LogWarning(exception, "安全事件写入失败：link={LinkId} type={Type}", link.Id, type);
        }
    }

    private async Task<long> ResolveDomainIdAsync(string host, CancellationToken cancellationToken)
    {
        var normalizedHost = ShortDomain.NormalizeHost(host);
        if (normalizedHost.Length == 0)
        {
            return (await ResolveDefaultDomainAsync(cancellationToken).ConfigureAwait(false)).Id;
        }

        var cached = await cache.GetOrSetDomainAsync(
            normalizedHost,
            token => LoadDomainAsync(normalizedHost, token),
            cancellationToken).ConfigureAwait(false);

        return cached.Id;
    }

    private async Task<CachedDomain> LoadDomainAsync(string host, CancellationToken cancellationToken)
    {
        var domain = await domains.FindByHostAsync(host, cancellationToken).ConfigureAwait(false);

        if (domain is null)
        {
            var stripped = ShortDomain.StripPort(host);
            if (!string.Equals(stripped, host, StringComparison.Ordinal))
            {
                domain = await domains.FindByHostAsync(stripped, cancellationToken).ConfigureAwait(false);
            }
        }

        // 未配置的 Host 一律落到默认域名：反代把 Host 改写成内部地址时依然可跳转。
        domain ??= await ResolveDefaultDomainAsync(cancellationToken).ConfigureAwait(false);

        return new CachedDomain(domain.Id, domain.Host, domain.Scheme);
    }

    private async Task<ShortDomain> ResolveDefaultDomainAsync(CancellationToken cancellationToken)
        => await domains.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
           ?? throw new BusinessException(BizCodes.LinkNotFound, "尚未配置短链域名");

    private Task<CachedLink> ResolveLinkAsync(long domainId, string code, CancellationToken cancellationToken)
        => cache.GetOrSetLinkAsync(
            domainId,
            code,
            token => LoadLinkAsync(domainId, code, token),
            cancellationToken);

    private async Task<CachedLink> LoadLinkAsync(long domainId, string code, CancellationToken cancellationToken)
    {
        var link = await links.FindByCodeAsync(domainId, code, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return CachedLink.Missing;
        }

        var routeList = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);

        return new CachedLink
        {
            Id = link.Id,
            WorkspaceId = link.WorkspaceId,
            DomainId = link.DomainId,
            Code = link.Code,
            TargetUrl = link.TargetUrl,
            FallbackUrl = link.FallbackUrl,
            RedirectType = (int)link.RedirectType,
            Status = (int)link.Status,
            StartsAtUtc = link.StartsAtUtc,
            ExpiresAtUtc = link.ExpiresAtUtc,
            ClickCount = link.ClickCount,
            MaxClicks = link.MaxClicks,
            PasswordHash = link.PasswordHash,
            BotPolicy = (int)link.BotPolicy,
            AllowList = IpAccessList.Parse(link.IpAllowList),
            BlockList = IpAccessList.Parse(link.IpBlockList),
            AntiBlockEnabled = link.AntiBlockEnabled,
            Routes = routeList.Select(r => r.ToSnapshot()).ToList(),
        };
    }
}
