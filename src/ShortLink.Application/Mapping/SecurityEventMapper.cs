using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Security;

namespace ShortLink.Application.Mapping;

/// <summary>安全事件到契约 DTO 的映射，同时输出类型码与中文名。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class SecurityEventMapper
{
    [MapProperty(nameof(SecurityEvent.Type), nameof(SecurityEventDto.Type), Use = nameof(SecurityTypeCode))]
    [MapProperty(nameof(SecurityEvent.Type), nameof(SecurityEventDto.TypeName), Use = nameof(SecurityTypeName))]
    public static partial SecurityEventDto ToDto(this SecurityEvent securityEvent);

    [UserMapping(Default = false)]
    private static string SecurityTypeCode(SecurityEventType type)
        => SecurityEventTypes.ToCode((int)type);

    [UserMapping(Default = false)]
    private static string SecurityTypeName(SecurityEventType type)
        => SecurityEventTypes.ToName(SecurityTypeCode(type));
}
