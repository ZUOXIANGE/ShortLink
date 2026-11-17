using Riok.Mapperly.Abstractions;
using ShortLink.Application.Messaging;
using ShortLink.Domain.Analytics;

namespace ShortLink.Application.Mapping;

/// <summary>领域实体与消息之间的投影。反向还原仍走领域工厂，保留校验与截断规则。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class MessagingMapper
{
    [MapProperty(nameof(ClickEvent.EventId), nameof(ClickRecorded.ClickId))]
    public static partial ClickRecorded ToClickRecorded(
        this ClickEvent click,
        Guid? quotaReservationId = null);
}
