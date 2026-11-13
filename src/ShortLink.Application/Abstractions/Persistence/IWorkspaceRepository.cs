using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>工作区与成员仓储。</summary>
public interface IWorkspaceRepository
{
    Task<Workspace?> FindDefaultAsync(CancellationToken cancellationToken);

    Task<Workspace?> FindByIdAsync(long id, CancellationToken cancellationToken);

    Task<WorkspaceMember?> FindMemberAsync(long workspaceId, long userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(long workspaceId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, WorkspaceMember>> ListMembersByUserIdsAsync(
        long workspaceId,
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken);

    /// <summary>统计该工作区内 Owner 数量，用于「至少保留一个所有者」校验。</summary>
    Task<int> CountByRoleAsync(long workspaceId, UserRole role, CancellationToken cancellationToken);

    void AddWorkspace(Workspace workspace);

    void AddMember(WorkspaceMember member);

    void RemoveMember(WorkspaceMember member);
}
