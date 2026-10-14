using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Resort.Application.Accommodation;
using Resort.Application.Audit;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Identity;
using Resort.Application.Properties;
using Resort.Application.Rates;
using Resort.Application.Reservations;

namespace Resort.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddScoped<CallerAccess>();
        services.AddScoped<AuditContext>();
        services.AddScoped<AccommodationService>();
        services.AddScoped<BlockService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<PropertyService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<GuestService>();
        services.AddScoped<GuestDocumentService>();
        services.AddScoped<RateService>();
        services.AddScoped<PackageService>();
        services.AddScoped<AvailabilityService>();
        services.AddScoped<ReservationService>();
        return services;
    }
}
