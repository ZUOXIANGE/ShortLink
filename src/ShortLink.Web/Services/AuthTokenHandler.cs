using System.Net.Http.Headers;

namespace ShortLink.Web.Services;

/// <summary>为每个请求附加 Bearer 令牌。</summary>
public sealed class AuthTokenHandler(TokenStore store) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await store.InitializeAsync();

        if (!string.IsNullOrEmpty(store.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", store.Token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
