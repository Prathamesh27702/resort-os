using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Domain.Identity;
using Resort.Domain.Organizations;

namespace Resort.Infrastructure.Persistence;

/// <summary>First-run setup: creates the first organisation, property and OWNER account.
/// Runs only when Bootstrap:Enabled is true AND the organizations table is empty.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";
    public bool Enabled { get; set; }
    public string OrganizationCode { get; set; } = "";
    public string OrganizationName { get; set; } = "";
    public string PropertyCode { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public string Timezone { get; set; } = "Asia/Kolkata";
    public string? GstStateCode { get; set; }
    public string OwnerEmail { get; set; } = "";
    public string OwnerName { get; set; } = "";
    /// <summary>Supply via environment (Bootstrap__OwnerPassword). The owner must change it on first sign-in
    /// unless OwnerMustChangePassword is false (development only).</summary>
    public string OwnerPassword { get; set; } = "";
    public bool OwnerMustChangePassword { get; set; } = true;
}

public sealed class Bootstrapper(AppDbContext db, IPasswordHasherService hasher, IOptions<BootstrapOptions> options, ILogger<Bootstrapper> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        if (await db.Organizations.IgnoreQueryFilters().AnyAsync(ct)) return;

        if (string.IsNullOrWhiteSpace(o.OwnerEmail) || o.OwnerPassword.Length < 10)
            throw new InvalidOperationException("Bootstrap is enabled but OwnerEmail/OwnerPassword (min 10 chars) are not set.");

        var org = new Organization { Code = o.OrganizationCode.ToUpperInvariant(), Name = o.OrganizationName };
        var property = new Property
        {
            OrganizationId = org.Id,
            Code = o.PropertyCode.ToUpperInvariant(),
            Name = o.PropertyName,
            Timezone = o.Timezone,
            GstStateCode = o.GstStateCode,
        };
        var owner = new User
        {
            OrganizationId = org.Id,
            FullName = o.OwnerName,
            PasswordHash = hasher.Hash(o.OwnerPassword),
            MustChangePassword = o.OwnerMustChangePassword,
        };
        owner.SetEmail(o.OwnerEmail);

        var ownerRole = await db.Roles.SingleAsync(r => r.Code == "OWNER" && r.OrganizationId == null, ct);
        owner.Roles.Add(new UserRole { UserId = owner.Id, RoleId = ownerRole.Id, Role = ownerRole });

        db.AddRange(org, property, owner);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Bootstrap created organisation {Org}, property {Property} and owner {Email}", org.Code, property.Code, owner.Email);
    }
}
