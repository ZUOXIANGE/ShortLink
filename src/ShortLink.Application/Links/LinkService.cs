using FluentValidation;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Links;

/// <summary>短链管理用例。</summary>
public sealed class LinkService(
    IShortLinkRepository links,
    IDomainRepository domains,
    IRouteRepository routes,
    IUserRepository users,
    IPasswordHasher passwordHasher,
    ILinkCache cache,
    IClock clock,
    ICurrentUser currentUser,
    WorkspaceContext workspace,
    IUnitOfWork unitOfWork,
    ISecretGenerator secrets,
    TargetUrlSecurityScanner targetScanner,
    IValidator<CreateLinkRequest> createValidator,
    IValidator<UpdateLinkRequest> updateValidator,
    IValidator<BatchLinkRequest> batchValidator)
{
    private const int MaxCodeAttempts = 8;

    public async Task<PagedResult<LinkDto>> QueryAsync(LinkQuery query, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        var page = await links
            .QueryAsync(query, workspaceId, currentUser.UserId, now, cancellationToken)
            .ConfigureAwait(false);

        var domainMap = (await domains.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id);

        var ids = page.Items.Select(l => l.Id).ToList();
        var routeCounts = await routes.CountByLinksAsync(ids, cancellationToken).ConfigureAwait(false);

        var creatorIds = page.Items
            .Where(l => l.CreatedByUserId is not null)
            .Select(l => l.CreatedByUserId!.Value)
            .Distinct()
            .ToList();
        var creators = creatorIds.Count == 0
            ? new Dictionary<long, string>()
            : (await users.ListAsync(creatorIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var items = page.Items
            .Select(link => link.ToDto(
                domainMap.GetValueOrDefault(link.DomainId),
                routeCounts.GetValueOrDefault(link.Id),
                creators.GetValueOrDefault(link.CreatedByUserId ?? 0)))
            .ToList();

        return new PagedResult<LinkDto>(items, page.Total, page.Page, page.PageSize);
    }

    public async Task<LinkDto> GetAsync(long id, CancellationToken cancellationToken)
    {
        var link = await LoadVisibleAsync(id, cancellationToken).ConfigureAwait(false);
        var domain = await domains.FindByIdAsync(link.DomainId, cancellationToken).ConfigureAwait(false);
        var routeList = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);
        string? creator = null;
        if (link.CreatedByUserId is { } creatorId)
        {
            var creators = await users.ListAsync([creatorId], cancellationToken).ConfigureAwait(false);
            creator = creators.Count > 0 ? creators[0].DisplayName : null;
        }

        return link.ToDto(domain, routeList.Count, creator);
    }

    /// <summary>
    /// 按短链地址（或短码）反查短链。
    /// <para>
    /// 外部集成与 AI 工具手里通常只有 <c>https://s.example.com/abc123</c>，没有数据库 ID，
    /// 因此需要一条「URL → 实体」的解析路径。短码只在域名内唯一，所以带 URL 时必须用 Host 定位域名；
    /// 只给短码时，仅当工作区只有一个域名才能无歧义解析。
    /// </para>
    /// </summary>
    public async Task<LinkDto> GetByShortUrlAsync(string shortUrlOrCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shortUrlOrCode))
        {
            throw new BusinessException(BizCodes.InvalidArgument, "短链地址不能为空");
        }

        var input = shortUrlOrCode.Trim();
        string code;
        ShortDomain? domain;

        if (input.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                throw new BusinessException(BizCodes.InvalidArgument, "短链地址格式不正确，需形如 https://s.example.com/abc123");
            }

            // 域名表里的 Host 存的是「主机名[:端口]」，而 Uri 会把默认端口吃掉；
            // 两种口径都试一次，否则 localhost:5080 这类带端口的部署永远匹配不上。
            var hostWithPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            domain = await domains.FindByHostAsync(hostWithPort, cancellationToken).ConfigureAwait(false)
                     ?? await domains.FindByHostAsync(uri.Host, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.LinkNotFound);

            code = uri.AbsolutePath.Trim('/');
        }
        else
        {
            code = input.Trim('/');
            var all = await domains.ListAsync(cancellationToken).ConfigureAwait(false);
            domain = all.Count == 1
                ? all[0]
                : throw new BusinessException(
                    BizCodes.InvalidArgument,
                    "当前存在多个短链域名，请传完整短链地址（含域名）以便定位");
        }

        if (code.Length == 0 || code.Contains('/'))
        {
            throw new BusinessException(BizCodes.InvalidArgument, "未能从短链地址中解析出短码");
        }

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var link = await links.FindByCodeAsync(domain.Id, code, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);

        // 与 LoadVisibleAsync 同口径：跨工作区一律按「不存在」处理，不泄露资源是否存在。
        if (link.WorkspaceId != workspaceId)
        {
            throw new BusinessException(BizCodes.LinkNotFound);
        }

        var routeList = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);
        return link.ToDto(domain, routeList.Count);
    }

    public async Task<LinkDto> CreateAsync(CreateLinkRequest request, CancellationToken cancellationToken)
    {
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var fallbackUrl = NormalizeUrl(request.FallbackUrl, request.TargetUrl);
        await targetScanner.EnsureAllowedAsync(request.TargetUrl, null, cancellationToken).ConfigureAwait(false);
        if (fallbackUrl is not null)
        {
            await targetScanner.EnsureAllowedAsync(fallbackUrl, null, cancellationToken).ConfigureAwait(false);
        }

        var domain = await ResolveDomainAsync(request.DomainId, cancellationToken).ConfigureAwait(false);
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);

        var code = request.Code?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            if (ShortCode.IsReserved(code))
            {
                throw new BusinessException(BizCodes.LinkCodeReserved);
            }

            if (!ShortCode.IsValid(code))
            {
                throw new BusinessException(BizCodes.LinkCodeInvalid);
            }

            if (await links.CodeExistsAsync(domain.Id, code, cancellationToken).ConfigureAwait(false))
            {
                throw new BusinessException(BizCodes.LinkCodeExists);
            }
        }
        else
        {
            code = await GenerateUniqueCodeAsync(domain.Id, cancellationToken).ConfigureAwait(false);
        }

        var draft = new LinkDraft
        {
            DomainId = domain.Id,
            WorkspaceId = workspaceId,
            Code = code,
            TargetUrl = request.TargetUrl,
            Title = request.Title,
            RedirectType = ToRedirectType(request.RedirectType),
            ExpiresAtUtc = NormalizeTime(request.ExpiresAtUtc),
            StartsAtUtc = NormalizeTime(request.StartsAtUtc),
            PasswordHash = string.IsNullOrEmpty(request.Password) ? null : passwordHasher.Hash(request.Password),
            Remark = request.Remark,
            Enabled = request.Enabled,
            MaxClicks = NormalizeMaxClicks(request.MaxClicks),
            BotPolicy = ToBotPolicy(request.BotPolicy),
            IpAllowList = request.IpAllowList,
            IpBlockList = request.IpBlockList,
            FallbackUrl = fallbackUrl,
            AntiBlockEnabled = request.AntiBlockEnabled,
            CreatedByUserId = currentUser.UserId,
        };

        var link = Link.Create(draft, clock.UtcNow);
        links.Add(link);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return link.ToDto(domain);
    }

    public async Task<LinkDto> UpdateAsync(long id, UpdateLinkRequest request, CancellationToken cancellationToken)
    {
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        // 对象级授权必须先于安全事件写入，避免越权请求在目标工作区留下审计记录。
        var link = await LoadWritableAsync(id, cancellationToken).ConfigureAwait(false);
        var fallbackUrl = NormalizeUrl(request.FallbackUrl, request.TargetUrl);
        await targetScanner.EnsureAllowedAsync(request.TargetUrl, id, cancellationToken).ConfigureAwait(false);
        if (fallbackUrl is not null)
        {
            await targetScanner.EnsureAllowedAsync(fallbackUrl, id, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.UtcNow;

        var draft = new LinkDraft
        {
            DomainId = link.DomainId,
            WorkspaceId = link.WorkspaceId,
            Code = link.Code,
            TargetUrl = request.TargetUrl,
            Title = request.Title,
            RedirectType = ToRedirectType(request.RedirectType),
            ExpiresAtUtc = NormalizeTime(request.ExpiresAtUtc),
            StartsAtUtc = NormalizeTime(request.StartsAtUtc),
            Remark = request.Remark,
            Enabled = request.Enabled,
            MaxClicks = NormalizeMaxClicks(request.MaxClicks),
            BotPolicy = ToBotPolicy(request.BotPolicy),
            IpAllowList = request.IpAllowList,
            IpBlockList = request.IpBlockList,
            FallbackUrl = fallbackUrl,
            AntiBlockEnabled = request.AntiBlockEnabled,
        };

        link.Update(draft, now);

        if (request.Password is not null)
        {
            link.SetPassword(
                string.IsNullOrEmpty(request.Password) ? null : passwordHasher.Hash(request.Password),
                now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await InvalidateAsync(link, cancellationToken).ConfigureAwait(false);

        var domain = await domains.FindByIdAsync(link.DomainId, cancellationToken).ConfigureAwait(false);
        return link.ToDto(domain);
    }

    public async Task<LinkDto> ToggleAsync(long id, CancellationToken cancellationToken)
    {
        var link = await LoadWritableAsync(id, cancellationToken).ConfigureAwait(false);

        link.Toggle(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await InvalidateAsync(link, cancellationToken).ConfigureAwait(false);

        var domain = await domains.FindByIdAsync(link.DomainId, cancellationToken).ConfigureAwait(false);
        return link.ToDto(domain);
    }

    /// <summary>
    /// 把短链设为指定启停状态（幂等）。
    /// <para>
    /// 与 <see cref="ToggleAsync"/> 的区别是并发语义：两个「设为启用」的请求同时到达时，
    /// toggle 会翻转两次回到停用，而本方法两次都写同一个目标状态。
    /// 需要外部系统（含 AI 工具、批量脚本）指定状态时用这个，不要用「先读再 toggle」。
    /// </para>
    /// </summary>
    public async Task<LinkDto> SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken)
    {
        var link = await LoadWritableAsync(id, cancellationToken).ConfigureAwait(false);
        var target = enabled ? LinkStatus.Enabled : LinkStatus.Disabled;

        if (link.Status != target)
        {
            if (enabled)
            {
                link.Enable(clock.UtcNow);
            }
            else
            {
                link.Disable(clock.UtcNow);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await InvalidateAsync(link, cancellationToken).ConfigureAwait(false);
        }

        var domain = await domains.FindByIdAsync(link.DomainId, cancellationToken).ConfigureAwait(false);
        return link.ToDto(domain);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var link = await LoadWritableAsync(id, cancellationToken).ConfigureAwait(false);

        links.Remove(link);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await InvalidateAsync(link, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量操作：单事务提交，逐条返回结果码。单条失败不影响其他条目，
    /// 让前端可以只对失败项重试，而不用整批回滚。
    /// </summary>
    public async Task<BatchResult> BatchAsync(BatchLinkRequest request, CancellationToken cancellationToken)
    {
        await batchValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var action = request.Action.Trim().ToLowerInvariant();
        var ids = request.Ids.Distinct().ToList();
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        ShortDomain? targetDomain = null;
        if (action == BatchActions.ChangeDomain)
        {
            targetDomain = await ResolveDomainAsync(request.DomainId, cancellationToken).ConfigureAwait(false);
        }

        var entities = (await links.FindManyAsync(ids, cancellationToken).ConfigureAwait(false))
            .Where(l => l.WorkspaceId == workspaceId)
            .ToDictionary(l => l.Id);

        var results = new List<BatchItemResult>(ids.Count);
        var invalidated = new List<(long DomainId, string Code)>();

        foreach (var id in ids)
        {
            if (!entities.TryGetValue(id, out var link))
            {
                results.Add(Failure(id, null, BizCodes.LinkNotFound));
                continue;
            }

            if (!LinkPolicy.CanModify(link, currentUser))
            {
                results.Add(Failure(id, link.Code, BizCodes.Forbidden));
                continue;
            }

            try
            {
                switch (action)
                {
                    case BatchActions.Enable:
                        link.Enable(now);
                        break;

                    case BatchActions.Disable:
                        link.Disable(now);
                        break;

                    case BatchActions.Delete:
                        links.Remove(link);
                        break;

                    case BatchActions.ChangeDomain:
                        if (targetDomain!.Id != link.DomainId
                            && await links
                                .CodeExistsExceptAsync(targetDomain.Id, link.Code, link.Id, cancellationToken)
                                .ConfigureAwait(false))
                        {
                            results.Add(Failure(id, link.Code, BizCodes.LinkCodeConflictInDomain));
                            continue;
                        }

                        invalidated.Add((link.DomainId, link.Code));
                        link.MoveToDomain(targetDomain!.Id, now);
                        break;

                    case BatchActions.ChangeExpiry:
                        link.ChangeExpiry(NormalizeTime(request.ExpiresAtUtc), now);
                        if (request.StartsAtUtc is not null)
                        {
                            link.ChangeStart(NormalizeTime(request.StartsAtUtc), now);
                        }

                        break;

                    default:
                        results.Add(Failure(id, link.Code, BizCodes.InvalidArgument));
                        continue;
                }

                results.Add(new BatchItemResult(id, link.Code, true, BizCodes.Success, "成功"));
            }
            catch (BusinessException exception)
            {
                results.Add(Failure(id, link.Code, exception.Code));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 缓存失效放在提交之后：避免事务回滚后缓存已被清空造成一次多余的穿透。
        foreach (var link in entities.Values)
        {
            invalidated.Add((link.DomainId, link.Code));
        }

        foreach (var (domainId, code) in invalidated.Distinct())
        {
            await cache.RemoveLinkAsync(domainId, code, cancellationToken).ConfigureAwait(false);
        }

        var succeeded = results.Count(r => r.Success);
        return new BatchResult(results.Count, succeeded, results.Count - succeeded, results);
    }

    /// <summary>下发（必要时生成）转化回调密钥。仅所有者 / 管理员与创建人可取。</summary>
    public async Task<ConversionKeyDto> GetConversionKeyAsync(long id, CancellationToken cancellationToken)
    {
        var link = await LoadWritableAsync(id, cancellationToken).ConfigureAwait(false);

        var secret = link.ConversionSecret;
        if (string.IsNullOrEmpty(secret))
        {
            await links
                .TrySetConversionSecretAsync(link.Id, secrets.Create(), clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            // 并发首次生成时，无论本次条件更新是否命中，都返回数据库最终选定的同一把密钥。
            secret = await links.GetConversionSecretAsync(link.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.Conflict, "转化密钥生成失败，请重试");
        }

        return new ConversionKeyDto(
            link.Id,
            link.Code,
            secret,
            "HMAC-SHA256",
            "X-Signature",
            "sha256={hex}",
            "{X-Timestamp}.{rawJsonBody}");
    }

    private async Task<Link> LoadVisibleAsync(long id, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var link = await links.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);

        return link.WorkspaceId == workspaceId
            ? link
            : throw new BusinessException(BizCodes.LinkNotFound);
    }

    private async Task<Link> LoadWritableAsync(long id, CancellationToken cancellationToken)
    {
        var link = await LoadVisibleAsync(id, cancellationToken).ConfigureAwait(false);

        LinkPolicy.EnsureCanModify(link, currentUser);
        return link;
    }

    private async Task InvalidateAsync(Link link, CancellationToken cancellationToken)
        => await cache.RemoveLinkAsync(link.DomainId, link.Code, cancellationToken).ConfigureAwait(false);

    private async Task<ShortDomain> ResolveDomainAsync(long? domainId, CancellationToken cancellationToken)
    {
        ShortDomain? domain;
        if (domainId is { } id)
        {
            domain = await domains.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.DomainNotFound);
        }
        else
        {
            domain = await domains.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.DomainNotFound, "请先配置一个短链域名");
        }

        if (!domain.IsEnabled)
        {
            throw new BusinessException(BizCodes.DomainNotFound, "该域名已停用");
        }

        return domain;
    }

    private async Task<string> GenerateUniqueCodeAsync(long domainId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            // 失败次数越多码越长，降低冲突概率；冲突概率极低，正常情况下第一次即命中。
            var length = ShortCode.DefaultLength + (attempt / 3);
            var code = ShortCode.Generate(length);
            if (!await links.CodeExistsAsync(domainId, code, cancellationToken).ConfigureAwait(false))
            {
                return code;
            }
        }

        throw new BusinessException(BizCodes.Conflict, "短码生成冲突，请重试或手动指定短码");
    }

    private static BatchItemResult Failure(long id, string? code, int bizCode)
        => new(id, code, false, bizCode, BizCodes.Describe(bizCode));

    private static LinkRedirectType ToRedirectType(int value)
        => value switch
        {
            301 => LinkRedirectType.Permanent301,
            307 => LinkRedirectType.Temporary307,
            308 => LinkRedirectType.Permanent308,
            _ => LinkRedirectType.Temporary302,
        };

    private static LinkBotPolicy ToBotPolicy(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            BotPolicies.Block => LinkBotPolicy.Block,
            BotPolicies.IgnoreCount => LinkBotPolicy.IgnoreCount,
            _ => LinkBotPolicy.Allow,
        };

    private static long? NormalizeMaxClicks(long? maxClicks)
        => maxClicks is > 0 ? maxClicks : null;

    private static DateTime? NormalizeTime(DateTime? value)
        => value?.ToUniversalTime() is { } utc && utc > DateTime.UtcNow.AddMinutes(-1) ? utc : null;

    private static string? NormalizeUrl(string? url, string fallback)
        => string.IsNullOrWhiteSpace(url)
            ? null
            : string.Equals(url.Trim(), fallback.Trim(), StringComparison.OrdinalIgnoreCase) ? null : url.Trim();
}
