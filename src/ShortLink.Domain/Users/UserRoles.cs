namespace ShortLink.Domain.Users;

/// <summary>
/// 账号角色。既作为平台级角色，也作为工作区成员角色：
/// <para>
/// <see cref="Owner"/> 最高（可转让工作区、管理所有成员），<see cref="Admin"/> 可管理域名与用户，
/// <see cref="Member"/> 只能管理短链，<see cref="Viewer"/> 只读。
/// </para>
/// 枚举值保持既有编号不变（0/1/2），Owner 追加为 3，避免历史数据迁移；
/// 角色高低一律通过 <see cref="UserRoles.Rank"/> 判断，不要直接比较枚举值大小。
/// </summary>
public enum UserRole
{
    /// <summary>管理员：可管理域名、短链与用户。</summary>
    Admin = 0,

    /// <summary>成员：可管理短链，不可管理域名。</summary>
    Member = 1,

    /// <summary>只读：仅可查看数据。</summary>
    Viewer = 2,

    /// <summary>所有者：工作区最高权限，可管理成员与全部资源。</summary>
    Owner = 3,
}

/// <summary>角色与令牌声明值之间的换算，以及权限比较。</summary>
public static class UserRoles
{
    public const string Owner = "owner";

    public const string Admin = "admin";

    public const string Member = "member";

    public const string Viewer = "viewer";

    /// <summary>JWT <c>role</c> 声明使用小写短名，避免受 <c>ClaimTypes.Role</c> 映射影响。</summary>
    public static string ToClaimValue(this UserRole role) => role switch
    {
        UserRole.Owner => Owner,
        UserRole.Admin => Admin,
        UserRole.Member => Member,
        _ => Viewer,
    };

    public static bool TryParse(string? value, out UserRole role)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Owner:
                role = UserRole.Owner;
                return true;
            case Admin:
                role = UserRole.Admin;
                return true;
            case Member:
                role = UserRole.Member;
                return true;
            case Viewer:
                role = UserRole.Viewer;
                return true;
            default:
                role = UserRole.Viewer;
                return false;
        }
    }

    public static int Rank(UserRole role) => role switch
    {
        UserRole.Owner => 4,
        UserRole.Admin => 3,
        UserRole.Member => 2,
        _ => 1,
    };

    /// <summary>当前角色是否达到要求（如 Member 满足 Viewer 要求）。</summary>
    public static bool AtLeast(this UserRole role, UserRole required) => Rank(role) >= Rank(required);

    public static string ToDisplayName(this UserRole role) => role switch
    {
        UserRole.Owner => "所有者",
        UserRole.Admin => "管理员",
        UserRole.Member => "成员",
        _ => "只读",
    };
}
