namespace ShortLink.Application.Options;

/// <summary>
/// 点击明细按月归档。归档与删除是同一次搬运：按月把超过热窗口的明细写入
/// <c>click_events_archive</c> 后再从主表移除，主表只保留热数据。
/// </summary>
public sealed class AnalyticsArchiveOptions
{
    public const string SectionName = "Analytics:Archive";

    /// <summary>开启后才会执行月度归档；默认关闭，避免无意中搬走历史明细。</summary>
    public bool Enabled { get; set; }

    /// <summary>超过多少天的明细进入归档；必须大于 0 才生效。</summary>
    public int ArchiveAfterDays { get; set; } = 180;

    /// <summary>单批搬运行数。过大可能撞上数据库的 SQL 变量上限，建议 200~2000。</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>单次运行最多搬运的行数，防止一次归档把数据库压满。</summary>
    public int MaxRowsPerRun { get; set; } = 200_000;

    /// <summary>每月在 UTC 几号执行（1~28，避免 2 月没有 29~31 号）。</summary>
    public int RunOnDayOfMonth { get; set; } = 1;

    /// <summary>每月在 UTC 几点执行（0~23）。</summary>
    public int RunAtHourUtc { get; set; } = 3;

    /// <summary>两批之间停顿的毫秒数，给主库留出让路空间。</summary>
    public int BatchDelayMilliseconds { get; set; } = 200;

    /// <summary>
    /// 启动期校验。开启归档但天数非法时直接失败：每日清理在归档开启时会让位，
    /// 静默跳过会让过期明细既不归档也不删除。
    /// </summary>
    public void Validate()
    {
        if (Enabled && ArchiveAfterDays <= 0)
        {
            throw new InvalidOperationException(
                "Analytics:Archive:ArchiveAfterDays 必须大于 0；否则归档开启后过期明细既不会归档也不会被清理");
        }
    }
}
