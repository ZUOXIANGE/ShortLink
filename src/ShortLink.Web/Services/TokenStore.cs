using System.Text.Json;
using Microsoft.JSInterop;
using ShortLink.Contracts.Auth;

namespace ShortLink.Web.Services;

/// <summary>
/// 令牌与当前用户的内存 + localStorage 双写存储。
/// 刷新页面时从 localStorage 恢复，避免重复登录。
/// </summary>
public sealed class TokenStore(IJSRuntime js)
{
    private const string TokenKey = "shortlink.token";
    private const string UserKey = "shortlink.user";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? Token { get; private set; }

    public UserDto? User { get; private set; }

    public bool Initialized { get; private set; }

    public bool HasToken => !string.IsNullOrEmpty(Token);

    public async Task InitializeAsync()
    {
        if (Initialized)
        {
            return;
        }

        Token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        var userJson = await js.InvokeAsync<string?>("localStorage.getItem", UserKey);
        if (!string.IsNullOrEmpty(userJson))
        {
            try
            {
                User = JsonSerializer.Deserialize<UserDto>(userJson, JsonOptions);
            }
            catch (JsonException)
            {
                User = null;
            }
        }

        Initialized = true;
    }

    public async Task SaveAsync(string token, UserDto user)
    {
        Token = token;
        User = user;
        Initialized = true;

        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        await js.InvokeVoidAsync("localStorage.setItem", UserKey, JsonSerializer.Serialize(user, JsonOptions));
    }

    public async Task ClearAsync()
    {
        Token = null;
        User = null;
        Initialized = true;

        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", UserKey);
    }
}
