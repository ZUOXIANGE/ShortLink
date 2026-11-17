namespace ShortLink.Infrastructure.Options;

/// <summary>JWT 配置。签名密钥长度必须 ≥ 32 字节。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ShortLink";

    public string Audience { get; set; } = "ShortLink.Admin";

    public string SigningKey { get; set; } = string.Empty;

    public int ExpiresMinutes { get; set; } = 720;
}
