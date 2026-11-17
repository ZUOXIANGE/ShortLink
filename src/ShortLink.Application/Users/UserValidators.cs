using FluentValidation;
using ShortLink.Application.Validation;
using ShortLink.Contracts.Users;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Users;

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
