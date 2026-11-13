using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShortLink.Contracts.Auth;

namespace ShortLink.Web.Services;

/// <summary>
/// 基于本地 JWT 的认证状态。令牌只用于构造界面身份，真正的鉴权由 API 判定；
/// 收到 40101 业务码时统一跳转登录页。
/// </summary>
public sealed class JwtAuthenticationStateProvider(TokenStore store, NavigationManager navigation)
    : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await store.InitializeAsync();

        if (!store.HasToken || store.User is null)
        {
            return AnonymousState;
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, store.User.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, store.User.DisplayName),
            new Claim(ClaimTypes.Role, store.User.Role),
            new Claim("user_name", store.User.UserName),
        ], "shortlink-jwt");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task SignInAsync(string token, UserDto user)
    {
        await store.SaveAsync(token, user);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync(bool redirectToLogin = false)
    {
        await store.ClearAsync();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

        if (redirectToLogin && !navigation.Uri.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            var relative = navigation.ToBaseRelativePath(navigation.Uri);
            var localPath = string.IsNullOrWhiteSpace(relative) ? "/" : $"/{relative}";
            navigation.NavigateTo($"/login?returnUrl={Uri.EscapeDataString(localPath)}");
        }
    }
}
