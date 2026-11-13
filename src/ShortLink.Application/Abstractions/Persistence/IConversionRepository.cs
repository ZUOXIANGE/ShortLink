using ShortLink.Domain.Analytics;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>按分支聚合的转化量。</summary>
public sealed record VariantConversion(long? RouteId, long Count, decimal Value);

/// <summary>转化聚合结果。</summary>
public sealed record ConversionAggregate(long Total, decimal Value, IReadOnlyList<VariantConversion> ByRoute)
{
    public static ConversionAggregate Empty { get; } = new(0, 0m, []);
}

/// <summary>转化事件仓储。以 <c>(link_id, event_id)</c> 唯一索引实现幂等。</summary>
public interface IConversionRepository
{
    Task<bool> ExistsAsync(long linkId, string eventId, CancellationToken cancellationToken);

    Task<ConversionAggregate> GetLinkAggregateAsync(
        long linkId,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken);

    /// <summary>回调响应使用的轻量总数，不加载分支聚合。</summary>
    Task<long> GetLinkTotalAsync(long linkId, CancellationToken cancellationToken);

    /// <summary>工作区维度的转化总量，用于控制台概览。</summary>
    Task<(long Count, decimal Value)> GetWorkspaceTotalsAsync(
        long workspaceId,
        DateTime? fromUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 幂等写入：命中 <c>(link_id, event_id)</c> 唯一索引时返回 false，
    /// 由仓储层把唯一约束冲突翻译成返回值，应用层不需要感知 EF 异常类型。
    /// </summary>
    Task<bool> TryAddAsync(ConversionEvent conversion, CancellationToken cancellationToken);
}
