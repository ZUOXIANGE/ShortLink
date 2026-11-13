using FluentValidation;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Options;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Links;

/// <summary>条件路由与 A/B 分支的管理用例。</summary>
public sealed class RouteService(
    IShortLinkRepository links,
    IRouteRepository routes,
    IUnitOfWork unitOfWork,
    ILinkCache cache,
    IClock clock,
    ICurrentUser currentUser,
    WorkspaceContext workspace,
    IOptions<SecurityOptions> securityOptions,
    TargetUrlSecurityScanner targetScanner,
    IValidator<SaveLinkRoutesRequest> validator)
{
    private SecurityOptions Security => securityOptions.Value;

    public async Task<IReadOnlyList<LinkRouteDto>> ListAsync(long linkId, CancellationToken cancellationToken)
    {
        var link = await LoadAsync(linkId, cancellationToken).ConfigureAwait(false);
        var items = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);
        return items
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id)
            .Select(r => r.ToDto())
            .ToList();
    }

    /// <summary>
    /// 按 (LinkId, Name) upsert 整表快照：保留同名分支 ID 与点击归因，删除本次未保留分支。
    /// 短链并发令牌与路由变更在同一事务提交，竞争保存只有一个版本能生效。
    /// </summary>
    public async Task<IReadOnlyList<LinkRouteDto>> ReplaceAsync(
        long linkId,
        SaveLinkRoutesRequest request,
        CancellationToken cancellationToken)
    {
        await validator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Routes.Count > Security.MaxRoutesPerLink)
        {
            throw new BusinessException(BizCodes.RouteLimitExceeded);
        }

        var link = await LoadAsync(linkId, cancellationToken).ConfigureAwait(false);
        LinkPolicy.EnsureCanModify(link, currentUser);

        // 扫描事件独立提交；后续路由保存失败不会把合法的拒绝审计一起回滚。
        foreach (var input in request.Routes)
        {
            await targetScanner
                .EnsureAllowedAsync(input.TargetUrl, link.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var existing = await routes.ListByLinkAsync(link.Id, transactionToken).ConfigureAwait(false);
            var existingByName = existing.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var retained = new HashSet<LinkRoute>();
            var now = clock.UtcNow;

            foreach (var input in request.Routes)
            {
                if (existingByName.TryGetValue(input.Name.Trim(), out var route))
                {
                    route.Update(
                        input.Name,
                        input.Priority,
                        input.Weight,
                        input.TargetUrl,
                        ToConditions(input),
                        input.IsEnabled,
                        now);
                    retained.Add(route);
                    continue;
                }

                var created = LinkRoute.Create(
                    link.Id,
                    input.Name,
                    input.Priority,
                    input.Weight,
                    input.TargetUrl,
                    ToConditions(input),
                    input.IsEnabled,
                    now);
                routes.Add(created);
            }

            routes.RemoveRange(existing.Where(route => !retained.Contains(route)));
            link.Touch(now);
            await unitOfWork.SaveChangesAsync(transactionToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        await cache.RemoveLinkAsync(link.DomainId, link.Code, cancellationToken).ConfigureAwait(false);

        var saved = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);
        return saved
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id)
            .Select(r => r.ToDto())
            .ToList();
    }

    /// <summary>
    /// 规则测试：用给定上下文求一次命中结果，并模拟多个访客估算分流比例。
    /// 上线前先在这里确认「CN 走 A、其余走 B」是否符合预期，避免线上试错。
    /// </summary>
    public async Task<RouteTestResult> TestAsync(
        long linkId,
        RouteTestRequest request,
        CancellationToken cancellationToken)
    {
        var link = await LoadAsync(linkId, cancellationToken).ConfigureAwait(false);
        var stored = await routes.ListByLinkAsync(link.Id, cancellationToken).ConfigureAwait(false);

        var snapshots = stored.Select(r => r.ToSnapshot()).ToList();
        var fallback = string.IsNullOrWhiteSpace(link.FallbackUrl) ? link.TargetUrl : link.FallbackUrl;

        var query = request.Query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var visitorKey = string.IsNullOrWhiteSpace(request.VisitorKey) ? "test" : request.VisitorKey.Trim();
        var context = new RouteContext(
            link.Id,
            request.Country,
            request.Province,
            request.Device,
            request.Browser,
            request.Os,
            request.Language,
            request.Referer,
            query,
            visitorKey);

        var selection = RouteSelector.Select(snapshots, context, fallback);

        var repeat = Math.Clamp(request.Repeat, 1, 10000);
        var names = new Dictionary<string, long>(StringComparer.Ordinal);
        var fallbackName = "兜底地址";

        for (var i = 0; i < repeat; i++)
        {
            var probe = context with { VisitorKey = $"{visitorKey}#{i}" };
            var pick = RouteSelector.Select(snapshots, probe, fallback);
            // 唯一的默认分支下 weighted 选择是确定的，用 visitorKey 轮转即可反映真实分流比例。
            var name = pick.RouteName ?? fallbackName;
            names[name] = names.GetValueOrDefault(name) + 1;
        }

        var buckets = names
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new RouteTestBucket(kv.Key, kv.Value, Math.Round(kv.Value * 100d / repeat, 2)))
            .ToList();

        return new RouteTestResult(selection.RouteName, selection.TargetUrl, selection.IsExperimental, fallback, buckets);
    }

    private async Task<Link> LoadAsync(long linkId, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var link = await links.FindByIdAsync(linkId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.LinkNotFound);

        return link.WorkspaceId == workspaceId
            ? link
            : throw new BusinessException(BizCodes.LinkNotFound);
    }

    private static RouteConditions ToConditions(LinkRouteInput input)
    {
        var match = string.Equals(input.Match, "any", StringComparison.OrdinalIgnoreCase)
            ? RouteMatchMode.Any
            : RouteMatchMode.All;

        var items = input.Conditions
            .Where(c => !string.IsNullOrWhiteSpace(c.Field))
            .Select(c => new RouteCondition(
                c.Field.Trim().ToLowerInvariant(),
                (c.Op ?? RouteOperators.Equal).Trim().ToLowerInvariant(),
                (c.Value ?? string.Empty).Trim()))
            .ToList();

        return RouteConditions.Create(match, items);
    }
}
