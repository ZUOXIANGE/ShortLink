using System.Data;
using Microsoft.EntityFrameworkCore;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Domain.Analytics;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Infrastructure.Persistence;

/// <summary>EF Core 上下文。所有列名使用 snake_case，时间统一以 UTC 存储。</summary>
public sealed class ShortLinkDbContext(DbContextOptions<ShortLinkDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();

    public DbSet<ShortDomain> Domains => Set<ShortDomain>();

    public DbSet<Link> Links => Set<Link>();

    public DbSet<LinkRoute> Routes => Set<LinkRoute>();

    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    public DbSet<ClickRetentionLease> ClickRetentionLeases => Set<ClickRetentionLease>();

    public DbSet<ConversionEvent> Conversions => Set<ConversionEvent>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShortLinkDbContext).Assembly);

        // MySQL 默认 utf8mb4 排序规则通常忽略大小写；短码与分支标识必须与 .NET/其他 provider 一样区分大小写。
        if (Database.IsMySql())
        {
            modelBuilder.Entity<Link>().Property(link => link.Code).UseCollation("utf8mb4_bin");
            modelBuilder.Entity<LinkRoute>().Property(route => route.Name).UseCollation("utf8mb4_bin");
        }

        base.OnModelCreating(modelBuilder);
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
        => SaveChangesAsync(cancellationToken);

    async Task IUnitOfWork.ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is not null)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var transaction = await Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await operation(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
