using System.Runtime.CompilerServices;
using Xunit;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Redirection;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;

namespace ShortLink.UnitTests;

internal sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

internal sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"hash:{password}";

    public bool Verify(string passwordHash, string password) => passwordHash == $"hash:{password}";
}

internal sealed class FakeLinkCache : ILinkCache
{
    private readonly Dictionary<string, CachedLink> _links = [];
    private readonly Dictionary<string, CachedDomain> _domains = [];

    public int LinkFactoryCalls { get; private set; }

    public Task<CachedLink> GetOrSetLinkAsync(
        long domainId,
        string code,
        Func<CancellationToken, Task<CachedLink>> factory,
        CancellationToken cancellationToken)
    {
        var key = $"{domainId}:{code}";
        if (_links.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        LinkFactoryCalls++;
        return Store(key);

        async Task<CachedLink> Store(string cacheKey)
        {
            var value = await factory(cancellationToken);
            _links[cacheKey] = value;
            return value;
        }
    }

    public Task RemoveLinkAsync(long domainId, string code, CancellationToken cancellationToken)
    {
        _links.Remove($"{domainId}:{code}");
        return Task.CompletedTask;
    }

    public Task<CachedDomain> GetOrSetDomainAsync(
        string host,
        Func<CancellationToken, Task<CachedDomain>> factory,
        CancellationToken cancellationToken)
        => GetOrAddAsync(_domains, host, factory, cancellationToken);

    public Task RemoveDomainAsync(string host, CancellationToken cancellationToken)
    {
        _domains.Remove(host);
        return Task.CompletedTask;
    }

    public Task ClearDomainsAsync(CancellationToken cancellationToken)
    {
        _domains.Clear();
        return Task.CompletedTask;
    }

    private static async Task<T> GetOrAddAsync<T>(
        Dictionary<string, T> store,
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        if (store.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var value = await factory(cancellationToken);
        store[key] = value;
        return value;
    }
}

internal sealed class FakeDomainRepository(params ShortDomain[] domains) : IDomainRepository
{
    public Task<IReadOnlyList<ShortDomain>> ListAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ShortDomain>>(domains);

    public Task<ShortDomain?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => Task.FromResult(domains.FirstOrDefault(d => d.Id == id));

    public Task<ShortDomain?> FindByHostAsync(string host, CancellationToken cancellationToken)
        => Task.FromResult(domains.FirstOrDefault(d => d.Host == host));

    public Task<ShortDomain?> FindDefaultAsync(CancellationToken cancellationToken)
        => Task.FromResult(domains.FirstOrDefault(d => d.IsDefault && d.IsEnabled));

    public Task<bool> HostExistsAsync(string host, CancellationToken cancellationToken)
        => Task.FromResult(domains.Any(d => d.Host == host));

    public Task<IReadOnlyDictionary<long, long>> CountLinksByDomainAsync(
        long workspaceId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>());

    public Task ClearDefaultAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Add(ShortDomain domain) { }

    public void Remove(ShortDomain domain) { }
}

internal sealed class FakeLinkRepository(params Link[] links) : IShortLinkRepository
{
    public Task<PagedResult<Link>> QueryAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<Link>(links, links.Length, 1, 20));

    public Task<IReadOnlyList<Link>> FindManyAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Link>>(links.Where(l => ids.Contains(l.Id)).ToList());

    public Task<bool> CodeExistsExceptAsync(
        long domainId,
        string code,
        long exceptId,
        CancellationToken cancellationToken)
        => Task.FromResult(links.Any(l => l.DomainId == domainId && l.Code == code && l.Id != exceptId));

    public async IAsyncEnumerable<Link> StreamAsync(
        LinkQuery query,
        long workspaceId,
        long? currentUserId,
        DateTime nowUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var link in links)
        {
            yield return link;
            await Task.Yield();
        }
    }

    public Task<Link?> FindByIdAsync(long id, CancellationToken cancellationToken)
        => Task.FromResult(links.FirstOrDefault(l => l.Id == id));

