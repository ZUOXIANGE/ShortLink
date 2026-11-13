using FluentValidation;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Domains;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Domains;

/// <summary>短链域名管理。</summary>
public sealed class DomainService(
    IDomainRepository domains,
    IShortLinkRepository links,
    IWorkspaceRepository workspaces,
    WorkspaceContext workspace,
    ICurrentUser currentUser,
    ILinkCache cache,
    IClock clock,
    IUnitOfWork unitOfWork,
    IValidator<CreateDomainRequest> createValidator,
    IValidator<UpdateDomainRequest> updateValidator)
{
    public async Task<IReadOnlyList<DomainDto>> ListAsync(CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var list = await domains.ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await domains.CountLinksByDomainAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var canManage = await CanManageGlobalDomainsAsync(cancellationToken).ConfigureAwait(false);

        return list
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Host, StringComparer.Ordinal)
            .Select(d => d.ToDto(counts.TryGetValue(d.Id, out var count) ? count : 0, canManage))
            .ToList();
    }

    public async Task<DomainDto> CreateAsync(CreateDomainRequest request, CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkspaceOwnerAsync(cancellationToken).ConfigureAwait(false);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var host = ShortDomain.NormalizeHost(request.Host);
        if (!ShortDomain.IsValidHost(host))
        {
            throw new BusinessException(BizCodes.DomainHostInvalid);
        }

        ShortDomain? domain = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            if (await domains.HostExistsAsync(host, transactionToken).ConfigureAwait(false))
            {
                throw new BusinessException(BizCodes.DomainHostExists);
            }

            var existing = await domains.ListAsync(transactionToken).ConfigureAwait(false);
            var isFirst = existing.Count == 0;
            var isDefault = request.IsDefault || isFirst;
            EnsureDefaultIsEnabled(isDefault, request.IsEnabled);

            if (isDefault && !isFirst)
            {
                await domains.ClearDefaultAsync(transactionToken).ConfigureAwait(false);
            }

            domain = ShortDomain.Create(
                host,
                request.Scheme,
                request.Name,
                isDefault,
                request.IsEnabled,
                clock.UtcNow);

            domains.Add(domain);
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        await cache.ClearDomainsAsync(cancellationToken).ConfigureAwait(false);
        return domain!.ToDto(0, canManage: true);
    }

    public async Task<DomainDto> UpdateAsync(long id, UpdateDomainRequest request, CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkspaceOwnerAsync(cancellationToken).ConfigureAwait(false);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        ShortDomain? domain = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            domain = await domains.FindByIdAsync(id, transactionToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.DomainNotFound);

            EnsureDefaultIsEnabled(request.IsDefault, request.IsEnabled);
            if (domain.IsDefault && !request.IsEnabled)
            {
                throw new BusinessException(BizCodes.DomainLastDisabled, "默认域名必须启用，请先切换默认域名");
            }

            if (request.IsDefault && !domain.IsDefault)
            {
                await domains.ClearDefaultAsync(transactionToken).ConfigureAwait(false);
            }

            if (!request.IsEnabled)
            {
                var enabledOthers = (await domains.ListAsync(transactionToken).ConfigureAwait(false))
                    .Any(d => d.Id != id && d.IsEnabled);
                if (!enabledOthers)
                {
                    throw new BusinessException(BizCodes.DomainLastDisabled);
                }
            }

            domain.Update(request.Scheme, request.Name, request.IsDefault, request.IsEnabled);
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        await cache.ClearDomainsAsync(cancellationToken).ConfigureAwait(false);

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var counts = await domains.CountLinksByDomainAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return domain!.ToDto(counts.GetValueOrDefault(id), canManage: true);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkspaceOwnerAsync(cancellationToken).ConfigureAwait(false);

        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var domain = await domains.FindByIdAsync(id, transactionToken).ConfigureAwait(false)
                         ?? throw new BusinessException(BizCodes.DomainNotFound);

            // 删除是全局操作，必须检查所有工作区，不能只看当前工作区的 LinkCount。
            var linkCount = await links.CountByDomainAsync(id, transactionToken).ConfigureAwait(false);
            if (linkCount > 0)
            {
                throw new BusinessException(BizCodes.DomainInUse, $"该域名下还有 {linkCount} 条短链，请先删除或迁移");
            }

            domains.Remove(domain);
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        await cache.ClearDomainsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureDefaultWorkspaceOwnerAsync(CancellationToken cancellationToken)
    {
        if (!await CanManageGlobalDomainsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(BizCodes.Forbidden, "仅默认工作区的 Owner 可以管理全局域名");
        }
    }

    private async Task<bool> CanManageGlobalDomainsAsync(CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var current = await workspaces.FindByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return current is { IsDefault: true } && currentUser.RoleValue == UserRole.Owner;
    }

    private static void EnsureDefaultIsEnabled(bool isDefault, bool isEnabled)
    {
        if (isDefault && !isEnabled)
        {
            throw new BusinessException(BizCodes.DomainLastDisabled, "默认域名必须启用");
        }
    }
}
