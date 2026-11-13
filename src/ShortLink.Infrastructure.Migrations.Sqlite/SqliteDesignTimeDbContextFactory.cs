using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Migrations.Sqlite;

/// <summary>SQLite 设计时上下文（本地开发与集成测试使用）。</summary>
public sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShortLinkDbContext>
{
    public ShortLinkDbContext CreateDbContext(string[] args)
    {
        var migrationsAssembly = typeof(SqliteDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ShortLinkDbContext>()
            .UseSqlite(
                "Data Source=design-time.db",
                builder => builder.MigrationsAssembly(migrationsAssembly))
            .Options;

        return new ShortLinkDbContext(options);
    }
}
