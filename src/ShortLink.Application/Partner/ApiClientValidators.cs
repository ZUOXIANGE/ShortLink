using FluentValidation;
using ShortLink.Contracts.Partner;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Partner;

public sealed class CreateApiClientRequestValidator : AbstractValidator<CreateApiClientRequest>
{
    public CreateApiClientRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("凭据名称不能为空")
            .MaximumLength(64).WithMessage("凭据名称不超过 64 个字符");

        RuleFor(x => x.UserId)
            .GreaterThan(0).WithMessage("必须指定绑定的工作区成员");

        RuleFor(x => x.Role)
            .Must(role => !string.IsNullOrWhiteSpace(role) && UserRoles.TryParse(role, out _))
            .WithMessage("角色只能是 owner / admin / member / viewer");

        RuleFor(x => x.Scopes)
            .NotEmpty().WithMessage("至少需要授予一个 scope")
            .Must(scopes => scopes.Count <= ApiScopes.All.Length)
            .WithMessage("scope 数量超过上限");

        RuleForEach(x => x.Scopes)
            .Must(ApiScopes.IsKnown).WithMessage("包含未知 scope");

        RuleFor(x => x.RateLimitPerMinute)
            .GreaterThan(0).When(x => x.RateLimitPerMinute is not null)
            .WithMessage("每分钟请求上限必须大于 0");

        RuleFor(x => x.AllowedIps)
            .MaximumLength(1024).WithMessage("IP 白名单不超过 1024 个字符");

        RuleFor(x => x.ExpiresAtUtc)
            .Must(at => at is null || at.Value.ToUniversalTime() > DateTime.UtcNow)
            .WithMessage("过期时间必须晚于当前时间");
    }
}

public sealed class UpdateApiClientRequestValidator : AbstractValidator<UpdateApiClientRequest>
{
    public UpdateApiClientRequestValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(64).When(x => x.Name is not null)
            .WithMessage("凭据名称不超过 64 个字符");

        RuleFor(x => x.Role)
            .Must(role => string.IsNullOrWhiteSpace(role) || UserRoles.TryParse(role, out _))
            .WithMessage("角色只能是 owner / admin / member / viewer");

        RuleFor(x => x.Scopes)
            .Must(scopes => scopes!.Count <= ApiScopes.All.Length)
            .When(x => x.Scopes is not null)
            .WithMessage("scope 数量超过上限");

        RuleForEach(x => x.Scopes)
            .Must(ApiScopes.IsKnown).WithMessage("包含未知 scope");

        RuleFor(x => x.RateLimitPerMinute)
            .GreaterThan(0).When(x => x.RateLimitPerMinute is not null)
            .WithMessage("每分钟请求上限必须大于 0");

        RuleFor(x => x.AllowedIps)
            .MaximumLength(1024).WithMessage("IP 白名单不超过 1024 个字符");

        RuleFor(x => x.ExpiresAtUtc)
            .Must(at => at is null || at.Value.ToUniversalTime() > DateTime.UtcNow)
            .WithMessage("过期时间必须晚于当前时间");
    }
}
