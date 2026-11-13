using System.Text;
using System.Text.Json;
using ShortLink.Application.Analytics;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;

namespace ShortLink.Api.Endpoints;

/// <summary>
/// 转化回调端点。必须按「原始请求体」验签，所以这里不用参数绑定，
/// 而是先读字符串、再反序列化，否则任何格式差异都会导致签名不匹配。
/// </summary>
public static class ConversionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapConversionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/conversions", async (
                HttpContext http,
                ConversionService service,
                CancellationToken cancellationToken) =>
            {
                http.Request.EnableBuffering();

                string rawBody;
                using (var reader = new StreamReader(
                           http.Request.Body,
                           Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: false,
                           leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }

                ConversionCallbackRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ConversionCallbackRequest>(rawBody, JsonOptions);
                }
                catch (JsonException)
                {
                    throw new BusinessException(BizCodes.ConversionPayloadInvalid, "请求体不是合法的 JSON");
                }

                if (request is null)
                {
                    throw new BusinessException(BizCodes.ConversionPayloadInvalid);
                }

                var result = await service.HandleAsync(
                    request,
                    http.Request.Headers["X-Signature"].ToString(),
                    http.Request.Headers["X-Timestamp"].ToString(),
                    rawBody,
                    http.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken);

                return ApiResponse.Ok(result);
            })
            .AllowAnonymous()
            .RequireRateLimiting("conversion")
            .WithTags("转化")
            .WithSummary("转化回调（签名 + 幂等）")
            .Produces<ApiResponse<ConversionResultDto>>();

        return group;
    }
}
