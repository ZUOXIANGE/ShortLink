using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Application.Abstractions;
using ShortLink.Domain.Users;
using ShortLink.Infrastructure.Options;

namespace ShortLink.Infrastructure.Security;

/// <summary>PBKDF2-SHA256 密码哈希，存储格式 <c>pbkdf2-sha256$迭代次数$salt$hash</c>。</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string passwordHash, string password)
    {
        if (string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        var parts = passwordHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>HS256 JWT 签发。密钥不足 32 字节时直接失败，避免弱签名密钥上线。</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public TokenResult Issue(User user, long workspaceId, UserRole role)
    {
        var settings = options.Value;
        var keyBytes = Encoding.UTF8.GetBytes(settings.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey 长度必须至少 32 字节");
        }

        var now = clock.UtcNow;
        var expires = now.AddMinutes(settings.ExpiresMinutes <= 0 ? 720 : settings.ExpiresMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim("name", user.UserName),
                new Claim("display_name", user.DisplayName),
                new Claim("role", role.ToClaimValue()),
                new Claim("ws", workspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(TokenClaimNames.TokenVersion, user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keyBytes),
                SecurityAlgorithms.HmacSha256),
        };

        return new TokenResult(_handler.CreateToken(descriptor), expires);
    }
}

/// <summary>加密随机密钥生成，输出小写十六进制。</summary>
public sealed class CryptoSecretGenerator : ISecretGenerator
{
    public string Create(int bytes = 32)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(Math.Clamp(bytes, 16, 64))).ToLowerInvariant();
}

/// <summary>
/// HMAC-SHA256 请求签名校验。签名串形如 <c>sha256=hex</c>，
/// 用固定时间比较避免通过响应耗时逐字节猜测签名。
/// </summary>
public sealed class HmacSignatureVerifier : ISignatureVerifier
{
    private const string Prefix = "sha256=";

    public bool Verify(string secret, string payload, string? providedSignature)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        var value = providedSignature.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}

/// <summary>系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
