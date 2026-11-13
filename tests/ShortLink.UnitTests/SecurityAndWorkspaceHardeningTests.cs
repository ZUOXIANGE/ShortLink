using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Application.Users;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;
using ShortLink.Infrastructure.Options;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;
using ShortLink.Infrastructure.Security;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class TokenSecurityTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Password_Reset_Role_And_Disable_Should_Increment_TokenVersion()
    {
        var user = User.Create("security", "安全账号", "hash", UserRole.Member, Now);

        user.ChangePassword("hash-2");
        Assert.Equal(1, user.TokenVersion);
        user.ResetPassword("hash-3", Now);
        Assert.Equal(2, user.TokenVersion);
        user.ChangeRole(UserRole.Admin);
        Assert.Equal(3, user.TokenVersion);
        user.Disable();
        Assert.Equal(4, user.TokenVersion);
    }

    [Fact]
    public void Jwt_Should_Contain_Current_TokenVersion()
    {
        var user = User.Create("token", "令牌账号", "hash", UserRole.Member, Now);
        user.ChangePassword("hash-2");
        var service = new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = "ShortLink.Tests",
                Audience = "ShortLink.Tests",
                SigningKey = "unit-tests-signing-key-with-at-least-32-bytes",
                ExpiresMinutes = 720,
            }),
            new FakeClock(Now));

        var token = service.Issue(user, 10, UserRole.Member);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.AccessToken);

        Assert.Equal("1", jwt.GetClaim(TokenClaimNames.TokenVersion).Value);
        Assert.Equal(Now.AddMinutes(720), token.ExpiresAtUtc);
    }

    [Fact]
    public async Task Validator_Should_Check_Legacy_Version_Status_And_Current_Membership()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var db = database.Context;
        var workspace = Workspace.Create("一", "one", true, Now);
        var otherWorkspace = Workspace.Create("二", "two", false, Now);
        db.Workspaces.AddRange(workspace, otherWorkspace);
        await db.SaveChangesAsync();

        var user = User.Create("member", "成员", "hash", UserRole.Member, Now);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var member = WorkspaceMember.Create(workspace.Id, user.Id, UserRole.Member, Now);
        db.WorkspaceMembers.Add(member);
        await db.SaveChangesAsync();

        var validator = new TokenValidator(new UserRepository(db), new WorkspaceRepository(db));

        // 升级前签发的令牌没有版本声明，API 按 0 传入；未改密的账号应继续可用。
        Assert.Equal(
            UserRole.Member,
            await validator.ValidateAsync(user.Id, workspace.Id, 0, TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(user.Id, otherWorkspace.Id, 0, TestContext.Current.CancellationToken));

        user.ChangeRole(UserRole.Admin);
        member.ChangeRole(UserRole.Admin);
        await db.SaveChangesAsync();

        Assert.Null(await validator.ValidateAsync(user.Id, workspace.Id, 0, TestContext.Current.CancellationToken));
        Assert.Equal(
            UserRole.Admin,
            await validator.ValidateAsync(user.Id, workspace.Id, 1, TestContext.Current.CancellationToken));

        user.Disable();
        await db.SaveChangesAsync();
        Assert.Null(await validator.ValidateAsync(user.Id, workspace.Id, 2, TestContext.Current.CancellationToken));

        user.Enable();
        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync();
        Assert.Null(await validator.ValidateAsync(user.Id, workspace.Id, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Production_With_Blank_AdminPassword_Should_Fail_Before_Initialization()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var initializer = new DatabaseInitializer(
            database.Context,
            new FakePasswordHasher(),
            Options.Create(new SeedOptions { AdminPassword = string.Empty }),
            new SecurityTestHostEnvironment { EnvironmentName = Environments.Production },
            NullLogger<DatabaseInitializer>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Seed:AdminPassword", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class LinkAccessPasswordValidationTests
{
    [Theory]
    [InlineData("short1")]
    [InlineData("abcdefgh")]
    [InlineData("12345678")]
    public void Create_Should_Reject_Weak_AccessPassword(string password)
    {
        var result = new CreateLinkRequestValidator().Validate(new CreateLinkRequest
        {
            TargetUrl = "https://example.com",
            Password = password,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateLinkRequest.Password));
    }

    [Fact]
    public void Create_Should_Accept_Strong_AccessPassword()
    {
        var result = new CreateLinkRequestValidator().Validate(new CreateLinkRequest
        {
            TargetUrl = "https://example.com",
            Password = "access123",
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Update_Should_Allow_Clearing_But_Reject_Weak_AccessPassword()
    {
        var validator = new UpdateLinkRequestValidator();
        var clearing = validator.Validate(new UpdateLinkRequest
        {
            TargetUrl = "https://example.com",
            Password = string.Empty,
        });
        var weak = validator.Validate(new UpdateLinkRequest
        {
            TargetUrl = "https://example.com",
            Password = "weak",
        });

        Assert.True(clearing.IsValid);
        Assert.False(weak.IsValid);
        Assert.Contains(weak.Errors, error => error.PropertyName == nameof(UpdateLinkRequest.Password));
    }
}

public sealed class UserWorkspaceIsolationTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Query_Should_Return_Only_Current_Workspace_Members()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var service = CreateService(database.Context, seed);

        var result = await service.QueryAsync(new UserQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Total);
        Assert.Contains(result.Items, user => user.Id == seed.OwnerId);
        Assert.Contains(result.Items, user => user.Id == seed.TargetId);
        Assert.DoesNotContain(result.Items, user => user.Id == seed.ForeignId);
    }

    [Fact]
    public async Task Query_Should_Use_Current_Membership_Role_Instead_Of_Global_Role()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var db = database.Context;
        var seed = await SeedAsync(db);
        var target = await db.Users.SingleAsync(user => user.Id == seed.TargetId);
        var membership = await db.WorkspaceMembers.SingleAsync(member =>
            member.WorkspaceId == seed.WorkspaceId && member.UserId == seed.TargetId);
        target.ChangeRole(UserRole.Owner);
        membership.ChangeRole(UserRole.Viewer);
        await db.SaveChangesAsync();
        var service = CreateService(db, seed);

        var result = await service.QueryAsync(
            new UserQuery { Role = "viewer" },
            TestContext.Current.CancellationToken);

        var projected = Assert.Single(result.Items, user => user.Id == seed.TargetId);
        Assert.Equal("viewer", projected.Role);
        Assert.DoesNotContain(result.Items, user => user.Id == seed.OwnerId);
    }

    [Fact]
    public async Task Update_And_Reset_Should_Not_Add_Or_Modify_Foreign_User()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var db = database.Context;
        var seed = await SeedAsync(db);
        var service = CreateService(db, seed);
        var foreignBefore = await db.Users.AsNoTracking().SingleAsync(user => user.Id == seed.ForeignId);

        var update = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            seed.ForeignId,
            new UpdateUserRequest { DisplayName = "越权修改", Role = "owner" },
            TestContext.Current.CancellationToken));
        var reset = await Assert.ThrowsAsync<BusinessException>(() => service.ResetPasswordAsync(
            seed.ForeignId,
            new ResetPasswordRequest { NewPassword = "Changed123" },
            TestContext.Current.CancellationToken));

        Assert.Equal(BizCodes.UserNotFound, update.Code);
        Assert.Equal(BizCodes.UserNotFound, reset.Code);
        Assert.False(await db.WorkspaceMembers.AnyAsync(member =>
            member.WorkspaceId == seed.WorkspaceId && member.UserId == seed.ForeignId));
        var foreignAfter = await db.Users.AsNoTracking().SingleAsync(user => user.Id == seed.ForeignId);
        Assert.Equal(foreignBefore.DisplayName, foreignAfter.DisplayName);
        Assert.Equal(foreignBefore.PasswordHash, foreignAfter.PasswordHash);
        Assert.Equal(foreignBefore.Role, foreignAfter.Role);
    }

    [Fact]
    public async Task Role_Change_Should_Update_Membership_And_Revoke_Old_Tokens()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var db = database.Context;
        var seed = await SeedAsync(db);
        var service = CreateService(db, seed);

        await service.UpdateAsync(
            seed.TargetId,
            new UpdateUserRequest { Role = "admin" },
            TestContext.Current.CancellationToken);

        var target = await db.Users.SingleAsync(user => user.Id == seed.TargetId);
        var member = await db.WorkspaceMembers.SingleAsync(item =>
            item.WorkspaceId == seed.WorkspaceId && item.UserId == seed.TargetId);
        Assert.Equal(UserRole.Admin, target.Role);
        Assert.Equal(UserRole.Admin, member.Role);
        Assert.Equal(1, target.TokenVersion);
    }

    [Fact]
    public async Task Create_Should_Roll_Back_User_When_Member_Insert_Fails()
    {
        await using var database = await SecuritySqliteDatabase.CreateAsync();
        var db = database.Context;
        var seed = await SeedAsync(db);
        var service = CreateService(db, seed);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_new_member BEFORE INSERT ON workspace_members " +
            "BEGIN SELECT RAISE(ABORT, 'forced member failure'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(
            new CreateUserRequest
            {
                UserName = "atomic-user",
                DisplayName = "原子创建",
                Password = "Atomic123",
                Role = "member",
            },
            TestContext.Current.CancellationToken));

        await using var verification = database.CreateContext();
        Assert.False(await verification.Users.AnyAsync(user => user.UserName == "atomic-user"));
        Assert.Equal(3, await verification.WorkspaceMembers.CountAsync());
    }

    private static UserService CreateService(ShortLinkDbContext db, SeedData seed)
    {
        var users = new UserRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var current = new SecurityTestCurrentUser(seed.OwnerId, seed.WorkspaceId);
        return new UserService(
            users,
            workspaces,
            new FakePasswordHasher(),
            db,
            new FakeClock(Now),
            current,
            new WorkspaceContext(current, workspaces),
            new CreateUserRequestValidator(),
            new UpdateUserRequestValidator(),
            new ResetPasswordRequestValidator());
    }

    private static async Task<SeedData> SeedAsync(ShortLinkDbContext db)
    {
        var workspace = Workspace.Create("当前", "current", true, Now);
        var otherWorkspace = Workspace.Create("其他", "other", false, Now);
        db.Workspaces.AddRange(workspace, otherWorkspace);
        await db.SaveChangesAsync();

        var owner = User.Create("owner", "所有者", "hash:Owner123", UserRole.Owner, Now);
        var target = User.Create("target", "目标", "hash:Target123", UserRole.Member, Now);
        var foreign = User.Create("foreign", "外部", "hash:Foreign123", UserRole.Viewer, Now);
        db.Users.AddRange(owner, target, foreign);
        await db.SaveChangesAsync();
        db.WorkspaceMembers.AddRange(
            WorkspaceMember.Create(workspace.Id, owner.Id, UserRole.Owner, Now),
            WorkspaceMember.Create(workspace.Id, target.Id, UserRole.Member, Now),
            WorkspaceMember.Create(otherWorkspace.Id, foreign.Id, UserRole.Viewer, Now));
        await db.SaveChangesAsync();
        return new SeedData(workspace.Id, owner.Id, target.Id, foreign.Id);
    }

    private sealed record SeedData(long WorkspaceId, long OwnerId, long TargetId, long ForeignId);
}

internal sealed class SecurityTestCurrentUser(long userId, long workspaceId) : ICurrentUser
{
    public long? UserId => userId;
    public string? UserName => "owner";
    public string? Role => "owner";
    public long? WorkspaceId => workspaceId;
    public bool IsAuthenticated => true;
    public UserRole RoleValue => UserRole.Owner;
    public bool IsAdmin => true;
    public bool AtLeast(UserRole required) => RoleValue.AtLeast(required);
}

internal sealed class SecurityTestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "ShortLink.UnitTests";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class SecuritySqliteDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ShortLinkDbContext> _options;

    private SecuritySqliteDatabase(SqliteConnection connection)
    {
        _connection = connection;
        _options = new DbContextOptionsBuilder<ShortLinkDbContext>()
            .UseSqlite(connection)
            .Options;
        Context = new ShortLinkDbContext(_options);
    }

    public ShortLinkDbContext Context { get; }

    public static async Task<SecuritySqliteDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new SecuritySqliteDatabase(connection);
        await database.Context.Database.EnsureCreatedAsync();
        return database;
    }

    public ShortLinkDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
