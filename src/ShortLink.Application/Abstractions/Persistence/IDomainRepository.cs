using ShortLink.Domain.Domains;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>域名仓储。</summary>
public interface IDomainRepository
{
    Task<IReadOnlyList<ShortDomain>> ListAsync(CancellationToken cancellationToken);

    Task<ShortDomain?> FindByIdAsync(long id, CancellationToken cancellationToken);

    Task<ShortDomain?> FindByHostAsync(string host, CancellationToken cancellationToken);

    Task<ShortDomain?> FindDefaultAsync(CancellationToken cancellationToken);

    Task<bool> HostExistsAsync(string host, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, long>> CountLinksByDomainAsync(long workspaceId, CancellationToken cancellationToken);

    Task ClearDefaultAsync(CancellationToken cancellationToken);

    void Add(ShortDomain domain);

    void Remove(ShortDomain domain);
}
