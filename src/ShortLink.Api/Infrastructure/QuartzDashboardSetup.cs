using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Quartz;
using ShortLink.Application.Auth;
using ShortLink.Contracts;
using ShortLink.Contracts.Auth;
using ShortLink.Domain.Users;
using ShortLink.Infrastructure.Analytics;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// Quartz Dashboard 的接入：Dashboard 自身不做认证，暴露给匿名等于把调度器写权限交出去。
/// 这里复用现有账号体系登录，把 JWT 放进 HttpOnly Cookie；JwtBearer 在 Dashboard 路径上读取它，
/// 因此令牌校验、角色回源与 WorkSpace 归属仍然走同一套逻辑。
/// </summary>
public static class QuartzDashboardSetup
{
    public const string CookieName = "shortlink_dashboard";
    public const string LoginPath = "/quartz-login";
    public const string LogoutPath = "/quartz-logout";

    private static readonly HashSet<string> AllowedJobTypes = new(StringComparer.Ordinal)
    {
        typeof(ClickArchiveJob).FullName!,
        typeof(ClickRetentionJob).FullName!,
        nameof(ClickArchiveJob),
        nameof(ClickRetentionJob),
    };

    public static IServiceCollection AddShortLinkQuartzDashboard(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ShortLinkQuartzDashboardOptions.SectionName);
        services.Configure<ShortLinkQuartzDashboardOptions>(section);

        var options = section.Get<ShortLinkQuartzDashboardOptions>()
                      ?? new ShortLinkQuartzDashboardOptions();
        options.Validate();

        services.AddAntiforgery();
        if (!options.Enabled)
        {
            return services;
        }

        services.AddQuartzDashboard(dashboard =>
        {
            dashboard.DashboardPath = options.NormalizedPath;
            dashboard.ReadOnly = options.ReadOnly;
            // 页面、SignalR 回路与静态资源都要经过同一策略，只给页面加 Authorize 会漏掉回路。
            dashboard.AuthorizationPolicy = AuthorizationSetup.Manager;
            // Dashboard 允许按字符串新增 Job，不设白名单等于把任意类型加载能力交给浏览器。
            dashboard.IsJobTypeAllowed = IsAllowedJobType;
        });

