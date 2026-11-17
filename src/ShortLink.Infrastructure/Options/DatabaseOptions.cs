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
