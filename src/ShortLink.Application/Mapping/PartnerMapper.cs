using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Partner;
using ShortLink.Domain.ApiClients;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Mapping;

/// <summary>第三方开放接口的投影。外部契约只暴露这里列出的字段。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class PartnerMapper
{
    public static partial PartnerLinkDto ToPartnerDto(this LinkDto link);

    [MapProperty(nameof(ApiClient.Role), nameof(ApiClientDto.Role), Use = nameof(RoleCode))]
    [MapProperty(nameof(ApiClient.SecretHint), nameof(ApiClientDto.SecretPreview), Use = nameof(SecretPreview))]
    [MapProperty(nameof(ApiClient.Scopes), nameof(ApiClientDto.Scopes), Use = nameof(ParseScopes))]
    public static partial ApiClientDto ToDto(this ApiClient client, string userName);

    public static partial PartnerOverviewDto ToPartnerDto(this DashboardOverviewDto overview);

    [UserMapping(Default = false)]
    private static string RoleCode(UserRole role) => role.ToClaimValue();

    [UserMapping(Default = false)]
    private static string SecretPreview(string secretHint) => $"****{secretHint}";

    [UserMapping(Default = false)]
    private static IReadOnlyList<string> ParseScopes(string scopes) => ApiScopes.Parse(scopes);
}
