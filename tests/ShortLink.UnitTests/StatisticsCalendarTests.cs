using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Time;
using Xunit;

namespace ShortLink.UnitTests;

/// <summary>
/// 统计口径日历。
/// <para>
/// 这里钉的是那条「只表现为数字偏小、几乎不会被发现」的错误：
/// 点击写入的 <c>click_date</c> 与统计查询的「今天」必须来自同一个时区。
/// 一旦两边口径不一致，跨日界的那几个小时的数据会被切成两段，今日数字凭空少一截。
/// </para>
/// </summary>
public sealed class StatisticsCalendarTests
{
    private static StatisticsCalendar Build(string timeZone, DateTime utcNow)
        => new(
            new FixedClock(utcNow),
            Options.Create(new StatisticsOptions { TimeZone = timeZone }));

    [Fact]
    public void Default_TimeZone_Should_Be_Utc()
    {
        var calendar = Build("UTC", new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Utc));

        Assert.Equal("UTC", calendar.TimeZoneId);
        Assert.Equal(new DateOnly(2026, 11, 11), calendar.Today);
    }

    [Fact]
    public void Utc_Plus_8_Should_Roll_Over_Before_Utc_Midnight()
    {
        // 北京时间 07:30（UTC 前一天 23:30）：按 UTC 口径会被算成「昨天」，
        // 这正是国内部署「早上八点前点流量算昨天」的根因。
        var instant = new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Utc);
        var calendar = Build("Asia/Shanghai", instant);

        Assert.Equal(new DateOnly(2026, 11, 12), calendar.ToDate(instant));
        Assert.Equal(new DateOnly(2026, 11, 12), calendar.Today);
    }

    [Fact]
    public void Unspecified_Kind_Should_Be_Treated_As_Utc()
    {
        // SQLite 读回的时间是 Unspecified；ConvertTimeFromUtc 遇到它按 UTC 处理，
        // 但一旦有人把 Kind 改成 Local，这里会抛异常。显式覆盖以免回归。
        var calendar = Build("Asia/Shanghai", DateTime.UtcNow);
        var unspecified = new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateOnly(2026, 11, 12), calendar.ToDate(unspecified));
    }

    [Fact]
    public void Negative_Offset_Zone_Should_Map_Backwards()
    {
        // 反方向同样要成立：UTC 00:30 在纽约（UTC-5）还是前一天 19:30。
        var instant = new DateTime(2026, 11, 11, 0, 30, 0, DateTimeKind.Utc);
        var calendar = Build("America/New_York", instant);

        Assert.Equal(new DateOnly(2026, 11, 10), calendar.ToDate(instant));
    }

    [Fact]
    public void Empty_TimeZone_Should_Be_Rejected()
    {
        var options = new StatisticsOptions { TimeZone = "   " };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Analytics:TimeZone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_TimeZone_Should_Be_Rejected_At_Startup()
    {
        var options = new StatisticsOptions { TimeZone = "Mars/Olympus" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Mars/Olympus", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Visitor_Fingerprint_Should_Follow_Statistics_Calendar_Not_Utc_Date()
    {
        // 同一件事的第二个口子：click_date 修好了、指纹还按 UTC 日切，
        // 结果就是「按北京时间的今天」被 UTC 零点劈成两段，同一访客算两次。
        var before = new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Utc); // 北京 11-12 07:30
        var after = before.AddHours(1);                                        // 北京 11-12 08:30
        var calendar = Build("Asia/Shanghai", before);

        var utcBefore = VisitorFingerprint.Compute("1.2.3.4", "UA", DateOnly.FromDateTime(before));
        var utcAfter = VisitorFingerprint.Compute("1.2.3.4", "UA", DateOnly.FromDateTime(after));
        var statsBefore = VisitorFingerprint.Compute("1.2.3.4", "UA", calendar.ToDate(before));
        var statsAfter = VisitorFingerprint.Compute("1.2.3.4", "UA", calendar.ToDate(after));

        Assert.NotEqual(utcBefore, utcAfter);
        Assert.Equal(statsBefore, statsAfter);
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
