using ShortLink.Web.Services;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class GeoMapNormalizerTests
{
    [Theory]
    [InlineData("cn", "CN")]
    [InlineData(" CN ", "CN")]
    [InlineData("中国", "CN")]
    [InlineData("中华人民共和国", "CN")]
    [InlineData("China", "CN")]
    [InlineData("china", "CN")]
    [InlineData("US", "US")]
    public void Country_Should_Normalize_To_Iso2(string value, string expected)
    {
        Assert.Equal(expected, GeoMapNormalizer.NormalizeCountry(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("United States")]
    [InlineData("C1")]
    public void Country_Should_Return_Null_When_Not_Iso2(string value)
    {
        Assert.Null(GeoMapNormalizer.NormalizeCountry(value));
    }

    [Theory]
    [InlineData("广东省", "广东")]
    [InlineData("广东", "广东")]
    [InlineData("北京市", "北京")]
    [InlineData("内蒙古自治区", "内蒙古")]
    [InlineData("广西壮族自治区", "广西")]
    [InlineData("新疆维吾尔自治区", "新疆")]
    [InlineData("宁夏回族自治区", "宁夏")]
    [InlineData("西藏自治区", "西藏")]
    [InlineData("香港特别行政区", "香港")]
    [InlineData("澳门特别行政区", "澳门")]
    [InlineData("台湾省", "台湾")]
    [InlineData("Guangdong", "广东")]
    [InlineData("Hong Kong", "香港")]
    [InlineData("Inner Mongolia", "内蒙古")]
    [InlineData("Xinjiang", "新疆")]
    [InlineData(" 内蒙古 自治区 ", "内蒙古")]
    public void Province_Should_Strip_Administrative_Suffix(string value, string expected)
    {
        Assert.Equal(expected, GeoMapNormalizer.NormalizeProvince(value));
    }

    [Fact]
    public void Province_Should_Not_Fuzzy_Match_Prefecture()
    {
        // 海南藏族自治州属于青海，不能被当成海南。
        Assert.Equal("海南藏族自治州", GeoMapNormalizer.NormalizeProvince("海南藏族自治州"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Province_Should_Return_Empty_For_Blank(string? value)
    {
        Assert.Equal(string.Empty, GeoMapNormalizer.NormalizeProvince(value));
    }
}
