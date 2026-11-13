using ShortLink.Contracts;

namespace ShortLink.Api.Infrastructure;

/// <summary>把 Asp.Versioning 产生的 ProblemDetails 转换为项目的统一业务信封。</summary>
public sealed class EnvelopeProblemDetailsWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context)
        => context.HttpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        if (http.Response.HasStarted)
        {
            return;
        }

        var isVersionError = context.ProblemDetails.Status == StatusCodes.Status400BadRequest
                             && context.ProblemDetails.Title?.Contains("version", StringComparison.OrdinalIgnoreCase) == true;
        var code = isVersionError ? BizCodes.ApiVersionUnsupported : BizCodes.InvalidArgument;
        var message = string.IsNullOrWhiteSpace(context.ProblemDetails.Detail)
            ? BizCodes.Describe(code)
            : context.ProblemDetails.Detail;
        await AuthenticationSetup.WriteEnvelopeAsync(http, code, message).ConfigureAwait(false);
    }
}
