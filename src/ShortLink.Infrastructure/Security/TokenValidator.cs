using Microsoft.Extensions.Caching.Memory;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Users;

namespace ShortLink.Infrastructure.Security;

/// <summary>
/// 每次认证都回源校验账号、令牌安全版本与当前工作区成员角色。
/// <para>
/// 账号行必须每次回源：<c>IsEnabled</c> 与 <c>TokenVersion</c> 是即时撤销的依据，
/// 缓存它们会让改密 / 停用后的旧令牌在一个 TTL 内继续可用。
/// 工作区成员角色单独缓存：角色变更会同时递增 <c>User.TokenVersion</c>，
/// 键里的版本号随之变化，因此不会读到旧角色；这样每个请求从两次查询降到一次。
/// 代价是「直接删除工作区成员」最多滞后一个 TTL——目前没有产品路径会移除成员，
/// 若以后新增该能力，必须在移除后失效对应缓存。
/// </para>
/// </summary>
public sealed class TokenValidator(
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IMemoryCache cache) : ITokenValidator
{
    private static readonly TimeSpan MembershipTtl = TimeSpan.FromSeconds(30);

    public async Task<UserRole?> ValidateAsync(
        long userId,
        long workspaceId,
        int tokenVersion,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsEnabled || user.TokenVersion != tokenVersion)
        {
            return null;
        }

        var key = MembershipKey(workspaceId, userId, tokenVersion);
        if (cache.TryGetValue<UserRole?>(key, out var cached) && cached is { } cachedRole)
        {
            return cachedRole;
        }

        var member = await workspaces
            .FindMemberAsync(workspaceId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (member is null)
        {
            return null;
        }

        cache.Set(key, member.Role, MembershipTtl);
        return member.Role;
    }

    private static string MembershipKey(long workspaceId, long userId, int tokenVersion)
        => $"shortlink:auth:member:{workspaceId}:{userId}:{tokenVersion}";
}
