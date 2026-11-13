namespace ShortLink.Domain.Links;

/// <summary>短链状态。</summary>
public enum LinkStatus
{
    /// <summary>已停用，访问时返回 404 落地页。</summary>
    Disabled = 0,

    /// <summary>启用中，正常跳转。</summary>
    Enabled = 1,
}

/// <summary>跳转类型，取值与 HTTP 状态码一致。</summary>
public enum LinkRedirectType
{
    /// <summary>301 永久重定向，浏览器与 CDN 会长期缓存，适合内容不变的链接。</summary>
    Permanent301 = 301,

    /// <summary>302 临时重定向，默认值，便于随时修改目标地址。</summary>
    Temporary302 = 302,

    /// <summary>307 临时重定向且保持原请求方法。</summary>
    Temporary307 = 307,

    /// <summary>308 永久重定向且保持原请求方法。</summary>
    Permanent308 = 308,
}

/// <summary>访问校验结果。</summary>
public enum LinkAccessResult
{
    /// <summary>可跳转。</summary>
    Ok = 0,

    /// <summary>已停用。</summary>
    Disabled = 1,

    /// <summary>已过期。</summary>
    Expired = 2,

    /// <summary>需要访问密码。</summary>
    PasswordRequired = 3,

    /// <summary>尚未到生效时间。</summary>
    NotStarted = 4,

    /// <summary>点击量已达上限。</summary>
    ClickCapped = 5,
}

/// <summary>
/// 新建短链的字段集合。参数较多，用草稿对象收口，避免长参数列表，
/// 同时保证聚合根的创建规则集中在一处。
/// </summary>
public sealed class LinkDraft
{
    public long DomainId { get; init; }

    public long WorkspaceId { get; init; }

    public string Code { get; init; } = string.Empty;

    public string TargetUrl { get; init; } = string.Empty;

    public string? Title { get; init; }

    public LinkRedirectType RedirectType { get; init; } = LinkRedirectType.Temporary302;

    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>生效时间；为空表示立即生效。</summary>
    public DateTime? StartsAtUtc { get; init; }

    public string? PasswordHash { get; init; }

    public string? Remark { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>点击封顶；达到后自动停用。为空表示不限制。</summary>
    public long? MaxClicks { get; init; }

    public LinkBotPolicy BotPolicy { get; init; } = LinkBotPolicy.Allow;

    /// <summary>IP 白名单，逗号或换行分隔，支持 CIDR；为空表示不限制。</summary>
    public string? IpAllowList { get; init; }

    /// <summary>IP 黑名单，逗号或换行分隔，支持 CIDR。</summary>
    public string? IpBlockList { get; init; }

    /// <summary>条件路由全部未命中时的兜底地址；为空则回落到 <see cref="Link.TargetUrl"/>。</summary>
    public string? FallbackUrl { get; init; }

    /// <summary>是否在微信 / QQ 内展示防红中间页。</summary>
    public bool AntiBlockEnabled { get; init; }

    public long? CreatedByUserId { get; init; }
}

/// <summary>短链聚合根。</summary>
public sealed class Link
{
    private Link()
    {
        Code = string.Empty;
        TargetUrl = string.Empty;
    }

    public long Id { get; private set; }

    /// <summary>所属工作区。单工作区部署下恒为默认工作区。</summary>
    public long WorkspaceId { get; private set; }

    public long DomainId { get; private set; }

    /// <summary>短码，与 <see cref="DomainId"/> 组合唯一。</summary>
    public string Code { get; private set; }

    public string TargetUrl { get; private set; }

    public string? Title { get; private set; }

    public LinkStatus Status { get; private set; }

