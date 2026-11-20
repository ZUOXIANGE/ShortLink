using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Options;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Geo;
using ShortLink.Infrastructure.Health;
using ShortLink.Infrastructure.Messaging;
using ShortLink.Infrastructure.Options;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;
using ShortLink.Infrastructure.Security;
using ShortLink.Infrastructure.Time;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ShortLink.Infrastructure;

/// <summary>基础设施层注册：数据库 provider 切换、仓储、安全、缓存与点击落库服务。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.Configure<RedirectOptions>(configuration.GetSection(RedirectOptions.SectionName));
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.Configure<AnalyticsRetentionOptions>(
            configuration.GetSection(AnalyticsRetentionOptions.SectionName));
        services.Configure<AnalyticsArchiveOptions>(
            configuration.GetSection(AnalyticsArchiveOptions.SectionName));
        services.Configure<StatisticsOptions>(configuration.GetSection(StatisticsOptions.SectionName));
        services.Configure<GeoIpOptions>(configuration.GetSection(GeoIpOptions.SectionName));
        services.Configure<PartnerApiOptions>(configuration.GetSection(PartnerApiOptions.SectionName));

        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                       ?? new DatabaseOptions();
        if (!string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(database.ConnectionString))
        {
            throw new InvalidOperationException(
                "Database:ConnectionString 不能为空；请通过 Database__ConnectionString 外部注入数据库连接");
        }

        // 统计时区在启动期校验：写错只表现为「今日点击」数字诡异，没人会想到是配置问题。
        var statistics = configuration.GetSection(StatisticsOptions.SectionName).Get<StatisticsOptions>()
                         ?? new StatisticsOptions();
        statistics.Validate();

        // 地理信息库路径同样在启动期校验：配了库却读不到文件，只会表现为「省份一直是空」。
        var geoIp = configuration.GetSection(GeoIpOptions.SectionName).Get<GeoIpOptions>()
                    ?? new GeoIpOptions();
        geoIp.Validate();

        // 第三方凭据的加密主密钥同样在启动期校验：空密钥或长度不对会让所有密文形同明文。
        var partnerApi = configuration.GetSection(PartnerApiOptions.SectionName).Get<PartnerApiOptions>()
                         ?? new PartnerApiOptions();
        partnerApi.Validate();

        // 归档开启但天数非法时启动失败：每日清理会为归档让位，静默跳过等于停止回收。
        var archive = configuration.GetSection(AnalyticsArchiveOptions.SectionName).Get<AnalyticsArchiveOptions>()
                      ?? new AnalyticsArchiveOptions();
        archive.Validate();

        var cache = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>()
                    ?? new CacheOptions();

        // 通过 IServiceProvider 解析 DatabaseOptions：provider 与连接串在运行时才确定，
        // 便于测试宿主与容器环境用配置覆盖，而不用改代码。
        services.AddDbContext<ShortLinkDbContext>((serviceProvider, options) =>
        {
            var dbOptions = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            ConfigureDatabase(options, dbOptions);
        });
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ShortLinkDbContext>());

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IDomainRepository, DomainRepository>();
        services.AddScoped<IShortLinkRepository, LinkRepository>();
        services.AddScoped<IRouteRepository, RouteRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<ISecurityRepository, SecurityRepository>();
        services.AddScoped<IConversionRepository, ConversionRepository>();
        services.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<DatabaseInitializer>();

        services.AddSingleton<IClock, SystemClock>();

        // 认证等短周期读取的小缓存；与 FusionCache 的短链快照缓存相互独立。
        services.AddMemoryCache();

        // 统计口径日历：写入（click_date）与读取（今日/趋势）共用一份换算，注册为单例——
        // TimeZoneInfo 是只读线程安全对象，构造一次即可，热路径上不再查时区表。
        services.AddSingleton<IStatisticsCalendar>(sp => new StatisticsCalendar(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptions<StatisticsOptions>>()));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<ITokenValidator, TokenValidator>();
        services.AddSingleton<ISecretGenerator, CryptoSecretGenerator>();
        services.AddSingleton<ISignatureVerifier, HmacSignatureVerifier>();
        services.AddSingleton<IApiSecretProtector, AesGcmApiSecretProtector>();
        services.AddScoped<IApiClientAuthenticator, ApiClientAuthenticator>();

        // 消息总线：点击批量落库与领域事件都从这里走。
        // 必须在 DbContext 注册之后装配——handler 依赖 scoped 的 ShortLinkDbContext。
        services.AddShortLinkMessaging(configuration);

        // 点击明细生命周期：Quartz 负责调度，服务本身只暴露「执行一次」。
        // 归档默认关闭；开启后按月搬运过期明细，每日清理会自动让位，避免删除未归档数据。
        services.AddSingleton<ClickRetentionService>();
        AddAnalyticsScheduling(services, configuration);

        services.AddSingleton<ILinkCache, FusionLinkCache>();
        services.AddSingleton<ICacheTopology, CacheTopology>();

        // 来源地解析：有离线库就用库（IP → 国家/省份），没有就只读 CDN 头。
        // 两种实现都注册头解析器——库查不到内网地址时要靠它兜底。
        // AddHttpContextAccessor 在这里兜一层（TryAdd 语义，宿主重复注册无害）：
        // 让基础设施层不隐式依赖宿主「记得注册它」。
        services.AddHttpContextAccessor();
        services.AddSingleton<HeaderGeoLocationResolver>();
        if (geoIp.HasDatabase)
        {
            services.AddSingleton<IGeoLocationResolver>(sp => new MaxMindGeoLocationResolver(
                sp.GetRequiredService<IOptions<GeoIpOptions>>(),
                sp.GetRequiredService<HeaderGeoLocationResolver>()));
        }
        else
        {
            services.AddSingleton<IGeoLocationResolver>(sp => sp.GetRequiredService<HeaderGeoLocationResolver>());
        }
        services.AddScoped<ICacheHealthProbe, RedisCacheHealthProbe>();

        var fusionCache = services
            .AddFusionCache()
            .WithDefaultEntryOptions(new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromSeconds(60),
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromMinutes(10),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(300),
            });

        if (!string.IsNullOrWhiteSpace(cache.RedisConnectionString))
        {
            services.AddSingleton<IClickQuota, RedisClickQuota>();
            services.AddSingleton<IApiNonceStore, RedisApiNonceStore>();

            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = cache.RedisConnectionString;
                options.InstanceName = "shortlink:";
            });

            // 多实例部署时必须有背板，否则各节点的本地缓存各自失效。
            fusionCache.WithStackExchangeRedisBackplane(options =>
                options.Configuration = cache.RedisConnectionString);

            // 限流用的连接。刻意懒加载（首次解析时才连），且 AbortOnConnectFail=false：
            // Redis 暂时不可用不应该让进程起不来，限流器会回落到本地窗口。
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(BuildRedisOptions(cache.RedisConnectionString!)));
        }
        else
        {
            services.AddSingleton<IClickQuota, InMemoryClickQuota>();
            services.AddSingleton<IApiNonceStore, MemoryApiNonceStore>();
        }

        return services;
    }

    private static void AddAnalyticsScheduling(IServiceCollection services, IConfiguration configuration)
    {
        var retention = configuration.GetSection(AnalyticsRetentionOptions.SectionName)
                            .Get<AnalyticsRetentionOptions>()
                        ?? new AnalyticsRetentionOptions();
        var archive = configuration.GetSection(AnalyticsArchiveOptions.SectionName)
                          .Get<AnalyticsArchiveOptions>()
                      ?? new AnalyticsArchiveOptions();

        // 日界固定按 UTC：归档/清理的配置项也是 UTC，避免部署时区与 Quartz 默认本地时区错位。
        var retentionHour = Math.Clamp(retention.RunAtHourUtc, 0, 23);
        // 只允许 1~28 号，保证每个月都存在该日期（29~31 号在 2 月会缺一次执行）。
        var archiveDay = Math.Clamp(archive.RunOnDayOfMonth, 1, 28);
        var archiveHour = Math.Clamp(archive.RunAtHourUtc, 0, 23);

        services.AddQuartz(quartz =>
        {
            quartz.ScheduleJob<ClickRetentionJob>(trigger => trigger
                .WithIdentity("click-retention-daily")
                .WithCronSchedule(
                    $"0 0 {retentionHour} * * ?",
                    schedule => schedule.InTimeZone(TimeZoneInfo.Utc)));

            quartz.ScheduleJob<ClickArchiveJob>(trigger => trigger
                .WithIdentity("click-archive-monthly")
                .WithCronSchedule(
                    $"0 0 {archiveHour} {archiveDay} * ?",
                    schedule => schedule.InTimeZone(TimeZoneInfo.Utc)));
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    }

    private static ConfigurationOptions BuildRedisOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        // 连不上时不要阻塞启动：让 multiplexer 在后台重连，调用方按失败处理。
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 2000;
        return options;
    }

    private static void ConfigureDatabase(DbContextOptionsBuilder options, DatabaseOptions database)
    {
        var connectionString = database.ConnectionString;

        switch (database.Provider.Trim().ToLowerInvariant())
        {
            case "sqlite":
                options.UseSqlite(
                    string.IsNullOrWhiteSpace(connectionString) ? "Data Source=shortlink.db" : connectionString,
                    builder => builder.MigrationsAssembly("ShortLink.Infrastructure.Migrations.Sqlite"));
                break;

            case "postgresql":
            case "postgres":
                options.UseNpgsql(
                    connectionString,
                    builder => builder.MigrationsAssembly("ShortLink.Infrastructure.Migrations.PostgreSql"));
                break;

            default:
                var version = Version.TryParse(database.MySqlVersion, out var parsed)
                    ? parsed
                    : new Version(8, 0, 36);
                options.UseMySql(
                    connectionString,
                    new MySqlServerVersion(version),
                    builder => builder.MigrationsAssembly("ShortLink.Infrastructure.Migrations.MySql"));
                break;
        }
    }
}
