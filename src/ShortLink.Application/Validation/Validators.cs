using FluentValidation;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Domains;
using ShortLink.Domain.Links;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Validation;

/// <summary>密码策略：8~128 位，至少包含一个字母与一个数字。</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    public const int MaxLength = 128;

    public static bool IsSatisfied(string? password)
        => !string.IsNullOrEmpty(password)
           && password.Length >= MinLength
           && password.Length <= MaxLength
           && password.Any(char.IsAsciiLetter)
           && password.Any(char.IsAsciiDigit);
}

/// <summary>新设置或更新的短链访问密码策略；既有短链验证密码时不套用该规则。</summary>
public static class LinkAccessPasswordPolicy
{
    public const int MinLength = 8;

    public const int MaxLength = 64;

    public static bool IsSatisfied(string? password)
        => !string.IsNullOrEmpty(password)
           && password.Length >= MinLength
           && password.Length <= MaxLength
           && password.Any(char.IsAsciiLetter)
           && password.Any(char.IsAsciiDigit);
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(64).WithMessage("用户名不能为空且不超过 64 个字符");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128).WithMessage("密码不能为空");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.OldPassword).NotEmpty().WithMessage("原密码不能为空");
        RuleFor(x => x.NewPassword)
            .Must(PasswordPolicy.IsSatisfied)
            .WithMessage("新密码至少 8 位，且需同时包含字母与数字");
    }
}

public sealed class CreateDomainRequestValidator : AbstractValidator<CreateDomainRequest>
{
    public CreateDomainRequestValidator()
    {
        RuleFor(x => x.Host)
            .NotEmpty().WithMessage("域名不能为空")
            .Must(host => ShortDomain.IsValidHost(ShortDomain.NormalizeHost(host)))
            .WithMessage("域名格式不合法，例如 s.example.com 或 localhost:5080");

        RuleFor(x => x.Scheme).Must(s => s is "http" or "https").WithMessage("协议只能是 http 或 https");
        RuleFor(x => x.Name).MaximumLength(64).WithMessage("域名名称不超过 64 个字符");
    }
}

public sealed class UpdateDomainRequestValidator : AbstractValidator<UpdateDomainRequest>
{
    public UpdateDomainRequestValidator()
    {
        RuleFor(x => x.Scheme).Must(s => s is "http" or "https").WithMessage("协议只能是 http 或 https");
        RuleFor(x => x.Name).MaximumLength(64).WithMessage("域名名称不超过 64 个字符");
    }
}

public sealed class CreateLinkRequestValidator : AbstractValidator<CreateLinkRequest>
{
    private static readonly int[] AllowedRedirectTypes = [301, 302, 307, 308];

    public CreateLinkRequestValidator()
    {
        RuleFor(x => x.TargetUrl)
            .NotEmpty().WithMessage("目标地址不能为空")
            .MaximumLength(2048).WithMessage("目标地址不超过 2048 个字符")
            .Must(BeAbsoluteHttpUrl).WithMessage("目标地址必须是 http/https 绝对地址");

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
            .Must(url => string.IsNullOrEmpty(url) || BeAbsoluteHttpUrl(url))
            .WithMessage("兜底地址必须是 http/https 绝对地址");
    }

    internal static bool BeAbsoluteHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class UpdateLinkRequestValidator : AbstractValidator<UpdateLinkRequest>
{
    private static readonly int[] AllowedRedirectTypes = [301, 302, 307, 308];

    public UpdateLinkRequestValidator()
    {
        RuleFor(x => x.TargetUrl)
            .NotEmpty().WithMessage("目标地址不能为空")
            .MaximumLength(2048).WithMessage("目标地址不超过 2048 个字符")
            .Must(CreateLinkRequestValidator.BeAbsoluteHttpUrl).WithMessage("目标地址必须是 http/https 绝对地址");

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
            .Must(url => string.IsNullOrEmpty(url) || CreateLinkRequestValidator.BeAbsoluteHttpUrl(url))
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
                .Must(CreateLinkRequestValidator.BeAbsoluteHttpUrl)
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

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName)
            .NotEmpty().WithMessage("用户名不能为空")
            .MaximumLength(64).WithMessage("用户名不超过 64 个字符")
            .Matches("^[A-Za-z0-9._-]+$").WithMessage("用户名只能包含字母、数字、点、下划线与短横线");

        RuleFor(x => x.DisplayName).MaximumLength(64).WithMessage("显示名不超过 64 个字符");

        RuleFor(x => x.Password)
            .Must(PasswordPolicy.IsSatisfied)
            .WithMessage("密码至少 8 位，且需同时包含字母与数字");

        RuleFor(x => x.Role)
            .Must(role => UserRoles.TryParse(role, out _))
            .WithMessage("角色只能是 owner / admin / member / viewer");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(64).WithMessage("显示名不超过 64 个字符");

        RuleFor(x => x.Role)
            .Must(role => string.IsNullOrWhiteSpace(role) || UserRoles.TryParse(role, out _))
            .WithMessage("角色只能是 owner / admin / member / viewer");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .Must(PasswordPolicy.IsSatisfied)
            .WithMessage("密码至少 8 位，且需同时包含字母与数字");
    }
}

public sealed class AbuseReportRequestValidator : AbstractValidator<AbuseReportRequest>
{
    public AbuseReportRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("短码不能为空")
            .Must(code => ShortCode.IsValid(code?.Trim() ?? string.Empty))
            .WithMessage("短码格式不合法");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("请填写举报原因")
            .MinimumLength(10).WithMessage("举报原因至少 10 个字")
            .MaximumLength(500).WithMessage("举报原因不超过 500 个字");

        RuleFor(x => x.Contact).MaximumLength(128).WithMessage("联系方式不超过 128 个字符");
    }
}
