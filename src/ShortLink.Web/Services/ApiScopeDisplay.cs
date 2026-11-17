using MudBlazor;
using ShortLink.Contracts.Partner;

namespace ShortLink.Web.Services;

/// <summary>scope 的前端展示文案与颜色，避免页面与对话框各写一份。</summary>
public static class ApiScopeDisplay
{
    public static string Label(string scope) => scope switch
    {
        ApiScopes.LinksRead => "links:read · 查询短链",
        ApiScopes.LinksWrite => "links:write · 创建 / 更新 / 启停",
        ApiScopes.LinksDelete => "links:delete · 删除短链",
        ApiScopes.AnalyticsRead => "analytics:read · 访问统计",
        _ => scope,
    };

    public static Color ChipColor(string scope) => scope switch
    {
        ApiScopes.LinksRead => Color.Info,
        ApiScopes.LinksWrite => Color.Primary,
        ApiScopes.LinksDelete => Color.Error,
        ApiScopes.AnalyticsRead => Color.Secondary,
        _ => Color.Default,
    };
}
