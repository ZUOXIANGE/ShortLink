using ShortLink.Domain.Users;

namespace ShortLink.Application.Abstractions;

/// <summary>签名认证通过后的调用者身份。Claims 由入口层按此生成。</summary>
public sealed record ApiClientIdentity(
    long ClientId,
    string ClientName,
    string KeyId,
    long WorkspaceId,
    long UserId,
    string UserName,
    UserRole Role,
    IReadOnlyList<string> Scopes,
    int? RateLimitPerMinute);

/// <summary>认证结果。<see cref="Code"/> 为 <c>18xxx</c> 业务码，由入口层写成统一信封。</summary>
public sealed record ApiClientAuthResult(bool Success, int Code, string Message, ApiClientIdentity? Identity)
{
    public static ApiClientAuthResult Ok(ApiClientIdentity identity)
        => new(true, Contracts.BizCodes.Success, "success", identity);

    public static ApiClientAuthResult Fail(int code)
        => new(false, code, Contracts.BizCodes.Describe(code), null);

    public static ApiClientAuthResult Fail(int code, string message)
        => new(false, code, message, null);
}

/// <summary>
/// 第三方请求认证：时间戳窗口 → 凭据状态 → IP 白名单 → HMAC → 回源账号与成员角色 → nonce。
/// 任何一步失败都返回业务码而不是抛异常，便于入口层区分 401 原因。
/// </summary>
public interface IApiClientAuthenticator
{
    Task<ApiClientAuthResult> AuthenticateAsync(
        string keyId,
        string canonicalPayload,
        string? signature,
        long timestamp,
        string nonce,
        string? remoteIp,
        CancellationToken cancellationToken);
}

/// <summary>secret 的静态加密。HMAC 需要原文，因此不能使用单向哈希。</summary>
public interface IApiSecretProtector
{
    string Protect(string secret);

    string Unprotect(string cipher);
}

/// <summary>nonce 去重。<see cref="IsDistributed"/> 为 false 时重放保护只在当前实例有效。</summary>
public interface IApiNonceStore
{
    bool IsDistributed { get; }

    /// <summary>首次写入返回 true；窗口内重复出现返回 false。</summary>
    Task<bool> TryRegisterAsync(
        string clientKey,
        string nonce,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);
}
