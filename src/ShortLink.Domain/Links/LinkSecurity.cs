using System.Net;

namespace ShortLink.Domain.Links;

/// <summary>机器人访问策略。</summary>
public enum LinkBotPolicy
{
    /// <summary>放行并照常计数（默认）。</summary>
    Allow = 0,

    /// <summary>拦截：返回 403 落地页并记录安全事件，不计入点击。</summary>
    Block = 1,

    /// <summary>放行跳转但不计入点击（用于把爬虫流量从统计口径里剔除）。</summary>
    IgnoreCount = 2,
}

/// <summary>
/// IP 访问名单。支持单 IP 与 CIDR，逗号 / 分号 / 换行分隔；无法解析的条目记录在
/// <see cref="InvalidEntries"/> 中由上层提示，匹配时直接忽略，保证坏配置不会阻断全部流量。
/// </summary>
public sealed class IpAccessList
{
    private readonly IReadOnlyList<IPNetwork> _networks;

    private IpAccessList(IReadOnlyList<IPNetwork> networks, IReadOnlyList<string> invalidEntries)
    {
        _networks = networks;
        InvalidEntries = invalidEntries;
    }

    public static IpAccessList Empty { get; } = new([], []);

    public bool IsEmpty => _networks.Count == 0;

    public int Count => _networks.Count;

    public IReadOnlyList<string> InvalidEntries { get; }

    public static IpAccessList Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var networks = new List<IPNetwork>();
        var invalid = new List<string>();

        foreach (var entry in text.Split([',', ';', '\n', '\r', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseNetwork(entry, out var network))
            {
                networks.Add(network);
            }
            else
            {
                invalid.Add(entry);
            }
        }

        return new IpAccessList(networks, invalid);
    }

    public bool Contains(string? address)
        => IPAddress.TryParse(Normalize(address), out var ip) && Contains(ip);

    public bool Contains(IPAddress? address)
    {
        if (address is null || _networks.Count == 0)
        {
            return false;
        }

        foreach (var network in _networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把 <c>::ffff:1.2.3.4</c> 这类映射地址还原成 IPv4，避免绕过 IPv4 规则。</summary>
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var value = address.Trim();
        return IPAddress.TryParse(value, out var ip) && ip.IsIPv4MappedToIPv6
            ? ip.MapToIPv4().ToString()
            : value;
    }

    private static bool TryParseNetwork(string entry, out IPNetwork network)
    {
        network = default;
        var value = entry.Trim();

        if (value.Length == 0)
        {
            return false;
        }

        // 允许省略前缀长度：单个 IP 视为 /32（IPv4）或 /128（IPv6）。
        if (!value.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(value, out var single))
            {
                return false;
            }

            value = $"{single}/{(single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)}";
        }

        return IPNetwork.TryParse(value, out network);
    }
}

/// <summary>URL 规则扫描结果。</summary>
public sealed record UrlScanResult(bool IsBlocked, IReadOnlyList<string> Violations, IReadOnlyList<string> Warnings)
{
    public static UrlScanResult Clean { get; } = new(false, [], []);
}

/// <summary>
/// 目标地址规则扫描：拦截明显的协议注入、内网地址、同域回环等风险目标。
/// <para>
/// 这里是「静态规则」而非黑名单情报：只判断地址本身是否可能被用于钓鱼/内网穿透，
/// 不做内容信誉判定，因此误杀率极低，可以默认开启拦截。
/// </para>
/// </summary>
public static class UrlScanner
{
    private static readonly string[] DangerousSchemes = ["javascript", "data", "vbscript", "file", "about"];

    public static UrlScanResult Scan(string? url, IReadOnlySet<string>? reservedHosts = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UrlScanResult(true, ["目标地址不能为空"], []);
        }

        var value = url.Trim();

        if (value.Length > 2048)
        {
            return new UrlScanResult(true, ["目标地址长度超过 2048 个字符"], []);
        }

        if (value.Any(char.IsControl))
        {
            return new UrlScanResult(true, ["目标地址包含不可见控制字符"], []);
        }

        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            var scheme = value[..colonIndex].ToLowerInvariant();
            if (DangerousSchemes.Contains(scheme, StringComparer.Ordinal))
            {
                return new UrlScanResult(true, [$"不允许使用 {scheme}: 协议"], []);
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return new UrlScanResult(true, ["目标地址不是合法的绝对地址"], []);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new UrlScanResult(true, ["目标地址必须是 http / https 协议"], []);
        }

        var violations = new List<string>();
        var warnings = new List<string>();
        var host = uri.DnsSafeHost.Trim('[', ']');

        if (string.IsNullOrWhiteSpace(host))
        {
            violations.Add("目标地址缺少主机名");
        }
        else if (reservedHosts is not null && reservedHosts.Contains(host))
        {
            violations.Add("目标地址指向本服务的短链域名，会形成跳转环");
        }
        else if (IsPrivateHost(host))
        {
            violations.Add("目标地址指向内网 / 本机地址，可能存在内网穿透风险");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            warnings.Add("目标地址包含 user:password@ 形式的凭据，常见于钓鱼链接");
        }

        if (warnings.Count == 0 && LooksLikeOpenRedirect(uri))
        {
            warnings.Add("目标地址带有嵌套跳转参数，请确认落地页可信");
        }

        return new UrlScanResult(violations.Count > 0, violations, warnings);
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] switch
            {
                10 => true,
                127 => true,
                0 => true,
                172 => bytes[1] >= 16 && bytes[1] <= 31,
                192 => bytes[1] == 168,
                169 => bytes[1] == 254,
                _ => false,
            },
            System.Net.Sockets.AddressFamily.InterNetworkV6 => ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Any),
            _ => false,
        };
    }

    private static bool LooksLikeOpenRedirect(Uri uri)
    {
        var query = uri.Query;
        if (query.Length < 2)
        {
            return false;
        }

        return query.Contains("url=", StringComparison.OrdinalIgnoreCase)
               || query.Contains("redirect=", StringComparison.OrdinalIgnoreCase)
               || query.Contains("target=", StringComparison.OrdinalIgnoreCase);
    }
}
