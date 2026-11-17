using FluentValidation;
using ShortLink.Application.Validation;
using ShortLink.Contracts.Auth;

namespace ShortLink.Application.Auth;

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
