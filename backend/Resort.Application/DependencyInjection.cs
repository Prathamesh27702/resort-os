using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Resort.Application.Accommodation;
using Resort.Application.Audit;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Payments;
using Resort.Application.Tax;
using Resort.Application.FrontDesk;
using Resort.Application.Guests;
using Resort.Application.Identity;
using Resort.Application.Invoicing;
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
        services.AddScoped<SystemActor>();
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
        services.AddScoped<CalendarService>();
        services.AddScoped<FrontDeskService>();
        services.AddScoped<TaxCalculator>();
        services.AddScoped<TaxService>();
        services.AddScoped<FolioPosting>();
        services.AddScoped<FolioService>();
        services.AddScoped<RoomChargeRun>();
        services.AddScoped<IFolioBalances, FolioBalances>();
        services.AddScoped<PaymentLedger>();
        services.AddScoped<CashDrawerService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentLinkService>();
        services.AddScoped<RazorpayWebhookService>();
        services.AddScoped<FolioBilling>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<ReceiptService>();
        services.AddSingleton<InvoicePrinter>();
        return services;
    }
}
