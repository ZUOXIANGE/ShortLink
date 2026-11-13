using ShortLink.Domain.Users;

namespace ShortLink.Application.Abstractions;

/// <summary>可替换时钟，便于测试过期与统计口径。</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// 统计口径的日历：把 UTC 时刻换算成「业务意义上的哪一天」。
/// <para>
/// 存在的理由：点击发生在一瞬间（UTC 时刻），但「今日点击」「按天趋势」问的是
/// 业务所在地的日历日。UTC+8 的部署如果直接用 UTC 日期，早上 8 点之前的点击会算进昨天，
/// 「今日」也会在早上 8 点而非零点切换——一天的报表有三分之一时间是错的。
/// </para>
/// <para>
/// 写入（<c>ClickEvent.ClickDate</c>）与读取（统计查询的日期区间）必须走同一份实现，
/// 否则同一天的数据会被切成两段，而且这种错误只会表现为数字偏小，很难被察觉。
/// </para>
/// </summary>
public interface IStatisticsCalendar
{
    /// <summary>统计口径使用的时区标识（IANA 或 Windows 均可，如 <c>Asia/Shanghai</c>）。</summary>
    string TimeZoneId { get; }

    /// <summary>该时区下的今天。</summary>
    DateOnly Today { get; }

    /// <summary>把 UTC 时刻换算成该时区下的日期。</summary>
    DateOnly ToDate(DateTime utcInstant);

    /// <summary>统计自然日的起点，转换为 UTC 瞬时，供点击与转化共用同一时间窗口。</summary>
    DateTime ToUtcStart(DateOnly date)
        => TimeZoneInfo.ConvertTimeToUtc(
            date.ToDateTime(TimeOnly.MinValue),
            TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
}

/// <summary>当前请求的登录主体。</summary>
public interface ICurrentUser
{
    long? UserId { get; }

    string? UserName { get; }

    /// <summary>角色码值（<c>owner</c> / <c>admin</c> / <c>member</c> / <c>viewer</c>）。</summary>
    string? Role { get; }

    /// <summary>当前工作区；未登录时为空。</summary>
    long? WorkspaceId { get; }

    bool IsAuthenticated { get; }

    /// <summary>解析后的角色；未登录按 <see cref="UserRole.Viewer"/> 处理。</summary>
    UserRole RoleValue { get; }

    /// <summary>是否为工作区管理员（Owner / Admin）。</summary>
    bool IsAdmin { get; }

    /// <summary>是否具备指定级别的角色。</summary>
    bool AtLeast(UserRole required);
}
