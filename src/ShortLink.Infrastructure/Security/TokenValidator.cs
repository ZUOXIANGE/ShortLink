using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Users;

namespace ShortLink.Infrastructure.Security;

/// <summary>每次认证都回源校验账号、令牌安全版本与当前工作区成员角色。</summary>
public sealed class TokenValidator(
    IUserRepository users,
    IWorkspaceRepository workspaces) : ITokenValidator
{
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

        var member = await workspaces
            .FindMemberAsync(workspaceId, userId, cancellationToken)
            .ConfigureAwait(false);

        return member?.Role;
    }
}
