namespace ShortLink.Contracts.Auth;

/// <summary>登录请求。</summary>
public sealed record LoginRequest
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>登录成功返回的令牌与当前用户。</summary>
public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, UserDto User);

/// <summary>当前登录用户。<c>Role</c> 为小写码值，<c>RoleName</c> 为展示名。</summary>
public sealed record UserDto(
    long Id,
    string UserName,
    string DisplayName,
    string Role,
    string RoleName,
    long WorkspaceId,
    string WorkspaceName,
    bool CanManageGlobalDomains = false);

/// <summary>修改密码请求。</summary>
public sealed record ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}
