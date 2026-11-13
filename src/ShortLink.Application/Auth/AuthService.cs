using FluentValidation;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Validation;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Application.Auth;

/// <summary>登录、当前用户与改密。登录令牌记录工作区与角色快照，认证阶段再回源刷新校验。</summary>
public sealed class AuthService(
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    ICurrentUser currentUser,
    IClock clock,
    IUnitOfWork unitOfWork,
    IValidator<LoginRequest> loginValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator)
{
    // 用户不存在时也做一次哈希校验，避免通过响应耗时枚举用户名。
    private readonly string _dummyHash = passwordHasher.Hash(Guid.NewGuid().ToString("N"));

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        await loginValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var user = await users.FindByUserNameAsync(request.UserName.Trim(), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            passwordHasher.Verify(_dummyHash, request.Password);
            throw new BusinessException(BizCodes.InvalidCredentials);
        }

        if (!passwordHasher.Verify(user.PasswordHash, request.Password))
        {
            throw new BusinessException(BizCodes.InvalidCredentials);
        }

        if (!user.IsEnabled)
        {
            throw new BusinessException(BizCodes.UserDisabled);
        }

        var (workspace, role) = await ResolveMembershipAsync(user, cancellationToken).ConfigureAwait(false);

        user.RecordLogin(clock.UtcNow);
        var token = tokenService.Issue(user, workspace.Id, role);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new LoginResponse(
            token.AccessToken,
            token.ExpiresAtUtc,
            user.ToDto(workspace.Id, workspace.Name, role, workspace.IsDefault && role == UserRole.Owner));
    }

    public async Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new BusinessException(BizCodes.Unauthorized);
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.Unauthorized);

        var workspaceId = currentUser.WorkspaceId ?? throw new BusinessException(BizCodes.Unauthorized);
        var workspace = await workspaces.FindByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false)
                        ?? throw new BusinessException(BizCodes.Unauthorized);
        var member = await workspaces.FindMemberAsync(workspaceId, user.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.Unauthorized);
        return user.ToDto(
            workspace.Id,
            workspace.Name,
            member.Role,
            workspace.IsDefault && member.Role == UserRole.Owner);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await changePasswordValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = currentUser.UserId ?? throw new BusinessException(BizCodes.Unauthorized);
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.Unauthorized);

        if (!passwordHasher.Verify(user.PasswordHash, request.OldPassword))
        {
            throw new BusinessException(BizCodes.OldPasswordIncorrect);
        }

        if (string.Equals(request.OldPassword, request.NewPassword, StringComparison.Ordinal))
        {
            throw new BusinessException(BizCodes.PasswordTooWeak, "新密码不能与原密码相同");
        }

        user.ChangePassword(passwordHasher.Hash(request.NewPassword));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解析登录后的默认工作区与实时成员角色；非成员不能隐式进入工作区。</summary>
    private async Task<(Workspace Workspace, UserRole Role)> ResolveMembershipAsync(
        User user,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaces.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new BusinessException(BizCodes.InternalError, "缺少默认工作区");

        var member = await workspaces
            .FindMemberAsync(workspace.Id, user.Id, cancellationToken)
            .ConfigureAwait(false);

        return member is null
            ? throw new BusinessException(BizCodes.Unauthorized)
            : (workspace, member.Role);
    }
}
