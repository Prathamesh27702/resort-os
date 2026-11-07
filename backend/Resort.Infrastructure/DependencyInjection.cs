using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Identity;
using Resort.Application.Payments;
using Resort.Application.Notifications;
using Resort.Infrastructure.Identity;
using Resort.Infrastructure.Notifications;
using Resort.Infrastructure.Payments;
using Resort.Infrastructure.Persistence;
using Resort.Infrastructure.Protection;

namespace Resort.Infrastructure;

public static class DependencyInjection
{
    /// <param name="production">True in production: the fake Razorpay is refused there.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, bool production = false)
    {
        var cs = ResolveConnectionString(config);

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(cs).Build());
        services.AddScoped<AuditInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<BootstrapOptions>(config.GetSection(BootstrapOptions.Section));
        services.Configure<DevSeedOptions>(config.GetSection(DevSeedOptions.Section));
        services.Configure<EncryptionOptions>(config.GetSection(EncryptionOptions.Section));
        services.Configure<FileStoreOptions>(config.GetSection(FileStoreOptions.Section));
        services.Configure<GuestOptions>(config.GetSection(GuestOptions.Section));

        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IFileStore, LocalDiskFileStore>();
        services.AddSingleton<Resort.Application.Invoicing.IPdfRenderer, Documents.PdfRenderer>();

        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
        services.AddSingleton<IUserAccessService, UserAccessService>();

        AddRazorpay(services, config, production);
        AddMessaging(services, config, production);

        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<Bootstrapper>();
        services.AddSingleton<DevSeeder>();
        return services;
    }

    /// <summary>
    /// Razorpay (Sprint 11): the real adapter over HttpClient, pointed at api.razorpay.com, or at the built-in fake when
    /// no keys are set outside production (see <see cref="RazorpayOptions"/>).
    /// </summary>
    private static void AddRazorpay(IServiceCollection services, IConfiguration config, bool production)
    {
        services.Configure<RazorpayOptions>(config.GetSection(RazorpayOptions.Section));
        var options = config.GetSection(RazorpayOptions.Section).Get<RazorpayOptions>() ?? new RazorpayOptions();
        var client = services.AddHttpClient<IPaymentGateway, RazorpayPaymentGateway>((sp, http) =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RazorpayOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;   // each try has its own timeout (RazorpayOptions.TimeoutSeconds)
        });
        if (options.UseFake(production))
        {
            services.PostConfigure<RazorpayOptions>(o => o.WithFakeDefaults());
            services.AddSingleton<FakeRazorpayServer>();
            client.ConfigurePrimaryHttpMessageHandler(sp => new FakeRazorpayHandler(sp.GetRequiredService<FakeRazorpayServer>()));
        }
    }

    /// <summary>
    /// WhatsApp and email (Sprint 24): the real adapters over HttpClient, pointed at the built-in fakes when no keys are set
    /// outside production (see <see cref="WhatsAppOptions"/> and <see cref="EmailOptions"/>). In production without keys
    /// the channel is OFF: messages fail with the reason instead of pretending to be sent.
    /// </summary>
    private static void AddMessaging(IServiceCollection services, IConfiguration config, bool production)
    {
        services.Configure<NotificationOptions>(config.GetSection(NotificationOptions.Section));

        services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.Section));
        var wa = config.GetSection(WhatsAppOptions.Section).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        var waMode = wa.Resolve(production);
        services.AddSingleton(new WhatsAppMode(waMode));
        var waClient = services.AddHttpClient<IWhatsAppClient, WhatsAppCloudClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhatsAppOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;   // each send has its own timeout (WhatsAppOptions.TimeoutSeconds)
        });
        if (waMode == ProviderModes.Fake)
        {
            services.PostConfigure<WhatsAppOptions>(o => o.WithFakeDefaults());
            services.AddSingleton<FakeWhatsAppServer>();
            waClient.ConfigurePrimaryHttpMessageHandler(sp => new FakeWhatsAppHandler(sp.GetRequiredService<FakeWhatsAppServer>()));
        }

        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        var email = config.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        var emailMode = email.Resolve(production);
        services.AddSingleton(new EmailMode(emailMode));
        var emailClient = services.AddHttpClient<IEmailClient, ResendEmailClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        });
        if (emailMode == ProviderModes.Fake)
        {
            services.PostConfigure<EmailOptions>(o => o.WithFakeDefaults());
            services.AddSingleton<FakeEmailServer>();
            emailClient.ConfigurePrimaryHttpMessageHandler(sp => new FakeEmailHandler(sp.GetRequiredService<FakeEmailServer>()));
        }
    }

    /// <summary>
    /// ConnectionStrings:Resort wins (tests set it explicitly); otherwise DATABASE_URL (brief §77), which may be an
    /// Npgsql string or a postgres:// URI.
    /// </summary>
    public static string ResolveConnectionString(IConfiguration config)
    {
        var cs = config.GetConnectionString("Resort");
        if (!string.IsNullOrWhiteSpace(cs)) return cs;

        var url = config["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("No database configured. Set DATABASE_URL (see .env.example).");

        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return url;

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        }.ConnectionString;
    }
}
