using Microsoft.AspNetCore.Authentication;

namespace ShortLink.Api.Infrastructure;

/// <summary>
/// 在限流之前为第三方端点完成签名认证。
/// <para>
/// 原因：限流中间件必须排在授权之前，否则「签名错误」会先被授权短路，
/// 攻击者可以用随机 keyId 无限打数据库和日志而永远碰不到配额。
/// 这里只对 <c>/api/v{version}/partner</c> 提前认证，并把成功的主体写回
/// <see cref="HttpContext.User"/>，让限流能按凭据分区；失败不写响应，
/// 仍由授权阶段统一产出信封。
/// </para>
/// </summary>
public sealed class PartnerAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPartnerRequest(context.Request.Path))
        {
            var result = await context
                .AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName)
                .ConfigureAwait(false);

            if (result.Succeeded && result.Principal is not null)
            {
                context.User = result.Principal;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>匹配 <c>/api/{version}/partner/...</c>，不依赖具体版本号。</summary>
    private static bool IsPartnerRequest(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3
               && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
               && string.Equals(segments[2], "partner", StringComparison.OrdinalIgnoreCase);
    }
}
