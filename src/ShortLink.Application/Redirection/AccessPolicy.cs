using ShortLink.Application.Abstractions;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Redirection;

/// <summary>访问放行判定结果。</summary>
public enum AccessDecision
{
    /// <summary>正常放行并计数。</summary>
    Allow = 0,

    /// <summary>放行但不计数（机器人「只统计真实用户」策略）。</summary>
    IgnoreCount = 1,

    /// <summary>命中 IP 黑名单。</summary>
    BlockIp = 2,

    /// <summary>不在 IP 白名单内。</summary>
    IpNotAllowed = 3,

    /// <summary>命中机器人拦截策略。</summary>
    BlockBot = 4,
}

/// <summary>
/// 访问策略判定：纯函数，不碰数据库，便于单测与在跳转热路径上零成本调用。
/// 判定顺序为「黑名单 → 白名单 → 机器人」，黑名单优先，避免被封禁 IP 通过白名单绕过。
/// </summary>
public static class AccessPolicy
{
    public static AccessDecision Evaluate(CachedLink link, string? ip, bool isBot)
    {
        var normalizedIp = IpAccessList.Normalize(ip);

        if (!link.BlockList.IsEmpty && link.BlockList.Contains(normalizedIp))
        {
            return AccessDecision.BlockIp;
        }

        if (!link.AllowList.IsEmpty && !link.AllowList.Contains(normalizedIp))
        {
            return AccessDecision.IpNotAllowed;
        }

        if (!isBot)
        {
            return AccessDecision.Allow;
        }

        return (LinkBotPolicy)link.BotPolicy switch
        {
            LinkBotPolicy.Block => AccessDecision.BlockBot,
            LinkBotPolicy.IgnoreCount => AccessDecision.IgnoreCount,
            _ => AccessDecision.Allow,
        };
    }

    /// <summary>微信 / QQ 内置浏览器判定，用于防红中间页。</summary>
    public static bool IsWeChatOrQQ(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return false;
        }

        return userAgent.Contains("MicroMessenger", StringComparison.OrdinalIgnoreCase)
               || userAgent.Contains("QQ/", StringComparison.OrdinalIgnoreCase)
               || userAgent.Contains("Qzone", StringComparison.OrdinalIgnoreCase);
    }
}
