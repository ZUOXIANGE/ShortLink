using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.ApiClients;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class ApiClientRepository(ShortLinkDbContext db) : IApiClientRepository
{
    public Task<ApiClient?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken)
        => db.ApiClients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.KeyId == keyId, cancellationToken);

    public Task<ApiClient?> FindByIdAsync(long workspaceId, long id, CancellationToken cancellationToken)
        => db.ApiClients.FirstOrDefaultAsync(
            c => c.Id == id && c.WorkspaceId == workspaceId, cancellationToken);

    public async Task<IReadOnlyList<ApiClient>> ListAsync(long workspaceId, CancellationToken cancellationToken)
        => await db.ApiClients
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId)
            .OrderByDescending(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(ApiClient client) => db.ApiClients.Add(client);

    public async Task TouchLastUsedAsync(
        long id,
        DateTime nowUtc,
        DateTime thresholdUtc,
        CancellationToken cancellationToken)
        => await db.ApiClients
            .Where(c => c.Id == id && (c.LastUsedAtUtc == null || c.LastUsedAtUtc < thresholdUtc))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.LastUsedAtUtc, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);
}
