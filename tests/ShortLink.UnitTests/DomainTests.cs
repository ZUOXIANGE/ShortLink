using Xunit;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;

namespace ShortLink.UnitTests;

public sealed class ShortCodeTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void Generate_Should_Return_Code_Of_Requested_Length(int length)
    {
        var code = ShortCode.Generate(length);

        Assert.Equal(length, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, ShortCode.Alphabet));
    }

    [Fact]
    public void Generate_Should_Not_Repeat_Within_Small_Batch()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => ShortCode.Generate()).ToHashSet(StringComparer.Ordinal);

        // 500 次生成的重复率应极低；出现大量重复说明随机源有问题。
        Assert.True(codes.Count >= 495, $"重复过多：{500 - codes.Count}");
    }

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("A_b-9", true)]
    [InlineData("ab", false)]
    [InlineData("api", false)]
    [InlineData("has space", false)]
    [InlineData("中文", false)]
    [InlineData("", false)]
    public void IsValid_Should_Enforce_Charset_Length_And_Reserved_Words(string code, bool expected)
        => Assert.Equal(expected, ShortCode.IsValid(code));

    [Fact]
    public void Slugify_Should_Strip_Non_Ascii_Characters()
        => Assert.Equal("hello-world", ShortCode.Slugify("Hello, World!"));
}

public sealed class ShortLinkDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static Link CreateLink(
        LinkStatus status = LinkStatus.Enabled,
        DateTime? expiresAt = null,
        string? passwordHash = null)
    {
        var link = Link.Create(
            new LinkDraft
            {
                DomainId = 1,
                WorkspaceId = 1,
                Code = "abc123",
                TargetUrl = "https://example.com",
                Title = "示例",
                RedirectType = LinkRedirectType.Temporary302,
                ExpiresAtUtc = expiresAt,
                PasswordHash = passwordHash,
                Enabled = status == LinkStatus.Enabled,
                CreatedByUserId = 1,
            },
            Now);

        return link;
    }

    [Fact]
    public void CheckAccess_Should_Return_Ok_When_Enabled_And_No_Password()
        => Assert.Equal(LinkAccessResult.Ok, CreateLink().CheckAccess(Now, passwordProvided: false));

    [Fact]
    public void CheckAccess_Should_Return_Disabled_When_Stopped()
        => Assert.Equal(
            LinkAccessResult.Disabled,
            CreateLink(LinkStatus.Disabled).CheckAccess(Now, passwordProvided: false));

    [Fact]
    public void CheckAccess_Should_Return_Expired_When_Past_Expiry()
        => Assert.Equal(
            LinkAccessResult.Expired,
            CreateLink(expiresAt: Now.AddMinutes(-1)).CheckAccess(Now, passwordProvided: false));

    [Fact]
    public void CheckAccess_Should_Require_Password_When_Not_Provided()
        => Assert.Equal(
            LinkAccessResult.PasswordRequired,
            CreateLink(passwordHash: "hash").CheckAccess(Now, passwordProvided: false));

    [Fact]
    public void CheckAccess_Should_Return_Ok_When_Password_Provided()
        => Assert.Equal(
            LinkAccessResult.Ok,
            CreateLink(passwordHash: "hash").CheckAccess(Now, passwordProvided: true));

    [Fact]
    public void RegisterClick_Should_Accumulate_Counter_And_Timestamp()
    {
        var link = CreateLink();

        link.RegisterClick(Now, 3);

        Assert.Equal(3, link.ClickCount);
        Assert.Equal(Now, link.LastAccessedAtUtc);
    }

    [Fact]
    public void SetPassword_With_Empty_String_Should_Clear_Password()
    {
        var link = CreateLink(passwordHash: "hash");

        link.SetPassword(string.Empty, Now);

        Assert.False(link.HasPassword);
    }
}

public sealed class ShortDomainTests
{
    [Theory]
    [InlineData("HTTPS://S.Example.COM/path?x=1", "s.example.com")]
    [InlineData("localhost:5080", "localhost:5080")]
    [InlineData("  s.example.com.  ", "s.example.com")]
    public void NormalizeHost_Should_Strip_Scheme_Path_And_Trailing_Dot(string input, string expected)
        => Assert.Equal(expected, ShortDomain.NormalizeHost(input));

    [Theory]
    [InlineData("s.example.com", true)]
    [InlineData("localhost:5080", true)]
    [InlineData("bad_host", false)]
    [InlineData("", false)]
    public void IsValidHost_Should_Validate_Host_Shape(string host, bool expected)
        => Assert.Equal(expected, ShortDomain.IsValidHost(host));

    [Fact]
    public void BuildShortUrl_Should_Use_Configured_Scheme_And_Host()
    {
        var domain = ShortDomain.Create("s.example.com", "https", "主域名", true, true, DateTime.UtcNow);

        Assert.Equal("https://s.example.com/abc123", domain.BuildShortUrl("abc123"));
    }

    [Fact]
    public void NormalizeScheme_Should_Fall_Back_To_Https()
        => Assert.Equal("https", ShortDomain.NormalizeScheme("ftp"));
}
