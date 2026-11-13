using ShortLink.Application.Abstractions;
using ShortLink.Application.Redirection;
using ShortLink.Domain.Links;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class RouteSelectorTests
{
    private const string Fallback = "https://example.com/fallback";

    private static RouteSnapshot Route(
        long id,
        string name,
        string target,
        int priority = 0,
        int weight = 0,
        RouteConditions? conditions = null,
        bool enabled = true)
        => new(id, name, priority, weight, target, conditions ?? RouteConditions.Empty, enabled);

    private static RouteConditions Where(string field, string op, string value)
        => RouteConditions.Create(RouteMatchMode.All, [new RouteCondition(field, op, value)]);

    private static RouteContext Context(
        string visitor = "v1",
        string? country = null,
        string? device = null,
        string? language = null,
        Dictionary<string, string>? query = null)
        => new(
            1,
            country,
            null,
            device,
            "Chrome",
            "Windows",
            language,
            null,
            query ?? [],
            visitor);

    [Fact]
    public void Select_Should_Return_Fallback_When_No_Routes()
        => Assert.Equal(Fallback, RouteSelector.Select([], Context(), Fallback).TargetUrl);

    [Fact]
    public void Select_Should_Match_Country_Condition()
    {
        var routes = new[]
        {
            Route(1, "CN", "https://cn.example.com", priority: 10, conditions: Where(RouteFields.Country, "eq", "CN")),
            Route(2, "默认", "https://default.example.com", priority: 0),
        };

        var cn = RouteSelector.Select(routes, Context(country: "CN"), Fallback);
        var us = RouteSelector.Select(routes, Context(country: "US"), Fallback);

        Assert.Equal("https://cn.example.com", cn.TargetUrl);
        Assert.Equal("CN", cn.RouteName);
        Assert.Equal("https://default.example.com", us.TargetUrl);
    }

    [Fact]
    public void Select_Should_Prefer_Higher_Priority()
    {
        var routes = new[]
        {
            Route(1, "低", "https://low.example.com", priority: 1, conditions: Where(RouteFields.Device, "eq", "mobile")),
            Route(2, "高", "https://high.example.com", priority: 9, conditions: Where(RouteFields.Device, "eq", "mobile")),
        };

        Assert.Equal("https://high.example.com", RouteSelector.Select(routes, Context(device: "mobile"), Fallback).TargetUrl);
    }

    [Fact]
    public void Select_Should_Support_In_And_Query_Operators()
    {
        var query = new Dictionary<string, string> { ["utm_source"] = "wechat" };

        var routes = new[]
        {
            Route(1, "渠道", "https://channel.example.com", conditions: RouteConditions.Create(
                RouteMatchMode.All,
                [
                    new RouteCondition(RouteFields.Language, "in", "zh-CN,zh"),
                    new RouteCondition(RouteFields.Query, "eq", "utm_source=wechat"),
                ])),
        };

        var hit = RouteSelector.Select(routes, Context(language: "zh-CN", query: query), Fallback);
        var miss = RouteSelector.Select(routes, Context(language: "en-US", query: query), Fallback);

        Assert.Equal("https://channel.example.com", hit.TargetUrl);
        Assert.Equal(Fallback, miss.TargetUrl);
    }

    [Fact]
    public void Select_Should_Split_By_Weight_And_Stay_Stable_Per_Visitor()
    {
        var routes = new[]
        {
            Route(1, "A", "https://a.example.com", priority: 5, weight: 50),
            Route(2, "B", "https://b.example.com", priority: 5, weight: 50),
        };

        var first = RouteSelector.Select(routes, Context(visitor: "visitor-42"), Fallback);
        var again = RouteSelector.Select(routes, Context(visitor: "visitor-42"), Fallback);

        Assert.Equal(first.RouteName, again.RouteName);
        Assert.True(first.IsExperimental);

        var a = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (RouteSelector.Select(routes, Context(visitor: $"visitor-{i}"), Fallback).RouteName == "A")
            {
                a++;
            }
        }

        // 2000 个访客、50/50 权重：允许 ±5 个百分点偏差，足以识别「完全不分流」的实现错误。
        Assert.InRange(a, 900, 1100);
    }

    [Fact]
    public void Select_Should_Skip_Disabled_Routes()
    {
        var routes = new[]
        {
            Route(1, "停用", "https://off.example.com", priority: 10, enabled: false),
        };

        Assert.Equal(Fallback, RouteSelector.Select(routes, Context(), Fallback).TargetUrl);
    }

    [Fact]
    public void ToSnapshot_Should_Disable_Route_With_Invalid_Condition_Json()
    {
        var route = LinkRoute.Create(1, "坏规则", 0, 0, "https://x.example.com", RouteConditions.Empty, true, DateTime.UtcNow);
        typeof(LinkRoute).GetProperty(nameof(LinkRoute.ConditionsJson))!
            .SetValue(route, "{ this is not json");

        var snapshot = route.ToSnapshot();

        Assert.False(snapshot.IsEnabled);
    }

    [Fact]
    public void Condition_Json_Should_RoundTrip()
    {
        var conditions = RouteConditions.Create(
            RouteMatchMode.Any,
            [new RouteCondition(RouteFields.Browser, "eq", "WeChat")]);

        var parsed = RouteConditions.TryParse(conditions.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(RouteMatchMode.Any, parsed!.Match);
        Assert.Single(parsed.Items);
        Assert.Equal("WeChat", parsed.Items[0].Value);
    }
}

