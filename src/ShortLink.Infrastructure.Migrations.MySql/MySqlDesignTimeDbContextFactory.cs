using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Infrastructure.Migrations.MySql;

/// <summary>
/// MySQL 设计时上下文。显式指定 <see cref="MySqlServerVersion"/>，生成迁移不需要连接数据库。
/// </summary>
public sealed class MySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShortLinkDbContext>
{
    public ShortLinkDbContext CreateDbContext(string[] args)
    {
        var migrationsAssembly = typeof(MySqlDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ShortLinkDbContext>()
            .UseMySql(
                "Server=127.0.0.1;Port=3306;Database=shortlink;User Id=root;Password=design-time;CharSet=utf8mb4;",
                new MySqlServerVersion(new Version(8, 0, 36)),
                builder => builder.MigrationsAssembly(migrationsAssembly))
            .Options;

        return new ShortLinkDbContext(options);
    }
}
