using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Domains;
using ShortLink.Application.Links;
using ShortLink.Application.Options;
using ShortLink.Application.Security;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class DomainRouteSecurityRegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DomainWrites_ShouldRequireOwnerInActualDefaultWorkspace()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (defaultWorkspaceId, otherWorkspaceId) = await SeedWorkspacesAsync(db);
        var user = new RegressionCurrentUser { WorkspaceId = defaultWorkspaceId, RoleValue = UserRole.Admin };
        var cache = new RegressionCache();
        var service = CreateDomainService(db, user, cache);

        var adminError = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateAsync(NewDomain("admin.example.com"), TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.Forbidden, adminError.Code);

        user.RoleValue = UserRole.Owner;
        user.WorkspaceId = otherWorkspaceId;
        service = CreateDomainService(db, user, cache);
        var otherWorkspaceError = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateAsync(NewDomain("other.example.com"), TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.Forbidden, otherWorkspaceError.Code);

        user.WorkspaceId = defaultWorkspaceId;
        service = CreateDomainService(db, user, cache);
        var created = await service.CreateAsync(NewDomain("owner.example.com"), TestContext.Current.CancellationToken);

        Assert.True(created.IsDefault);
        Assert.True(created.IsEnabled);
        Assert.Equal(1, cache.ClearDomainsCalls);
    }

    [Fact]
    public async Task DomainList_ShouldScopeLinkCount_AndDisabledHostShouldNotResolve()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long secondaryDomainId;
        string secondaryHost;
        await using (var db = database.CreateContext())
        {
            var (defaultWorkspaceId, otherWorkspaceId) = await SeedWorkspacesAsync(db);
            var defaultDomain = ShortDomain.Create("default.example.com", "https", "默认", true, true, Now);
            var secondary = ShortDomain.Create("secondary.example.com", "https", "第二", false, true, Now);
            db.Domains.AddRange(defaultDomain, secondary);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.Links.AddRange(
                CreateLink(defaultWorkspaceId, secondary.Id, "workspace-one"),
                CreateLink(otherWorkspaceId, secondary.Id, "workspace-two"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            secondaryDomainId = secondary.Id;
            secondaryHost = secondary.Host;

            var user = new RegressionCurrentUser { WorkspaceId = defaultWorkspaceId, RoleValue = UserRole.Owner };
            var service = CreateDomainService(db, user, new RegressionCache());
            var list = await service.ListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, list.Single(d => d.Id == secondaryDomainId).LinkCount);

            await service.UpdateAsync(
                secondaryDomainId,
                new UpdateDomainRequest { Name = "第二", Scheme = "https", IsDefault = false, IsEnabled = false },
                TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            var repository = new DomainRepository(db);
            Assert.Null(await repository.FindByHostAsync(secondaryHost, TestContext.Current.CancellationToken));
            Assert.NotNull(await repository.FindByHostAsync("default.example.com", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DefaultDomainSwitch_ShouldBeSingleAndEnabled()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
        var first = ShortDomain.Create("first.example.com", "https", "第一", true, true, Now);
        var second = ShortDomain.Create("second.example.com", "https", "第二", false, true, Now);
        db.Domains.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = new RegressionCurrentUser { WorkspaceId = defaultWorkspaceId, RoleValue = UserRole.Owner };
        var service = CreateDomainService(db, user, new RegressionCache());
        await service.UpdateAsync(
            second.Id,
            new UpdateDomainRequest { Name = "第二", Scheme = "https", IsDefault = true, IsEnabled = true },
            TestContext.Current.CancellationToken);

        var defaults = await db.Domains.Where(d => d.IsDefault).ToListAsync(TestContext.Current.CancellationToken);
        var defaultDomain = Assert.Single(defaults);
        Assert.Equal(second.Id, defaultDomain.Id);
        Assert.True(defaultDomain.IsEnabled);
        Assert.Equal(1, await db.Domains.CountAsync(d => d.DefaultSlot == 1, TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            second.Id,
            new UpdateDomainRequest { Name = "第二", Scheme = "https", IsDefault = true, IsEnabled = false },
            TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.DomainLastDisabled, error.Code);
    }

    [Fact]
    public async Task RouteReplace_ShouldPreserveIdentityAndHistory_AndScanEveryTarget()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long linkId;
        long routeId;
        long defaultWorkspaceId;
        await using (var db = database.CreateContext())
        {
            (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var domain = ShortDomain.Create("route.example.com", "https", "路由", true, true, Now);
            db.Domains.Add(domain);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var link = CreateLink(defaultWorkspaceId, domain.Id, "route-upsert");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;

            var service = CreateRouteService(db, new RegressionCurrentUser
            {
                WorkspaceId = defaultWorkspaceId,
                RoleValue = UserRole.Owner,
            });
            var saved = await service.ReplaceAsync(linkId, Routes(
                Route("A", "https://a.example.com/old"),
                Route("B", "https://b.example.com/old")), TestContext.Current.CancellationToken);
            routeId = saved.Single(r => r.Name == "A").Id;
            await db.Routes.Where(r => r.Id == routeId).ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ClickCount, 7L),
                TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            var service = CreateRouteService(db, new RegressionCurrentUser
            {
                WorkspaceId = defaultWorkspaceId,
                RoleValue = UserRole.Owner,
            });
            var saved = await service.ReplaceAsync(
                linkId,
                Routes(Route("A", "https://a.example.com/new")),
                TestContext.Current.CancellationToken);

            var route = Assert.Single(saved);
            Assert.Equal(routeId, route.Id);
            Assert.Equal(7, route.ClickCount);
            Assert.Equal("https://a.example.com/new", route.TargetUrl);

            var error = await Assert.ThrowsAsync<BusinessException>(() => service.ReplaceAsync(
                linkId,
                Routes(Route("A", "http://127.0.0.1/private")),
                TestContext.Current.CancellationToken));
            Assert.Equal(BizCodes.LinkTargetRejected, error.Code);
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.SecurityEvents.CountAsync(
                e => e.Type == SecurityEventType.UrlRejected,
                TestContext.Current.CancellationToken));
            Assert.Equal("https://a.example.com/new", await db.Routes.Select(r => r.TargetUrl).SingleAsync(
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConcurrentRouteReplace_ShouldCommitOneCompleteVersionOnly()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long linkId;
        long defaultWorkspaceId;
        await using (var db = database.CreateContext())
        {
            (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var domain = ShortDomain.Create("concurrent-routes.example.com", "https", "路由", true, true, Now);
            db.Domains.Add(domain);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var link = CreateLink(defaultWorkspaceId, domain.Id, "route-race");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
        }

        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        await firstDb.Links.SingleAsync(l => l.Id == linkId, TestContext.Current.CancellationToken);
        await secondDb.Links.SingleAsync(l => l.Id == linkId, TestContext.Current.CancellationToken);

        var firstService = CreateRouteService(firstDb, new RegressionCurrentUser
        {
            WorkspaceId = defaultWorkspaceId,
            RoleValue = UserRole.Owner,
        });
        var secondService = CreateRouteService(secondDb, new RegressionCurrentUser
        {
            WorkspaceId = defaultWorkspaceId,
            RoleValue = UserRole.Owner,
        });

        var results = await Task.WhenAll(
            TryReplace(firstService, linkId, Routes(
                Route("A1", "https://a1.example.com"),
                Route("A2", "https://a2.example.com"))),
            TryReplace(secondService, linkId, Routes(
                Route("B1", "https://b1.example.com"),
                Route("B2", "https://b2.example.com"))));

        Assert.Equal(1, results.Count(success => success));
        await using (var db = database.CreateContext())
        {
            var names = await db.Routes.Where(r => r.LinkId == linkId).Select(r => r.Name).OrderBy(n => n)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.True(
                names.SequenceEqual(["A1", "A2"]) || names.SequenceEqual(["B1", "B2"]),
                $"并发保存混入了不同版本：{string.Join(',', names)}");
        }

        static async Task<bool> TryReplace(RouteService service, long id, SaveLinkRoutesRequest request)
        {
            try
            {
                await service.ReplaceAsync(id, request, TestContext.Current.CancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
            catch (DbException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task ConcurrentDefaultSwitch_ShouldNeverLeaveTwoDefaults()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long firstTargetId;
        long secondTargetId;
        long defaultWorkspaceId;
        await using (var db = database.CreateContext())
        {
            (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var original = ShortDomain.Create("original.example.com", "https", "原默认", true, true, Now);
            var first = ShortDomain.Create("target-one.example.com", "https", "目标一", false, true, Now);
            var second = ShortDomain.Create("target-two.example.com", "https", "目标二", false, true, Now);
            db.Domains.AddRange(original, first, second);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstTargetId = first.Id;
            secondTargetId = second.Id;
        }

        var results = await Task.WhenAll(
            TrySwitch(firstTargetId),
            TrySwitch(secondTargetId));

        Assert.Contains(results, success => success);
        await using (var db = database.CreateContext())
        {
            var selected = Assert.Single(await db.Domains.Where(d => d.IsDefault).ToListAsync(
                TestContext.Current.CancellationToken));
            Assert.True(selected.IsEnabled);
            Assert.Equal(1, await db.Domains.CountAsync(d => d.DefaultSlot == 1, TestContext.Current.CancellationToken));
        }

        async Task<bool> TrySwitch(long id)
        {
            try
            {
                await using var db = database.CreateContext();
                var service = CreateDomainService(db, new RegressionCurrentUser
                {
                    WorkspaceId = defaultWorkspaceId,
                    RoleValue = UserRole.Owner,
                }, new RegressionCache());
                await service.UpdateAsync(id, new UpdateDomainRequest
                {
                    Name = "目标",
                    Scheme = "https",
                    IsDefault = true,
                    IsEnabled = true,
                }, TestContext.Current.CancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
            catch (DbException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task LinkUpdate_ShouldAuthorizeBeforeAudit_AndScanFallback()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
        var domain = ShortDomain.Create("links.example.com", "https", "短链", true, true, Now);
        db.Domains.Add(domain);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var link = CreateLink(defaultWorkspaceId, domain.Id, "fallback-scan", createdByUserId: 2);
        db.Links.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = new RegressionCurrentUser
        {
            UserId = 1,
            WorkspaceId = defaultWorkspaceId,
            RoleValue = UserRole.Member,
        };
        var service = CreateLinkService(db, user, new RegressionCache(), new RegressionSecretGenerator());

        var forbidden = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            link.Id,
            UpdateRequest("http://127.0.0.1/main", null),
            TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.Forbidden, forbidden.Code);
        Assert.Equal(0, await db.SecurityEvents.CountAsync(TestContext.Current.CancellationToken));

        user.RoleValue = UserRole.Owner;
        var rejected = await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(
            link.Id,
            UpdateRequest("https://safe.example.com", "http://127.0.0.1/fallback"),
            TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.LinkTargetRejected, rejected.Code);
        Assert.Equal(1, await db.SecurityEvents.CountAsync(
            e => e.Type == SecurityEventType.UrlRejected,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConversionKey_ShouldReturnSamePersistedSecretToAllConcurrentCallers()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long linkId;
        long defaultWorkspaceId;
        await using (var db = database.CreateContext())
        {
            (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var domain = ShortDomain.Create("conversion.example.com", "https", "转化", true, true, Now);
            db.Domains.Add(domain);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var link = CreateLink(defaultWorkspaceId, domain.Id, "concurrent-key");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
        }

        var secrets = new RegressionSecretGenerator();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = database.CreateContext();
            var user = new RegressionCurrentUser { WorkspaceId = defaultWorkspaceId, RoleValue = UserRole.Owner };
            var service = CreateLinkService(db, user, new RegressionCache(), secrets);
            return await service.GetConversionKeyAsync(linkId, TestContext.Current.CancellationToken);
        }));

        var selectedSecret = Assert.Single(results.Select(r => r.Secret).Distinct());
        await using (var db = database.CreateContext())
        {
            Assert.Equal(selectedSecret, await new LinkRepository(db).GetConversionSecretAsync(
                linkId,
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AbuseReport_ShouldDefaultToReview_AndUseAtomicDisableWhenEnabled()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long linkId;
        long domainId;
        long defaultWorkspaceId;
        await using (var db = database.CreateContext())
        {
            (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var domain = ShortDomain.Create("abuse.example.com", "https", "举报", true, true, Now);
            db.Domains.Add(domain);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var link = CreateLink(defaultWorkspaceId, domain.Id, "abuse-review");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
            domainId = domain.Id;
        }

        var defaultOptions = new SecurityOptions { AbuseReportsPerHourPerIp = 2 };
        Assert.False(defaultOptions.AbuseAutoDisable);
        await using (var db = database.CreateContext())
        {
            var service = CreateSecurityService(db, defaultWorkspaceId, defaultOptions, new RegressionCache());
            await service.ReportAbuseAsync(Report("abuse-review"), "203.0.113.10", "agent", TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(LinkStatus.Enabled, await db.Links.Select(l => l.Status).SingleAsync(
                TestContext.Current.CancellationToken));
        }

        await using (var db = database.CreateContext())
        {
            var cache = new RegressionCache();
            var service = CreateSecurityService(db, defaultWorkspaceId, new SecurityOptions
            {
                AbuseAutoDisable = true,
                AbuseReportsPerHourPerIp = 2,
            }, cache);
            await service.ReportAbuseAsync(Report("abuse-review"), "203.0.113.10", "agent", TestContext.Current.CancellationToken);
            await service.ReportAbuseAsync(Report("abuse-review"), "203.0.113.10", "agent", TestContext.Current.CancellationToken);
            Assert.Equal(1, cache.RemoveLinkCalls);
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(LinkStatus.Disabled, await db.Links.Where(l => l.Id == linkId).Select(l => l.Status).SingleAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.SecurityEvents.CountAsync(
                e => e.Type == SecurityEventType.AbuseReported,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Links.CountAsync(l => l.DomainId == domainId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AbuseQuota_ShouldFailClosedUnderConcurrency()
    {
        await using var database = await RegressionDatabase.CreateAsync();
        long linkId;
        await using (var db = database.CreateContext())
        {
            var (defaultWorkspaceId, _) = await SeedWorkspacesAsync(db);
            var link = CreateLink(defaultWorkspaceId, 1, "quota-race");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
        }

        var accepted = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await using var db = database.CreateContext();
            var report = SecurityEvent.Create(
                SecurityEventType.AbuseReported,
                $"concurrent report {index}",
                1,
                linkId,
                "quota-race",
                "203.0.113.20",
                "agent",
                null,
                Now);
            return await new SecurityRepository(db).TryAddReportWithinQuotaAsync(
                report,
                3,
                Now.AddHours(-1),
                TestContext.Current.CancellationToken);
        }));

        await using (var db = database.CreateContext())
        {
            var stored = await db.SecurityEvents.CountAsync(TestContext.Current.CancellationToken);
            Assert.Equal(accepted.Count(value => value), stored);
            Assert.InRange(stored, 1, 3);
        }
    }

    private static DomainService CreateDomainService(
        ShortLinkDbContext db,
        RegressionCurrentUser user,
        RegressionCache cache)
    {
        var workspaces = new WorkspaceRepository(db);
        return new DomainService(
            new DomainRepository(db),
            new LinkRepository(db),
            workspaces,
            new WorkspaceContext(user, workspaces),
            user,
            cache,
            new RegressionClock(),
            db,
            new CreateDomainRequestValidator(),
            new UpdateDomainRequestValidator());
    }

    private static RouteService CreateRouteService(ShortLinkDbContext db, RegressionCurrentUser user)
    {
        var domains = new DomainRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var workspace = new WorkspaceContext(user, workspaces);
        var options = Options.Create(new SecurityOptions());
        var scanner = new TargetUrlSecurityScanner(
            domains,
            new SecurityRepository(db),
            db,
            workspace,
            new RegressionClock(),
            options);
        return new RouteService(
            new LinkRepository(db),
            new RouteRepository(db),
            db,
            new RegressionCache(),
            new RegressionClock(),
            user,
            workspace,
            options,
            scanner,
            new SaveLinkRoutesRequestValidator());
    }

    private static LinkService CreateLinkService(
        ShortLinkDbContext db,
        RegressionCurrentUser user,
        RegressionCache cache,
        RegressionSecretGenerator secrets)
    {
        var domains = new DomainRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var workspace = new WorkspaceContext(user, workspaces);
        var options = Options.Create(new SecurityOptions());
        var scanner = new TargetUrlSecurityScanner(
            domains,
            new SecurityRepository(db),
            db,
            workspace,
            new RegressionClock(),
            options);
        return new LinkService(
            new LinkRepository(db),
            domains,
            new RouteRepository(db),
            null!,
            new RegressionPasswordHasher(),
            cache,
            new RegressionClock(),
            user,
            workspace,
            db,
            secrets,
            scanner,
            new CreateLinkRequestValidator(),
            new UpdateLinkRequestValidator(),
            new BatchLinkRequestValidator());
    }

    private static SecurityService CreateSecurityService(
        ShortLinkDbContext db,
        long workspaceId,
        SecurityOptions options,
        RegressionCache cache)
    {
        var user = new RegressionCurrentUser { WorkspaceId = workspaceId, RoleValue = UserRole.Viewer };
        return new SecurityService(
            new SecurityRepository(db),
            new LinkRepository(db),
            new DomainRepository(db),
            cache,
            new RegressionClock(),
            new WorkspaceContext(user, new WorkspaceRepository(db)),
            Options.Create(options),
            new AbuseReportRequestValidator());
    }

    private static async Task<(long DefaultWorkspaceId, long OtherWorkspaceId)> SeedWorkspacesAsync(
        ShortLinkDbContext db)
    {
        var defaultWorkspace = Workspace.Create("默认", "default", true, Now);
        var otherWorkspace = Workspace.Create("其他", "other", false, Now);
        db.Workspaces.AddRange(defaultWorkspace, otherWorkspace);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (defaultWorkspace.Id, otherWorkspace.Id);
    }

    private static Link CreateLink(long workspaceId, long domainId, string code, long? createdByUserId = null)
        => Link.Create(new LinkDraft
        {
            WorkspaceId = workspaceId,
            DomainId = domainId,
            Code = code,
            TargetUrl = "https://example.com/landing",
            CreatedByUserId = createdByUserId,
            Enabled = true,
        }, Now);

    private static CreateDomainRequest NewDomain(string host) => new()
    {
        Host = host,
        Name = host,
        Scheme = "https",
        IsEnabled = true,
    };

    private static SaveLinkRoutesRequest Routes(params LinkRouteInput[] routes) => new() { Routes = [..routes] };

    private static LinkRouteInput Route(string name, string target) => new()
    {
        Name = name,
        TargetUrl = target,
        Priority = 1,
        Weight = 100,
        IsEnabled = true,
    };

    private static UpdateLinkRequest UpdateRequest(string target, string? fallback) => new()
    {
        TargetUrl = target,
        FallbackUrl = fallback,
        Enabled = true,
    };

    private static AbuseReportRequest Report(string code) => new()
    {
        Code = code,
        Reason = "这是用于安全回归测试的详细举报原因。",
    };
}

internal sealed class RegressionCurrentUser : ICurrentUser
{
    public long? UserId { get; set; } = 1;
    public string? UserName => "tester";
    public string? Role => RoleValue.ToClaimValue();
    public long? WorkspaceId { get; set; }
    public bool IsAuthenticated => true;
    public UserRole RoleValue { get; set; } = UserRole.Owner;
    public bool IsAdmin => RoleValue.AtLeast(UserRole.Admin);
    public bool AtLeast(UserRole required) => RoleValue.AtLeast(required);
}

internal sealed class RegressionClock : IClock
{
    public DateTime UtcNow => new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
}

internal sealed class RegressionPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"hash:{password}";
    public bool Verify(string passwordHash, string password) => passwordHash == Hash(password);
}

internal sealed class RegressionSecretGenerator : ISecretGenerator
{
    private int _next;
    public string Create(int bytes = 32) => $"secret-{Interlocked.Increment(ref _next):D4}";
}

internal sealed class RegressionCache : ILinkCache
{
    public int ClearDomainsCalls { get; private set; }
    public int RemoveLinkCalls { get; private set; }

    public Task<CachedLink> GetOrSetLinkAsync(
        long domainId,
        string code,
        Func<CancellationToken, Task<CachedLink>> factory,
        CancellationToken cancellationToken)
        => factory(cancellationToken);

    public Task RemoveLinkAsync(long domainId, string code, CancellationToken cancellationToken)
    {
        RemoveLinkCalls++;
        return Task.CompletedTask;
    }

    public Task<CachedDomain> GetOrSetDomainAsync(
        string host,
        Func<CancellationToken, Task<CachedDomain>> factory,
        CancellationToken cancellationToken)
        => factory(cancellationToken);

    public Task RemoveDomainAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ClearDomainsAsync(CancellationToken cancellationToken)
    {
        ClearDomainsCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class RegressionDatabase : IAsyncDisposable
{
    private RegressionDatabase(SqliteConnection keepAlive, DbContextOptions<ShortLinkDbContext> options)
    {
        _keepAlive = keepAlive;
        _options = options;
    }

    private readonly SqliteConnection _keepAlive;
    private readonly DbContextOptions<ShortLinkDbContext> _options;

    public static async Task<RegressionDatabase> CreateAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:regression-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<ShortLinkDbContext>().UseSqlite(connectionString).Options;
        await using var db = new ShortLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new RegressionDatabase(keepAlive, options);
    }

    public ShortLinkDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();
}
