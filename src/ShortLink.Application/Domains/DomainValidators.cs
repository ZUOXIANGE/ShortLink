using FluentValidation;
using ShortLink.Contracts.Domains;
using ShortLink.Domain.Domains;

namespace ShortLink.Application.Domains;

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
