namespace ShortLink.Contracts;

/// <summary>
/// 全局业务码表。按业务域分段，与 HTTP 状态码解耦；只能新增，不得复用或改变既有含义。
/// 文档：<c>docs/api/biz-codes.md</c>。
/// </summary>
public static class BizCodes
{
    /// <summary>成功。</summary>
    public const int Success = 200;

    // ── 公共段 ───────────────────────────────────────────────
    /// <summary>已受理：异步任务已入队。</summary>
    public const int Accepted = 20201;

    /// <summary>参数校验失败，明细见 <c>errors</c>。</summary>
    public const int InvalidArgument = 40001;

    /// <summary>API 版本缺失或不受支持。</summary>
    public const int ApiVersionUnsupported = 40002;

    /// <summary>未认证或令牌失效。</summary>
    public const int Unauthorized = 40101;

    /// <summary>已认证但无权限。</summary>
    public const int Forbidden = 40301;

    /// <summary>资源不存在。</summary>
    public const int NotFound = 40401;

    /// <summary>资源冲突。</summary>
    public const int Conflict = 40901;

    /// <summary>请求过于频繁。</summary>
    public const int RateLimited = 42901;

    /// <summary>客户端已取消请求。</summary>
    public const int ClientClosedRequest = 49901;

    /// <summary>服务器内部错误。</summary>
    public const int InternalError = 50001;

    // ── 认证与用户 11xxx ──────────────────────────────────────
    public const int InvalidCredentials = 11001;
    public const int UserDisabled = 11002;
    public const int UserNotFound = 11003;
    public const int OldPasswordIncorrect = 11004;
    public const int PasswordTooWeak = 11005;
    public const int UserNameExists = 11006;

    /// <summary>角色取值不在允许范围内。</summary>
    public const int UserRoleInvalid = 11007;

    /// <summary>不能移除最后一个所有者 / 管理员。</summary>
    public const int UserLastOwner = 11008;

    /// <summary>不能对自己执行该操作（禁用、降级、删除）。</summary>
    public const int UserSelfOperation = 11009;

    // ── 域名 12xxx ───────────────────────────────────────────
    public const int DomainNotFound = 12001;
    public const int DomainHostExists = 12002;
    public const int DomainInUse = 12003;
    public const int DomainHostInvalid = 12004;
    public const int DomainLastDisabled = 12005;

    // ── 短链 13xxx ───────────────────────────────────────────
    public const int LinkNotFound = 13001;
    public const int LinkCodeExists = 13002;
    public const int LinkCodeReserved = 13003;
    public const int LinkDisabled = 13004;
    public const int LinkExpired = 13005;
    public const int LinkPasswordRequired = 13006;
    public const int LinkPasswordIncorrect = 13007;
    public const int LinkTargetInvalid = 13008;
    public const int LinkCodeInvalid = 13009;
    public const int LinkQuotaExceeded = 13010;

    /// <summary>尚未到生效时间。</summary>
    public const int LinkNotStarted = 13011;

    /// <summary>点击量已达封顶阈值。</summary>
    public const int LinkClickCapped = 13012;

    /// <summary>目标地址被安全规则拒绝。</summary>
    public const int LinkTargetRejected = 13013;

    /// <summary>批量操作条数超过上限。</summary>
    public const int LinkBatchTooLarge = 13014;

    /// <summary>短码在该域名下冲突，批量改域名时逐条返回。</summary>
    public const int LinkCodeConflictInDomain = 13015;

    /// <summary>来源 IP 不在允许范围内。</summary>
    public const int LinkIpNotAllowed = 13016;

    /// <summary>命中机器人拦截策略。</summary>
    public const int LinkBotBlocked = 13017;

    /// <summary>点击封顶已停用该短链。</summary>
    public const int LinkClickCapReached = 13018;

    // ── 条件路由 14xxx ───────────────────────────────────────
    public const int RouteNotFound = 14001;
    public const int RouteInvalid = 14002;
    public const int RouteNameDuplicate = 14003;
    public const int RouteLimitExceeded = 14004;

    // ── 转化回调 15xxx ───────────────────────────────────────
    public const int ConversionSignatureInvalid = 15001;
    public const int ConversionPayloadInvalid = 15002;
    public const int ConversionTimestampExpired = 15003;

    // ── 安全 16xxx ───────────────────────────────────────────
    public const int SecurityEventNotFound = 16001;
    public const int AbuseReportInvalid = 16002;

    // ── 导出 17xxx ───────────────────────────────────────────
    public const int ExportRangeTooLarge = 17001;

    /// <summary>把业务码映射为可展示的中文文案（仅用于未携带 msg 的场景）。</summary>
    public static string Describe(int code) => code switch
    {
        Success => "success",
        Accepted => "已受理",
        InvalidArgument => "参数校验失败",
        ApiVersionUnsupported => "接口版本不受支持",
        Unauthorized => "登录状态已失效，请重新登录",
        Forbidden => "没有访问权限",
        NotFound => "资源不存在",
        Conflict => "数据已被修改，请重试",
        RateLimited => "请求过于频繁，请稍后再试",
        ClientClosedRequest => "请求已取消",
        InternalError => "系统繁忙，请稍后重试",
        InvalidCredentials => "用户名或密码错误",
        UserDisabled => "账号已被禁用",
        UserNotFound => "用户不存在",
        OldPasswordIncorrect => "原密码不正确",
        PasswordTooWeak => "密码强度不足，至少 8 位且包含字母与数字",
        UserNameExists => "用户名已存在",
        UserRoleInvalid => "角色取值不合法",
        UserLastOwner => "至少需要保留一个所有者",
        UserSelfOperation => "不能对当前登录账号执行该操作",
        DomainNotFound => "域名不存在",
        DomainHostExists => "该域名已存在",
        DomainInUse => "域名下仍有短链，无法删除",
        DomainHostInvalid => "域名格式不合法",
        DomainLastDisabled => "至少需要保留一个可用域名",
        LinkNotFound => "短链接不存在",
        LinkCodeExists => "短码已被占用",
        LinkCodeReserved => "该短码属于系统保留字",
        LinkDisabled => "短链接已停用",
        LinkExpired => "短链接已过期",
        LinkPasswordRequired => "该链接需要访问密码",
        LinkPasswordIncorrect => "访问密码不正确",
        LinkTargetInvalid => "目标地址不合法",
        LinkCodeInvalid => "短码格式不合法",
        LinkQuotaExceeded => "短链数量已达上限",
        LinkNotStarted => "短链接尚未生效",
        LinkClickCapped => "短链接点击量已达上限",
        LinkTargetRejected => "目标地址未通过安全规则校验",
        LinkBatchTooLarge => "单次批量操作不能超过 500 条",
        LinkCodeConflictInDomain => "目标域名下短码已存在",
        LinkIpNotAllowed => "来源 IP 不在允许范围内",
        LinkBotBlocked => "自动访问已被拦截",
        LinkClickCapReached => "点击量达上限，短链已自动停用",
        RouteNotFound => "路由规则不存在",
        RouteInvalid => "路由规则不合法",
        RouteNameDuplicate => "同一短链下的分支名称不能重复",
        RouteLimitExceeded => "单条短链最多配置 20 个分支",
        ConversionSignatureInvalid => "转化回调签名校验失败",
        ConversionPayloadInvalid => "转化回调参数不合法",
        ConversionTimestampExpired => "转化回调时间戳已过期",
        SecurityEventNotFound => "安全事件不存在",
        AbuseReportInvalid => "举报内容不合法",
        ExportRangeTooLarge => "导出范围过大，请缩小筛选条件",
        _ => "未知业务状态",
    };
}
