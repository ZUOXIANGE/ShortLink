using System.ComponentModel;
using ModelContextProtocol.Server;
using ShortLink.Application.Links;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Users;
using ShortLink.Mcp.Models;

namespace ShortLink.Mcp.Tools;

/// <summary>
/// 短链读写工具。
/// <para>
/// 这一层只做三件事：把工具参数翻译成应用层请求、调用 <see cref="LinkService"/>、
/// 把结果投影成模型可读的少量字段。<b>不写任何业务规则</b>——
/// 角色、归属、工作区隔离、短码校验都在应用层与领域层，这里再判一遍必然两套口径。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LinkTools(LinkService links, McpToolRunner runner)
{
    private const string GetToolName = "shortlink_get";

    [McpServerTool(Name = "shortlink_create", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description(
        "创建一条短链。targetUrl 必填（http/https 绝对地址）；code 留空则由服务端生成随机短码。" +
        "写入操作，成功返回含短链地址与 ID 的摘要。需要「成员」及以上角色。")]
    public Task<LinkSummary> CreateAsync(
        [Description("目标地址，必须是 http:// 或 https:// 开头的绝对 URL")] string targetUrl,
        [Description("自定义短码，3-32 位字母/数字/-/_；留空由服务端生成")] string? code = null,
        [Description("标题，便于在后台检索，可留空")] string? title = null,
        [Description("归属域名 ID；留空使用默认域名。不确定时先调 shortlink_list_domains")] long? domainId = null,
        [Description("过期时间，ISO 8601（如 2026-12-31T23:59:59Z）；留空为永久有效")] string? expiresAtUtc = null,
        [Description("访问密码；留空为无密码访问")] string? password = null,
        [Description("点击上限，达到后自动停用；留空为不限")] long? maxClicks = null,
        [Description("跳转类型：301 / 302（默认） / 307 / 308")] int redirectType = 302,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_create",
            UserRole.Member,
            async ct =>
            {
                McpToolRunner.EnsureArgument(!string.IsNullOrWhiteSpace(targetUrl), "targetUrl 不能为空");

                var request = new CreateLinkRequest
                {
                    TargetUrl = targetUrl.Trim(),
                    Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
                    Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                    DomainId = domainId,
                    ExpiresAtUtc = ParseUtc(expiresAtUtc, nameof(expiresAtUtc)),
                    Password = string.IsNullOrWhiteSpace(password) ? null : password,
                    MaxClicks = maxClicks,
                    RedirectType = redirectType,
                };

                var created = await links.CreateAsync(request, ct).ConfigureAwait(false);
                return LinkSummary.From(created);
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_get", ReadOnly = true)]
    [Description(
        "按 ID 或短链地址查询单条短链详情。linkId 与 shortUrl 二选一；" +
        "已知短链地址时用 shortUrl（短码只在域名内唯一，仅有短码时需工作区只有一个域名）。只读操作。")]
    public Task<LinkSummary> GetAsync(
        [Description("短链 ID；与 shortUrl 二选一")] long? linkId = null,
        [Description("完整短链地址，如 https://s.example.com/abc123；与 linkId 二选一")] string? shortUrl = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            GetToolName,
            UserRole.Viewer,
            async ct =>
            {
                var dto = await ResolveAsync(linkId, shortUrl, ct).ConfigureAwait(false);
                return LinkSummary.From(dto);
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_list", ReadOnly = true)]
    [Description(
        "分页查询当前工作区的短链列表，可按关键词与状态筛选。只读操作。" +
        "想找「某条短链」时优先用 shortlink_get；需要浏览或统计条数时用本工具。")]
    public Task<LinkPage> ListAsync(
        [Description("关键词，匹配短码 / 标题 / 目标地址")] string? keyword = null,
        [Description("状态筛选：enabled / disabled / expired / capped；留空为全部")] string? status = null,
        [Description("排序：created_desc（默认） / created_asc / clicks_desc / last_access_desc")] string? sort = null,
        [Description("只看自己创建的短链")] bool? mine = null,
        [Description("页码，从 1 开始")] int page = 1,
        [Description("每页条数，1-200，默认 20")] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_list",
            UserRole.Viewer,
            async ct =>
            {
                var query = new LinkQuery
                {
                    Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
                    Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
                    Sort = string.IsNullOrWhiteSpace(sort) ? null : sort.Trim(),
                    Mine = mine,
                    Page = page,
                    PageSize = pageSize,
                };

                var result = await links.QueryAsync(query, ct).ConfigureAwait(false);

                return new LinkPage(
                    result.Total,
                    result.Page,
                    result.PageSize,
                    result.Items.Select(LinkSummary.From).ToList());
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_set_enabled", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "启用或停用一条短链。停用后短链立即失效（跳转返回失效页），但数据保留，可再次启用。" +
        "幂等操作：状态与目标一致时不做改动。需要「成员」及以上角色且只能操作自己创建的短链（管理员除外）。")]
    public Task<LinkSummary> SetEnabledAsync(
        [Description("短链 ID")] long linkId,
        [Description("true 启用，false 停用")] bool enabled,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_set_enabled",
            UserRole.Member,
            async ct =>
            {
                // 用 SetEnabledAsync 而不是「先 Get 再 Toggle」：后者在两个相反请求并发时
                // 会翻转两次回到原状态，而工具调用恰恰是最容易并发重试的场景。
                var updated = await links.SetEnabledAsync(linkId, enabled, ct).ConfigureAwait(false);
                return LinkSummary.From(updated);
            },
            cancellationToken);

    [McpServerTool(Name = "shortlink_delete", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description(
        "永久删除一条短链及其跳转数据，不可恢复。属于破坏性操作：" +
        "调用前必须向用户明确确认，并优先考虑用 shortlink_set_enabled 停用。" +
        "需要「成员」及以上角色且只能删除自己创建的短链（管理员除外）。")]
    public Task<string> DeleteAsync(
        [Description("要删除的短链 ID；不确定时先用 shortlink_get 确认目标")] long linkId,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_delete",
            UserRole.Member,
            async ct =>
            {
                var existing = await links.GetAsync(linkId, ct).ConfigureAwait(false);
                await links.DeleteAsync(linkId, ct).ConfigureAwait(false);
                return $"已删除短链 {existing.Code}（ID {existing.Id}，原目标 {existing.TargetUrl}）";
            },
            cancellationToken);

    private async Task<LinkDto> ResolveAsync(long? linkId, string? shortUrl, CancellationToken cancellationToken)
    {
        if (linkId is { } id)
        {
            return await links.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(shortUrl))
        {
            return await links.GetByShortUrlAsync(shortUrl, cancellationToken).ConfigureAwait(false);
        }

        throw new BusinessException(BizCodes.InvalidArgument, "linkId 与 shortUrl 必须提供一个");
    }

    /// <summary>
    /// 把模型给的 ISO 8601 字符串转成 UTC。
    /// 用字符串而不是 <c>DateTime?</c> 参数，是因为反序列化失败会变成协议级错误（模型看不到可纠正的提示）；
    /// 在这里解析可以返回「格式不对，应形如 …」这种能自我修正的业务错误。
    /// </summary>
    private static DateTime? ParseUtc(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new BusinessException(
                BizCodes.InvalidArgument,
                $"{parameterName} 不是合法的 ISO 8601 时间，应形如 2026-12-31T23:59:59Z");
        }

        return parsed.UtcDateTime;
    }
}
