using FluentValidation;
using ShortLink.Contracts.Security;
using ShortLink.Domain.Links;

namespace ShortLink.Application.Security;

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
