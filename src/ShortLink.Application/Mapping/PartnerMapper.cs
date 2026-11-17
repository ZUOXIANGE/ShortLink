using ShortLink.Contracts.Links;
using ShortLink.Contracts.Partner;
using ShortLink.Domain.ApiClients;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Mapping;

/// <summary>第三方开放接口的投影。外部契约只暴露这里列出的字段。</summary>
public static class PartnerMapper
{
    public static PartnerLinkDto ToPartnerDto(this LinkDto link)
        => new(
            link.Id,
            link.Code,
            link.ShortUrl,
            link.TargetUrl,
            link.Title,
            link.Status,
            link.RedirectType,
            link.StartsAtUtc,
            link.ExpiresAtUtc,
            link.ClickCount,
            link.MaxClicks,
            link.HasPassword,
            link.AntiBlockEnabled,
            link.CreatedAtUtc,
            link.LastAccessedAtUtc);

    public static ApiClientDto ToDto(this ApiClient client, string userName)
        => new(
            client.Id,
            client.Name,
            client.KeyId,
            $"****{client.SecretHint}",
            client.UserId,
            userName,
            client.Role.ToClaimValue(),
            ApiScopes.Parse(client.Scopes),
            client.RateLimitPerMinute,
            client.AllowedIps,
            client.IsEnabled,
            client.ExpiresAtUtc,
            client.LastUsedAtUtc,
            client.CreatedByUserId,
            client.CreatedAtUtc,
            client.RevokedAtUtc);
}