    public LinkRedirectType RedirectType { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public DateTime? StartsAtUtc { get; private set; }

    /// <summary>访问密码哈希；为空表示无密码。</summary>
    public string? PasswordHash { get; private set; }

    public string? Remark { get; private set; }

    public long ClickCount { get; private set; }

    public long? MaxClicks { get; private set; }

    public LinkBotPolicy BotPolicy { get; private set; }

    public string? IpAllowList { get; private set; }

    public string? IpBlockList { get; private set; }

    public string? FallbackUrl { get; private set; }

    public bool AntiBlockEnabled { get; private set; }

    /// <summary>转化回调的 HMAC 密钥；仅通过专用接口下发。</summary>
    public string? ConversionSecret { get; private set; }

    /// <summary>跨数据库的乐观并发令牌，每次管理写操作都会更新。</summary>
    public string ConcurrencyToken { get; private set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? LastAccessedAtUtc { get; private set; }

    public long? CreatedByUserId { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public bool HasConversionSecret => !string.IsNullOrEmpty(ConversionSecret);

    public static Link Create(LinkDraft draft, DateTime nowUtc)
    {
        var link = new Link
        {
            WorkspaceId = draft.WorkspaceId,
            DomainId = draft.DomainId,
            Code = draft.Code,
            ClickCount = 0,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CreatedByUserId = draft.CreatedByUserId,

            // 密码走单独通道：Update 只处理明文可写字段，哈希由调用方传入。
            PasswordHash = string.IsNullOrEmpty(draft.PasswordHash) ? null : draft.PasswordHash,
        };

        link.Update(draft, nowUtc);
        return link;
    }

    /// <summary>
    /// 更新可变字段。短码与域名不在可变范围内：已经分发出去的短链必须始终指向同一条记录。
    /// 密码不在此处修改，由 <see cref="SetPassword"/> 单独处理（需要哈希）。
    /// </summary>
    public void Update(LinkDraft draft, DateTime nowUtc)
    {
        TargetUrl = draft.TargetUrl.Trim();
        Title = TrimToNull(draft.Title, 200);
        RedirectType = draft.RedirectType;
        ExpiresAtUtc = draft.ExpiresAtUtc;
        StartsAtUtc = draft.StartsAtUtc;
        Remark = TrimToNull(draft.Remark, 500);
        Status = draft.Enabled ? LinkStatus.Enabled : LinkStatus.Disabled;
        MaxClicks = draft.MaxClicks is > 0 ? draft.MaxClicks : null;
        BotPolicy = draft.BotPolicy;
        IpAllowList = TrimToNull(draft.IpAllowList, 2000);
        IpBlockList = TrimToNull(draft.IpBlockList, 2000);
        FallbackUrl = TrimToNull(draft.FallbackUrl, 2048);
        AntiBlockEnabled = draft.AntiBlockEnabled;
        Touch(nowUtc);
    }

    /// <summary>设置或清除访问密码；传入 null 表示不修改，传空串表示清除。</summary>
    public void SetPassword(string? passwordHash, DateTime nowUtc)
    {
        PasswordHash = string.IsNullOrEmpty(passwordHash) ? null : passwordHash;
        Touch(nowUtc);
    }

    public void SetConversionSecret(string secret, DateTime nowUtc)
    {
        ConversionSecret = secret;
        Touch(nowUtc);
    }

    /// <summary>批量改域名：仅切换所属域名，短码保持不变（目标域名需提前校验唯一性）。</summary>
    public void MoveToDomain(long domainId, DateTime nowUtc)
    {
        DomainId = domainId;
        Touch(nowUtc);
    }

    public void ChangeExpiry(DateTime? expiresAtUtc, DateTime nowUtc)
    {
        ExpiresAtUtc = expiresAtUtc;
        Touch(nowUtc);
    }

    /// <summary>批量改生效时间。</summary>
    public void ChangeStart(DateTime? startsAtUtc, DateTime nowUtc)
    {
        StartsAtUtc = startsAtUtc;
        Touch(nowUtc);
    }

    public void Enable(DateTime nowUtc)
    {
        Status = LinkStatus.Enabled;
        Touch(nowUtc);
    }

    public void Disable(DateTime nowUtc)
    {
        Status = LinkStatus.Disabled;
        Touch(nowUtc);
    }

    public void Toggle(DateTime nowUtc)
    {
        if (Status == LinkStatus.Enabled)
        {
            Disable(nowUtc);
        }
        else
        {
            Enable(nowUtc);
        }
    }

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc is { } expiresAt && expiresAt <= nowUtc;

    public bool IsNotStarted(DateTime nowUtc) => StartsAtUtc is { } startsAt && startsAt > nowUtc;

    public bool IsClickCapped => MaxClicks is { } max && ClickCount >= max;

    /// <summary>访问前的领域校验；不含密码正确性判定（由应用层用哈希器验证）。</summary>
    public LinkAccessResult CheckAccess(DateTime nowUtc, bool passwordProvided)
    {
        if (Status == LinkStatus.Disabled)
        {
            return LinkAccessResult.Disabled;
        }

        if (IsNotStarted(nowUtc))
        {
            return LinkAccessResult.NotStarted;
        }

        if (IsExpired(nowUtc))
        {
            return LinkAccessResult.Expired;
        }

        if (IsClickCapped)
        {
            return LinkAccessResult.ClickCapped;
        }

        if (HasPassword && !passwordProvided)
        {
            return LinkAccessResult.PasswordRequired;
        }

        return LinkAccessResult.Ok;
    }

    /// <summary>记录一次点击。批量落库时由仓储使用增量更新，不依赖实体跟踪。</summary>
    public void RegisterClick(DateTime nowUtc, long delta = 1)
    {
        if (delta <= 0)
        {
            return;
        }

        ClickCount += delta;
        LastAccessedAtUtc = nowUtc;
    }

    /// <summary>标记一次管理写操作，供路由保存等跨表操作串行化并发版本。</summary>
    public void Touch(DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        ConcurrencyToken = Guid.NewGuid().ToString("N");
    }

    private static string? TrimToNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
