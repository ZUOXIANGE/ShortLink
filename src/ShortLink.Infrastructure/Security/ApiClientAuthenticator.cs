using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Domain.Users;

namespace ShortLink.Infrastructure.Security;

/// <summary>
/// 第三方请求认证。校验顺序刻意安排为「便宜且无状态 → 有状态」：
/// 时间戳 → 凭据状态 → IP → HMAC → 账号/成员关系 → nonce。
/// nonce 放在最后登记，避免攻击者用错误签名提前消耗合法客户端的 nonce。
/// </summary>
public sealed class ApiClientAuthenticator(
    IApiClientRepository clients,
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IApiSecretProtector protector,
    IApiNonceStore nonces,
    ISignatureVerifier signatures,
    IClock clock,
    IOptions<PartnerApiOptions> options,
    ILogger<ApiClientAuthenticator> logger) : IApiClientAuthenticator
{
    // keyId 不存在时也要算一次 HMAC，避免通过响应耗时枚举有效凭据。
    private const string DummySecret = "shortlink-dummy-secret-for-timing-equalization";

    public async Task<ApiClientAuthResult> AuthenticateAsync(
        string keyId,
        string canonicalPayload,
        string? signature,
        long timestamp,
        string nonce,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;

        DateTimeOffset instant;
        try
        {
            instant = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail(BizCodes.ApiTimestampExpired, keyId);
        }

        if (Math.Abs(now.Subtract(instant.UtcDateTime).TotalSeconds) > settings.TimestampToleranceSeconds)
        {
            return Fail(BizCodes.ApiTimestampExpired, keyId);
        }

        var client = await clients.FindByKeyIdAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            signatures.Verify(DummySecret, canonicalPayload, signature);
            return Fail(BizCodes.ApiKeyInvalid, keyId);
        }

        if (!client.IsEnabled
            || client.IsRevoked
            || (client.ExpiresAtUtc is { } expiresAt && expiresAt <= now))
        {
            return Fail(BizCodes.ApiKeyInvalid, keyId);
        }

        if (!IsIpAllowed(client.AllowedIps, remoteIp))
        {
            return Fail(BizCodes.ApiIpNotAllowed, keyId);
        }

        string secret;
        try
        {
            secret = protector.Unprotect(client.SecretCipher);
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(exception, "凭据密文无法解密，keyId={KeyId}", client.KeyId);
            return Fail(BizCodes.ApiKeyInvalid, keyId);
        }

        if (!signatures.Verify(secret, canonicalPayload, signature))
        {
            return Fail(BizCodes.ApiSignatureInvalid, keyId);
        }

        var user = await users.FindByIdAsync(client.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsEnabled)
        {
            return Fail(BizCodes.ApiKeyInvalid, keyId);
        }

        var member = await workspaces
            .FindMemberAsync(client.WorkspaceId, client.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (member is null)
        {
            return Fail(BizCodes.ApiKeyInvalid, keyId);
        }

        // 角色取「成员当前角色」与「凭据角色上限」的较低者：
        // 账号被降级立即生效，凭据持有者也永远拿不到超过配置上限的权限。
        var role = UserRoles.Rank(member.Role) <= UserRoles.Rank(client.Role)
            ? member.Role
            : client.Role;

        var nonceTtl = TimeSpan.FromSeconds(settings.NonceTtlSeconds);
        if (!await nonces
                .TryRegisterAsync(client.KeyId, nonce, nonceTtl, cancellationToken)
                .ConfigureAwait(false))
        {
            return Fail(BizCodes.ApiNonceReplayed, keyId);
        }

        var touchThreshold = now.AddSeconds(-Math.Max(settings.LastUsedWriteIntervalSeconds, 0));
        if (client.LastUsedAtUtc is null || client.LastUsedAtUtc < touchThreshold)
        {
            await clients
                .TouchLastUsedAsync(client.Id, now, touchThreshold, cancellationToken)
                .ConfigureAwait(false);
        }

        return ApiClientAuthResult.Ok(new ApiClientIdentity(
            client.Id,
            client.Name,
            client.KeyId,
            client.WorkspaceId,
            user.Id,
            user.UserName,
            role,
            ApiScopesList(client.Scopes),
            client.RateLimitPerMinute));
    }

    private ApiClientAuthResult Fail(int code, string keyId)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning("第三方 API 认证失败 keyId={KeyId} code={Code}", keyId, code);
        }

        return ApiClientAuthResult.Fail(code);
    }

    private static string[] ApiScopesList(string scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsIpAllowed(string? allowedIps, string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(allowedIps))
        {
            return true;
        }

        if (!IPAddress.TryParse(remoteIp, out var address))
        {
            return false;
        }

        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (var entry in allowedIps.Split(
                     [',', ';', ' ', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var exact)
                && (exact.IsIPv4MappedToIPv6 ? exact.MapToIPv4() : exact).Equals(address))
            {
                return true;
            }

            if (IPNetwork.TryParse(entry, out var network) && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
