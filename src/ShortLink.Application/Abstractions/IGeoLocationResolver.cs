namespace ShortLink.Application.Abstractions;

/// <summary>
/// 一次访问的地理归属。
/// <para>
/// 字段口径与请求头一致：<see cref="Country"/> 存 ISO 3166-1 alpha-2（如 <c>CN</c>），
/// 不存国家名。混用代码与名称会把同一国家拆成两个维度，而这类误差只在报表里表现为
/// 「某个国家少了一半流量」，几乎不可能被主动发现。
/// </para>
/// </summary>
public sealed record GeoLocation(string? Country, string? Province)
{
    /// <summary>解析不出任何信息时的占位值。</summary>
    public static GeoLocation Empty { get; } = new(null, null);

    public bool IsEmpty => Country is null && Province is null;
}

/// <summary>
/// 访问来源地解析。两种实现：读 CDN 请求头（零依赖、精度取决于 CDN），
/// 或查离线 IP 库（不依赖 CDN、精度取决于库）。
/// </summary>
public interface IGeoLocationResolver
{
    /// <summary>按客户端 IP 解析；拿不到有效结果时返回 <see cref="GeoLocation.Empty"/>。</summary>
    GeoLocation Resolve(string? ip);
}
