using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class WorkspaceRepository(ShortLinkDbContext db) : IWorkspaceRepository
{
    public Task<Workspace?> FindDefaultAsync(CancellationToken cancellationToken)
        => db.Workspaces
            .AsNoTracking()
            .OrderByDescending(w => w.IsDefault)
            .ThenBy(w => w.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Workspace?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public Task<WorkspaceMember?> FindMemberAsync(
        long workspaceId,
        long userId,
        CancellationToken cancellationToken)
        => db.WorkspaceMembers
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(
        long workspaceId,
        CancellationToken cancellationToken)
        => await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<long, WorkspaceMember>> ListMembersByUserIdsAsync(
        long workspaceId,
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<long, WorkspaceMember>();
        }

        var rows = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && userIds.Contains(m.UserId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(m => m.UserId);
    }

    public Task<int> CountByRoleAsync(long workspaceId, UserRole role, CancellationToken cancellationToken)
        => db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.Role == role, cancellationToken);

    public void AddWorkspace(Workspace workspace) => db.Workspaces.Add(workspace);

    public void AddMember(WorkspaceMember member) => db.WorkspaceMembers.Add(member);

    public void RemoveMember(WorkspaceMember member) => db.WorkspaceMembers.Remove(member);
}
