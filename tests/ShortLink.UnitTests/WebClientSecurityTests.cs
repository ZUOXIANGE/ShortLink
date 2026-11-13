using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShortLink.Contracts.Links;
using ShortLink.Web.Services;
using Xunit;

namespace ShortLink.UnitTests;

public sealed class ReturnUrlGuardTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/links?status=enabled")]
    [InlineData("/links/42/stats#trend")]
    public void SafePaths_Should_Be_Preserved(string returnUrl)
        => Assert.Equal(returnUrl, ReturnUrlGuard.GetSafePath(returnUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/")]
    [InlineData("https:evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/")]
    [InlineData("\\evil.example\\")]
    [InlineData("/\\evil.example/")]
    [InlineData("/%2Fevil.example/")]
    [InlineData("/%5Cevil.example/")]
    public void UnsafePaths_Should_Fall_Back_To_Root(string? returnUrl)
        => Assert.Equal("/", ReturnUrlGuard.GetSafePath(returnUrl));
}

public sealed class ApiClientDownloadTests
{
    [Fact]
    public async Task Http200_Json_Unauthorized_Should_Fail_And_Redirect_To_Login()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"code":40101,"msg":"登录状态已失效","data":null}""",
                Encoding.UTF8,
                "application/json"),
        };
        var navigation = new FakeNavigationManager();
        var client = CreateClient(response, navigation);

        var result = await client.ExportLinksAsync(new LinkQuery(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Bytes);
        Assert.Contains("登录状态已失效", result.Error, StringComparison.Ordinal);
        Assert.StartsWith("/login?returnUrl=", navigation.Target, StringComparison.Ordinal);
        var encodedReturnUrl = navigation.Target!["/login?returnUrl=".Length..];
        Assert.Equal("/links", ReturnUrlGuard.GetSafePath(Uri.UnescapeDataString(encodedReturnUrl)));
    }

    [Fact]
    public async Task Csv_Should_Read_Truncation_From_Exposed_Content_Disposition()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("code\ntest\n"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = "links.csv",
        };
        content.Headers.ContentDisposition.Parameters.Add(new NameValueHeaderValue("truncated", "true"));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var client = CreateClient(response, new FakeNavigationManager());

        var result = await client.ExportLinksAsync(new LinkQuery(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Truncated);
        Assert.Equal("links.csv", result.FileName);
    }

    private static ApiClient CreateClient(HttpResponseMessage response, FakeNavigationManager navigation)
    {
        var http = new HttpClient(new StubHttpMessageHandler(response))
        {
            BaseAddress = new Uri("http://api.example/"),
        };
        var authentication = new JwtAuthenticationStateProvider(new TokenStore(new FakeJsRuntime()), navigation);
        return new ApiClient(http, authentication, null!);
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class FakeNavigationManager : NavigationManager
    {
        public FakeNavigationManager()
            => Initialize("http://localhost/", "http://localhost/links");

        public string? Target { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
            => Target = uri;
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
            => ValueTask.FromResult<TValue>(default!);
    }
}
