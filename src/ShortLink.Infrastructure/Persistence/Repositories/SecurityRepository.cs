using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Security;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class SecurityRepository(ShortLinkDbContext db) : ISecurityRepository
{
    public async Task<PagedResult<SecurityEvent>> QueryAsync(
        SecurityEventQuery query,
        long workspaceId,
        DateTime fromUtc,
        CancellationToken cancellationToken)
    {
        var source = db.SecurityEvents
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.CreatedAtUtc >= fromUtc);

        if (SecurityEventTypeParser.TryParse(query.Type, out var type))
        {
            source = source.Where(e => e.Type == type);
        }

        if (query.LinkId is { } linkId)
        {
            source = source.Where(e => e.LinkId == linkId);
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            var code = query.Code.Trim();
            source = source.Where(e => e.Code == code);
        }

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await source
            .OrderByDescending(e => e.Id)
            .Skip(query.Skip)
            .Take(query.EffectivePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<SecurityEvent>(items, total, query.EffectivePage, query.EffectivePageSize);
    }

    public async Task<bool> TryAddReportWithinQuotaAsync(
        SecurityEvent securityEvent,
        int quota,
        DateTime fromUtc,
        CancellationToken cancellationToken)
    {
        if (quota <= 0)
        {
            return false;
        }

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            var recent = await db.SecurityEvents.CountAsync(
                e => e.Type == SecurityEventType.AbuseReported
                     && e.LinkId == securityEvent.LinkId
                     && e.Ip == securityEvent.Ip
                     && e.CreatedAtUtc >= fromUtc,
                cancellationToken).ConfigureAwait(false);

            if (recent >= quota)
            {
                return false;
            }

            db.SecurityEvents.Add(securityEvent);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 并发版本冲突时宁可少记一条匿名举报，也不能突破配额。
            return false;
        }
        catch (DbUpdateException exception) when (exception.InnerException is DbException)
        {
            // SQLite 锁竞争、MySQL 死锁与 PostgreSQL 序列化失败均安全拒绝。
            return false;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public void Add(SecurityEvent securityEvent) => db.SecurityEvents.Add(securityEvent);
}

/// <summary>安全事件类型字符串码 → 枚举，供列表筛选复用。</summary>
internal static class SecurityEventTypeParser
{
    public static bool TryParse(string? code, out SecurityEventType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        switch (code.Trim().ToLowerInvariant())
        {
            case SecurityEventTypes.IpBlocked:
                type = SecurityEventType.IpBlocked;
                return true;
            case SecurityEventTypes.IpNotAllowed:
                type = SecurityEventType.IpNotAllowed;
                return true;
            case SecurityEventTypes.BotBlocked:
                type = SecurityEventType.BotBlocked;
                return true;
            case SecurityEventTypes.UrlRejected:
                type = SecurityEventType.UrlRejected;
                return true;
            case SecurityEventTypes.AbuseReported:
                type = SecurityEventType.AbuseReported;
                return true;
            case SecurityEventTypes.ClickCapReached:
                type = SecurityEventType.ClickCapReached;
                return true;
            default:
                return false;
        }
    }
}
