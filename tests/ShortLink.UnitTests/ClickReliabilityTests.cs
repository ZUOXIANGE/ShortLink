using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Messaging;
using ShortLink.Application.Redirection;
using ShortLink.Contracts;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Analytics;
using ShortLink.Domain.Links;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Messaging.Handlers;
using ShortLink.Infrastructure.Persistence;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class ClickReliabilityTests
{
    [Fact]
    public async Task InMemoryQuota_Should_Enforce_Cap_And_Release_Only_Once()
    {
        var quota = new InMemoryClickQuota(NullLogger<InMemoryClickQuota>.Instance);
        var link = new CachedLink { Id = 42, ClickCount = 2, MaxClicks = 5 };

        var reservations = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => quota.TryReserveAsync(link, TestContext.Current.CancellationToken).AsTask()));

        var acquired = reservations.Where(r => r is not null).Select(r => r!).ToArray();
        Assert.Equal(3, acquired.Length);

        await quota.ReleaseAsync(acquired[0], TestContext.Current.CancellationToken);
        await quota.ReleaseAsync(acquired[0], TestContext.Current.CancellationToken);

        Assert.NotNull(await quota.TryReserveAsync(link, TestContext.Current.CancellationToken));
        Assert.Null(await quota.TryReserveAsync(link, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InMemoryQuota_Completed_Reservation_Must_Not_Be_Refunded_By_Duplicate_Failure()
    {
        var quota = new InMemoryClickQuota(NullLogger<InMemoryClickQuota>.Instance);
        var link = new CachedLink { Id = 43, ClickCount = 0, MaxClicks = 1 };
        var reservation = await quota.TryReserveAsync(link, TestContext.Current.CancellationToken);
        Assert.NotNull(reservation);

        await quota.CompleteAsync(reservation!.ReservationId, link.Id, TestContext.Current.CancellationToken);
        await quota.ReleaseAsync(reservation.ReservationId, link.Id, TestContext.Current.CancellationToken);

        Assert.Null(await quota.TryReserveAsync(link, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClickHandler_Should_Not_Duplicate_Details_Or_Counters_On_Retry()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        long linkId;
        long routeId;
        await using (var db = database.CreateContext())
        {
            var link = TestData.CreateLink("retry-click");
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
            var route = LinkRoute.Create(linkId, "A", 1, 100, "https://example.com/a", null, true, TestData.Now);
            db.Routes.Add(route);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            routeId = route.Id;
        }

        var click = TestData.CreateClick(linkId, routeId, TestData.Now, false, "visitor");
        var message = ClickRecorded.From(click);
        Assert.Equal(click.EventId, message.ClickId);
        Assert.Equal(click.EventId, message.ToEntity().EventId);

        await using (var db = database.CreateContext())
        {
            var quota = new InMemoryClickQuota(NullLogger<InMemoryClickQuota>.Instance);
            await ClickRecordedHandler.Handle(
                [message, message],
                db,
                null!,
                quota,
                null!,
                NullLogger<ClickRecordedBatchMarker>.Instance,
                TestContext.Current.CancellationToken);
            await ClickRecordedHandler.Handle(
                [message],
                db,
                null!,
                quota,
                null!,
                NullLogger<ClickRecordedBatchMarker>.Instance,
                TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.ClickEvents.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Links.Select(l => l.ClickCount).SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Routes.Select(r => r.ClickCount).SingleAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Redirect_Should_Reserve_After_Password_And_Release_For_Failed_Publish()
    {
        var domain = ShortDomain.Create("s.example.com", "https", "default", true, true, TestData.Now);
        typeof(ShortDomain).GetProperty(nameof(ShortDomain.Id))!.SetValue(domain, 1L);
        var link = Link.Create(new LinkDraft
        {
            DomainId = 1,
            WorkspaceId = 1,
            Code = "abc123",
            TargetUrl = "https://example.com",
            PasswordHash = "hash:secret",
            MaxClicks = 1,
        }, TestData.Now);
        typeof(Link).GetProperty(nameof(Link.Id))!.SetValue(link, 10L);
        var quota = new InMemoryClickQuota(NullLogger<InMemoryClickQuota>.Instance);
        var service = new RedirectService(
            new FakeLinkRepository(link),
            new FakeDomainRepository(domain),
            new FakeRouteRepository(),
            new FakeLinkCache(),
            new FakePasswordHasher(),
            new FakeSecurityRepository(),
            new FakeUnitOfWork(),
            new FakeClock(TestData.Now),
            Microsoft.Extensions.Options.Options.Create(new ShortLink.Application.Options.RedirectOptions()),
            Microsoft.Extensions.Options.Options.Create(new ShortLink.Application.Options.SecurityOptions()),
            quota,
            NullLogger<RedirectService>.Instance);
        var context = new RedirectRequestContext(
            null, null, null, null, null, null, "desktop", "Chrome", "Windows", false,
            new Dictionary<string, string>(), "visitor");

        await Assert.ThrowsAsync<BusinessException>(() => service.ResolveAsync(
            "s.example.com", "abc123", "wrong", context, TestContext.Current.CancellationToken));

        var first = await service.ResolveAsync(
            "s.example.com", "abc123", "secret", context, TestContext.Current.CancellationToken);
        Assert.NotNull(first.QuotaReservation);
        await service.ReleaseQuotaAsync(first);

        var second = await service.ResolveAsync(
            "s.example.com", "abc123", "secret", context, TestContext.Current.CancellationToken);
        Assert.NotNull(second.QuotaReservation);
        var capped = await Assert.ThrowsAsync<BusinessException>(() => service.ResolveAsync(
            "s.example.com", "abc123", "secret", context, TestContext.Current.CancellationToken));
        Assert.Equal(BizCodes.LinkClickCapped, capped.Code);
    }
}

public sealed class PagingRegressionTests
{
    [Fact]
    public void Skip_Should_Not_Overflow_And_Deep_Pages_Should_Be_Clamped()
    {
        var request = new TestPageRequest { Page = int.MaxValue, PageSize = int.MaxValue };

        Assert.Equal(PageRequest.MaxPage, request.EffectivePage);
        Assert.Equal(200, request.EffectivePageSize);
        Assert.Equal(1_999_800, request.Skip);
        Assert.True(request.Skip >= 0);
    }

    private sealed class TestPageRequest : PageRequest;
}

internal sealed class ClickSqliteTestDatabase : IAsyncDisposable
{
    private ClickSqliteTestDatabase(SqliteConnection connection, DbContextOptions<ShortLinkDbContext> options)
    {
        Connection = connection;
        Options = options;
    }

    private SqliteConnection Connection { get; }

    internal DbContextOptions<ShortLinkDbContext> Options { get; }

    public static async Task<ClickSqliteTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ShortLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new ShortLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new ClickSqliteTestDatabase(connection, options);
    }

    public ShortLinkDbContext CreateContext() => new(Options);

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

internal static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    public static Link CreateLink(string code, long? maxClicks = null)
        => Link.Create(new LinkDraft
        {
            WorkspaceId = 1,
            DomainId = 1,
            Code = code,
            TargetUrl = "https://example.com",
            MaxClicks = maxClicks,
        }, Now);

    public static ClickEvent CreateClick(
        long linkId,
        long? routeId,
        DateTime clickedAtUtc,
        bool isBot,
        string visitorHash)
        => ClickEvent.Create(
            1,
            linkId,
            1,
            routeId,
            "abc123",
            clickedAtUtc,
            DateOnly.FromDateTime(clickedAtUtc),
            visitorHash,
            "127.0.0.1",
            "test-agent",
            null,
            null,
            null,
            "desktop",
            "Chrome",
            "Windows",
            isBot);
}
