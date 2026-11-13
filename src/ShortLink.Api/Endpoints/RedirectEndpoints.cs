using System.Net;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Redirection;
using ShortLink.Contracts;
using ShortLink.Domain.Analytics;
using ShortLink.Infrastructure.Analytics;
using ShortLink.Infrastructure.Diagnostics;

namespace ShortLink.Api.Endpoints;

/// <summary>
/// 短码跳转端点。这是唯一对外提供 HTTP 语义（302/403/404/410）的路径：
/// 浏览器、搜索引擎与 CDN 需要真实状态码，因此不走统一信封，错误以 HTML 落地页呈现。
/// </summary>
public static class RedirectEndpoints
{
    private const string CodeRoute = "/{code:regex(^[A-Za-z0-9_-]{{3,32}}$)}";

    public static IEndpointRouteBuilder MapRedirectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(CodeRoute, (
                string code,
                HttpContext context,
                RedirectService service,
                IClickRecorder recorder,
                IClock clock,
                IStatisticsCalendar calendar,
                IGeoLocationResolver geo,
                CancellationToken cancellationToken)
            => HandleAsync(context, code, null, service, recorder, clock, calendar, geo, cancellationToken))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost(CodeRoute, async (
                string code,
                HttpContext context,
                RedirectService service,
                IClickRecorder recorder,
                IClock clock,
                IStatisticsCalendar calendar,
                IGeoLocationResolver geo,
                CancellationToken cancellationToken) =>
            {
                var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                return await HandleAsync(
                    context,
                    code,
                    form["password"].ToString(),
                    service,
                    recorder,
                    clock,
                    calendar,
                    geo,
                    cancellationToken).ConfigureAwait(false);
            })
            .AllowAnonymous()
            .RequireRateLimiting(Infrastructure.RateLimitingSetup.LinkPasswordPolicy)
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string code,
        string? password,
        RedirectService service,
        IClickRecorder recorder,
        IClock clock,
        IStatisticsCalendar calendar,
        IGeoLocationResolver geo,
        CancellationToken cancellationToken)
    {
        try
        {
            // 只取一次时钟：访客指纹与点击日期都从这一瞬间推导。
            // 分两次取会在跨零点的那一跳上让两者分属不同日期（指纹按新一天、日期按旧一天），
            // 表现为独立访客数在日界处突增，且极难复现。
            var now = clock.UtcNow;
            var clickDate = calendar.ToDate(now);
            var requestContext = BuildContext(context, clickDate, geo);
            var outcome = await service
                .ResolveAsync(context.Request.Host.Value ?? string.Empty, code, password, requestContext, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.CountClick)
            {
                var published = await RecordClickAsync(context, outcome, requestContext, recorder, now, clickDate)
                    .ConfigureAwait(false);
                if (!published)
                {
                    await service.ReleaseQuotaAsync(outcome).ConfigureAwait(false);
                }
            }

            ShortLinkDiagnostics.Redirects.Add(1, new KeyValuePair<string, object?>("result", "ok"));
            context.Response.Headers.CacheControl = "no-store";

            // 微信 / QQ 内先给中间页，用户点「继续访问」时带 direct=1 再回来，避免陷入循环。
            var direct = context.Request.Query.ContainsKey("direct");
            if (outcome.Interstitial && !direct)
            {
                return Html(
                    StatusCodes.Status200OK,
                    RedirectPageRenderer.Interstitial(outcome.Code, outcome.TargetUrl, null));
            }

            return Results.Redirect(
                outcome.TargetUrl,
                permanent: outcome.StatusCode is 301 or 308,
                preserveMethod: outcome.StatusCode is 307 or 308);
        }
        catch (BusinessException exception)
        {
            ShortLinkDiagnostics.Redirects.Add(
                1,
                new KeyValuePair<string, object?>("result", ResultLabel(exception.Code)));

            if (exception.Code is BizCodes.LinkIpNotAllowed or BizCodes.LinkBotBlocked)
            {
                ShortLinkDiagnostics.SecurityBlocks.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        "reason",
                        exception.Code == BizCodes.LinkBotBlocked ? "bot_blocked" : "ip_blocked"));
            }

            context.Response.Headers.CacheControl = "no-store";

            return exception.Code switch
            {
                BizCodes.LinkNotFound => Html(
                    StatusCodes.Status404NotFound,
                    RedirectPageRenderer.ErrorPage("链接不存在", "该短链接不存在或已被删除。")),

                BizCodes.LinkDisabled => Html(
                    StatusCodes.Status404NotFound,
                    RedirectPageRenderer.ErrorPage("链接已停用", "该短链接已被管理员停用。")),

                BizCodes.LinkNotStarted => Html(
                    StatusCodes.Status404NotFound,
                    RedirectPageRenderer.ErrorPage("链接尚未生效", "该短链接还没有到生效时间。")),

                BizCodes.LinkExpired => Html(
                    StatusCodes.Status410Gone,
                    RedirectPageRenderer.ErrorPage("链接已过期", "该短链接已超过有效期。")),

                BizCodes.LinkClickCapped => Html(
                    StatusCodes.Status410Gone,
                    RedirectPageRenderer.ErrorPage("链接已达到访问上限", "该短链接的访问次数已达上限。")),

                BizCodes.LinkIpNotAllowed => Html(
                    StatusCodes.Status403Forbidden,
                    RedirectPageRenderer.BlockedPage("当前网络环境不在该链接的允许范围内。")),

                BizCodes.LinkBotBlocked => Html(
                    StatusCodes.Status403Forbidden,
                    RedirectPageRenderer.BlockedPage("检测到自动访问，已按链接策略拦截。")),

                BizCodes.LinkPasswordRequired => Html(
                    StatusCodes.Status200OK,
                    RedirectPageRenderer.PasswordForm(code, null)),

                BizCodes.LinkPasswordIncorrect => Html(
                    StatusCodes.Status200OK,
                    RedirectPageRenderer.PasswordForm(code, "访问密码不正确，请重试。")),

                _ => Html(
                    StatusCodes.Status500InternalServerError,
                    RedirectPageRenderer.ErrorPage("服务暂时不可用", "请稍后再试。")),
            };
        }
    }

    private static RedirectRequestContext BuildContext(
        HttpContext context,
        DateOnly clickDate,
        IGeoLocationResolver geo)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var referer = context.Request.Headers.Referer.ToString();
        var agent = UserAgentParser.Parse(userAgent);
        var ip = context.Connection.RemoteIpAddress?.ToString();

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.Query)
        {
            if (!string.IsNullOrEmpty(key))
            {
                query[key] = value.ToString();
            }
        }

        // 来源地交给解析器：有离线库时按 IP 查（不依赖 CDN 是否注入了头），
        // 没有库时读可配置的 CDN 请求头。
        var location = geo.Resolve(ip);

        return new RedirectRequestContext(
            ip,
            userAgent,
            referer,
            PrimaryLanguage(context.Request.Headers.AcceptLanguage.ToString()),
            location.Country,
            location.Province,
            agent.Device,
            agent.Browser,
            agent.OperatingSystem,
            agent.IsBot,
            query,
            // 指纹里的日期必须与 click_date 同源（都来自 IStatisticsCalendar），
            // 否则非 UTC 部署下「今日独立访客」会在 UTC 零点把一个自然日拆成两段、重复计数。
            VisitorFingerprint.Compute(ip, userAgent, clickDate));
    }

    private static IResult Html(int statusCode, string html)
        => Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    private static ValueTask<bool> RecordClickAsync(
        HttpContext context,
        RedirectOutcome outcome,
        RedirectRequestContext requestContext,
        IClickRecorder recorder,
        DateTime now,
        DateOnly clickDate)
    {
        // 只投递消息、不等落库。发布失败由实现内部兜底（记日志 + 计数），
        // 因此这里不需要 try/catch——统计永远不该让一次正常跳转变成 500。
        return recorder.EnqueueAsync(ClickEvent.Create(
            outcome.WorkspaceId,
            outcome.LinkId,
            outcome.DomainId,
            outcome.RouteId,
            outcome.Code,
            now,
            clickDate,
            requestContext.VisitorKey,
            requestContext.Ip,
            requestContext.UserAgent,
            requestContext.Referer,
            requestContext.Country,
            requestContext.Province,
            requestContext.Device,
            requestContext.Browser,
            requestContext.Os,
            requestContext.IsBot),
            outcome.QuotaReservation?.ReservationId);
    }

    /// <summary>Accept-Language 形如 <c>zh-CN,zh;q=0.9</c>，取第一个语言标签即可。</summary>
    private static string? PrimaryLanguage(string acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return null;
        }

        var first = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    private static string ResultLabel(int code) => code switch
    {
        BizCodes.Success => "ok",
        BizCodes.LinkNotFound => "not_found",
        BizCodes.LinkDisabled => "disabled",
        BizCodes.LinkNotStarted => "not_started",
        BizCodes.LinkExpired => "expired",
        BizCodes.LinkClickCapped => "click_capped",
        BizCodes.LinkPasswordRequired => "password_required",
        BizCodes.LinkPasswordIncorrect => "password_invalid",
        BizCodes.LinkIpNotAllowed => "ip_not_allowed",
        BizCodes.LinkBotBlocked => "bot_blocked",
        _ => "error",
    };
}
