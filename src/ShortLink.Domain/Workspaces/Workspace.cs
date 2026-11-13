namespace ShortLink.Domain.Workspaces;

/// <summary>
/// 工作区（团队）。短链、路由、安全事件与转化都归属工作区，
/// 为将来「一个部署多个团队」预留隔离边界；当前版本初始化时会创建默认工作区。
/// </summary>
public sealed class Workspace
{
    private Workspace()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public long Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>URL 友好标识，用于将来按工作区生成独立入口。</summary>
    public string Slug { get; private set; }

    /// <summary>默认工作区：无法确定归属时（如匿名跳转）落到这里。</summary>
    public bool IsDefault { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static Workspace Create(string name, string slug, bool isDefault, DateTime nowUtc)
        => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "默认工作区" : name.Trim(),
            Slug = NormalizeSlug(slug),
            IsDefault = isDefault,
            CreatedAtUtc = nowUtc,
        };

    public void Rename(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name.Trim();
        }
    }

    private static string NormalizeSlug(string slug)
    {
        var value = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return "default";
        }

        var chars = value
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();

        return new string(chars).Trim('-') is { Length: > 0 } normalized ? normalized : "default";
    }
}

/// <summary>工作区成员：把账号与工作区绑定，并给出该工作区内的角色。</summary>
public sealed class WorkspaceMember
{
    private WorkspaceMember()
    {
    }

    public long Id { get; private set; }

    public long WorkspaceId { get; private set; }

    public long UserId { get; private set; }

    public Users.UserRole Role { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static WorkspaceMember Create(long workspaceId, long userId, Users.UserRole role, DateTime nowUtc)
        => new()
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            CreatedAtUtc = nowUtc,
        };

    public void ChangeRole(Users.UserRole role) => Role = role;
}
