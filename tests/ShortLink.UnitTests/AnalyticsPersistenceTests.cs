using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Analytics;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Analytics;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class AnalyticsPersistenceTests
{
    [Fact]
    public async Task Analytics_Should_Use_Link_Counter_And_Exclude_Bots_From_All_UV()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        long linkId;
        await using (var db = database.CreateContext())
        {
            var link = TestData.CreateLink("analytics");
            typeof(Link).GetProperty(nameof(Link.ClickCount))!.SetValue(link, 99L);
            db.Links.Add(link);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;

            var day1 = TestData.Now.Date;
            var day2 = day1.AddDays(1);
            db.ClickEvents.AddRange(
                TestData.CreateClick(linkId, 77, day1.AddHours(1), false, "visitor-a"),
                TestData.CreateClick(linkId, 77, day1.AddHours(2), false, "visitor-a"),
                TestData.CreateClick(linkId, 78, day1.AddHours(3), true, "bot-only"),
                TestData.CreateClick(linkId, 79, day2.AddHours(1), false, "visitor-b"),
                TestData.CreateClick(linkId, 79, day2.AddHours(2), true, "bot-only-today"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            var repository = new AnalyticsRepository(db);
            var from = DateOnly.FromDateTime(TestData.Now.Date);
            var to = from.AddDays(1);
            var stats = await repository.GetLinkStatsAsync(linkId, from, to, TestContext.Current.CancellationToken);

            Assert.Equal(99, stats.TotalClicks);
            Assert.Equal(2, stats.UniqueVisitors);
            Assert.Equal(2, stats.TodayClicks);
            Assert.Equal(2, stats.Trend.Count);
            Assert.Equal((3L, 1L), (stats.Trend[0].Clicks, stats.Trend[0].UniqueVisitors));
            Assert.Equal((2L, 1L), (stats.Trend[1].Clicks, stats.Trend[1].UniqueVisitors));

            var recent = await repository.GetRecentClicksAsync(linkId, 10, TestContext.Current.CancellationToken);
            Assert.All(recent, click => Assert.NotNull(click.RouteId));

            var streamed = new List<ClickLogDto>();
            await foreach (var click in repository.StreamClicksAsync(linkId, from, TestContext.Current.CancellationToken))
            {
                streamed.Add(click);
            }

            Assert.Equal(5, streamed.Count);
            Assert.All(streamed, click => Assert.NotNull(click.RouteId));

            var dashboard = await repository.GetDashboardAsync(1, from, to, TestContext.Current.CancellationToken);
            Assert.Equal(99, dashboard.TotalClicks);
            Assert.Equal(1, dashboard.UniqueVisitorsToday);
            Assert.Equal(1, dashboard.Trend[1].UniqueVisitors);
        }
    }

    [Fact]
    public async Task ConversionRepository_Should_Only_Map_Matching_Unique_Conflict_To_Duplicate()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        long firstId;
        await using (var db = database.CreateContext())
        {
            var repository = new ConversionRepository(db);
            var first = ConversionEvent.Create(1, 10, null, "event-1", "purchase", 1.25m, null, TestData.Now);
            Assert.True(await repository.TryAddAsync(first, TestContext.Current.CancellationToken));
            firstId = first.Id;

            var duplicate = ConversionEvent.Create(1, 10, null, "event-1", "purchase", 99m, null, TestData.Now);
            Assert.False(await repository.TryAddAsync(duplicate, TestContext.Current.CancellationToken));

            var second = ConversionEvent.Create(1, 10, null, "event-2", "purchase", 2.75m, null, TestData.Now);
            Assert.True(await repository.TryAddAsync(second, TestContext.Current.CancellationToken));

            var totals = await repository.GetWorkspaceTotalsAsync(1, TestData.Now.AddDays(-1), TestContext.Current.CancellationToken);
            Assert.Equal((2L, 4m), totals);
            Assert.Equal(2, await repository.GetLinkTotalAsync(10, TestContext.Current.CancellationToken));
        }

        await using (var db = database.CreateContext())
        {
            var repository = new ConversionRepository(db);
            var primaryKeyConflict = ConversionEvent.Create(1, 10, null, "different-event", "purchase", 1m, null, TestData.Now);
            typeof(ConversionEvent).GetProperty(nameof(ConversionEvent.Id))!.SetValue(primaryKeyConflict, firstId);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => repository.TryAddAsync(primaryKeyConflict, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConversionService_Should_Reject_Route_From_Another_Link()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        long linkId;
        long otherRouteId;
        await using (var db = database.CreateContext())
        {
            var domain = ShortDomain.Create("s.example.com", "https", "default", true, true, TestData.Now);
            var link = TestData.CreateLink("conversion");
            link.SetConversionSecret("secret", TestData.Now);
            var otherLink = TestData.CreateLink("other-link");
            db.Domains.Add(domain);
            db.Links.AddRange(link, otherLink);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            linkId = link.Id;
            var route = LinkRoute.Create(otherLink.Id, "other", 1, 100, "https://example.com/other", null, true, TestData.Now);
            db.Routes.Add(route);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            otherRouteId = route.Id;
        }

        await using (var db = database.CreateContext())
        {
            var service = new ConversionService(
                new ConversionRepository(db),
                new LinkRepository(db),
                new DomainRepository(db),
                new RouteRepository(db),
                new TestClock(TestData.Now),
                new AcceptSignatureVerifier(),
                NullLogger<ConversionService>.Instance);
            var request = new ConversionCallbackRequest
            {
                LinkId = linkId,
                RouteId = otherRouteId,
                EventId = "cross-link-route",
                Timestamp = new DateTimeOffset(TestData.Now).ToUnixTimeSeconds(),
            };

            var exception = await Assert.ThrowsAsync<BusinessException>(() => service.HandleAsync(
                request,
                "accepted",
                request.Timestamp.Value.ToString(CultureInfo.InvariantCulture),
                "{}",
                null,
                TestContext.Current.CancellationToken));

            Assert.Equal(BizCodes.ConversionPayloadInvalid, exception.Code);
            Assert.Equal(0, await db.Conversions.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Retention_Should_Record_Success_Only_After_Completion_And_Coordinate_Replicas()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        await using (var db = database.CreateContext())
        {
            db.ClickEvents.Add(TestData.CreateClick(1, null, TestData.Now.AddDays(-100), false, "old"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var service = CreateRetentionService(database, TestData.Now);
        var settings = RetentionSettings();
        Assert.Equal(1, await service.PurgeAsync(TestData.Now, settings, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.PurgeAsync(TestData.Now, settings, TestContext.Current.CancellationToken));

        await using (var db = database.CreateContext())
        {
            var lease = await db.ClickRetentionLeases.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DateOnly.FromDateTime(TestData.Now), lease.LastCompletedDate);
            Assert.Null(lease.OwnerId);
        }
    }

    [Fact]
    public async Task Retention_Should_Not_Record_Success_When_Purge_Fails()
    {
        await using var database = await ClickSqliteTestDatabase.CreateAsync();
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE click_events", TestContext.Current.CancellationToken);
        }

        var service = CreateRetentionService(database, TestData.Now);
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.PurgeAsync(TestData.Now, RetentionSettings(), TestContext.Current.CancellationToken));

        await using (var db = database.CreateContext())
        {
            var lease = await db.ClickRetentionLeases.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(lease.LastCompletedDate);
            Assert.Null(lease.OwnerId);
        }
    }

    private static ClickRetentionService CreateRetentionService(ClickSqliteTestDatabase database, DateTime now)
    {
        var services = new ServiceCollection()
            .AddSingleton(database.Options)
            .AddScoped<ShortLinkDbContext>()
            .BuildServiceProvider();
        return new ClickRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(RetentionSettings()),
            new TestClock(now),
            NullLogger<ClickRetentionService>.Instance);
    }

    private static AnalyticsRetentionOptions RetentionSettings() => new()
    {
        Enabled = true,
        ClickRetentionDays = 30,
        BatchSize = 100,
        MaxRowsPerRun = 1000,
        BatchDelayMilliseconds = 0,
    };

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class AcceptSignatureVerifier : ISignatureVerifier
    {
        public bool Verify(string secret, string payload, string? providedSignature) => true;
    }
}