    public Task<Link?> FindByCodeAsync(long domainId, string code, CancellationToken cancellationToken)
        => Task.FromResult(links.FirstOrDefault(l => l.DomainId == domainId && l.Code == code));

    public Task<bool> CodeExistsAsync(long domainId, string code, CancellationToken cancellationToken)
        => Task.FromResult(links.Any(l => l.DomainId == domainId && l.Code == code));

    public Task<LinkCounters> GetCountersAsync(
        long workspaceId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => Task.FromResult(new LinkCounters(links.Length, links.Length, 0));

    public Task<int> CountByDomainAsync(long domainId, CancellationToken cancellationToken)
        => Task.FromResult(links.Count(l => l.DomainId == domainId));

    public Task<bool> TrySetConversionSecretAsync(
        long id,
        string secret,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var link = links.SingleOrDefault(l => l.Id == id);
        if (link is null || link.HasConversionSecret)
        {
            return Task.FromResult(false);
        }

        link.SetConversionSecret(secret, nowUtc);
        return Task.FromResult(true);
    }

    public Task<string?> GetConversionSecretAsync(long id, CancellationToken cancellationToken)
        => Task.FromResult(links.SingleOrDefault(l => l.Id == id)?.ConversionSecret);

    public Task<bool> DisableIfEnabledAsync(long id, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var link = links.SingleOrDefault(l => l.Id == id);
        if (link is null || link.Status != LinkStatus.Enabled)
        {
            return Task.FromResult(false);
        }

        link.Disable(nowUtc);
        return Task.FromResult(true);
    }

    public void Add(Link link) { }

    public void Remove(Link link) { }
}

internal sealed class FakeRouteRepository(params LinkRoute[] routes) : IRouteRepository
{
    public Task<IReadOnlyList<LinkRoute>> ListByLinkAsync(long linkId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<LinkRoute>>(routes.Where(r => r.LinkId == linkId).ToList());

    public Task<bool> ExistsForLinkAsync(long routeId, long linkId, CancellationToken cancellationToken)
        => Task.FromResult(routes.Any(route => route.Id == routeId && route.LinkId == linkId));

    public Task<IReadOnlyDictionary<long, int>> CountByLinksAsync(
        IReadOnlyList<long> linkIds,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<long, int>>(routes
            .Where(r => linkIds.Contains(r.LinkId))
            .GroupBy(r => r.LinkId)
            .ToDictionary(g => g.Key, g => g.Count()));

    public void Add(LinkRoute route) { }

    public void RemoveRange(IEnumerable<LinkRoute> routes) { }
}

internal sealed class FakeSecurityRepository : ISecurityRepository
{
    public List<ShortLink.Domain.Security.SecurityEvent> Items { get; } = [];

    public Task<PagedResult<ShortLink.Domain.Security.SecurityEvent>> QueryAsync(
        ShortLink.Contracts.Security.SecurityEventQuery query,
        long workspaceId,
        DateTime fromUtc,
        CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<ShortLink.Domain.Security.SecurityEvent>(
            Items, Items.Count, 1, 20));

    public Task<bool> TryAddReportWithinQuotaAsync(
        ShortLink.Domain.Security.SecurityEvent securityEvent,
        int quota,
        DateTime fromUtc,
        CancellationToken cancellationToken)
    {
        var recent = Items.Count(e =>
            e.LinkId == securityEvent.LinkId
            && e.Ip == securityEvent.Ip
            && e.Type == ShortLink.Domain.Security.SecurityEventType.AbuseReported
            && e.CreatedAtUtc >= fromUtc);
        if (recent >= quota)
        {
            return Task.FromResult(false);
        }

        Items.Add(securityEvent);
        return Task.FromResult(true);
    }

    public void Add(ShortLink.Domain.Security.SecurityEvent securityEvent) => Items.Add(securityEvent);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.FromResult(1);
    }

    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
        => operation(cancellationToken);
}

public sealed class RedirectServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ShortDomain DefaultDomain = CreateDefaultDomain();

