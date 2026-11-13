using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Messaging;
using ShortLink.Domain.Analytics;
using ShortLink.Infrastructure.Diagnostics;
using Wolverine;

namespace ShortLink.Infrastructure.Messaging;

/// <summary>
/// 点击记录器的 Wolverine 实现。
/// <para>
/// 与引入消息总线之前的区别只有一处：入队对象从「领域实体」变成「可序列化的消息」。
/// 语义仍然是<b>不等待落库</b>——发布到本地队列就是把消息放进内存通道，
/// 返回的 <see cref="ValueTask"/> 在内存端点下几乎同步完成。
/// </para>
/// <para>
/// 换成 Kafka 后这里会真正变成网络往返，跳转延迟会受影响。若出现这种情况，
/// 正确做法是给点击单独配一个非阻塞发送端点，而不是把发布改成 fire-and-forget
/// （后者会把「发送失败」变成静默数据丢失）。
/// </para>
/// </summary>
public sealed class WolverineClickRecorder(IMessageBus bus, ILogger<WolverineClickRecorder> logger) : IClickRecorder
{
    public async ValueTask<bool> EnqueueAsync(
        ClickEvent clickEvent,
        Guid? quotaReservationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clickEvent);

        try
        {
            await bus.PublishAsync(ClickRecorded.From(clickEvent) with { QuotaReservationId = quotaReservationId })
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            // 跳转路径绝不能因为统计失败而失败：记录后继续，由调用方释放配额并照常重定向。
            logger.LogError(exception, "点击消息发布失败：短链 {Code}", clickEvent.Code);
            ShortLinkDiagnostics.ClicksDropped.Add(1);
            return false;
        }
    }
}
