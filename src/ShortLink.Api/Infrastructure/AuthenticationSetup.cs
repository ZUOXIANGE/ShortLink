using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using ShortLink.Domain.Users;
using ShortLink.Infrastructure.Options;
using ShortLink.Mcp.Options;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// JWT 认证与框架层归一化：401/403 由认证中间件直接产生，必须手写成统一信封，
/// 否则会出现「同一服务两种错误结构」。
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // 选项在运行时解析：允许配置源（环境变量、测试宿主）在启动后再覆盖。
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtAccessor) => Configure(bearer, jwtAccessor.Value));

        return services;
    }

    private static void Configure(JwtBearerOptions bearer, JwtOptions options)
    {
        var key = Encoding.UTF8.GetBytes(options.SigningKey);
        if (key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey 长度必须至少 32 字节（HS256 安全下限）");
        }

        {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };

                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ValidateTokenAsync,
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();

                        // MCP 客户端靠真实 401 触发重新认证，并按 WWW-Authenticate 做资源发现。
                        // 这里若也归一化成 200 信封，客户端会认为连接成功、解析不出 JSON-RPC 响应，
                        // 表现为「连上了但没有工具」。路径白名单在认证阶段管不到，只能在这里判。
                        if (IsMcpRequest(context.HttpContext))
                        {
                            var response = context.HttpContext.Response;
                            response.StatusCode = StatusCodes.Status401Unauthorized;
                            if (!response.Headers.ContainsKey("WWW-Authenticate"))
                            {
                                response.Headers.WWWAuthenticate = "Bearer";
                            }

                            return;
                        }

                        await WriteEnvelopeAsync(
                            context.HttpContext,
                            BizCodes.Unauthorized,
                            BizCodes.Describe(BizCodes.Unauthorized)).ConfigureAwait(false);
                    },
                OnForbidden = async context =>
                {
                    // 同上：MCP 侧保持 403 原始语义，交给客户端框架处理。
                    if (IsMcpRequest(context.HttpContext))
                    {
                        return;
                    }

                    await WriteEnvelopeAsync(
                        context.HttpContext,
                        BizCodes.Forbidden,
                        BizCodes.Describe(BizCodes.Forbidden)).ConfigureAwait(false);
                },
            };
        }
    }

    private static async Task ValidateTokenAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null
            || !long.TryParse(principal.FindFirstValue("sub"), out var userId)
            || userId <= 0
            || !long.TryParse(principal.FindFirstValue("ws"), out var workspaceId)
            || workspaceId <= 0)
        {
            context.Fail("令牌缺少有效的用户或工作区声明");
            return;
        }

        // 旧令牌没有版本声明时按 0 处理，与升级前账号的默认版本兼容。
        var versionValue = principal.FindFirstValue(TokenClaimNames.TokenVersion);
        var tokenVersion = 0;
        if (!string.IsNullOrEmpty(versionValue)
            && (!int.TryParse(versionValue, out tokenVersion) || tokenVersion < 0))
        {
            context.Fail("令牌版本无效");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ITokenValidator>();
        var currentRole = await validator
            .ValidateAsync(userId, workspaceId, tokenVersion, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (currentRole is not { } role)
        {
            context.Fail("账号、令牌版本或工作区成员关系已失效");
            return;
        }

        if (principal.Identity is ClaimsIdentity identity)
        {
            var snapshotRole = identity.FindFirst("role");
            if (snapshotRole is not null)
            {
                identity.RemoveClaim(snapshotRole);
            }

            identity.AddClaim(new Claim("role", role.ToClaimValue()));
        }
    }

    /// <summary>当前请求是否指向 MCP 端点（路径取自配置，改 <c>Mcp:Path</c> 无需同步改这里）。</summary>
    private static bool IsMcpRequest(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<McpOptions>>()?.Value;

        return options is { Enabled: true } &&
               context.Request.Path.StartsWithSegments(options.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把框架层结果写成 HTTP 200 + 统一信封，用于认证、限流等中间件。</summary>
    public static async Task WriteEnvelopeAsync(HttpContext context, int code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-Biz-Code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response
            .WriteAsJsonAsync(ApiResponse.Fail(code, message), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