    private static ShortDomain CreateDefaultDomain()
    {
        var domain = ShortDomain.Create("s.example.com", "https", "默认", isDefault: true, isEnabled: true, Now);
        typeof(ShortDomain).GetProperty(nameof(ShortDomain.Id))!.SetValue(domain, 1L);
        return domain;
    }

    private static Link BuildLink(
        bool enabled = true,
        DateTime? expiresAt = null,
        string? password = null,
        string code = "abc123",
        string target = "https://example.com/landing")
    {
        var link = Link.Create(
            new LinkDraft
            {
                DomainId = 1,
                WorkspaceId = 1,
                Code = code,
                TargetUrl = target,
                RedirectType = LinkRedirectType.Temporary302,
                ExpiresAtUtc = expiresAt,
                PasswordHash = password is null ? null : $"hash:{password}",
                Enabled = enabled,
            },
            Now);

        typeof(Link).GetProperty(nameof(Link.Id))!.SetValue(link, 10L);
        return link;
    }

    private static RedirectService BuildService(
        Link? link,
        ShortDomain? domain = null,
        IClock? clock = null,
        LinkRoute[]? routes = null)
        => new(
            new FakeLinkRepository(link is null ? [] : [link]),
            new FakeDomainRepository(domain ?? DefaultDomain),
            new FakeRouteRepository(routes ?? []),
            new FakeLinkCache(),
            new FakePasswordHasher(),
            new FakeSecurityRepository(),
            new FakeUnitOfWork(),
            clock ?? new FakeClock(Now),
            Microsoft.Extensions.Options.Options.Create(new ShortLink.Application.Options.RedirectOptions()),
            Microsoft.Extensions.Options.Options.Create(new ShortLink.Application.Options.SecurityOptions()),
            new ShortLink.Infrastructure.Analytics.InMemoryClickQuota(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ShortLink.Infrastructure.Analytics.InMemoryClickQuota>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedirectService>.Instance);

    private static RedirectRequestContext Context(string? ip = null, string? userAgent = null)
        => new(ip, userAgent, null, null, null, null, "desktop", "Chrome", "Windows", false,
            new Dictionary<string, string>(), "visitor-1");

    [Fact]
    public async Task Resolve_Should_Return_Target_For_Enabled_Link()
    {
        var service = BuildService(BuildLink());

        var outcome = await service.ResolveAsync("s.example.com", "abc123", null, Context(), CancellationToken.None);

        Assert.Equal("https://example.com/landing", outcome.TargetUrl);
        Assert.Equal(302, outcome.StatusCode);
        Assert.Equal(10, outcome.LinkId);
    }

    [Fact]
    public async Task Resolve_Should_Fall_Back_To_Default_Domain_When_Host_Unknown()
    {
        var service = BuildService(BuildLink());

        var outcome = await service.ResolveAsync("unknown.internal:8080", "abc123", null, Context(), CancellationToken.None);

        Assert.Equal("https://example.com/landing", outcome.TargetUrl);
    }

    [Fact]
    public async Task Resolve_Should_Reject_Disabled_Link()
    {
        var service = BuildService(BuildLink(enabled: false));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "abc123", null, Context(), CancellationToken.None));

        Assert.Equal(BizCodes.LinkDisabled, exception.Code);
    }

