using Riok.Mapperly.Abstractions;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Analytics;

namespace ShortLink.Application.Mapping;

/// <summary>统计明细的查询投影。条件直接翻译为 SQL，不把点击明细拉进内存。</summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class AnalyticsMapper
{
    public static partial IQueryable<ClickLogDto> ProjectToClickLogDto(this IQueryable<ClickEvent> source);
}
