using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Resort.Application.Audit;
using Resort.Application.Common;
using Resort.Application.Identity;
using Resort.Application.Properties;

namespace Resort.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddScoped<CallerAccess>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<PropertyService>();
        services.AddScoped<AuditQueryService>();
        return services;
    }
}
