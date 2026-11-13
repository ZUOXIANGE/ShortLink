namespace ShortLink.Contracts;

/// <summary>分页结果。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}

/// <summary>
/// 分页请求基类：页码从 1 开始，页大小上限 200。
/// <para>
/// 属性声明为可空是刻意的：Minimal API 的 <c>[AsParameters]</c> 会把非可空属性当作必填参数，
/// 调用方少传一个 <c>page</c> 就会得到 40001。绑定层拿到 null，再由
/// <see cref="EffectivePage"/> / <see cref="EffectivePageSize"/> 归一化，对业务层保持非空口径。
/// </para>
/// </summary>
public abstract class PageRequest
{
    /// <summary>限制深分页，最大偏移 1,999,800 行，避免大 OFFSET 拖垮数据库及 int 溢出。</summary>
    public const int MaxPage = 10_000;

    public int? Page { get; set; }

    public int? PageSize { get; set; }

    public int EffectivePage => Page switch
    {
        null or < 1 => 1,
        > MaxPage => MaxPage,
        _ => Page.Value,
    };

    public int EffectivePageSize => PageSize switch
    {
        null or < 1 => 20,
        > 200 => 200,
        _ => PageSize.Value,
    };

    public int Skip => (EffectivePage - 1) * EffectivePageSize;
}
