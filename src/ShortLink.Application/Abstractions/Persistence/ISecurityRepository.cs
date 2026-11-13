using ShortLink.Contracts.Security;
using ShortLink.Contracts;
using ShortLink.Domain.Security;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>安全事件仓储。查询与写入分离，写入走轻量插入，不参与短链事务。</summary>
public interface ISecurityRepository
{
    Task<PagedResult<SecurityEvent>> QueryAsync(
        SecurityEventQuery query,
        long workspaceId,
        DateTime fromUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 在可序列化事务内完成小时配额检查与举报插入；并发冲突时安全拒绝本次举报。
    /// </summary>
    Task<bool> TryAddReportWithinQuotaAsync(
        SecurityEvent securityEvent,
        int quota,
        DateTime fromUtc,
        CancellationToken cancellationToken);

    void Add(SecurityEvent securityEvent);
}