public sealed class IpAccessListTests
{
    [Fact]
    public void Contains_Should_Match_Cidr_And_Single_Ip()
    {
        var list = IpAccessList.Parse("10.0.0.0/8, 192.168.1.10\n203.0.113.0/24");

        Assert.True(list.Contains("10.24.5.6"));
        Assert.True(list.Contains("192.168.1.10"));
        Assert.True(list.Contains("203.0.113.99"));
        Assert.False(list.Contains("192.168.1.11"));
        Assert.False(list.Contains((string?)null));
    }

    [Fact]
    public void Contains_Should_Normalize_Ipv4_Mapped_Ipv6()
    {
        var list = IpAccessList.Parse("192.168.0.0/16");

        Assert.True(list.Contains("::ffff:192.168.1.1"));
    }

    [Fact]
    public void Parse_Should_Collect_Invalid_Entries_Without_Failing()
    {
        var list = IpAccessList.Parse("10.0.0.0/8, not-an-ip, 300.1.1.1");

        Assert.Equal(1, list.Count);
        Assert.Equal(2, list.InvalidEntries.Count);
    }

    [Fact]
    public void Empty_List_Should_Not_Match_Anything()
    {
        Assert.True(IpAccessList.Empty.IsEmpty);
        Assert.False(IpAccessList.Empty.Contains("10.0.0.1"));
    }
}

public sealed class UrlScannerTests
{
    [Fact]
    public void Scan_Should_Accept_Regular_Https_Url()
        => Assert.False(UrlScanner.Scan("https://example.com/landing?x=1").IsBlocked);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://10.1.2.3/internal")]
    [InlineData("http://192.168.0.10:8080/")]
    [InlineData("http://localhost:5080/api")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    public void Scan_Should_Block_Dangerous_Targets(string url)
        => Assert.True(UrlScanner.Scan(url).IsBlocked);

    [Fact]
    public void Scan_Should_Block_Target_Pointing_Back_To_Short_Domain()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s.example.com" };

        var result = UrlScanner.Scan("https://s.example.com/abc123", reserved);

        Assert.True(result.IsBlocked);
    }

    [Fact]
    public void Scan_Should_Warn_On_Credentials_In_Url()
    {
        var result = UrlScanner.Scan("https://user:pass@example.com/");

        Assert.False(result.IsBlocked);
        Assert.Single(result.Warnings);
    }
}

public sealed class AccessPolicyTests
{
    private static CachedLink Link(string? allow = null, string? block = null, LinkBotPolicy bot = LinkBotPolicy.Allow)
        => new()
        {
            Id = 1,
            WorkspaceId = 1,
            DomainId = 1,
            Code = "abc123",
            TargetUrl = "https://example.com",
            Status = (int)LinkStatus.Enabled,
            BotPolicy = (int)bot,
            AllowList = IpAccessList.Parse(allow),
            BlockList = IpAccessList.Parse(block),
        };

    [Fact]
    public void Blocklist_Should_Win_Over_Allowlist()
    {
        var link = Link(allow: "10.0.0.0/8", block: "10.1.2.3");

        Assert.Equal(AccessDecision.BlockIp, AccessPolicy.Evaluate(link, "10.1.2.3", isBot: false));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(link, "10.9.9.9", isBot: false));
    }

    [Fact]
    public void Allowlist_Should_Reject_Other_Ip()
    {
        var link = Link(allow: "203.0.113.0/24");

        Assert.Equal(AccessDecision.IpNotAllowed, AccessPolicy.Evaluate(link, "198.51.100.7", isBot: false));
    }

    [Fact]
    public void Bot_Policy_Should_Control_Bots_Only()
    {
        var blocked = Link(bot: LinkBotPolicy.Block);
        var ignored = Link(bot: LinkBotPolicy.IgnoreCount);

        Assert.Equal(AccessDecision.BlockBot, AccessPolicy.Evaluate(blocked, "198.51.100.7", isBot: true));
        Assert.Equal(AccessDecision.IgnoreCount, AccessPolicy.Evaluate(ignored, "198.51.100.7", isBot: true));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(blocked, "198.51.100.7", isBot: false));
    }

    [Fact]
    public void IsWeChatOrQQ_Should_Detect_InApp_Browsers()
    {
        Assert.True(AccessPolicy.IsWeChatOrQQ("Mozilla/5.0 MicroMessenger/8.0.40"));
        Assert.True(AccessPolicy.IsWeChatOrQQ("Mozilla/5.0 QQ/8.9.33.10396"));
        Assert.False(AccessPolicy.IsWeChatOrQQ("Mozilla/5.0 Chrome/126.0.0.0"));
    }
}

public sealed class LinkLifecycleTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static Link Create(DateTime? startsAt = null, long? maxClicks = null)
        => Link.Create(
            new LinkDraft
            {
                DomainId = 1,
                WorkspaceId = 1,
                Code = "abc123",
                TargetUrl = "https://example.com",
                StartsAtUtc = startsAt,
                MaxClicks = maxClicks,
            },
            Now);

    [Fact]
    public void CheckAccess_Should_Return_NotStarted_Before_Start_Time()
        => Assert.Equal(
            LinkAccessResult.NotStarted,
            Create(startsAt: Now.AddHours(1)).CheckAccess(Now, passwordProvided: false));

    [Fact]
    public void CheckAccess_Should_Return_ClickCapped_After_Threshold()
    {
        var link = Create(maxClicks: 2);

        link.RegisterClick(Now, 2);

        Assert.True(link.IsClickCapped);
        Assert.Equal(LinkAccessResult.ClickCapped, link.CheckAccess(Now, passwordProvided: false));
    }

    [Fact]
    public void Update_Should_Ignore_Non_Positive_MaxClicks()
    {
        var link = Create(maxClicks: 5);

        link.Update(
            new LinkDraft { TargetUrl = "https://example.com", MaxClicks = 0, Enabled = true },
            Now);

        Assert.Null(link.MaxClicks);
    }
}
