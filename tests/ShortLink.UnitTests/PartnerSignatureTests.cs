using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ShortLink.Application.Options;
using ShortLink.Application.Partner;
using ShortLink.Infrastructure.Security;
using Xunit;

namespace ShortLink.UnitTests;

/// <summary>
/// 第三方签名口径与密钥加密。签名串是外部契约的一部分，
/// 任何格式调整都必须先改这里，避免「文档一种写法、服务端另一种写法」。
/// </summary>
public sealed class PartnerSignatureTests
{
    private const string DevKey = "c2hvcnRsaW5rLWRldi1wYXJ0bmVyLXNlY3JldC0zMmI=";

    [Fact]
    public void Canonical_String_Should_Match_Documented_Format()
    {
        var body = Encoding.UTF8.GetBytes("""{"targetUrl":"https://example.com"}""");

        var canonical = PartnerSignature.Build(
            "post",
            "/api/v1/partner/links",
            [
                new KeyValuePair<string, string?>("status", "enabled"),
                new KeyValuePair<string, string?>("domainId", "1"),
            ],
            body,
            "1730000000",
            "a1b2c3d4e5f6");

        var expected = string.Join(
            '\n',
            "POST",
            "/api/v1/partner/links",
            "domainId=1&status=enabled",
            "1730000000",
            "a1b2c3d4e5f6",
            "720649efe0928cbf00374ce443ab41349443a989ef27a5a0c5743c5767f6d4d7");

        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Query_Order_Should_Not_Change_Canonical_String()
    {
        var first = PartnerSignature.Build(
            "GET", "/api/v1/partner/links",
            [new("a", "1"), new("b", "2")],
            ReadOnlySpan<byte>.Empty, "1", "n");

        var second = PartnerSignature.Build(
            "GET", "/api/v1/partner/links",
            [new("b", "2"), new("a", "1")],
            ReadOnlySpan<byte>.Empty, "1", "n");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Empty_Body_Should_Use_Empty_String_Hash()
    {
        var canonical = PartnerSignature.Build(
            "GET", "/api/v1/partner/links", [], ReadOnlySpan<byte>.Empty, "1", "n");

        Assert.Contains("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", canonical);
    }

    [Fact]
    public void Encode_Should_Use_Percent20_For_Space()
    {
        Assert.Equal("a%20b", PartnerSignature.Encode("a b"));
        Assert.Equal("a%2Bb", PartnerSignature.Encode("a+b"));
    }

    [Fact]
    public void Secret_Protector_Should_Roundtrip_And_Reject_Tampering()
    {
        var protector = new AesGcmApiSecretProtector(
            Options.Create(new PartnerApiOptions { SecretEncryptionKey = DevKey }));

        var secret = "0123456789abcdef0123456789abcdef";
        var cipher = protector.Protect(secret);
        Assert.NotEqual(secret, cipher);
        Assert.Equal(secret, protector.Unprotect(cipher));

        var tampered = Convert.FromBase64String(cipher);
        tampered[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(tampered)));
    }

    [Fact]
    public void Options_Should_Reject_Empty_Or_Short_Key()
    {
        Assert.Throws<InvalidOperationException>(() => new PartnerApiOptions().Validate());

        var shortKey = new PartnerApiOptions
        {
            SecretEncryptionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("too-short")),
        };
        Assert.Throws<InvalidOperationException>(() => shortKey.Validate());

        new PartnerApiOptions { SecretEncryptionKey = DevKey }.Validate();
    }

    [Fact]
    public void Options_Should_Require_Nonce_Ttl_At_Least_Twice_The_Window()
    {
        var options = new PartnerApiOptions
        {
            SecretEncryptionKey = DevKey,
            TimestampToleranceSeconds = 300,
            NonceTtlSeconds = 300,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }
}
