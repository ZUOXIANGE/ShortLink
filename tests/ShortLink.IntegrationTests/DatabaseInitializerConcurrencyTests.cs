using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShortLink.Infrastructure.Options;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Security;
using Xunit;

namespace ShortLink.IntegrationTests;

public sealed class DatabaseInitializerConcurrencyTests
{
    [Fact]
    public async Task Two_Instances_Should_Initialize_Empty_Database_Exactly_Once()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"shortlink-init-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        try
        {
            await using var firstDb = CreateContext(connectionString);
            await using var secondDb = CreateContext(connectionString);
            var first = CreateInitializer(firstDb);
            var second = CreateInitializer(secondDb);

            await Task.WhenAll(
                first.InitializeAsync(TestContext.Current.CancellationToken),
                second.InitializeAsync(TestContext.Current.CancellationToken));

            await using var verification = CreateContext(connectionString);
            Assert.Equal(1, await verification.Workspaces.CountAsync());
            Assert.Equal(1, await verification.Users.CountAsync());
            Assert.Equal(1, await verification.WorkspaceMembers.CountAsync());
            Assert.Equal(1, await verification.Domains.CountAsync());
        }
        finally
        {
            // Windows 下 SQLite 连接池会保留文件句柄；先清池再清理临时测试库。
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + ".initialize.lock");
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    private static ShortLinkDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<ShortLinkDbContext>()
            .UseSqlite(connectionString, builder => builder.MigrationsAssembly("ShortLink.Infrastructure.Migrations.Sqlite"))
            .Options);

    private static DatabaseInitializer CreateInitializer(ShortLinkDbContext db)
        => new(
            db,
            new Pbkdf2PasswordHasher(),
            Options.Create(new SeedOptions
            {
                AdminUserName = "admin",
                AdminPassword = "Concurrent123",
                AdminDisplayName = "并发初始化",
                DefaultDomainHost = "localhost",
                DefaultDomainScheme = "http",
            }),
            new InitializationTestEnvironment(),
            NullLogger<DatabaseInitializer>.Instance);

    private sealed class InitializationTestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ShortLink.IntegrationTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
