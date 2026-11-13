using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Application.Analytics;
using ShortLink.Application.Auth;
using ShortLink.Application.Domains;
using ShortLink.Application.Links;
using ShortLink.Application.Redirection;
using ShortLink.Application.Security;
using ShortLink.Application.Users;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts.Auth;
using ShortLink.Contracts.Domains;
using ShortLink.Contracts.Links;
using ShortLink.Contracts.Security;
using ShortLink.Contracts.Users;

namespace ShortLink.Application;

/// <summary>应用层注册。校验器与用例显式注册，避免运行时程序集扫描。</summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
        services.AddScoped<IValidator<CreateDomainRequest>, CreateDomainRequestValidator>();
        services.AddScoped<IValidator<UpdateDomainRequest>, UpdateDomainRequestValidator>();
        services.AddScoped<IValidator<CreateLinkRequest>, CreateLinkRequestValidator>();
        services.AddScoped<IValidator<UpdateLinkRequest>, UpdateLinkRequestValidator>();
        services.AddScoped<IValidator<BatchLinkRequest>, BatchLinkRequestValidator>();
        services.AddScoped<IValidator<SaveLinkRoutesRequest>, SaveLinkRoutesRequestValidator>();
        services.AddScoped<IValidator<CreateUserRequest>, CreateUserRequestValidator>();
        services.AddScoped<IValidator<UpdateUserRequest>, UpdateUserRequestValidator>();
        services.AddScoped<IValidator<ResetPasswordRequest>, ResetPasswordRequestValidator>();
        services.AddScoped<IValidator<AbuseReportRequest>, AbuseReportRequestValidator>();

        services.AddScoped<WorkspaceContext>();

        services.AddScoped<AuthService>();
        services.AddScoped<DomainService>();
        services.AddScoped<TargetUrlSecurityScanner>();
        services.AddScoped<LinkService>();
        services.AddScoped<RouteService>();
        services.AddScoped<ExportService>();
        services.AddScoped<RedirectService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ConversionService>();
        services.AddScoped<UserService>();
        services.AddScoped<SecurityService>();

        return services;
    }
}
