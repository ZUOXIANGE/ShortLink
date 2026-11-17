using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Mapping;

/// <summary>用户到契约 DTO 的映射。角色同时输出码值与显示名，由调用方提供工作区上下文。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class UserMapper
{
    public static UserDto ToDto(
        this User user,
        long workspaceId,
        string workspaceName,
        UserRole role,
        bool canManageGlobalDomains = false)
        => MapUser(
            user,
            workspaceId,
            workspaceName,
            role.ToClaimValue(),
            role.ToDisplayName(),
            canManageGlobalDomains);

    public static ManagedUserDto ToManagedDto(this User user, long workspaceId, UserRole role)
        => MapManagedUser(user, workspaceId, role.ToClaimValue(), role.ToDisplayName());

    [MapperIgnoreSource(nameof(User.Role))]
    private static partial UserDto MapUser(
        User user,
        long workspaceId,
        string workspaceName,
        string role,
        string roleName,
        bool canManageGlobalDomains);

    [MapperIgnoreSource(nameof(User.Role))]
    private static partial ManagedUserDto MapManagedUser(
        User user,
        long workspaceId,
        string role,
        string roleName);
}
