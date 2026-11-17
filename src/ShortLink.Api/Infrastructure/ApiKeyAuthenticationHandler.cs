using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;
using ShortLink.Contracts;
using ShortLink.Domain.Users;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 第三方 API 的签名认证方案。只在 <c>/api/v1/partner</c> 的策略里显式引用，
/// 后台端点继续走 JWT，两套身份不存在互相降级或越权复用。
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    public const string KeyHeader = "X-Api-Key";

    public const string TimestampHeader = "X-Timestamp";

    public const string NonceHeader = "X-Nonce";

    public const string SignatureHeader = "X-Signature";

    private const string FailureCodeItem = "shortlink:api-auth:code";

    private const string FailureMessageItem = "shortlink:api-auth:message";

    private readonly IApiClientAuthenticator _authenticator;

    private readonly PartnerApiOptions _options;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiClientAuthenticator authenticator,
        IOptions<PartnerApiOptions> partnerOptions)
        : base(options, logger, encoder)
    {
        _authenticator = authenticator;
        _options = partnerOptions.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var keyId = Header(KeyHeader);
        var timestampHeader = Header(TimestampHeader);
        var nonce = Header(NonceHeader);
        var signature = Header(SignatureHeader);

        // 完全没有签名头：交给授权层产生 challenge，而不是伪装成「匿名已认证」。
        if (keyId.Length == 0 && timestampHeader.Length == 0 && nonce.Length == 0 && signature.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (keyId.Length == 0
            || nonce.Length == 0
            || signature.Length == 0
            || !long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return Fail(BizCodes.ApiKeyInvalid, "缺少签名请求头或时间戳格式不正确");
        }

        var body = await ReadBodyAsync().ConfigureAwait(false);
        if (body is null)
        {
            return Fail(BizCodes.ApiBodyTooLarge);
        }

        var canonical = ApiRequestCanonicalizer.Build(Request, body, timestampHeader, nonce);
        var result = await _authenticator
            .AuthenticateAsync(
                keyId,
                canonical,
                signature,
                timestamp,
                nonce,
                Context.Connection.RemoteIpAddress?.ToString(),
                Context.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Success || result.Identity is null)
        {
            return Fail(result.Code, result.Message);
        }

        var identity = result.Identity;

        // 供限流中间件按凭据配额分区；认证一定先于 UseRateLimiter 执行。
        Context.Items[RateLimitingSetup.ApiClientRateLimitItem] = identity.RateLimitPerMinute;

        Claim[] claims =
        [
            new("sub", identity.UserId.ToString(CultureInfo.InvariantCulture)),
            new("name", identity.UserName),
            new("role", identity.Role.ToClaimValue()),
            new("ws", identity.WorkspaceId.ToString(CultureInfo.InvariantCulture)),
            new("client_id", identity.KeyId),
            new("client_name", identity.ClientName),
            new("scope", string.Join(' ', identity.Scopes)),
            new("auth_method", SchemeName),
        ];

        // roleClaimType 必须显式写成 "role"：ClaimsIdentity 默认找 ClaimTypes.Role，
        // 不显式指定会让所有 RequireRole 策略恒为 403。
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "role"));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items.TryGetValue(FailureCodeItem, out var value) && value is int failureCode
            ? failureCode
            : BizCodes.ApiKeyInvalid;

        var message = Context.Items.TryGetValue(FailureMessageItem, out var messageValue) && messageValue is string text
            ? text
            : BizCodes.Describe(code);

        await AuthenticationSetup.WriteEnvelopeAsync(Context, code, message).ConfigureAwait(false);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        => await AuthenticationSetup
            .WriteEnvelopeAsync(Context, BizCodes.ApiScopeDenied, BizCodes.Describe(BizCodes.ApiScopeDenied))
            .ConfigureAwait(false);

    private AuthenticateResult Fail(int code, string? message = null)
    {
        var text = string.IsNullOrWhiteSpace(message) ? BizCodes.Describe(code) : message;
        Context.Items[FailureCodeItem] = code;
        Context.Items[FailureMessageItem] = text;
        return AuthenticateResult.Fail(text);
    }

    private string Header(string name)
        => Request.Headers.TryGetValue(name, out var value) ? value.ToString().Trim() : string.Empty;

    /// <summary>
    /// 读取原始请求体用于签名。返回 null 表示超过大小上限。
    /// 读取后必须回卷，否则后续模型绑定拿到的是空流。
    /// </summary>
    private async Task<byte[]?> ReadBodyAsync()
    {
        if (Request.ContentLength is { } length && length > _options.MaxBodyBytes)
        {
            return null;
        }

        Request.EnableBuffering();
        try
        {
            if (Request.Body.CanSeek)
            {
                Request.Body.Position = 0;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            long total = 0;

            while (true)
            {
                var read = await Request.Body.ReadAsync(chunk, Context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _options.MaxBodyBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        finally
        {
            if (Request.Body.CanSeek)
            {
                Request.Body.Position = 0;
            }
        }
    }
}