    [Fact]
    public async Task Resolve_Should_Reject_Expired_Link()
    {
        var service = BuildService(BuildLink(expiresAt: Now.AddMinutes(-5)));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "abc123", null, Context(), CancellationToken.None));

        Assert.Equal(BizCodes.LinkExpired, exception.Code);
    }

    [Fact]
    public async Task Resolve_Should_Require_Password_Then_Accept_Correct_One()
    {
        var service = BuildService(BuildLink(password: "secret"));

        var required = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "abc123", null, Context(), CancellationToken.None));
        Assert.Equal(BizCodes.LinkPasswordRequired, required.Code);

        var wrong = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "abc123", "wrong", Context(), CancellationToken.None));
        Assert.Equal(BizCodes.LinkPasswordIncorrect, wrong.Code);

        var outcome = await service.ResolveAsync("s.example.com", "abc123", "secret", Context(), CancellationToken.None);
        Assert.Equal("https://example.com/landing", outcome.TargetUrl);
    }

    [Fact]
    public async Task Resolve_Should_Reject_Reserved_Or_Invalid_Code()
    {
        var service = BuildService(BuildLink(code: "abc123"));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "api", null, Context(), CancellationToken.None));

        Assert.Equal(BizCodes.LinkNotFound, exception.Code);
    }

    [Fact]
    public async Task Resolve_Should_Return_NotFound_For_Unknown_Code()
    {
        var service = BuildService(BuildLink());

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => service.ResolveAsync("s.example.com", "zzz999", null, Context(), CancellationToken.None));

        Assert.Equal(BizCodes.LinkNotFound, exception.Code);
    }
}

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_Then_Verify_Should_Succeed()
    {
        var hasher = new ShortLink.Infrastructure.Security.Pbkdf2PasswordHasher();
        var hash = hasher.Hash("Admin@123456");

        Assert.True(hasher.Verify(hash, "Admin@123456"));
        Assert.False(hasher.Verify(hash, "admin@123456"));
    }

    [Fact]
    public void Hash_Should_Produce_Different_Salt_Each_Time()
    {
        var hasher = new ShortLink.Infrastructure.Security.Pbkdf2PasswordHasher();

        Assert.NotEqual(hasher.Hash("same-password"), hasher.Hash("same-password"));
    }

    [Fact]
    public void Verify_Should_Reject_Malformed_Hash()
    {
        var hasher = new ShortLink.Infrastructure.Security.Pbkdf2PasswordHasher();

        Assert.False(hasher.Verify("not-a-hash", "whatever"));
        Assert.False(hasher.Verify(string.Empty, "whatever"));
    }
}

public sealed class UserAgentParserTests
{
    [Fact]
    public void Parse_Should_Detect_WeChat_Mobile()
    {
        var info = ShortLink.Infrastructure.Analytics.UserAgentParser.Parse(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 MicroMessenger/8.0.40 Mobile/15E148");

        Assert.Equal("WeChat", info.Browser);
        Assert.Equal("mobile", info.Device);
        Assert.Equal("iOS", info.OperatingSystem);
        Assert.False(info.IsBot);
    }

    [Fact]
    public void Parse_Should_Detect_Desktop_Chrome()
    {
        var info = ShortLink.Infrastructure.Analytics.UserAgentParser.Parse(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0.0.0 Safari/537.36");

        Assert.Equal("Chrome", info.Browser);
        Assert.Equal("desktop", info.Device);
        Assert.Equal("Windows", info.OperatingSystem);
    }

    [Fact]
    public void Parse_Should_Flag_Bots()
    {
        var info = ShortLink.Infrastructure.Analytics.UserAgentParser.Parse("Googlebot/2.1 (+http://www.google.com/bot.html)");

        Assert.True(info.IsBot);
    }

    [Fact]
    public void VisitorFingerprint_Should_Be_Stable_Within_Same_Day_And_Differ_Across_Days()
    {
        var day1 = ShortLink.Infrastructure.Analytics.VisitorFingerprint.Compute("1.2.3.4", "UA", Today());
        var day1Again = ShortLink.Infrastructure.Analytics.VisitorFingerprint.Compute("1.2.3.4", "UA", Today());
        var day2 = ShortLink.Infrastructure.Analytics.VisitorFingerprint.Compute("1.2.3.4", "UA", Today().AddDays(1));

        Assert.Equal(day1, day1Again);
        Assert.NotEqual(day1, day2);
        Assert.Equal(32, day1.Length);

        static DateOnly Today() => new(2026, 11, 11);
    }
}
