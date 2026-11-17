namespace ShortLink.Infrastructure.Options;

/// <summary>
/// 访问来源地解析配置。
/// <para>
/// 默认只读 CDN 请求头（零依赖，精度取决于 CDN 是否注入了这些头）；
/// 配置 <see cref="DatabasePath"/> 后改用离线 IP 库（MaxMind GeoLite2 的
/// <c>.mmdb</c>），不再依赖 CDN，且能给到省份。
/// </para>
/// </summary>
public sealed class GeoIpOptions
{
    public const string SectionName = "GeoIp";

    /// <summary>
    /// 离线库文件路径（<c>GeoLite2-City.mmdb</c> 或 <c>GeoLite2-Country.mmdb</c>）。
    /// 留空则只用请求头。文件不存在时<b>启动失败</b>——
    /// 路径写错如果静默回落请求头，会表现为「配了库但省份还是空」，排查成本很高。
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>国家 ISO 码所在的请求头，按顺序取第一个非空值。</summary>
    public string[] CountryHeaders { get; set; } = ["CF-IPCountry", "X-Country"];

    /// <summary>省份所在的请求头，按顺序取第一个非空值。</summary>
    public string[] ProvinceHeaders { get; set; } = ["X-Province"];

    /// <summary>离线库的本地化语言优先级，用于取国家/省份的显示名。</summary>
    public string[] Locales { get; set; } = ["zh-CN", "en"];

    public bool HasDatabase => !string.IsNullOrWhiteSpace(DatabasePath);

    /// <summary>启动期校验：配了库就必须能读到文件，否则宁可起不来。</summary>
    public void Validate()
    {
        if (!HasDatabase)
        {
            return;
        }

        if (!File.Exists(DatabasePath))
        {
            throw new InvalidOperationException(
                $"GeoIp:DatabasePath 指向的文件不存在：{DatabasePath}。留空以仅使用 CDN 请求头，或下载 GeoLite2 库放到该路径。");
        }
    }
}
