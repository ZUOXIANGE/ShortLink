using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using ShortLink.Domain.Links;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Links;

/// <summary>短链的访问控制规则：工作区隔离 + 角色 + 归属，集中一处避免各处口径不一致。</summary>
public static class LinkPolicy
{
    /// <summary>当前操作者是否能修改该短链。</summary>
    public static bool CanModify(Link link, ICurrentUser user)
    {
        if (!user.AtLeast(UserRole.Member))
        {
            return false;
        }

        if (user.AtLeast(UserRole.Admin))
        {
            return true;
        }

        // 成员只能改自己创建的短链；管理员可改工作区内全部。
        return link.CreatedByUserId is { } owner && owner == user.UserId;
    }

    public static void EnsureCanModify(Link link, ICurrentUser user)
    {
        if (!user.AtLeast(UserRole.Member))
        {
            throw new BusinessException(BizCodes.Forbidden, "当前角色为只读，无法修改短链");
        }

        if (!CanModify(link, user))
        {
            throw new BusinessException(BizCodes.Forbidden, "只能操作自己创建的短链");
        }
    }
}
