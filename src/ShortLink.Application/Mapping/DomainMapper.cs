using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Domains;
using ShortLink.Domain.Domains;

namespace ShortLink.Application.Mapping;

/// <summary>短链域名到契约 DTO 的映射。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class DomainMapper
{
    public static partial DomainDto ToDto(this ShortDomain domain, long linkCount, bool canManage = false);
}
