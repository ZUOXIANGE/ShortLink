using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Migrations.PostgreSql;

/// <summary>PostgreSQL 设计时上下文。</summary>
public sealed class PostgreSqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShortLinkDbContext>
{
    public ShortLinkDbContext CreateDbContext(string[] args)
    {
        var migrationsAssembly = typeof(PostgreSqlDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ShortLinkDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=shortlink;Username=postgres;Password=design-time",
                builder => builder.MigrationsAssembly(migrationsAssembly))
            .Options;

        return new ShortLinkDbContext(options);
    }
}
