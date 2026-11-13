using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace ShortLink.IntegrationTests;

/// <summary>
/// 容器化集成测试的开关。
/// <para>
/// 默认<b>关闭</b>：PR 路径只跑 SQLite 宿主，保持秒级反馈；要验证 MySQL 方言、Redis 原子限流时
/// 设 <c>SHORTLINK_TEST_CONTAINERS=1</c> 显式开启（本地验证与夜间任务）。
/// 关掉时用例以「跳过」而不是「通过」结束——静默通过等于永远不知道它没跑。
/// </para>
/// </summary>
internal static class ContainerTestGate
{
    private const string VariableName = "SHORTLINK_TEST_CONTAINERS";

    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable(VariableName), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>未开启时动态跳过；开启时什么也不做。</summary>
    public static void SkipUnlessEnabled()
    {
        if (!Enabled)
        {
            Assert.Skip($"{VariableName} 未设为 1，跳过容器化集成测试（需要 Docker）。");
        }
    }
}

/// <summary>
/// 容器化宿主基类：配置口径与 <see cref="ApiFactory"/> 一致，区别只在数据库/缓存是真容器。
/// 刻意不继承 <see cref="ApiFactory"/>——那会把「SQLite 临时文件清理」一起带进来。
/// </summary>
public abstract class ContainerizedApiFactory(string databaseProvider, string databaseConnectionString, string redisConnectionString)
    : WebApplicationFactory<Program>
{
    /// <summary>测试专用种子口令；生产走 Secret，这里只为断言登录可用。</summary>
    internal const string AdminPassword = "It_Admin_123";

    private const string SigningKey = "shortlink-integration-test-signing-key-32b";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Provider", databaseProvider);
        builder.UseSetting("Database:ConnectionString", databaseConnectionString);
        builder.UseSetting("Database:MySqlVersion", "8.4.0");
        builder.UseSetting("Cache:RedisConnectionString", redisConnectionString);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        // 观测导出与限流会干扰宿主启动时序，沿用既有测试口径：关导出、放宽限流。
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("RateLimit:AuthPermitPerMinute", "1000");
        builder.UseSetting("RateLimit:ConversionPermitPerMinute", "1000");
        builder.UseSetting("RateLimit:AbusePermitPerMinute", "1000");
        builder.UseSetting("Mcp:Enabled", "true");
    }
}

/// <summary>
/// MySQL 环境：容器只起一次、宿主只建一次（迁移在集合初始化时跑完，不占用例的执行时间）。
/// 容器与工厂必须由同一个 fixture 持有——xunit 的集合 fixture 之间不能互相注入，
/// 拆成两个 fixture 会拿不到「容器先于工厂」的顺序保证。
/// </summary>
public sealed class MySqlHostFixture : IAsyncLifetime
{
    // 只用于一次性测试容器的口令，不是任何环境的凭据。
    private const string ContainerPassword = "ShortLink_It_123";
    private const string Database = "shortlink";

    private MySqlContainer? _container;

    public MySqlApiFactory? Factory { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!ContainerTestGate.Enabled)
        {
            return;
        }

        _container = new MySqlBuilder()
            .WithImage("mysql:8.4")
            .WithDatabase(Database)
            .WithUsername("shortlink")
            .WithPassword(ContainerPassword)
            .Build();

        await _container.StartAsync();

        // 连接串按项目既有格式拼装：AllowUserVariables / UseAffectedRows / CharSet
        // 是真实部署依赖的参数，测试里省掉就等于没测到真实行为。
        var connectionString =
            $"Server={_container.Hostname};Port={_container.GetMappedPublicPort(3306)};Database={Database};" +
            $"User Id=shortlink;Password={ContainerPassword};" +
            "CharSet=utf8mb4;AllowUserVariables=true;UseAffectedRows=false;";

        Factory = new MySqlApiFactory(connectionString);

        // 主动触发宿主启动：DatabaseInitializer 在这里跑真 MySQL 迁移。
        // 放到用例里做会让第一条用例承担全部迁移耗时，迁移失败也会被埋在业务断言里。
        _ = Factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>MySQL + 真实 MySQL 迁移的宿主。</summary>
public sealed class MySqlApiFactory(string connectionString) : ContainerizedApiFactory(
    databaseProvider: "MySql",
    databaseConnectionString: connectionString,
    redisConnectionString: string.Empty);

/// <summary>PostgreSQL + 真实 PostgreSQL 迁移的宿主。</summary>
public sealed class PostgreSqlApiFactory(string connectionString) : ContainerizedApiFactory(
    databaseProvider: "postgresql",
    databaseConnectionString: connectionString,
    redisConnectionString: string.Empty);

/// <summary>
/// SQLite + Redis 的宿主：补齐「配了 Redis 才走」的分支（背板、分布式限流、Redis 探针）。
/// </summary>
public sealed class RedisApiFactory(string connectionString) : ContainerizedApiFactory(
    databaseProvider: "Sqlite",
    databaseConnectionString: $"Data Source={Path.Combine(Path.GetTempPath(), $"shortlink-it-redis-{Guid.NewGuid():N}.db")}",
    redisConnectionString: connectionString);

/// <summary>
/// PostgreSQL 环境：第三个 provider 的迁移与方言也进同一套回归网。
/// 迁移程序集是独立的一份（<c>ShortLink.Infrastructure.Migrations.PostgreSql</c>），
/// 只测 SQLite/MySQL 覆盖不到它。
/// </summary>
public sealed class PostgreSqlHostFixture : IAsyncLifetime
{
    // 只用于一次性测试容器的口令，不是任何环境的凭据。
    private const string ContainerPassword = "ShortLink_It_123";
    private const string Database = "shortlink";

    private PostgreSqlContainer? _container;

    public PostgreSqlApiFactory? Factory { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!ContainerTestGate.Enabled)
        {
            return;
        }

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase(Database)
            .WithUsername("shortlink")
            .WithPassword(ContainerPassword)
            .Build();

        await _container.StartAsync();

        // 库由容器创建（EF 的 Migrate 会建库，但依赖连到 postgres 库的权限，
        // 容器里直接建好更接近生产：生产也不会让应用去 CREATE DATABASE）。
        var connectionString =
            $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};Database={Database};" +
            $"Username=shortlink;Password={ContainerPassword}";

        Factory = new PostgreSqlApiFactory(connectionString);

        // 主动触发启动，让 PostgreSQL 迁移在集合初始化阶段跑完。
        _ = Factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Redis 环境：同样由单个 fixture 持有容器 + 宿主。</summary>
public sealed class RedisHostFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public RedisApiFactory? Factory { get; private set; }

    /// <summary><c>host:port</c>：既给宿主配置，也给限流器单测直接连。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!ContainerTestGate.Enabled)
        {
            return;
        }

        _container = new RedisBuilder()
            .WithImage("redis:8.6")
            .Build();

        await _container.StartAsync();
        ConnectionString = $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
        Factory = new RedisApiFactory(ConnectionString);
        _ = Factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>MySQL 集合：容器与宿主各一份，供集合内所有用例共享。</summary>
[CollectionDefinition(MySqlCollection.Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlHostFixture>
{
    public const string Name = "mysql";
}

/// <summary>Redis 集合：同上。</summary>
[CollectionDefinition(RedisCollection.Name)]
public sealed class RedisCollection : ICollectionFixture<RedisHostFixture>
{
    public const string Name = "redis";
}

/// <summary>PostgreSQL 集合：同上。</summary>
[CollectionDefinition(PostgreSqlCollection.Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlHostFixture>
{
    public const string Name = "postgresql";
}
