using FluentValidation;
using ShortLink.Application.Validation;
using ShortLink.Contracts.Links;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Links;

public sealed class CreateLinkRequestValidator : AbstractValidator<CreateLinkRequest>
{
    private static readonly int[] AllowedRedirectTypes = [301, 302, 307, 308];

    public CreateLinkRequestValidator()
    {
        RuleFor(x => x.TargetUrl)
            .NotEmpty().WithMessage("目标地址不能为空")
            .MaximumLength(2048).WithMessage("目标地址不超过 2048 个字符")
            .Must(UrlRules.IsAbsoluteHttpUrl).WithMessage("目标地址必须是 http/https 绝对地址");

        RuleFor(x => x.Code)
            .Must(code => string.IsNullOrEmpty(code) || ShortCode.IsValid(code))
            .WithMessage("短码只能包含字母、数字、下划线与短横线，长度 3~32，且不能是系统保留字");

        RuleFor(x => x.Title).MaximumLength(200).WithMessage("标题不超过 200 个字符");
        RuleFor(x => x.Remark).MaximumLength(500).WithMessage("备注不超过 500 个字符");
        RuleFor(x => x.RedirectType)
            .Must(type => AllowedRedirectTypes.Contains(type))
            .WithMessage("跳转类型只支持 301 / 302 / 307 / 308");
        RuleFor(x => x.Password)
            .Must(p => string.IsNullOrEmpty(p) || LinkAccessPasswordPolicy.IsSatisfied(p))
            .WithMessage("访问密码至少 8 位且包含字母与数字，最长 64 个字符");
        RuleFor(x => x.ExpiresAtUtc)
            .Must(at => at is null || at.Value > DateTime.UtcNow.AddMinutes(-1))
            .WithMessage("过期时间必须晚于当前时间");

        RuleFor(x => x.BotPolicy)
            .Must(BotPolicies.IsValid)
            .WithMessage("机器人策略只能是 allow / block / ignore_count");
        RuleFor(x => x.MaxClicks)
            .GreaterThan(0)
            .When(x => x.MaxClicks is not null)
            .WithMessage("点击上限必须大于 0");
        RuleFor(x => x.IpAllowList).MaximumLength(2000).WithMessage("IP 白名单不超过 2000 个字符");
        RuleFor(x => x.IpBlockList).MaximumLength(2000).WithMessage("IP 黑名单不超过 2000 个字符");
        RuleFor(x => x.FallbackUrl)
            .Must(url => string.IsNullOrEmpty(url) || UrlRules.IsAbsoluteHttpUrl(url))
            .WithMessage("兜底地址必须是 http/https 绝对地址");
    }
}

public sealed class UpdateLinkRequestValidator : AbstractValidator<UpdateLinkRequest>
{
    private static readonly int[] AllowedRedirectTypes = [301, 302, 307, 308];

    public UpdateLinkRequestValidator()
    {
        RuleFor(x => x.TargetUrl)
            .NotEmpty().WithMessage("目标地址不能为空")
            .MaximumLength(2048).WithMessage("目标地址不超过 2048 个字符")
            .Must(UrlRules.IsAbsoluteHttpUrl).WithMessage("目标地址必须是 http/https 绝对地址");

        RuleFor(x => x.Title).MaximumLength(200).WithMessage("标题不超过 200 个字符");
        RuleFor(x => x.Remark).MaximumLength(500).WithMessage("备注不超过 500 个字符");
        RuleFor(x => x.RedirectType)
            .Must(type => AllowedRedirectTypes.Contains(type))
            .WithMessage("跳转类型只支持 301 / 302 / 307 / 308");
        RuleFor(x => x.Password)
            .Must(p => p is null || p.Length == 0 || LinkAccessPasswordPolicy.IsSatisfied(p))
            .WithMessage("访问密码至少 8 位且包含字母与数字，最长 64 个字符");
        RuleFor(x => x.ExpiresAtUtc)
            .Must(at => at is null || at.Value > DateTime.UtcNow.AddMinutes(-1))
            .WithMessage("过期时间必须晚于当前时间");

        RuleFor(x => x.BotPolicy)
            .Must(BotPolicies.IsValid)
            .WithMessage("机器人策略只能是 allow / block / ignore_count");
        RuleFor(x => x.MaxClicks)
            .GreaterThan(0)
            .When(x => x.MaxClicks is not null)
            .WithMessage("点击上限必须大于 0");
        RuleFor(x => x.IpAllowList).MaximumLength(2000).WithMessage("IP 白名单不超过 2000 个字符");
        RuleFor(x => x.IpBlockList).MaximumLength(2000).WithMessage("IP 黑名单不超过 2000 个字符");
        RuleFor(x => x.FallbackUrl)
            .Must(url => string.IsNullOrEmpty(url) || UrlRules.IsAbsoluteHttpUrl(url))
            .WithMessage("兜底地址必须是 http/https 绝对地址");
    }
}

