namespace ShortLink.Application.Options;

/// <summary>跳转行为配置。</summary>
public sealed class RedirectOptions
{
    public const string SectionName = "Redirect";

    /// <summary>
    /// 防红中间页策略：<c>auto</c>（默认，按短链开关）/ <c>always</c>（所有短链都提示）/ <c>off</c>。
    /// </summary>
    public string WeChatInterstitial { get; set; } = "auto";

    /// <summary>密码表单与错误页是否允许被搜索引擎索引（默认 noindex）。</summary>
    public bool AllowIndexing { get; set; }
}
