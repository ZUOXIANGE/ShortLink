using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Options;
using ZiggyCreatures.Caching.Fusion;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class FusionLinkCacheRegressionTests
{
    [Fact]
    public async Task FactoryFailureWithoutSnapshot_ShouldPropagateInsteadOfBecomingMissing()
    {
        await using var fixture = new FusionFixture();
        var linkCache = fixture.CreateLinkCache();

        await Assert.ThrowsAsync<InvalidOperationException>(() => linkCache.GetOrSetLinkAsync(
            1,
            "missing-source",
            _ => throw new InvalidOperationException("database unavailable"),
            TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() => linkCache.GetOrSetDomainAsync(
            "unknown.example.com",
            _ => throw new InvalidOperationException("database unavailable"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredEnabledSnapshot_ShouldNotBeServedWhenSourceFails()
    {
        await using var fixture = new FusionFixture();
        var linkCache = fixture.CreateLinkCache();
        await fixture.Cache.SetAsync(
            "shortlink:link:1:stale",
            new CachedLink { Id = 42, Status = 1 },
            new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMilliseconds(20),
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromMinutes(10),
            },
            token: TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => linkCache.GetOrSetLinkAsync(
            1,
            "stale",
            _ => throw new InvalidOperationException("database unavailable"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearDomains_ShouldEvictHostAndFallbackEntries()
    {
        await using var fixture = new FusionFixture();
        var linkCache = fixture.CreateLinkCache();
        var calls = 0;

        await linkCache.GetOrSetDomainAsync("one.example.com", _ =>
        {
            calls++;
            return Task.FromResult(new CachedDomain(1, "one.example.com", "https"));
        }, TestContext.Current.CancellationToken);
        await linkCache.GetOrSetDomainAsync("unknown.internal", _ =>
        {
            calls++;
            return Task.FromResult(new CachedDomain(1, "one.example.com", "https"));
        }, TestContext.Current.CancellationToken);

        await linkCache.ClearDomainsAsync(TestContext.Current.CancellationToken);
        await linkCache.GetOrSetDomainAsync("one.example.com", _ =>
        {
            calls++;
            return Task.FromResult(new CachedDomain(1, "one.example.com", "https"));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    private sealed class FusionFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public FusionFixture()
        {
            var services = new ServiceCollection();
            services.AddFusionCache();
            _services = services.BuildServiceProvider();
        }

        public IFusionCache Cache => _services.GetRequiredService<IFusionCache>();

        public FusionLinkCache CreateLinkCache() => new(
            Cache,
            Options.Create(new CacheOptions { LinkTtlSeconds = 5, DomainTtlSeconds = 5 }));

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();
    }
}