public sealed class BatchLinkRequestValidator : AbstractValidator<BatchLinkRequest>
{
    public BatchLinkRequestValidator()
    {
        RuleFor(x => x.Action)
            .Must(BatchActions.IsValid)
            .WithMessage("批量操作只支持 enable / disable / delete / change_domain / change_expiry");

        RuleFor(x => x.Ids)
            .NotEmpty().WithMessage("请至少选择一条短链")
            .Must(ids => ids.Count <= BatchLinkRequest.MaxItems)
            .WithMessage($"单次批量操作不能超过 {BatchLinkRequest.MaxItems} 条");

        RuleFor(x => x.DomainId)
            .NotNull()
            .When(x => string.Equals(x.Action?.Trim(), BatchActions.ChangeDomain, StringComparison.OrdinalIgnoreCase))
            .WithMessage("批量改域名时必须指定目标域名");

        RuleFor(x => x.ExpiresAtUtc)
            .Must(at => at is null || at.Value > DateTime.UtcNow.AddMinutes(-1))
            .When(x => string.Equals(x.Action?.Trim(), BatchActions.ChangeExpiry, StringComparison.OrdinalIgnoreCase))
            .WithMessage("过期时间必须晚于当前时间");
    }
}

public sealed class SaveLinkRoutesRequestValidator : AbstractValidator<SaveLinkRoutesRequest>
{
    public SaveLinkRoutesRequestValidator()
    {
        RuleFor(x => x.Routes)
            .Must(routes => routes.Count <= SaveLinkRoutesRequest.MaxRoutes)
            .WithMessage($"单条短链最多配置 {SaveLinkRoutesRequest.MaxRoutes} 个分支");

        RuleFor(x => x.Routes)
            .Must(routes => routes
                .Select(r => r.Name?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count() == routes.Count(r => !string.IsNullOrWhiteSpace(r.Name)))
            .WithMessage("同一短链下的分支名称不能重复");

        RuleForEach(x => x.Routes).ChildRules(route =>
        {
            route.RuleFor(r => r.Name)
                .NotEmpty().WithMessage("分支名称不能为空")
                .MaximumLength(64).WithMessage("分支名称不超过 64 个字符");

            route.RuleFor(r => r.TargetUrl)
                .NotEmpty().WithMessage("分支目标地址不能为空")
                .Must(UrlRules.IsAbsoluteHttpUrl)
                .WithMessage("分支目标地址必须是 http/https 绝对地址");

            route.RuleFor(r => r.Priority)
                .InclusiveBetween(LinkRoute.MinPriority, LinkRoute.MaxPriority)
                .WithMessage($"优先级范围 {LinkRoute.MinPriority} ~ {LinkRoute.MaxPriority}");

            route.RuleFor(r => r.Weight)
                .InclusiveBetween(0, LinkRoute.MaxWeight)
                .WithMessage($"权重范围 0 ~ {LinkRoute.MaxWeight}");

            route.RuleFor(r => r.Match)
                .Must(m => m is null || m is "all" or "any" or "ALL" or "ANY")
                .WithMessage("match 只能是 all 或 any");

            route.RuleForEach(r => r.Conditions).ChildRules(condition =>
            {
                condition.RuleFor(c => c.Field)
                    .Must(RouteFields.IsValid)
                    .WithMessage($"条件字段只支持 {string.Join(" / ", RouteFields.All)}");

                condition.RuleFor(c => c.Op)
                    .Must(RouteOperators.IsValid)
                    .WithMessage($"条件运算符只支持 {string.Join(" / ", RouteOperators.All)}");

                condition.RuleFor(c => c.Value)
                    .MaximumLength(256).WithMessage("条件值不超过 256 个字符");
            });
        });
    }
}
