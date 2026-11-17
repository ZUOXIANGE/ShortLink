namespace ShortLink.Contracts.Partner;

/// <summary>API 凭据（secret 只在创建 / 轮换的响应里出现一次）。</summary>
public sealed record ApiClientDto(
    long Id,
    string Name,
    string KeyId,
    string SecretPreview,
    long UserId,
    string UserName,
    string Role,
    IReadOnlyList<string> Scopes,
    int? RateLimitPerMinute,
    string? AllowedIps,
    bool IsEnabled,
    DateTime? ExpiresAtUtc,
    DateTime? LastUsedAtUtc,
    long? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc);

/// <summary>创建凭据的响应：<see cref="Secret"/> 只返回这一次。</summary>
public sealed record CreateApiClientResponse(ApiClientDto Client, string Secret);

public sealed record RotateApiClientSecretResponse(ApiClientDto Client, string Secret);

/// <summary>
/// 创建凭据。UserId 必须是当前工作区成员；Role 是凭据角色上限，不能高于该成员当前角色。
/// </summary>
public sealed class CreateApiClientRequest
{
    public string Name { get; set; } = string.Empty;

    public long UserId { get; set; }

    /// <summary><c>owner</c> / <c>admin</c> / <c>member</c> / <c>viewer</c>，默认 member。</summary>
    public string Role { get; set; } = "member";

    public List<string> Scopes { get; set; } = [];

    /// <summary>每分钟请求上限；留空使用全局默认值。</summary>
    public int? RateLimitPerMinute { get; set; }

    /// <summary>允许的来源 IP / CIDR，逗号或换行分隔；留空不限制。</summary>
    public string? AllowedIps { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>更新凭据。空值表示不修改；Role 仍受绑定成员当前角色限制。</summary>
public sealed class UpdateApiClientRequest
{
    public string? Name { get; set; }

    public string? Role { get; set; }

    public List<string>? Scopes { get; set; }

    public int? RateLimitPerMinute { get; set; }

    public string? AllowedIps { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>清除过期时间（与 <see cref="ExpiresAtUtc"/> 二选一）。</summary>
    public bool ClearExpiry { get; set; }

    public bool? IsEnabled { get; set; }
}
