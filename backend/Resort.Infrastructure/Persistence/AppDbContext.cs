using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Accommodation;
using Resort.Domain.Audit;
using Resort.Domain.Common;
using Resort.Domain.Guests;
using Resort.Domain.Identity;
using Resort.Domain.Organizations;

namespace Resort.Infrastructure.Persistence;

/// <summary>
/// The schema is owned by the SQL files in /db/migrations; this model only maps onto it
/// (EF migrations are deliberately not used). Names are converted to snake_case.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options), IAppDbContext
{
    /// <summary>Read by the tenant query filters. NULL for anonymous callers, which makes every
    /// tenant-scoped query return nothing unless the code explicitly calls IgnoreQueryFilters().</summary>
    internal Guid? CurrentOrganizationId => currentUser.OrganizationId;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertySetting> PropertySettings => Set<PropertySetting>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginEvent> LoginEvents => Set<LoginEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Amenity> Amenities => Set<Amenity>();
    public DbSet<AccommodationType> AccommodationTypes => Set<AccommodationType>();
    public DbSet<AccommodationTypeAmenity> AccommodationTypeAmenities => Set<AccommodationTypeAmenity>();
    public DbSet<AccommodationUnit> AccommodationUnits => Set<AccommodationUnit>();
    public DbSet<Bed> Beds => Set<Bed>();
    public DbSet<UnitImage> UnitImages => Set<UnitImage>();
    public DbSet<UnitBlock> UnitBlocks => Set<UnitBlock>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<GuestAddress> GuestAddresses => Set<GuestAddress>();
    public DbSet<GuestDocument> GuestDocuments => Set<GuestDocument>();
    public DbSet<GuestPreference> GuestPreferences => Set<GuestPreference>();
    public DbSet<GuestConsent> GuestConsents => Set<GuestConsent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>().ToTable("organizations");
        b.Entity<Property>().ToTable("properties");
        b.Entity<PropertySetting>(e =>
        {
            e.ToTable("property_settings");
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId);
        });
        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(x => x.Code);
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionCode });
        });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            // Declared so EF inserts the replacement token before updating the old row that points to it.
            e.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById);
        });
        b.Entity<LoginEvent>(e =>
        {
            e.ToTable("login_events");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
        });
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Changes).HasColumnType("jsonb");
        });

        // ---- Sprint 2: accommodation (database/migrations/V004__accommodation.sql)
        b.Entity<Amenity>().ToTable("amenities");
        b.Entity<AccommodationType>(e =>
        {
            e.ToTable("accommodation_types");
            e.Property(x => x.SizeSqm).HasColumnType("numeric(6,1)");
            e.HasMany(x => x.Amenities).WithOne().HasForeignKey(x => x.AccommodationTypeId);
        });
        b.Entity<AccommodationTypeAmenity>(e =>
        {
            e.ToTable("accommodation_type_amenities");
            e.HasKey(x => new { x.AccommodationTypeId, x.AmenityId });
        });
        b.Entity<AccommodationUnit>(e =>
        {
            e.ToTable("accommodation_units");
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.AccommodationTypeId);
            e.HasMany(x => x.Beds).WithOne().HasForeignKey(x => x.UnitId);
        });
        b.Entity<Bed>().ToTable("beds");
        b.Entity<UnitImage>().ToTable("unit_images");
        b.Entity<UnitBlock>(e =>
        {
            e.ToTable("unit_blocks");
            // "period" is a generated column used only by the database's EXCLUDE constraint; not mapped.
            e.Ignore(x => x.IsActive);
        });

        // ---- Sprint 3: guests (database/migrations/V005__guests.sql)
        b.Entity<Guest>(e =>
        {
            e.ToTable("guests");
            // Generated (STORED) columns used for search; never written by the app.
            e.Property(x => x.SearchText).HasComputedColumnSql("search_text", stored: true);
            e.Property(x => x.PhoneDigits).HasComputedColumnSql("phone_digits", stored: true);
            e.Property(x => x.Nationality).HasColumnType("char(2)");
            e.HasMany(x => x.Addresses).WithOne().HasForeignKey(x => x.GuestId);
            e.HasMany(x => x.Preferences).WithOne().HasForeignKey(x => x.GuestId);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.FullName);
        });
        b.Entity<GuestAddress>(e =>
        {
            e.ToTable("guest_addresses");
            e.Property(x => x.StateCode).HasColumnType("char(2)");
            e.Property(x => x.CountryCode).HasColumnType("char(2)");
            e.Ignore(x => x.IsEmpty);
        });
        b.Entity<GuestDocument>(e =>
        {
            e.ToTable("guest_documents");
            e.Property(x => x.IssuingCountry).HasColumnType("char(2)");
        });
        b.Entity<GuestPreference>().ToTable("guest_preferences");
        b.Entity<GuestConsent>().ToTable("guest_consents");

        foreach (var entity in b.Model.GetEntityTypes())
        {
            var clr = entity.ClrType;

            // Uuid PKs are generated by the app (UUIDv7), never by the database.
            if (typeof(Entity).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(Entity.Id)).ValueGeneratedNever();

            // Optimistic concurrency on PostgreSQL's xmin system column.
            if (typeof(IVersioned).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(IVersioned.Version))
                    .HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

            if (typeof(IOrganizationScoped).IsAssignableFrom(clr))
                TenantFilterMethod.MakeGenericMethod(clr).Invoke(this, [b]);
            else if (typeof(ISoftDelete).IsAssignableFrom(clr))
                SoftDeleteFilterMethod.MakeGenericMethod(clr).Invoke(this, [b]);
        }

        ApplySnakeCase(b);
    }

    private static readonly MethodInfo TenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo SoftDeleteFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SetSoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private void SetTenantFilter<T>(ModelBuilder b) where T : class, IOrganizationScoped
    {
        Expression<Func<T, bool>> filter = e => e.OrganizationId == CurrentOrganizationId;
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
            filter = e => e.OrganizationId == CurrentOrganizationId && ((ISoftDelete)e).DeletedAt == null;
        b.Entity<T>().HasQueryFilter(filter);
    }

    private static void SetSoftDeleteFilter<T>(ModelBuilder b) where T : class, ISoftDelete =>
        b.Entity<T>().HasQueryFilter(e => e.DeletedAt == null);

    private static void ApplySnakeCase(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
                if (p.GetColumnName() != "xmin")
                    p.SetColumnName(ToSnake(p.Name));
            foreach (var k in entity.GetKeys()) k.SetName(ToSnake(k.GetName() ?? ""));
            foreach (var fk in entity.GetForeignKeys()) fk.SetConstraintName(ToSnake(fk.GetConstraintName() ?? ""));
        }
    }

    internal static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
