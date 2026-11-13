using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(ShortLinkDbContext db) : IUserRepository
{
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);

    public Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> FindByIdInWorkspaceAsync(
        long workspaceId,
        long id,
        CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(
            u => u.Id == id && db.WorkspaceMembers.Any(m => m.WorkspaceId == workspaceId && m.UserId == id),
            cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken)
        => db.Users.AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> ListAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PagedResult<User>> QueryAsync(
        long workspaceId,
        UserQuery query,
        CancellationToken cancellationToken)
    {
        var source = db.Users
            .AsNoTracking()
            .Where(u => db.WorkspaceMembers.Any(m => m.WorkspaceId == workspaceId && m.UserId == u.Id));

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(u => u.UserName.Contains(keyword) || u.DisplayName.Contains(keyword));
        }

        if (UserRoles.TryParse(query.Role, out var role) && !string.IsNullOrWhiteSpace(query.Role))
        {
            source = source.Where(u => db.WorkspaceMembers.Any(
                m => m.WorkspaceId == workspaceId && m.UserId == u.Id && m.Role == role));
        }

        if (query.IsEnabled is { } enabled)
        {
            source = source.Where(u => u.IsEnabled == enabled);
        }

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await source
            .OrderBy(u => u.Id)
            .Skip(query.Skip)
            .Take(query.EffectivePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<User>(items, total, query.EffectivePage, query.EffectivePageSize);
    }

    public Task<int> CountByRoleAsync(UserRole role, CancellationToken cancellationToken)
        => db.Users.CountAsync(u => u.Role == role, cancellationToken);

    public void Add(User user) => db.Users.Add(user);

    public void Remove(User user) => db.Users.Remove(user);
}
