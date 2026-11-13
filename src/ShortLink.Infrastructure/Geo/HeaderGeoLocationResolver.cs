using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Options;

namespace ShortLink.Infrastructure.Geo;

/// <summary>
/// 从 CDN 请求头读国家/省份。零依赖，但只有 CDN 注入了这些头才有值——
/// 直连部署下它会一直返回空，这是设计上的预期，不是缺陷。
/// <para>
/// 头名做成可配置（<c>GeoIp:CountryHeaders</c> / <c>GeoIp:ProvinceHeaders</c>）：
/// 阿里云、腾讯云、Cloudflare 注入的头名各不相同，硬编码意味着换 CDN 就要改代码。
/// </para>
/// </summary>
public sealed class HeaderGeoLocationResolver(
    IHttpContextAccessor accessor,
    IOptions<GeoIpOptions> options) : IGeoLocationResolver
{
    public GeoLocation Resolve(string? ip)
    {
        // ip 参数在这里不用：这个实现的信息源就是请求头。
        // 保留参数是为了与离线库实现共用同一个抽象——调用方不必知道背后是哪种来源。
        var headers = accessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return GeoLocation.Empty;
        }

        return new GeoLocation(
            FirstNonEmpty(headers, options.Value.CountryHeaders),
            FirstNonEmpty(headers, options.Value.ProvinceHeaders));
    }

    private static string? FirstNonEmpty(IHeaderDictionary headers, string[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = headers[name].ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
