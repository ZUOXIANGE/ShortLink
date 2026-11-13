using System.Globalization;
using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Analytics;
using ShortLink.Domain.Analytics;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Analytics;

/// <summary>
/// 转化回调用例。安全边界三件套：
/// <list type="number">
/// <item>HMAC-SHA256 签名，密钥只在服务端之间传递；</item>
/// <item>时间戳 5 分钟窗口，防重放；</item>
/// <item><c>event_id</c> 唯一索引，防重复计数。</item>
/// </list>
/// </summary>
public sealed class ConversionService(
    IConversionRepository conversions,
    IShortLinkRepository links,
    IDomainRepository domains,
    IRouteRepository routes,
    IClock clock,
    ISignatureVerifier signatureVerifier,
    ILogger<ConversionService> logger)
{
    private const int TimestampToleranceSeconds = 300;

    public async Task<ConversionResultDto> HandleAsync(
        ConversionCallbackRequest request,
        string? signature,
        string? timestampHeader,
        string rawBody,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 64)
        {
            throw new BusinessException(BizCodes.ConversionPayloadInvalid, "eventId 必填且不超过 64 个字符");
        }

        request.Metric ??= "conversion";
        if (request.Metric.Length > 64)
        {
            throw new BusinessException(BizCodes.ConversionPayloadInvalid, "metric 不超过 64 个字符");
        }

        var link = await ResolveLinkAsync(request, cancellationToken).ConfigureAwait(false);

        if (!link.HasConversionSecret)
        {
            throw new BusinessException(
                BizCodes.ConversionSignatureInvalid,
                "该短链未启用转化回调，请先在控制台获取转化密钥");
        }

        var timestamp = ResolveTimestamp(request, timestampHeader);
        DateTimeOffset timestampInstant;
        try
        {
            timestampInstant = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new BusinessException(BizCodes.ConversionPayloadInvalid, "时间戳超出有效范围", innerException: exception);
        }

        var drift = Math.Abs(clock.UtcNow.Subtract(timestampInstant.UtcDateTime).TotalSeconds);
        if (drift > TimestampToleranceSeconds)
        {
            throw new BusinessException(BizCodes.ConversionTimestampExpired);
        }

        if (!signatureVerifier.Verify(link.ConversionSecret!, $"{timestamp}.{rawBody}", signature))
        {
            throw new BusinessException(BizCodes.ConversionSignatureInvalid);
        }

        if (request.RouteId is { } routeId
            && !await routes.ExistsForLinkAsync(routeId, link.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(BizCodes.ConversionPayloadInvalid, "routeId 不属于当前短链");
        }

        if (await conversions.ExistsAsync(link.Id, request.EventId, cancellationToken).ConfigureAwait(false))
        {
            var existingTotal = await conversions.GetLinkTotalAsync(link.Id, cancellationToken).ConfigureAwait(false);
            return new ConversionResultDto(link.Id, request.EventId, true, existingTotal);
        }

        var conversion = ConversionEvent.Create(
            link.WorkspaceId,
            link.Id,
            request.RouteId,
            request.EventId,
            string.IsNullOrWhiteSpace(request.Metric) ? "conversion" : request.Metric,
            request.Value,
            ip,
            clock.UtcNow);

        var added = await conversions.TryAddAsync(conversion, cancellationToken).ConfigureAwait(false);
        if (!added)
        {
            // 并发重复提交：唯一索引兜底，按幂等命中处理。
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("转化回调并发重复，已按幂等处理：link={LinkId} event={EventId}", link.Id, request.EventId);
            }
        }

        var total = await conversions.GetLinkTotalAsync(link.Id, cancellationToken).ConfigureAwait(false);
        return new ConversionResultDto(link.Id, request.EventId, !added, total);
    }

    private async Task<Link> ResolveLinkAsync(ConversionCallbackRequest request, CancellationToken cancellationToken)
    {
        if (request.LinkId is { } id)
        {
            return await links.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Code) || !ShortCode.IsValid(request.Code.Trim()))
        {
            throw new BusinessException(BizCodes.ConversionPayloadInvalid, "需要提供 linkId 或合法的 code");
        }

        var domain = await domains.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException(BizCodes.DomainNotFound);

        return await links.FindByCodeAsync(domain.Id, request.Code.Trim(), cancellationToken).ConfigureAwait(false)
               ?? throw new BusinessException(BizCodes.LinkNotFound);
    }

    private static long ResolveTimestamp(ConversionCallbackRequest request, string? timestampHeader)
    {
        if (long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromHeader))
        {
            return fromHeader;
        }

        return request.Timestamp ?? throw new BusinessException(
            BizCodes.ConversionPayloadInvalid,
            "缺少 X-Timestamp 请求头");
    }
}
