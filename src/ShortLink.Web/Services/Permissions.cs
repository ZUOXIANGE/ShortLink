using ShortLink.Contracts.Links;

namespace ShortLink.Web.Services;

/// <summary>
/// 前端角色判定。与后端 <c>AuthorizationSetup</c> 的策略保持同一口径：
/// <c>viewer</c> 只读、<c>member</c> 可写自己创建的、<c>admin</c> 可写全部、<c>owner</c> 可管账号。
/// <para>
/// 这里只用于「不显示按钮」，真正的权限判定始终在服务端；即使前端判断出错，
/// 也只会得到 11003 之类的拒绝码，不会越权。
/// </para>
/// </summary>
public sealed class Permissions(TokenStore tokens)
{
    public string Role => tokens.User?.Role?.ToLowerInvariant() ?? "viewer";

    public string RoleName => tokens.User?.RoleName ?? "只读";

    public long? UserId => tokens.User?.Id;

    /// <summary>可执行写操作（新建 / 编辑自己的 / 批量）。</summary>
    public bool CanWrite => Rank(Role) >= Rank("member");

    /// <summary>可管理全局资源（他人短链、安全事件、账号）。</summary>
    public bool CanManage => Rank(Role) >= Rank("admin");

    public bool IsOwner => Rank(Role) >= Rank("owner");

    /// <summary>是否可修改指定短链：管理员不限，成员仅限自己创建的。</summary>
    public bool CanModify(LinkDto? link)
    {
        if (CanManage)
        {
            return true;
        }

        if (!CanWrite)
        {
            return false;
        }

        // 新建时没有既有对象；编辑时 creator 为空视为历史数据，允许有写权限的人处理。
        return link is null || link.CreatedByUserId is null || link.CreatedByUserId == UserId;
    }

    public static int Rank(string? role) => role?.ToLowerInvariant() switch
    {
        "owner" => 3,
        "admin" => 2,
        "member" => 1,
        _ => 0,
    };

    public static string RoleDisplayName(string? role) => role?.ToLowerInvariant() switch
    {
        "owner" => "所有者",
        "admin" => "管理员",
        "member" => "成员",
        _ => "只读",
    };
}
