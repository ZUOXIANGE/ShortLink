namespace ShortLink.Api.Infrastructure;

/// <summary>
/// Quartz Dashboard 的宿主配置。Dashboard 可以启停/删除 Job，默认关闭且只读；
/// 开启后仍要求 Owner/Admin 登录，不能匿名暴露。
/// </summary>
public sealed class ShortLinkQuartzDashboardOptions
{
    public const string SectionName = "QuartzDashboard";

    /// <summary>默认关闭：Dashboard 具备调度写操作，属于额外攻击面。</summary>
    public bool Enabled { get; set; }

    /// <summary>Dashboard 挂载路径，必须是单个根路径（默认 <c>/quartz</c>）。</summary>
    public string Path { get; set; } = "/quartz";

    /// <summary>只读模式：隐藏暂停、恢复、立即触发、删除等所有写操作。</summary>
    public bool ReadOnly { get; set; } = true;

    public string NormalizedPath => NormalizePath(Path);

    public void Validate()
    {
        var path = NormalizedPath;
        if (!path.StartsWith('/'))
        {
            throw new InvalidOperationException("QuartzDashboard:Path 必须以 / 开头");
        }

        // 这些前缀已经被框架或本项目占用，挂进去会让命中顺序变得不可预测。
        string[] reserved = ["/api", "/health", "/_blazor", "/_framework", "/_content"];
        foreach (var prefix in reserved)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"QuartzDashboard:Path 不能使用保留前缀 {prefix}");
            }
        }
    }

    public static string NormalizePath(string? path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "/quartz";
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        var trimmed = value.TrimEnd('/');
        return trimmed.Length == 0 ? "/quartz" : trimmed;
    }
}
