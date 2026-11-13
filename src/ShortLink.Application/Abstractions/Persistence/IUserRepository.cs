using ShortLink.Contracts;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>账号仓储。</summary>
public interface IUserRepository
{
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken);

    Task<User?> FindByIdInWorkspaceAsync(long workspaceId, long id, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> ListAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken);

    Task<PagedResult<User>> QueryAsync(long workspaceId, UserQuery query, CancellationToken cancellationToken);

    /// <summary>统计拥有指定角色的账号数，用于「至少保留一个管理员」校验。</summary>
    Task<int> CountByRoleAsync(UserRole role, CancellationToken cancellationToken);

    void Add(User user);

    void Remove(User user);
}
