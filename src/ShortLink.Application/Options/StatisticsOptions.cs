namespace ShortLink.Application.Options;

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
