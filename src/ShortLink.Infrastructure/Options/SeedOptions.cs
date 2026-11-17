namespace ShortLink.Infrastructure.Options;

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
