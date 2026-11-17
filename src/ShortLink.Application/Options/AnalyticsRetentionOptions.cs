namespace ShortLink.Application.Options;

/// <summary>点击明细保留策略。明细是平台里增长最快的表，必须有明确的回收口径。</summary>
public sealed class AnalyticsRetentionOptions
{
    public const string SectionName = "Analytics:Retention";

    /// <summary>开启后按 <see cref="ClickRetentionDays"/> 清理历史点击明细。</summary>
    public bool Enabled { get; set; }

    /// <summary>保留天数，按 UTC 日期切分；必须大于 0 才生效。</summary>
    public int ClickRetentionDays { get; set; } = 180;

    /// <summary>单批删除行数。过大可能撞上数据库的 SQL 变量上限，建议 200~2000。</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>单次运行最多删除的行数，防止一次清理把数据库压满。</summary>
    public int MaxRowsPerRun { get; set; } = 200_000;

    /// <summary>每天在 UTC 几点执行（0~23）。</summary>
    public int RunAtHourUtc { get; set; } = 3;

    /// <summary>两批之间停顿的毫秒数，给主库留出让路空间。</summary>
    public int BatchDelayMilliseconds { get; set; } = 200;
}
