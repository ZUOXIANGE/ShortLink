namespace ShortLink.Application.Options;

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

    /// <summary>第三方开放接口：单个 API 凭据每分钟允许的请求数（凭据未单独配置时使用）。</summary>
    public int PartnerPermitPerMinute { get; set; } = 300;
}
