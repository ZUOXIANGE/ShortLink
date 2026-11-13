using FluentValidation;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Application.Users;

/// <summary>
/// 账号与角色管理。权限模型两条硬约束：
/// <list type="number">
/// <item>只能授予不高于自己的角色，避免管理员自我提权。</item>
/// <item>不能移除最后一个所有者，避免工作区失去管理者。</item>
/// </list>
/// </summary>
public sealed class UserService(
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICurrentUser currentUser,
    WorkspaceContext workspace,
    IValidator<CreateUserRequest> createValidator,
    IValidator<UpdateUserRequest> updateValidator,
    IValidator<ResetPasswordRequest> resetValidator)
{
    public async Task<PagedResult<ManagedUserDto>> QueryAsync(UserQuery query, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var page = await users.QueryAsync(workspaceId, query, cancellationToken).ConfigureAwait(false);

        var members = await workspaces.ListMembersByUserIdsAsync(
            workspaceId,
            page.Items.Select(u => u.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var items = page.Items
            .Select(u => u.ToManagedDto(workspaceId, members[u.Id].Role))
            .ToList();
        return new PagedResult<ManagedUserDto>(items, page.Total, page.Page, page.PageSize);
    }

    public async Task<ManagedUserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        if (!UserRoles.TryParse(request.Role, out var role))
        {
            throw new BusinessException(BizCodes.UserRoleInvalid);
        }

        EnsureCanAssign(role);

        var userName = request.UserName.Trim();
        if (await users.FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new BusinessException(BizCodes.UserNameExists);
        }

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        var user = User.Create(
            userName,
            string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName,
            passwordHasher.Hash(request.Password),
            role,
            now);

        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            users.Add(user);
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);

            workspaces.AddMember(WorkspaceMember.Create(workspaceId, user.Id, role, now));
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return user.ToManagedDto(workspaceId, role);
    }

    public async Task<ManagedUserDto> UpdateAsync(
        long id,
        UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var (user, member) = await FindMemberUserAsync(workspaceId, id, cancellationToken).ConfigureAwait(false);

        if (user.Id == currentUser.UserId)
        {
            throw new BusinessException(BizCodes.UserSelfOperation);
        }

        if (!currentUser.RoleValue.AtLeast(member.Role))
        {
            throw new BusinessException(BizCodes.Forbidden, "不能修改权限高于自己的账号");
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            user.Rename(request.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            if (!UserRoles.TryParse(request.Role, out var role))
            {
                throw new BusinessException(BizCodes.UserRoleInvalid);
            }

            if (role != member.Role)
            {
                EnsureCanAssign(role);
                await EnsureNotLastOwnerAsync(workspaceId, member.Role, role, cancellationToken).ConfigureAwait(false);
                user.ChangeRole(role);
                member.ChangeRole(role);
            }
        }

        if (request.IsEnabled is { } enabled && enabled != user.IsEnabled)
        {
            if (!enabled)
            {
                await EnsureNotLastOwnerAsync(workspaceId, member.Role, UserRole.Viewer, cancellationToken)
                    .ConfigureAwait(false);
                user.Disable();
            }
            else
            {
                user.Enable();
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.ToManagedDto(workspaceId, member.Role);
    }

    public async Task ResetPasswordAsync(
        long id,
        ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await resetValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var (user, member) = await FindMemberUserAsync(workspaceId, id, cancellationToken).ConfigureAwait(false);

        if (!currentUser.RoleValue.AtLeast(member.Role))
        {
            throw new BusinessException(BizCodes.Forbidden, "不能重置权限高于自己的账号密码");
        }

        user.ResetPassword(passwordHasher.Hash(request.NewPassword), clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureCanAssign(UserRole role)
    {
        if (!currentUser.AtLeast(role))
        {
            throw new BusinessException(BizCodes.Forbidden, "不能授予高于自己权限的角色");
        }
    }

    private async Task EnsureNotLastOwnerAsync(
        long workspaceId,
        UserRole currentRole,
        UserRole nextRole,
        CancellationToken cancellationToken)
    {
        if (currentRole != UserRole.Owner || nextRole == UserRole.Owner)
        {
            return;
        }

        var owners = await workspaces.CountByRoleAsync(workspaceId, UserRole.Owner, cancellationToken)
            .ConfigureAwait(false);

        if (owners <= 1)
        {
            throw new BusinessException(BizCodes.UserLastOwner);
        }
    }

    private async Task<(User User, WorkspaceMember Member)> FindMemberUserAsync(
        long workspaceId,
        long userId,
        CancellationToken cancellationToken)
    {
        var member = await workspaces.FindMemberAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.UserNotFound);
        var user = await users.FindByIdInWorkspaceAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.UserNotFound);
        return (user, member);
    }
}
