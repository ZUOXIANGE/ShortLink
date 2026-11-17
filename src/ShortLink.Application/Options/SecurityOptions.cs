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
