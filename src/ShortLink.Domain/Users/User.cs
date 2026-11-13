namespace ShortLink.Domain.Users;

/// <summary>后台账号。</summary>
public sealed class User
{
    private User()
    {
        UserName = string.Empty;
        DisplayName = string.Empty;
        PasswordHash = string.Empty;
    }

    public long Id { get; private set; }

    public string UserName { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>令牌安全版本；密码、角色或停用状态变化时递增，使已签发令牌立即失效。</summary>
    public int TokenVersion { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? UpdatedAtUtc { get; private set; }

    public DateTime? LastLoginAtUtc { get; private set; }

    public static User Create(
        string userName,
        string displayName,
        string passwordHash,
        UserRole role,
        DateTime nowUtc)
        => new()
        {
            UserName = userName.Trim(),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            IsEnabled = true,
            CreatedAtUtc = nowUtc,
        };

    public void ChangePassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        TokenVersion++;
    }

    /// <summary>管理员重置他人密码（不校验原密码）。</summary>
    public void ResetPassword(string passwordHash, DateTime nowUtc)
    {
        PasswordHash = passwordHash;
        TokenVersion++;
        UpdatedAtUtc = nowUtc;
    }

    public void Rename(string displayName)
    {
        DisplayName = displayName.Trim();
    }

    public void ChangeRole(UserRole role)
    {
        Role = role;
        TokenVersion++;
    }

    public void RecordLogin(DateTime nowUtc) => LastLoginAtUtc = nowUtc;

    public void Enable() => IsEnabled = true;

    public void Disable()
    {
        IsEnabled = false;
        TokenVersion++;
    }
}
