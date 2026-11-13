using ShortLink.Domain.Users;

namespace ShortLink.Application.Abstractions;

/// <summary>密码哈希与校验。</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string passwordHash, string password);
}

/// <summary>签发结果。</summary>
public sealed record TokenResult(string AccessToken, DateTime ExpiresAtUtc);

public static class TokenClaimNames
{
    public const string TokenVersion = "token_version";
}

/// <summary>访问令牌签发。令牌携带工作区、角色快照与用户安全版本。</summary>
public interface ITokenService
{
    TokenResult Issue(User user, long workspaceId, UserRole role);
}

/// <summary>认证后的实时安全校验；返回当前工作区角色，无效时返回 null。</summary>
public interface ITokenValidator
{
    Task<UserRole?> ValidateAsync(
        long userId,
        long workspaceId,
        int tokenVersion,
        CancellationToken cancellationToken);
}

/// <summary>随机密钥生成（转化回调密钥等）。</summary>
public interface ISecretGenerator
{
    /// <summary>生成十六进制随机串，默认 32 字节（64 个字符）。</summary>
    string Create(int bytes = 32);
}

/// <summary>
/// 请求签名校验。约定：签名值为 <c>sha256=</c> + HMACSHA256(secret, payload) 的十六进制小写。
/// </summary>
public interface ISignatureVerifier
{
    bool Verify(string secret, string payload, string? providedSignature);
}
