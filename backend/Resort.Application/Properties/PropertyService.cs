using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Organizations;

namespace Resort.Application.Properties;

public sealed class PropertyService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock)
{
    /// <summary>Properties the caller can see (org-wide grant, or a grant for that property).</summary>
    public async Task<IReadOnlyList<PropertyDto>> ListAsync(CancellationToken ct)
    {
        var acc = await access.GetAsync(ct);
        var all = await db.Properties.OrderBy(p => p.Name).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return all.Where(p => acc.Has(Perm.PropertyRead, p.Id)).Select(p => ToDto(p, now)).ToList();
    }

    public async Task<PropertyDto> GetAsync(Guid propertyId, CancellationToken ct) =>
        ToDto(await LoadAsync(propertyId, ct), clock.GetUtcNow());

    public async Task<PropertyDto> CreateAsync(CreatePropertyRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.Properties.AnyAsync(p => p.Code == code, ct))
            throw new ConflictAppException("PROPERTY_CODE_TAKEN", $"Property code '{code}' is already used.");

        var p = new Property { OrganizationId = orgId, Code = code };
        Apply(p, r.Name, r.LegalName, r.PropertyType, r.Timezone, r.Currency, r.AddressLine1, r.AddressLine2, r.City,
            r.District, r.StateName, r.GstStateCode, r.PostalCode, r.CountryCode, r.Phone, r.Email, r.CheckInTime,
            r.CheckOutTime, r.Latitude, r.Longitude, isActive: true);
        db.Properties.Add(p);
        await db.SaveChangesAsync(ct);
        return ToDto(p, clock.GetUtcNow());
    }

    public async Task<PropertyDto> UpdateAsync(Guid propertyId, UpdatePropertyRequest r, CancellationToken ct)
    {
        var p = await LoadAsync(propertyId, ct);
        ConcurrencyGuard.ExpectVersion(db, p, r.Version);
        Apply(p, r.Name, r.LegalName, r.PropertyType, r.Timezone, r.Currency, r.AddressLine1, r.AddressLine2, r.City,
            r.District, r.StateName, r.GstStateCode, r.PostalCode, r.CountryCode, r.Phone, r.Email, r.CheckInTime,
            r.CheckOutTime, r.Latitude, r.Longitude, r.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(p, clock.GetUtcNow());
    }

    public async Task<IReadOnlyList<PropertySettingDto>> GetSettingsAsync(Guid propertyId, CancellationToken ct)
    {
        await LoadAsync(propertyId, ct);
        var rows = await db.PropertySettings.Where(s => s.PropertyId == propertyId).OrderBy(s => s.Key).ToListAsync(ct);
        return rows.Select(s => new PropertySettingDto(s.Key, JsonDocument.Parse(s.Value).RootElement.Clone(), s.UpdatedAt)).ToList();
    }

    public async Task<PropertySettingDto> PutSettingAsync(Guid propertyId, string key, JsonElement value, CancellationToken ct)
    {
        var p = await LoadAsync(propertyId, ct);
        var row = await db.PropertySettings.SingleOrDefaultAsync(s => s.PropertyId == propertyId && s.Key == key, ct);
        if (row is null)
        {
            row = new PropertySetting { OrganizationId = p.OrganizationId, PropertyId = p.Id, Key = key };
            db.PropertySettings.Add(row);
        }
        row.Value = value.GetRawText();
        await db.SaveChangesAsync(ct);
        return new PropertySettingDto(row.Key, value.Clone(), row.UpdatedAt);
    }

    private async Task<Property> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Properties.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundAppException("Property", id);

    private static void Apply(Property p, string name, string? legalName, string type, string tz, string currency,
        string? a1, string? a2, string? city, string? district, string? state, string? gstState, string? postal,
        string country, string? phone, string? email, TimeOnly ci, TimeOnly co, decimal? lat, decimal? lng, bool isActive)
    {
        p.Name = name.Trim();
        p.LegalName = NullIfBlank(legalName);
        p.PropertyType = type;
        p.Timezone = tz;
        p.Currency = currency.ToUpperInvariant();
        p.AddressLine1 = NullIfBlank(a1);
        p.AddressLine2 = NullIfBlank(a2);
        p.City = NullIfBlank(city);
        p.District = NullIfBlank(district);
        p.StateName = NullIfBlank(state);
        p.GstStateCode = NullIfBlank(gstState);
        p.PostalCode = NullIfBlank(postal);
        p.CountryCode = country.ToUpperInvariant();
        p.Phone = NullIfBlank(phone);
        p.Email = NullIfBlank(email);
        p.CheckInTime = ci;
        p.CheckOutTime = co;
        p.Latitude = lat;
        p.Longitude = lng;
        p.IsActive = isActive;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static PropertyDto ToDto(Property p, DateTimeOffset now) => new(
        p.Id, p.Code, p.Name, p.LegalName, p.PropertyType, p.Timezone, p.Currency, p.AddressLine1, p.AddressLine2,
        p.City, p.District, p.StateName, p.GstStateCode, p.PostalCode, p.CountryCode, p.Phone, p.Email,
        p.CheckInTime, p.CheckOutTime, p.Latitude, p.Longitude, p.IsActive, p.LocalDate(now), p.Version);
}

public sealed class OrganizationService(IAppDbContext db, ICurrentUser caller)
{
    public async Task<OrganizationDto> GetAsync(CancellationToken ct) => ToDto(await LoadAsync(ct));

    public async Task<OrganizationDto> UpdateAsync(UpdateOrganizationRequest r, CancellationToken ct)
    {
        var org = await LoadAsync(ct);
        ConcurrencyGuard.ExpectVersion(db, org, r.Version);
        org.Name = r.Name.Trim();
        org.LegalName = string.IsNullOrWhiteSpace(r.LegalName) ? null : r.LegalName.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(org);
    }

    // Organisation isn't IOrganizationScoped (it IS the tenant), so scope it explicitly.
    private async Task<Organization> LoadAsync(CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        return await db.Organizations.SingleAsync(o => o.Id == orgId, ct);
    }

    private static OrganizationDto ToDto(Organization o) =>
        new(o.Id, o.Code, o.Name, o.LegalName, o.BaseCurrency, o.CountryCode, o.Version);
}
