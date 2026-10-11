using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Identity;
using Resort.Infrastructure.Identity;
using Resort.Infrastructure.Persistence;
using Resort.Infrastructure.Protection;

namespace Resort.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
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

        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
        services.AddSingleton<IUserAccessService, UserAccessService>();

        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<Bootstrapper>();
        services.AddSingleton<DevSeeder>();
        return services;
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
