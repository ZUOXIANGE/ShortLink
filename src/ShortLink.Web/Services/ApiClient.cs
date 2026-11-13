using System.Net.Http.Json;
using System.Text.Json;
using MudBlazor;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Contracts.Users;

namespace ShortLink.Web.Services;

/// <summary>
/// 统一 API 客户端：按 <c>code</c> 判定成败（HTTP 永远 200，不能用 response.ok），
/// 40101 视为登录失效，直接清理令牌并跳转登录页。
/// </summary>
public sealed class ApiClient(
    HttpClient http,
    JwtAuthenticationStateProvider authStateProvider,
    ISnackbar snackbar)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApiResponse<LoginResponse>> LoginAsync(string userName, string password)
        => await PostAsync<LoginResponse>("api/v1/auth/login", new LoginRequest
        {
            UserName = userName,
            Password = password,
        }).ConfigureAwait(false);

    public Task<ApiResponse<UserDto>> GetCurrentUserAsync()
        => GetAsync<UserDto>("api/v1/auth/me");

    public Task<ApiResponse<object?>> ChangePasswordAsync(string oldPassword, string newPassword)
        => PostAsync<object?>("api/v1/auth/change-password", new ChangePasswordRequest
        {
            OldPassword = oldPassword,
            NewPassword = newPassword,
        });

    public Task<ApiResponse<IReadOnlyList<DomainDto>>> GetDomainsAsync()
        => GetAsync<IReadOnlyList<DomainDto>>("api/v1/domains");

    public Task<ApiResponse<DomainDto>> CreateDomainAsync(CreateDomainRequest request)
        => PostAsync<DomainDto>("api/v1/domains", request);

    public Task<ApiResponse<DomainDto>> UpdateDomainAsync(long id, UpdateDomainRequest request)
        => SendAsync<DomainDto>(HttpMethod.Put, $"api/v1/domains/{id}", request);

    public Task<ApiResponse<object?>> DeleteDomainAsync(long id)
        => SendAsync<object?>(HttpMethod.Delete, $"api/v1/domains/{id}", null);

    public Task<ApiResponse<PagedResult<LinkDto>>> QueryLinksAsync(
        LinkQuery query,
        CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<LinkDto>>($"api/v1/links?{BuildLinkQuery(query)}", cancellationToken);

    /// <summary>下载短链列表 CSV。响应体不是信封，成功时直接拿字节，失败时才解析信封。</summary>
    public Task<DownloadResult> ExportLinksAsync(LinkQuery query, CancellationToken cancellationToken = default)
        => DownloadAsync($"api/v1/links/export?{BuildLinkQuery(query)}", cancellationToken);

    public Task<DownloadResult> ExportClicksAsync(long id, int days, CancellationToken cancellationToken = default)
        => DownloadAsync($"api/v1/links/{id}/clicks/export?days={days}", cancellationToken);

    public Task<ApiResponse<LinkDto>> CreateLinkAsync(CreateLinkRequest request)
        => PostAsync<LinkDto>("api/v1/links", request);

    public Task<ApiResponse<LinkDto>> UpdateLinkAsync(long id, UpdateLinkRequest request)
        => SendAsync<LinkDto>(HttpMethod.Put, $"api/v1/links/{id}", request);

    public Task<ApiResponse<LinkDto>> ToggleLinkAsync(long id)
        => PostAsync<LinkDto>($"api/v1/links/{id}/toggle", null);

    public Task<ApiResponse<object?>> DeleteLinkAsync(long id)
        => SendAsync<object?>(HttpMethod.Delete, $"api/v1/links/{id}", null);

    public Task<ApiResponse<BatchResult>> BatchAsync(BatchLinkRequest request)
        => PostAsync<BatchResult>("api/v1/links/batch", request);

    public Task<ApiResponse<IReadOnlyList<LinkRouteDto>>> GetRoutesAsync(long id)
        => GetAsync<IReadOnlyList<LinkRouteDto>>($"api/v1/links/{id}/routes");

    public Task<ApiResponse<IReadOnlyList<LinkRouteDto>>> SaveRoutesAsync(long id, SaveLinkRoutesRequest request)
        => SendAsync<IReadOnlyList<LinkRouteDto>>(HttpMethod.Put, $"api/v1/links/{id}/routes", request);

    public Task<ApiResponse<RouteTestResult>> TestRoutesAsync(long id, RouteTestRequest request)
        => PostAsync<RouteTestResult>($"api/v1/links/{id}/routes/test", request);

    public Task<ApiResponse<ConversionKeyDto>> GetConversionKeyAsync(long id)
        => GetAsync<ConversionKeyDto>($"api/v1/links/{id}/conversion-key");

    public Task<ApiResponse<LinkStatsDto>> GetLinkStatsAsync(long id, int days)
        => GetAsync<LinkStatsDto>($"api/v1/links/{id}/stats?days={days}");

    public Task<ApiResponse<IReadOnlyList<ClickLogDto>>> GetRecentClicksAsync(long id, int take)
        => GetAsync<IReadOnlyList<ClickLogDto>>($"api/v1/links/{id}/clicks?take={take}");

    public Task<ApiResponse<DashboardOverviewDto>> GetDashboardAsync(int days)
        => GetAsync<DashboardOverviewDto>($"api/v1/dashboard/overview?days={days}");

    public Task<ApiResponse<PagedResult<SecurityEventDto>>> QuerySecurityEventsAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={query.EffectivePage}",
            $"pageSize={query.EffectivePageSize}",
        };

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            parameters.Add($"type={Uri.EscapeDataString(query.Type)}");
        }

        if (query.LinkId is { } linkId)
        {
            parameters.Add($"linkId={linkId}");
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            parameters.Add($"code={Uri.EscapeDataString(query.Code)}");
        }

        if (query.Days is { } days)
        {
            parameters.Add($"days={days}");
        }

        return GetAsync<PagedResult<SecurityEventDto>>(
            $"api/v1/security/events?{string.Join('&', parameters)}",
            cancellationToken);
    }

    public Task<ApiResponse<PagedResult<ManagedUserDto>>> QueryUsersAsync(
        UserQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={query.EffectivePage}",
            $"pageSize={query.EffectivePageSize}",
        };

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            parameters.Add($"keyword={Uri.EscapeDataString(query.Keyword)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            parameters.Add($"role={Uri.EscapeDataString(query.Role)}");
        }

        if (query.IsEnabled is { } enabled)
        {
            parameters.Add($"isEnabled={enabled.ToString().ToLowerInvariant()}");
        }

        return GetAsync<PagedResult<ManagedUserDto>>(
            $"api/v1/users?{string.Join('&', parameters)}",
            cancellationToken);
    }

    public Task<ApiResponse<ManagedUserDto>> CreateUserAsync(CreateUserRequest request)
        => PostAsync<ManagedUserDto>("api/v1/users", request);

    public Task<ApiResponse<ManagedUserDto>> UpdateUserAsync(long id, UpdateUserRequest request)
        => SendAsync<ManagedUserDto>(HttpMethod.Put, $"api/v1/users/{id}", request);

    public Task<ApiResponse<object?>> ResetPasswordAsync(long id, ResetPasswordRequest request)
        => PostAsync<object?>($"api/v1/users/{id}/reset-password", request);

    private static string BuildLinkQuery(LinkQuery query)
    {
        var parameters = new List<string>
        {
            $"page={query.EffectivePage}",
            $"pageSize={query.EffectivePageSize}",
            $"sort={Uri.EscapeDataString(query.Sort ?? "created_desc")}",
        };

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            parameters.Add($"keyword={Uri.EscapeDataString(query.Keyword)}");
        }

        if (query.DomainId is { } domainId)
        {
            parameters.Add($"domainId={domainId}");
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            parameters.Add($"status={Uri.EscapeDataString(query.Status)}");
        }

        if (query.Mine == true)
        {
            parameters.Add("mine=true");
        }

        if (query.CreatedByUserId is { } creatorId)
        {
            parameters.Add($"createdByUserId={creatorId}");
        }

        return string.Join('&', parameters);
    }

    private Task<ApiResponse<T>> GetAsync<T>(string url, CancellationToken cancellationToken = default)
        => SendAsync<T>(HttpMethod.Get, url, null, cancellationToken);

    private Task<ApiResponse<T>> PostAsync<T>(string url, object? body)
        => SendAsync<T>(HttpMethod.Post, url, body);

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return ApiResponse.Fail<T>(BizCodes.InternalError, "服务端没有返回内容");
            }

            var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(payload, JsonOptions);
            if (envelope is null)
            {
                return ApiResponse.Fail<T>(BizCodes.InternalError, "响应格式无法解析");
            }

            if (envelope.Code == BizCodes.Unauthorized)
            {
                await authStateProvider.SignOutAsync(redirectToLogin: true).ConfigureAwait(false);
            }

            return envelope;
        }
        catch (HttpRequestException exception)
        {
            return ApiResponse.Fail<T>(BizCodes.InternalError, $"无法连接服务端：{exception.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ApiResponse.Fail<T>(BizCodes.ClientClosedRequest, "请求超时，请重试");
        }
        catch (JsonException)
        {
            return ApiResponse.Fail<T>(BizCodes.InternalError, "响应格式无法解析");
        }
    }

    /// <summary>
    /// 下载二进制响应（CSV）。成功时响应体就是文件本身；失败时后端仍会给出统一信封。
    /// 不论 HTTP 状态码都先识别 JSON，避免把 HTTP 200 的业务错误保存成 CSV。
    /// </summary>
    private async Task<DownloadResult> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                         || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;

            // 业务错误始终可能是 HTTP 200，因此先按 Content-Type 识别信封，再看 HTTP 状态。
            if (isJson)
            {
                var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var error = TryReadEnvelope(errorPayload);
                if (error?.Code == BizCodes.Unauthorized)
                {
                    await authStateProvider.SignOutAsync(redirectToLogin: true).ConfigureAwait(false);
                }

                return DownloadResult.Fail(error switch
                {
                    null => $"导出失败（HTTP {(int)response.StatusCode}）：JSON 错误响应无法解析",
                    { Code: BizCodes.Success } => "导出失败：服务端返回了 JSON 而非 CSV",
                    _ => ErrorMessages.From(error.Code, error.Msg),
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                return DownloadResult.Fail($"导出失败（HTTP {(int)response.StatusCode}）");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = disposition?.FileNameStar
                           ?? disposition?.FileName?.Trim('"')
                           ?? "export.csv";
            var truncated = IsTruncated(response);

            return new DownloadResult(true, bytes, fileName, null, truncated);
        }
        catch (HttpRequestException exception)
        {
            return DownloadResult.Fail($"无法连接服务端：{exception.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return DownloadResult.Fail("请求超时，请重试");
        }
    }

    private static bool IsTruncated(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Export-Truncated", out var headerValues)
            && headerValues.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return response.Content.Headers.ContentDisposition?.Parameters.Any(parameter =>
                   string.Equals(parameter.Name, "truncated", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(parameter.Value, "true", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static ApiResponse<object?>? TryReadEnvelope(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ApiResponse<object?>>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>统一处理失败提示，成功时不打扰用户。</summary>
    public void NotifyIfFailed<T>(ApiResponse<T> response)
    {
        if (response.Code == BizCodes.Success)
        {
            return;
        }

        snackbar.Add(ErrorMessages.From(response.Code, response.Msg), Severity.Error);
    }
}

/// <summary>文件下载结果。<see cref="Bytes"/> 仅在成功时有值。</summary>
public sealed record DownloadResult(
    bool Success,
    byte[]? Bytes,
    string? FileName,
    string? Error,
    bool Truncated = false)
{
    public static DownloadResult Fail(string error) => new(false, null, null, error);
}
