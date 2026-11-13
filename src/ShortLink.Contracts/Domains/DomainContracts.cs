namespace ShortLink.Contracts.Domains;

/// <summary>域名。</summary>
public sealed record DomainDto(
    long Id,
    string Host,
    string Scheme,
    string Name,
    bool IsDefault,
    bool IsEnabled,
    long LinkCount,
    DateTime CreatedAtUtc,
    bool CanManage = false);

/// <summary>新建域名。Host 只填主机名（如 <c>s.example.com</c>），协议由 <see cref="Scheme"/> 决定。</summary>
public sealed record CreateDomainRequest
{
    public string Host { get; set; } = string.Empty;

    public string Scheme { get; set; } = "https";

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public bool IsEnabled { get; set; } = true;
}

/// <summary>更新域名。Host 创建后不可变，避免既有短链失效。</summary>
public sealed record UpdateDomainRequest
{
    public string Name { get; set; } = string.Empty;

    public string Scheme { get; set; } = "https";

    public bool IsDefault { get; set; }

    public bool IsEnabled { get; set; } = true;
}