        return services;
    }

    public static void MapShortLinkQuartzDashboard(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ShortLinkQuartzDashboardOptions>>().Value;
        if (!options.Enabled)
        {
            app.Logger.LogInformation("Quartz Dashboard 未启用（QuartzDashboard:Enabled=false）");
            return;
        }

        // Dashboard 的样式与脚本是静态 Web 资源；不映射时页面能打开但所有交互无响应。
        app.MapStaticAssets();

        app.MapGet(LoginPath, LoginPage);
        app.MapPost(LoginPath, Login).RequireRateLimiting("auth");
        app.MapGet(LogoutPath, Logout);

        app.MapQuartzDashboard().RequireAuthorization(AuthorizationSetup.Manager);

        if (app.Logger.IsEnabled(LogLevel.Information))
        {
            var dashboardPath = options.NormalizedPath;
            app.Logger.LogInformation(
                "Quartz Dashboard 已启用：{Path}（只读={ReadOnly}）",
                dashboardPath,
                options.ReadOnly);
        }
    }

    /// <summary>当前请求是否属于 Dashboard（含 Blazor 回路的框架路径）。</summary>
    public static bool IsDashboardRequest(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<ShortLinkQuartzDashboardOptions>>()?.Value;
        if (options is not { Enabled: true })
        {
            return false;
        }

        var path = context.Request.Path;
        return path.StartsWithSegments(options.NormalizedPath, StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>浏览器可直接导航的 Dashboard 页面；未登录时重定向到登录页，而不是返回 JSON。</summary>
    public static bool IsDashboardPageRequest(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<ShortLinkQuartzDashboardOptions>>()?.Value;
        return options is { Enabled: true }
               && context.Request.Path.StartsWithSegments(
                   options.NormalizedPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IResult LoginPage(
        HttpContext context,
        IAntiforgery antiforgery,
        IOptions<ShortLinkQuartzDashboardOptions> options)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Content(
            RenderLoginPage(
                tokens.RequestToken,
                options.Value.NormalizedPath,
                context.Request.Query["returnUrl"].ToString(),
                error: null,
                denied: context.Request.Query.ContainsKey("denied")),
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> Login(
        HttpContext context,
        AuthService auth,
        IAntiforgery antiforgery,
        IOptions<ShortLinkQuartzDashboardOptions> optionsAccessor)
    {
        var options = optionsAccessor.Value;

        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return LoginError(
                context,
                antiforgery,
                options,
                returnUrl: null,
                "表单已过期，请刷新后重试。");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var returnUrl = form["returnUrl"].ToString();

        try
        {
            var login = await auth.LoginAsync(
                new LoginRequest
                {
                    UserName = form["userName"].ToString(),
                    Password = form["password"].ToString(),
                },
                context.RequestAborted).ConfigureAwait(false);

            if (login.User.Role is not (UserRoles.Owner or UserRoles.Admin))
            {
                return LoginError(
                    context,
                    antiforgery,
                    options,
                    returnUrl,
                    "该账号没有管理员权限，无法访问调度台。",
                    StatusCodes.Status403Forbidden);
            }

            context.Response.Cookies.Append(CookieName, login.AccessToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = login.ExpiresAtUtc,
                IsEssential = true,
            });

            return Results.Redirect(ResolveReturnUrl(returnUrl, options.NormalizedPath));
        }
        catch (BusinessException exception)
        {
            return LoginError(
                context,
                antiforgery,
                options,
                returnUrl,
                exception.Message);
        }
    }

    private static IResult Logout(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Path = "/",
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
        });

        return Results.Redirect(LoginPath);
    }

    private static IResult LoginError(
        HttpContext context,
        IAntiforgery antiforgery,
        ShortLinkQuartzDashboardOptions options,
        string? returnUrl,
        string message,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Content(
            RenderLoginPage(tokens.RequestToken, options.NormalizedPath, returnUrl, message),
            "text/html; charset=utf-8",
            statusCode: statusCode);
    }

    private static string ResolveReturnUrl(string? value, string dashboardPath)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith('/')
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("/\\", StringComparison.Ordinal))
        {
            return dashboardPath;
        }

        return value.StartsWith(dashboardPath, StringComparison.OrdinalIgnoreCase)
            ? value
            : dashboardPath;
    }

    private static bool IsAllowedJobType(string name)
        => AllowedJobTypes.Contains(name.Split(',')[0].Trim());

    private static string RenderLoginPage(
        string? requestToken,
        string dashboardPath,
        string? returnUrl,
        string? error,
        bool denied = false)
    {
        var message = error ?? (denied ? "当前账号没有管理员权限，无法访问调度台。" : null);
        var errorHtml = message is null
            ? string.Empty
            : $"<div class=\"error\">{WebUtility.HtmlEncode(message)}</div>";
        var token = WebUtility.HtmlEncode(requestToken ?? string.Empty);
        var target = WebUtility.HtmlEncode(returnUrl ?? string.Empty);

        return $$"""
                 <!DOCTYPE html>
                 <html lang="zh-CN">
                 <head>
                   <meta charset="utf-8" />
                   <meta name="viewport" content="width=device-width, initial-scale=1" />
                   <title>ShortLink · Quartz 调度台</title>
                   <style>
                     :root { color-scheme: light dark; }
                     * { box-sizing: border-box; }
                     body {
                       margin: 0; min-height: 100vh; display: grid; place-items: center;
                       font-family: system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
                       background: radial-gradient(circle at 20% 10%, #e8f0ff, #f7f9fc 45%, #eef2f8);
                       color: #1f2937;
                     }
                     .card {
                       width: min(92vw, 380px); padding: 32px; border-radius: 16px;
                       background: #fff; box-shadow: 0 18px 50px rgba(15, 23, 42, .12);
                       border: 1px solid rgba(15, 23, 42, .06);
                     }
                     h1 { margin: 0 0 6px; font-size: 22px; letter-spacing: -.02em; }
                     p.sub { margin: 0 0 24px; color: #64748b; font-size: 13px; line-height: 1.5; }
                     label { display: block; margin: 14px 0 6px; font-size: 13px; font-weight: 600; }
                     input[type=text], input[type=password] {
                       width: 100%; padding: 11px 12px; border-radius: 10px;
                       border: 1px solid #cbd5e1; font-size: 14px; outline: none;
                     }
                     input[type=text]:focus, input[type=password]:focus {
                       border-color: #2563eb; box-shadow: 0 0 0 3px rgba(37, 99, 235, .15);
                     }
                     button {
                       width: 100%; margin-top: 22px; padding: 11px 12px; border: 0;
                       border-radius: 10px; background: #2563eb; color: #fff;
                       font-size: 14px; font-weight: 600; cursor: pointer;
                     }
                     button:hover { background: #1d4ed8; }
                     .error {
                       margin: 0 0 4px; padding: 10px 12px; border-radius: 10px;
                       background: #fef2f2; color: #b91c1c; font-size: 13px;
                     }
                     .foot { margin-top: 18px; color: #94a3b8; font-size: 12px; text-align: center; }
                   </style>
                 </head>
                 <body>
                   <main class="card">
                     <h1>Quartz 调度台</h1>
                     <p class="sub">查看归档与清理任务。仅 Owner / Admin 可访问。</p>
                     {{errorHtml}}
                     <form method="post" action="{{LoginPath}}">
                       <input type="hidden" name="__RequestVerificationToken" value="{{token}}" />
                       <input type="hidden" name="returnUrl" value="{{target}}" />
                       <label for="userName">账号</label>
                       <input id="userName" name="userName" type="text" autocomplete="username" required />
                       <label for="password">密码</label>
                       <input id="password" name="password" type="password" autocomplete="current-password" required />
                       <button type="submit">登录</button>
                     </form>
                     <div class="foot">{{WebUtility.HtmlEncode(dashboardPath)}}</div>
                   </main>
                 </body>
                 </html>
                 """;
    }
}
