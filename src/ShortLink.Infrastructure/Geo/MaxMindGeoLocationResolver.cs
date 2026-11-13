using System.Net;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Infrastructure.Options;

namespace ShortLink.Infrastructure.Geo;

/// <summary>
/// 离线 IP 库解析（MaxMind GeoLite2 / GeoIP2 的 <c>.mmdb</c>）。
/// <para>
/// 相比 CDN 头，它的价值在于「不依赖链路」：请求头一旦被中间层丢掉或被客户端伪造，
/// 统计口径就没了；离线库只看 IP，且能给出省份。
/// </para>
/// <para>
/// 库文件在构造时打开并常驻（<see cref="DatabaseReader"/> 的查询是线程安全的只读操作），
/// 不是每次请求都读盘。IP 查不到（内网地址、库里没有的段）时回落到请求头——
/// 内网地址在国内部署里很常见，直接返回空会让报表凭空少一块。
/// </para>
/// </summary>
public sealed class MaxMindGeoLocationResolver : IGeoLocationResolver, IDisposable
{
    private readonly DatabaseReader _reader;
    private readonly IGeoLocationResolver _headerFallback;
    private readonly bool _supportsCity;

    public MaxMindGeoLocationResolver(
        IOptions<GeoIpOptions> options,
        IGeoLocationResolver headerFallback)
    {
        var settings = options.Value;

        // 路径与文件存在性已在启动期校验过（GeoIpOptions.Validate）。
        _reader = new DatabaseReader(settings.DatabasePath!, settings.Locales);
        _headerFallback = headerFallback;

        // 库类型决定调用哪个查询方法：对 Country 库调 TryCity 没有意义，
        // 这里一次性判定，避免在热路径上靠异常做分支。
        _supportsCity = _reader.Metadata.DatabaseType.Contains("City", StringComparison.OrdinalIgnoreCase);
    }

    public GeoLocation Resolve(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return _headerFallback.Resolve(ip);
        }

        try
        {
            if (_supportsCity && _reader.TryCity(address, out var city))
            {
                // Subdivisions 从大到小，取最后一级即「省/州」。
                var province = city.Subdivisions.Count > 0 ? city.Subdivisions[^1].Name : null;
                return new GeoLocation(city.Country.IsoCode, province);
            }

            if (_reader.TryCountry(address, out var country) && !string.IsNullOrWhiteSpace(country.Country.IsoCode))
            {
                return new GeoLocation(country.Country.IsoCode, null);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // 库类型不匹配或文件在运行期不可读：不让跳转因为统计信息而失败。
            return _headerFallback.Resolve(ip);
        }

        // 查不到（内网地址、库里没有的网段）：用请求头兜底一次。
        return _headerFallback.Resolve(ip);
    }

    public void Dispose() => _reader.Dispose();
}
