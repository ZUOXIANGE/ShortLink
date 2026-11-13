using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShortLink.Infrastructure.Geo;
using ShortLink.Infrastructure.Options;
using Xunit;

namespace ShortLink.UnitTests;

/// <summary>
/// CDN 请求头解析。
/// <para>
/// 重点是「头名可配置」与「按顺序取第一个非空」：不同 CDN 注入的头名不同，
/// 而多级代理下常见的形态是「前一个头存在但为空」，此时必须继续往后找，
/// 否则会得到一个空国家而不是有效值。
/// </para>
/// </summary>
public sealed class GeoLocationResolverTests
{
    private static HeaderGeoLocationResolver Build(
        IHeaderDictionary headers,
        GeoIpOptions? options = null)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return new HeaderGeoLocationResolver(
            new StubHttpContextAccessor(context),
            Options.Create(options ?? new GeoIpOptions()));
    }

    [Fact]
    public void Country_Should_Prefer_Cdn_Header_Over_Fallback_Header()
    {
        var resolver = Build(new HeaderDictionary
        {
            ["CF-IPCountry"] = "CN",
            ["X-Country"] = "US",
            ["X-Province"] = "浙江",
        });

        var location = resolver.Resolve("1.2.3.4");

        Assert.Equal("CN", location.Country);
        Assert.Equal("浙江", location.Province);
    }

    [Fact]
    public void Empty_Primary_Header_Should_Fall_Through_To_Next()
    {
        // 多级代理下最常见的形态：头存在但为空。
        var resolver = Build(new HeaderDictionary
        {
            ["CF-IPCountry"] = string.Empty,
            ["X-Country"] = "SG",
        });

        Assert.Equal("SG", resolver.Resolve("1.2.3.4").Country);
    }

    [Fact]
    public void Header_Names_Should_Be_Configurable()
    {
        var resolver = Build(
            new HeaderDictionary
            {
                ["X-Tencent-Region"] = "JP",
                ["X-Tencent-Province"] = "Tokyo",
                ["CF-IPCountry"] = "CN",
            },
            new GeoIpOptions
            {
                CountryHeaders = ["X-Tencent-Region"],
                ProvinceHeaders = ["X-Tencent-Province"],
            });

        var location = resolver.Resolve("1.2.3.4");

        // 只认配置里的头：CF-IPCountry 虽在，但已不在名单内。
        Assert.Equal("JP", location.Country);
        Assert.Equal("Tokyo", location.Province);
    }

    [Fact]
    public void Values_Should_Not_Be_Html_Encoded()
    {
        // 曾经在读取处做了 HtmlEncode，导致带 & 的值永远匹配不上条件路由，
        // 而这类错误只表现为「某条路由从不命中」。编码应发生在渲染处，不在采集处。
        var resolver = Build(new HeaderDictionary { ["X-Province"] = "A&B" });

        Assert.Equal("A&B", resolver.Resolve("1.2.3.4").Province);
    }

    [Fact]
    public void No_Context_Should_Return_Empty()
    {
        var resolver = new HeaderGeoLocationResolver(
            new StubHttpContextAccessor(null),
            Options.Create(new GeoIpOptions()));

        Assert.True(resolver.Resolve("1.2.3.4").IsEmpty);
    }

    [Fact]
    public void Missing_Database_File_Should_Fail_At_Startup()
    {
        var options = new GeoIpOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mmdb"),
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("GeoIp:DatabasePath", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
