using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;

namespace ShortLink.Infrastructure.Time;

/// <summary>
/// 统计口径日历。<see cref="TimeZoneInfo"/> 是线程安全的只读对象，构造一次即可复用，
/// 因此注册为单例；每次换算不查表、不分配时区对象。
/// </summary>
public sealed class StatisticsCalendar : IStatisticsCalendar
{
    private readonly IClock _clock;
    private readonly TimeZoneInfo _zone;

    public StatisticsCalendar(IClock clock, IOptions<StatisticsOptions> options)
    {
        _clock = clock;
        TimeZoneId = string.IsNullOrWhiteSpace(options.Value.TimeZone)
            ? "UTC"
            : options.Value.TimeZone.Trim();

        // 正常路径上，非法时区已经在启动期被 Validate() 拦下；这里是兜底，
        // 保证错误信息同样明确（而不是在第一次跳转时才抛 TimeZoneNotFoundException）。
        _zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    public string TimeZoneId { get; }

    public DateOnly Today => ToDate(_clock.UtcNow);

    public DateOnly ToDate(DateTime utcInstant)
    {
        // 三种 Kind 都要能处理：从数据库读回的 SQLite 时间是 Unspecified，
        // 内存里算出来的通常是 Utc，而 ConvertTimeFromUtc 遇到 Local 会直接抛。
        var utc = utcInstant.Kind switch
        {
            DateTimeKind.Utc => utcInstant,
            DateTimeKind.Local => utcInstant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc),
        };

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _zone));
    }
}
