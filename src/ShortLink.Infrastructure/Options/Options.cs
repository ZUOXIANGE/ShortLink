namespace ShortLink.Infrastructure.Options;

/// <summary>数据库连接与 provider 选择。</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary><c>MySql</c>（生产默认）/ <c>Sqlite</c>（本地零依赖）/ <c>PostgreSql</c>。</summary>
    public string Provider { get; set; } = "MySql";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>MySQL 服务端版本，供 provider 生成方言，不做连接级 AutoDetect（启动期不回源）。</summary>
    public string MySqlVersion { get; set; } = "8.0.36";
}

/// <summary>JWT 配置。签名密钥长度必须 ≥ 32 字节。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ShortLink";

    public string Audience { get; set; } = "ShortLink.Admin";

    public string SigningKey { get; set; } = string.Empty;

    public int ExpiresMinutes { get; set; } = 720;
}

/// <summary>缓存配置。</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>留空则只用进程内缓存；配置后启用 Redis 分布式缓存 + 背板。</summary>
    public string? RedisConnectionString { get; set; }

    public int LinkTtlSeconds { get; set; } = 60;

    public int DomainTtlSeconds { get; set; } = 60;
}

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

/// <summary>初始化种子。</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string AdminUserName { get; set; } = "admin";

    /// <summary>仅开发环境可在 appsettings.Development.json 提供默认值；其他环境必须外部注入。</summary>
    public string AdminPassword { get; set; } = string.Empty;

    public string AdminDisplayName { get; set; } = "管理员";

    /// <summary>首次启动时写入的默认短链域名，指向本地 API。</summary>
    public string DefaultDomainHost { get; set; } = "localhost:5080";

    public string DefaultDomainScheme { get; set; } = "http";
}
