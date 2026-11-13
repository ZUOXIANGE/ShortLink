namespace ShortLink.Application.Abstractions;

/// <summary>一次已预留的点击额度。仅在对应消息未成功发布时释放；落库后保留并校准为实际计数。</summary>
public sealed class ClickQuotaReservation(Guid reservationId, long linkId, long maxClicks)
{
    private int _released;

    public Guid ReservationId { get; } = reservationId;

    public long LinkId { get; } = linkId;

    public long MaxClicks { get; } = maxClicks;

    public static ClickQuotaReservation Create(long linkId, long maxClicks)
        => new(Guid.NewGuid(), linkId, maxClicks);

    /// <summary>同一预留最多补偿一次，避免重试释放把可用额度放大。</summary>
    public bool TryMarkReleased() => Interlocked.CompareExchange(ref _released, 1, 0) == 0;
}

/// <summary>
/// 点击封顶配额端口。实现必须在跳转热路径上原子预留，且不得访问数据库。
/// 初始计数来自 <see cref="CachedLink.ClickCount"/>，批落库后用短链表实际计数校准。
/// </summary>
public interface IClickQuota
{
    /// <summary>预留一个额度；达到上限时返回 null。后端不可用时应安全失败，不能放行超发。</summary>
    ValueTask<ClickQuotaReservation?> TryReserveAsync(CachedLink link, CancellationToken cancellationToken);

    /// <summary>消息发布失败时补偿释放。实现应尽力保证重复释放不会把计数降到实际点击数以下。</summary>
    ValueTask ReleaseAsync(ClickQuotaReservation reservation, CancellationToken cancellationToken = default);

    /// <summary>批落库失败时按预留 ID 补偿；重复释放必须是幂等空操作。</summary>
    ValueTask ReleaseAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default);

    /// <summary>批落库成功后把预留标记为已消费，禁止后续重复消息再释放该额度。</summary>
    ValueTask CompleteAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default);

    /// <summary>点击批提交后校准实际计数；不得降低尚未落库的预留数。</summary>
    ValueTask SynchronizeAsync(long linkId, long actualCount, CancellationToken cancellationToken = default);
}
