using Microsoft.Extensions.Logging;
using ShortLink.Application.Messaging;
using ShortLink.Domain.Security;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Messaging.Handlers;

/// <summary>
/// 点击封顶审计。
/// <para>
/// 从「点击批处理事务内顺手写一行」改成独立消费者，换来两件事：
/// 审计写入失败不再拖累点击落库；将来要加「封顶后发通知 / 调 webhook」时，
/// 只是再加一个 handler，不必改动已经稳定的批处理路径。
/// </para>
/// <para>
/// <b>幂等性</b>：当前不做去重。代价是持久化传输（outbox / Kafka）重投时可能写出重复审计行，
/// 而审计重复是「多一条记录」，比「漏一条记录」可接受得多。若后续要对账，可在此按
/// <c>(LinkId, OccurredAtUtc)</c> 建唯一索引。
/// </para>
/// </summary>
public static class LinkClickCappedHandler
{
    public static async Task Handle(
        LinkClickCapped message,
        ShortLinkDbContext db,
        ILogger<LinkClickCappedMarker> logger,
        CancellationToken cancellationToken)
    {
        db.SecurityEvents.Add(SecurityEvent.Create(
            SecurityEventType.ClickCapReached,
            $"点击量达到上限（{message.ClickCount}/{message.MaxClicks}），已自动停用",
            message.WorkspaceId,
            message.LinkId,
            message.Code,
            message.Ip,
            null,
            message.Country,
            message.OccurredAtUtc));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "短链 {Code} 因点击封顶自动停用（{Clicks}/{Max}）",
            message.Code,
            message.ClickCount,
            message.MaxClicks);
    }
}

/// <summary>给静态 handler 提供稳定的日志分类名。</summary>
public sealed class LinkClickCappedMarker;
