using System.Runtime.CompilerServices;
using System.Text.Json;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Links;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Users;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class ExportServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("=1+1")]
    [InlineData("\t+2")]
    [InlineData("\u2003-3")]
    [InlineData("\u00A0@SUM(1)")]
    [InlineData("\u0001=1+1")]
    [InlineData("\u200B=1+1")]
    [InlineData("\uFEFF=1+1")]
    public async Task LinksCsv_Should_Guard_Formulas_After_Unicode_Whitespace_Or_Controls(string value)
    {
        var domain = CreateDomain();
        var link = CreateLink("formula", target: "https://example.com");
        SetId(link, 9);
        var click = CreateClick(referer: value);
        var service = CreateService(
            new ExportLinkRepository(link),
            new ExportAnalyticsRepository(click),
            domain);
        await using var writer = new StringWriter();

        await service.WriteClicksAsync(9, 30, writer, TestContext.Current.CancellationToken);

        Assert.Contains($",'{value},", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinksCsv_Should_Flush_By_Batch_Instead_Of_Every_Row()
    {
        var domain = CreateDomain();
        var links = Enumerable.Range(0, 501)
            .Select(index => CreateLink($"bulk{index}", target: "https://example.com"))
            .ToArray();
        var service = CreateService(new ExportLinkRepository(links), new ExportAnalyticsRepository(), domain);
        await using var writer = new TrackingTextWriter();

        await service.WriteLinksAsync(new LinkQuery(), writer, TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.FlushCount);
    }

    [Fact]
    public async Task LinkExportMetadata_Should_Report_Deterministic_Truncation()
    {
        var domain = CreateDomain();
        var repository = new ExportLinkRepository { TotalRows = ExportService.MaxRows + 1L };
        var service = CreateService(repository, new ExportAnalyticsRepository(), domain);

        var metadata = await service.GetLinkExportMetadataAsync(
            new LinkQuery { Page = 8, PageSize = 200 },
            TestContext.Current.CancellationToken);

        Assert.True(metadata.Truncated);
        Assert.Equal(ExportService.MaxRows, metadata.RowCount);
        Assert.Equal(ExportService.MaxRows + 1L, metadata.TotalRows);
    }

    [Fact]
    public async Task ClicksCsv_Should_Write_RouteId()
    {
        var domain = CreateDomain();
        var link = CreateLink("routed", target: "https://example.com");
        SetId(link, 9);
        var click = CreateClick(routeId: 42);
        var service = CreateService(
            new ExportLinkRepository(link),
            new ExportAnalyticsRepository(click),
            domain);
        await using var writer = new StringWriter();

        await service.WriteClicksAsync(9, 30, writer, TestContext.Current.CancellationToken);

        var dataRow = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Equal("42", dataRow.Split(',')[2]);
    }

    private static ExportService CreateService(
        ExportLinkRepository links,
        ExportAnalyticsRepository analytics,
        ShortDomain domain)
    {
        var user = new ExportCurrentUser();
        return new ExportService(
            links,
            new FakeDomainRepository(domain),
            analytics,
            new FakeClock(Now),
            new ExportCalendar(),
            user,
            new WorkspaceContext(user, null!));
    }

    private sealed class ExportCalendar : IStatisticsCalendar
    {
        public string TimeZoneId => "UTC";
        public DateOnly Today => DateOnly.FromDateTime(Now);
        public DateOnly ToDate(DateTime utcInstant) => DateOnly.FromDateTime(utcInstant);
    }

    private static ShortDomain CreateDomain()
    {
        var domain = ShortDomain.Create("s.example.com", "https", "默认", isDefault: true, isEnabled: true, Now);
        SetId(domain, 1);
        return domain;
    }

    private static ClickLogDto CreateClick(string? referer = null, long? routeId = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = JsonSerializer.Serialize(new
        {
            id = 1,
            code = "routed",
            routeId,
            clickedAtUtc = Now,
            ip = "127.0.0.1",
            referer,
            isBot = false,
        }, options);
        return JsonSerializer.Deserialize<ClickLogDto>(payload, options)!;
    }

    private static Link CreateLink(string code, string target, string? title = null)
        => Link.Create(new LinkDraft
        {
            WorkspaceId = 1,
            DomainId = 1,
            Code = code,
            TargetUrl = target,
            Title = title,
        }, Now);

    private static void SetId(object entity, long id)
        => entity.GetType().GetProperty("Id")!.SetValue(entity, id);

    private sealed class TrackingTextWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }

    private sealed class ExportCurrentUser : ICurrentUser
    {
        public long? UserId => 1;
        public string? UserName => "admin";
        public string? Role => "owner";
        public long? WorkspaceId => 1;
        public bool IsAuthenticated => true;
        public UserRole RoleValue => UserRole.Owner;
        public bool IsAdmin => true;
        public bool AtLeast(UserRole required) => true;
    }

    private sealed class ExportLinkRepository(params Link[] items) : IShortLinkRepository
    {
        public long TotalRows { get; init; } = items.Length;

        public Task<PagedResult<Link>> QueryAsync(
            LinkQuery query,
            long workspaceId,
            long? currentUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken)
            => Task.FromResult(new PagedResult<Link>(
                items.Take(query.EffectivePageSize).ToList(),
                TotalRows,
                query.EffectivePage,
                query.EffectivePageSize));

        public async IAsyncEnumerable<Link> StreamAsync(
            LinkQuery query,
            long workspaceId,
            long? currentUserId,
            DateTime nowUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task<Link?> FindByIdAsync(long id, CancellationToken cancellationToken)
            => Task.FromResult(items.FirstOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<Link>> FindManyAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Link>>(items.Where(item => ids.Contains(item.Id)).ToList());

        public Task<Link?> FindByCodeAsync(long domainId, string code, CancellationToken cancellationToken)
            => Task.FromResult(items.FirstOrDefault(item => item.DomainId == domainId && item.Code == code));

        public Task<bool> CodeExistsAsync(long domainId, string code, CancellationToken cancellationToken)
            => Task.FromResult(items.Any(item => item.DomainId == domainId && item.Code == code));

        public Task<bool> CodeExistsExceptAsync(
            long domainId,
            string code,
            long exceptId,
            CancellationToken cancellationToken)
            => Task.FromResult(items.Any(item =>
                item.DomainId == domainId && item.Code == code && item.Id != exceptId));

        public Task<LinkCounters> GetCountersAsync(
            long workspaceId,
            DateTime nowUtc,
            CancellationToken cancellationToken)
            => Task.FromResult(new LinkCounters(TotalRows, TotalRows, 0));

        public Task<int> CountByDomainAsync(long domainId, CancellationToken cancellationToken)
            => Task.FromResult(items.Count(item => item.DomainId == domainId));

        public Task<bool> TrySetConversionSecretAsync(
            long id,
            string secret,
            DateTime nowUtc,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<string?> GetConversionSecretAsync(long id, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<bool> DisableIfEnabledAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public void Add(Link link) { }
        public void Remove(Link link) { }
    }

    private sealed class ExportAnalyticsRepository(params ClickLogDto[] clicks) : IAnalyticsRepository
    {
        public async IAsyncEnumerable<ClickLogDto> StreamClicksAsync(
            long linkId,
            DateOnly fromDate,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var click in clicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return click;
                await Task.Yield();
            }
        }

        public Task<LinkClickAggregate> GetLinkStatsAsync(
            long linkId,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DashboardClickAggregate> GetDashboardAsync(
            long workspaceId,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ClickLogDto>> GetRecentClicksAsync(
            long linkId,
            int take,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<VariantClicks>> GetVariantClicksAsync(
            long linkId,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
