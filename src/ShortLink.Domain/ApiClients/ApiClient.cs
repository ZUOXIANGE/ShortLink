using ShortLink.Domain.Users;

namespace ShortLink.Domain.ApiClients;

/// <summary>
/// 第三方 API 凭据。绑定一个工作区与一个成员账号，签名认证通过后以该账号身份执行用例。
/// <para>
/// <see cref="Role"/> 是凭据的角色上限：实际生效角色取「账号当前成员角色」与它的较低者，
/// 这样给管理员账号发凭据时也能收窄到 Member，避免集成密钥天然拥有全工作区写权限。
/// </para>
/// </summary>
public sealed class ApiClient
{
    private ApiClient()
    {
        Name = string.Empty;
        KeyId = string.Empty;
        SecretCipher = string.Empty;
        SecretHint = string.Empty;
        Scopes = string.Empty;
    }

    public long Id { get; private set; }

    public long WorkspaceId { get; private set; }

    /// <summary>凭据代表的工作区成员账号。</summary>
    public long UserId { get; private set; }

    public string Name { get; private set; }

    /// <summary>公开的凭据标识，随请求头传输，可出现在日志里。</summary>
    public string KeyId { get; private set; }

    /// <summary>AES-GCM 加密后的 secret；服务端需要解密后重算 HMAC，不能只存哈希。</summary>
    public string SecretCipher { get; private set; }

    /// <summary>secret 末 4 位，仅用于列表页辨识。</summary>
    public string SecretHint { get; private set; }

    /// <summary>角色上限，实际角色不会高于它。</summary>
    public UserRole Role { get; private set; }

    /// <summary>空格分隔的 scope 集合，如 <c>links:read links:write</c>。</summary>
    public string Scopes { get; private set; }

    public int? RateLimitPerMinute { get; private set; }

    /// <summary>允许的来源 IP / CIDR，逗号或换行分隔；为空表示不限制。</summary>
    public string? AllowedIps { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public DateTime? LastUsedAtUtc { get; private set; }

    public long? CreatedByUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static ApiClient Create(
        long workspaceId,
        long userId,
        string name,
        string keyId,
        string secretCipher,
        string secretHint,
        UserRole role,
        string scopes,
        int? rateLimitPerMinute,
        string? allowedIps,
        DateTime? expiresAtUtc,
        long? createdByUserId,
        DateTime nowUtc)
        => new()
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Name = name.Trim(),
            KeyId = keyId,
            SecretCipher = secretCipher,
            SecretHint = secretHint,
            Role = role,
            Scopes = scopes,
            RateLimitPerMinute = rateLimitPerMinute is > 0 ? rateLimitPerMinute : null,
            AllowedIps = string.IsNullOrWhiteSpace(allowedIps) ? null : allowedIps.Trim(),
            IsEnabled = true,
            ExpiresAtUtc = expiresAtUtc,
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = nowUtc,
        };

    public void Update(
        string name,
        UserRole role,
        string scopes,
        int? rateLimitPerMinute,
        string? allowedIps,
        DateTime? expiresAtUtc,
        bool enabled)
    {
        Name = name.Trim();
        Role = role;
        Scopes = scopes;
        RateLimitPerMinute = rateLimitPerMinute is > 0 ? rateLimitPerMinute : null;
        AllowedIps = string.IsNullOrWhiteSpace(allowedIps) ? null : allowedIps.Trim();
        ExpiresAtUtc = expiresAtUtc;
        IsEnabled = enabled && !IsRevoked;
    }

    public void RotateSecret(string secretCipher, string secretHint)
    {
        SecretCipher = secretCipher;
        SecretHint = secretHint;
    }

    public void Revoke(DateTime nowUtc)
    {
        IsEnabled = false;
        RevokedAtUtc ??= nowUtc;
    }

    public void RecordUse(DateTime nowUtc) => LastUsedAtUtc = nowUtc;
}
