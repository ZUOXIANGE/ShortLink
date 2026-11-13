namespace ShortLink.Contracts.Users;

/// <summary>用户管理视图。含平台角色、工作区归属与最近登录时间。</summary>
public sealed record ManagedUserDto(
    long Id,
    string UserName,
    string DisplayName,
    string Role,
    string RoleName,
    bool IsEnabled,
    long WorkspaceId,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

/// <summary>新建账号。</summary>
public sealed class CreateUserRequest
{
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary><c>owner</c> / <c>admin</c> / <c>member</c> / <c>viewer</c>，默认 member。</summary>
    public string Role { get; set; } = "member";
}

/// <summary>更新账号资料与角色。</summary>
public sealed class UpdateUserRequest
{
    public string? DisplayName { get; set; }

    public string? Role { get; set; }

    public bool? IsEnabled { get; set; }
}

/// <summary>管理员重置密码。</summary>
public sealed class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>用户列表查询。</summary>
public sealed class UserQuery : PageRequest
{
    public string? Keyword { get; set; }

    public string? Role { get; set; }

    public bool? IsEnabled { get; set; }
}
