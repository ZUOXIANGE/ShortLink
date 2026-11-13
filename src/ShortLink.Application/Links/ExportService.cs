using System.Globalization;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;

namespace ShortLink.Application.Links;

/// <summary>
/// CSV 导出。逐行流式写出，内存占用与结果集大小无关；
/// 首行写 UTF-8 BOM，保证 Excel 双击打开不乱码。
/// </summary>
public sealed class ExportService(
    IShortLinkRepository links,
    IDomainRepository domains,
    IAnalyticsRepository analytics,
    IClock clock,
    IStatisticsCalendar calendar,
    ICurrentUser currentUser,
    WorkspaceContext workspace)
{
    public const int MaxRows = 100_000;

    private const int FlushBatchSize = 500;
    private const char Bom = '\uFEFF';

    private static readonly string[] LinkHeaders =
    [
        "短码", "短链接", "目标地址", "标题", "状态", "跳转类型",
        "生效时间(UTC)", "过期时间(UTC)", "点击量", "点击上限", "机器人策略",
        "兜底地址", "分支数", "创建人ID", "创建时间(UTC)", "最后访问时间(UTC)",
    ];

    private static readonly string[] ClickHeaders =
    [
        "点击时间(UTC)", "短码", "分支ID", "IP", "国家", "设备", "浏览器", "操作系统", "来源", "是否机器人",
    ];

    public async Task<ExportMetadata> GetLinkExportMetadataAsync(LinkQuery query, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var countQuery = new LinkQuery
        {
            Page = 1,
            PageSize = 1,
            Keyword = query.Keyword,
            DomainId = query.DomainId,
            Status = query.Status,
            Sort = query.Sort,
            Mine = query.Mine,
            CreatedByUserId = query.CreatedByUserId,
        };
        var result = await links
            .QueryAsync(countQuery, workspaceId, currentUser.UserId, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return new ExportMetadata(
            result.Total,
            (int)Math.Min(result.Total, MaxRows),
            result.Total > MaxRows);
    }

    public async Task WriteLinksAsync(LinkQuery query, TextWriter writer, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var domainMap = (await domains.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id);

        await writer.WriteAsync(Bom).ConfigureAwait(false);
        await WriteRowAsync(writer, LinkHeaders).ConfigureAwait(false);

        var count = 0;
        await foreach (var link in links
                           .StreamAsync(query, workspaceId, currentUser.UserId, now, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (count >= MaxRows)
            {
                break;
            }

            var dto = link.ToDto(domainMap.GetValueOrDefault(link.DomainId));
            await WriteRowAsync(writer, [
                dto.Code,
                dto.ShortUrl,
                dto.TargetUrl,
                dto.Title,
                StatusLabel(dto, now),
                dto.RedirectType.ToString(CultureInfo.InvariantCulture),
                Format(dto.StartsAtUtc),
                Format(dto.ExpiresAtUtc),
                dto.ClickCount.ToString(CultureInfo.InvariantCulture),
                dto.MaxClicks?.ToString(CultureInfo.InvariantCulture),
                dto.BotPolicy,
                dto.FallbackUrl,
                dto.RouteCount.ToString(CultureInfo.InvariantCulture),
                dto.CreatedByUserId?.ToString(CultureInfo.InvariantCulture),
                Format(dto.CreatedAtUtc),
                Format(dto.LastAccessedAtUtc),
            ]).ConfigureAwait(false);

            if (++count % FlushBatchSize == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteClicksAsync(
        long linkId,
        int days,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        // 先校验短链归属，避免换个 ID 就能导出别的工作区的访问明细。
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var link = await links.FindByIdAsync(linkId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);
        if (link.WorkspaceId != workspaceId)
        {
            throw new BusinessException(BizCodes.LinkNotFound);
        }

        var from = calendar.Today.AddDays(-(Math.Clamp(days, 1, 365) - 1));

        await writer.WriteAsync(Bom).ConfigureAwait(false);
        await WriteRowAsync(writer, ClickHeaders).ConfigureAwait(false);

        var count = 0;
        await foreach (var click in analytics
                           .StreamClicksAsync(link.Id, from, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            await WriteRowAsync(writer, [
                Format(click.ClickedAtUtc),
                click.Code,
                click.RouteId?.ToString(CultureInfo.InvariantCulture),
                click.Ip,
                click.Country,
                click.Device,
                click.Browser,
                click.OperatingSystem,
                click.Referer,
                click.IsBot ? "是" : "否",
            ]).ConfigureAwait(false);

            // 按批刷写，避免逐行 Flush 导致大量小 IO。
            if (++count % FlushBatchSize == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRowAsync(TextWriter writer, IEnumerable<string?> values)
    {
        var line = string.Join(',', values.Select(Escape));
        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    /// <summary>CSV 转义：含分隔符、引号、换行时整体加引号，内部引号翻倍。</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Excel 会忽略公式前缀前的空白、控制与零宽格式字符，因此不能只检查 value[0]。
        var formulaIndex = 0;
        while (formulaIndex < value.Length
               && (char.IsWhiteSpace(value[formulaIndex])
                   || char.IsControl(value[formulaIndex])
                   || char.GetUnicodeCategory(value[formulaIndex]) == UnicodeCategory.Format))
        {
            formulaIndex++;
        }

        var safe = formulaIndex < value.Length && value[formulaIndex] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;

        return safe.Contains(',', StringComparison.Ordinal)
               || safe.Contains('"', StringComparison.Ordinal)
               || safe.Contains('\n', StringComparison.Ordinal)
               || safe.Contains('\r', StringComparison.Ordinal)
            ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safe;
    }

    private static string? Format(DateTime? value)
        => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string StatusLabel(LinkDto link, DateTime nowUtc)
    {
        if (link.MaxClicks is { } maxClicks && link.ClickCount >= maxClicks)
        {
            return "已封顶";
        }

        if (link.ExpiresAtUtc is { } expiresAt && expiresAt <= nowUtc)
        {
            return "已过期";
        }

        if (link.StartsAtUtc is { } startsAt && startsAt > nowUtc)
        {
            return "未生效";
        }

        return link.Status == "enabled" ? "启用中" : "已停用";
    }
}

public sealed record ExportMetadata(long TotalRows, int RowCount, bool Truncated);
