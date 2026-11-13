using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;
using ShortLink.Infrastructure.Options;

namespace ShortLink.Infrastructure.Persistence;

/// <summary>
/// 启动期迁移与种子数据。跨实例初始化锁覆盖迁移和种子全过程，种子写入再置于单事务中，
/// 避免多副本空库竞争以及工作区、账号、成员关系只初始化一部分的状态。
/// </summary>
public sealed class DatabaseInitializer(
    ShortLinkDbContext db,
    IPasswordHasher passwordHasher,
    IOptions<SeedOptions> seedOptions,
    IHostEnvironment environment,
    ILogger<DatabaseInitializer> logger)
{
    private const string MySqlLockName = "shortlink:database-initializer";
    private const long PostgreSqlLockId = 4_713_082_641_239_971_137;
    private static readonly SemaphoreSlim SqliteProcessLock = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction() && string.IsNullOrWhiteSpace(seedOptions.Value.AdminPassword))
        {
            throw new InvalidOperationException(
                "Production 必须通过 Seed:AdminPassword 或 Seed__AdminPassword 外部注入非空初始管理员密码");
        }

        await using var initializationLock = await AcquireInitializationLockAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var workspace = await EnsureWorkspaceAsync(now, cancellationToken).ConfigureAwait(false);
        await BackfillWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);

        if (!await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await SeedAdminAsync(workspace, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EnsureMembershipsAsync(workspace, now, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Workspace> EnsureWorkspaceAsync(DateTime now, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces
            .OrderByDescending(w => w.IsDefault)
            .ThenBy(w => w.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (workspace is not null)
        {
            return workspace;
        }

        workspace = Workspace.Create("默认工作区", "default", isDefault: true, now);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("已创建默认工作区 {Workspace}", workspace.Name);
        }
        return workspace;
    }

    /// <summary>旧版本数据（workspace_id = 0）统一归入默认工作区。</summary>
    private async Task BackfillWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var links = await db.Links
            .Where(l => l.WorkspaceId == 0)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.WorkspaceId, workspace.Id), cancellationToken)
            .ConfigureAwait(false);

        var clicks = await db.ClickEvents
            .Where(c => c.WorkspaceId == 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkspaceId, workspace.Id), cancellationToken)
            .ConfigureAwait(false);

        if (links + clicks > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "已把 {Links} 条短链与 {Clicks} 条点击明细归入默认工作区", links, clicks);
        }
    }

    private async Task SeedAdminAsync(
        Workspace workspace,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var seed = seedOptions.Value;
        if (string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            throw new InvalidOperationException(
                "首次初始化必须通过 Seed:AdminPassword 或 Seed__AdminPassword 外部注入非空初始管理员密码");
        }

        var admin = User.Create(
            seed.AdminUserName,
            seed.AdminDisplayName,
            passwordHasher.Hash(seed.AdminPassword),
            UserRole.Owner,
            now);

        db.Users.Add(admin);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.WorkspaceMembers.Add(WorkspaceMember.Create(workspace.Id, admin.Id, UserRole.Owner, now));

        if (!await db.Domains.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            var host = ShortDomain.NormalizeHost(seed.DefaultDomainHost);
            if (ShortDomain.IsValidHost(host))
            {
                db.Domains.Add(ShortDomain.Create(
                    host,
                    seed.DefaultDomainScheme,
                    "本地默认域名",
                    isDefault: true,
                    isEnabled: true,
                    now));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogWarning(
            "已初始化所有者账号 {UserName}（密码来自外部 Seed:AdminPassword 配置），请登录后立即修改密码",
            seed.AdminUserName);
    }

    /// <summary>升级场景：给还没有成员记录的账号补一条，角色沿用账号自身角色。</summary>
    private async Task EnsureMembershipsAsync(
        Workspace workspace,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var userIds = await db.Users.Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspace.Id)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var missing = userIds.Except(existing).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var users = await db.Users
            .Where(u => missing.Contains(u.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var user in users)
        {
            // 首个账号提升为所有者：升级后工作区至少要有一个能管理成员的人。
            var role = existing.Count == 0 && user == users[0] ? UserRole.Owner : user.Role;
            db.WorkspaceMembers.Add(WorkspaceMember.Create(workspace.Id, user.Id, role, now));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("已为 {Count} 个历史账号补齐工作区成员记录", users.Count);
        }
    }

    private async Task<IAsyncDisposable> AcquireInitializationLockAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return await SqliteInitializationLock
                .AcquireAsync(db.Database.GetConnectionString(), cancellationToken)
                .ConfigureAwait(false);
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ExecuteScalarAsync(
                connection,
                $"SELECT GET_LOCK('{MySqlLockName}', 60)",
                cancellationToken).ConfigureAwait(false);
            if (result is null || Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new TimeoutException("等待 MySQL 数据库初始化锁超时");
            }

            return new RelationalInitializationLock(
                connection,
                $"SELECT RELEASE_LOCK('{MySqlLockName}')",
                logger);
        }

        if (provider.Contains("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteNonQueryAsync(
                connection,
                $"SELECT pg_advisory_lock({PostgreSqlLockId})",
                cancellationToken).ConfigureAwait(false);
            return new RelationalInitializationLock(
                connection,
                $"SELECT pg_advisory_unlock({PostgreSqlLockId})",
                logger);
        }

        throw new InvalidOperationException($"数据库 provider '{provider}' 不支持跨实例初始化锁");
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 65;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class RelationalInitializationLock(
        DbConnection connection,
        string releaseSql,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    await ExecuteNonQueryAsync(connection, releaseSql, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "释放数据库初始化锁失败，关闭连接以释放服务端锁");
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>SQLite 用进程内信号量加数据库旁路文件锁，串行化同库初始化。</summary>
    private sealed class SqliteInitializationLock : IAsyncDisposable
    {
        private FileStream? _fileLock;

        private SqliteInitializationLock()
        {
        }

        public static async Task<SqliteInitializationLock> AcquireAsync(
            string? connectionString,
            CancellationToken cancellationToken)
        {
            var instance = new SqliteInitializationLock();
            await SqliteProcessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var builder = new SqliteConnectionStringBuilder(connectionString);
                if (builder.Mode != SqliteOpenMode.Memory
                    && !string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    var databasePath = Path.GetFullPath(builder.DataSource);
                    instance._fileLock = await AcquireFileLockAsync(
                        databasePath + ".initialize.lock",
                        cancellationToken).ConfigureAwait(false);
                }

                return instance;
            }
            catch
            {
                instance._fileLock?.Dispose();
                SqliteProcessLock.Release();
                throw;
            }
        }

        private static async Task<FileStream> AcquireFileLockAsync(
            string lockPath,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException exception) when (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("等待 SQLite 数据库初始化锁超时", exception);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _fileLock?.Dispose();
            SqliteProcessLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
