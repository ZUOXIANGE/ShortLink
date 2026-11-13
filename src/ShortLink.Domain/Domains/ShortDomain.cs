using System.Text.RegularExpressions;

namespace ShortLink.Domain.Domains;

/// <summary>短链域名（可用于生成短链的对外主机名）。</summary>
public sealed partial class ShortDomain
{
    private ShortDomain()
    {
        Host = string.Empty;
        Scheme = "https";
        Name = string.Empty;
    }

    public long Id { get; private set; }

    /// <summary>对外主机名，已归一化为小写；允许显式端口（如 <c>localhost:5080</c>）。</summary>
    public string Host { get; private set; }

    /// <summary>对外协议，<c>http</c> 或 <c>https</c>。</summary>
    public string Scheme { get; private set; }

    public string Name { get; private set; }

    /// <summary>未命中任何域名时使用的兜底域名。</summary>
    public bool IsDefault { get; private set; }

    /// <summary>默认域名固定为 1，其余为 null；唯一索引保证全库至多一个默认域名。</summary>
    public int? DefaultSlot { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static ShortDomain Create(
        string host,
        string scheme,
        string name,
        bool isDefault,
        bool isEnabled,
        DateTime nowUtc)
        => new()
        {
            Host = NormalizeHost(host),
            Scheme = NormalizeScheme(scheme),
            Name = string.IsNullOrWhiteSpace(name) ? NormalizeHost(host) : name.Trim(),
            IsDefault = isDefault,
            DefaultSlot = isDefault ? 1 : null,
            IsEnabled = isEnabled,
            CreatedAtUtc = nowUtc,
        };

    public void Update(string scheme, string name, bool isDefault, bool isEnabled)
    {
        Scheme = NormalizeScheme(scheme);
        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name.Trim();
        }

        IsDefault = isDefault;
        DefaultSlot = isDefault ? 1 : null;
        IsEnabled = isEnabled;
    }

    public string BuildShortUrl(string code) => $"{Scheme}://{Host}/{code}";

    /// <summary>归一化主机名：去掉协议、路径、查询串与首尾空白，转为小写。</summary>
    public static string NormalizeHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            value = value[(schemeIndex + 3)..];
        }

        var slashIndex = value.IndexOfAny(['/', '?', '#']);
        if (slashIndex >= 0)
        {
            value = value[..slashIndex];
        }

        return value.TrimEnd('.');
    }

    /// <summary>去掉端口，用于 Host 头与配置不一致（反代、容器网络）时的兜底匹配。</summary>
    public static string StripPort(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(']') < colon ? host[..colon] : host;
    }

    public static bool IsValidHost(string? host) => !string.IsNullOrEmpty(host) && HostRegex().IsMatch(host);

    public static string NormalizeScheme(string? scheme)
        => string.Equals(scheme?.Trim(), "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:\d{2,5})?$")]
    private static partial Regex HostRegex();
}
