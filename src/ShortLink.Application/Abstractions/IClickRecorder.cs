using ShortLink.Domain.Analytics;

namespace ShortLink.Application.Abstractions;

/// <summary>
/// 点击事件写入端口。实现把点击投递到消息总线，由后台批量落库：
/// 跳转路径不等待数据库写入，从而保证跳转延迟不被统计拖垮。
/// <para>
/// 返回 <see cref="ValueTask"/> 而不是 <c>void</c>：投递本身可能失败（队列满、broker 不可用），
/// 调用方需要能 <c>await</c> 到「已交给总线」这个事实。实现内部对异常做了兜底，
/// 因此<b>不抛异常</b>——跳转不会因为统计失败而失败。返回值表示是否已成功交给总线，
/// 调用方据此补偿跳转前预留的点击额度。
/// </para>
/// </summary>
public interface IClickRecorder
{
    ValueTask<bool> EnqueueAsync(
        ClickEvent clickEvent,
        Guid? quotaReservationId = null,
        CancellationToken cancellationToken = default);
}
