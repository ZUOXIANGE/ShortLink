using Riok.Mapperly.Abstractions;
using ShortLink.Application.Abstractions;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Mapping;

/// <summary>跳转缓存快照的投影。字符串名单在这里解析一次，后续每次跳转复用解析结果。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class CacheMapper
{
    [MapProperty(nameof(Link.IpAllowList), nameof(CachedLink.AllowList), Use = nameof(ParseIpList))]
    [MapProperty(nameof(Link.IpBlockList), nameof(CachedLink.BlockList), Use = nameof(ParseIpList))]
    public static partial CachedLink ToCachedLink(this Link link, IReadOnlyList<RouteSnapshot> routes);

    public static partial CachedDomain ToCachedDomain(this ShortDomain domain);

    [UserMapping(Default = false)]
    private static IpAccessList ParseIpList(string? value) => IpAccessList.Parse(value);
}
