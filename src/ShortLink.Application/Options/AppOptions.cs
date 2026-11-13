namespace ShortLink.Application.Options;

/// <summary>目标地址扫描策略。</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary><c>enforce</c>（默认，命中规则即拒绝）/ <c>warn</c>（仅告警仍放行）/ <c>off</c>。</summary>
    public string UrlScanMode { get; set; } = "enforce";

    /// <summary>单条短链允许的最大分支数。</summary>
    public int MaxRoutesPerLink { get; set; } = 20;

    /// <summary>举报命中后是否自动停用短链；默认仅记录并等待人工审核。</summary>
    public bool AbuseAutoDisable { get; set; }

    /// <summary>同一 IP 在时间窗内对同一短链的举报上限，超过则忽略。</summary>
    public int AbuseReportsPerHourPerIp { get; set; } = 5;

    /// <summary>是否启用安全事件记录。</summary>
    public bool RecordEvents { get; set; } = true;

    public bool IsEnforcing => string.Equals(UrlScanMode, "enforce", StringComparison.OrdinalIgnoreCase);

    public bool IsOff => string.Equals(UrlScanMode, "off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>限流阈值配置。集成测试与压测环境需要放宽，生产按实际承载调整。</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>
    /// 是否用 Redis 做跨实例共享配额：留空表示「配置了 <c>Cache:RedisConnectionString</c> 就自动启用」。
    /// 多实例部署必须启用，否则实际配额是「单机阈值 × 实例数」。
    /// </summary>
    public bool? UseDistributed { get; set; }

    /// <summary>登录接口：单 IP 每分钟允许的尝试次数。</summary>
    public int AuthPermitPerMinute { get; set; } = 10;

    /// <summary>转化回调：单 IP 每分钟允许的请求数。</summary>
    public int ConversionPermitPerMinute { get; set; } = 600;

    /// <summary>滥用举报：单 IP 每分钟允许的请求数。</summary>
    public int AbusePermitPerMinute { get; set; } = 20;

    /// <summary>短链访问密码：同一 IP 对同一短码每分钟允许的尝试次数。</summary>
    public int LinkPasswordPermitPerMinute { get; set; } = 10;
}

/// <summary>
/// 统计口径。决定「一天」从什么时候开始。
/// <para>
/// 默认 <c>UTC</c>，与历史行为一致（升级不改变已有数据的分桶方式）。
/// 面向单一地区的部署应配成本地时区（如 <c>Asia/Shanghai</c>），否则「今日」会在
/// 当地时间早上 8 点切换。<b>改动此值会让历史明细与新明细落在不同的日界上</b>，
/// 切换点附近那一天的统计会出现重叠或缺口，需按运维窗口执行。
/// </para>
/// </summary>
public sealed class StatisticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>统计时区：IANA（<c>Asia/Shanghai</c>）或 Windows（<c>China Standard Time</c>）标识。</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// 启动期校验。宁可起不来也不要静默回落 UTC——
    /// 时区写错只表现为「今日点击」数字诡异，没人会第一时间怀疑配置。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeZone))
        {
            throw new InvalidOperationException("Analytics:TimeZone 不能为空（不区分时区请显式填 UTC）");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Analytics:TimeZone 无法识别：'{TimeZone}'。请使用 IANA 标识（如 Asia/Shanghai）或 Windows 标识（如 China Standard Time）",
                exception);
        }
    }
}

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

/// <summary>跳转行为配置。</summary>
public sealed class RedirectOptions
{
    public const string SectionName = "Redirect";

    /// <summary>
    /// 防红中间页策略：<c>auto</c>（默认，按短链开关）/ <c>always</c>（所有短链都提示）/ <c>off</c>。
    /// </summary>
    public string WeChatInterstitial { get; set; } = "auto";

    /// <summary>密码表单与错误页是否允许被搜索引擎索引（默认 noindex）。</summary>
    public bool AllowIndexing { get; set; }
}
