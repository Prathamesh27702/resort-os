using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.Rates;

namespace Resort.Application.Rates;

/// <summary>
/// Packages for one property: a fixed price for N nights in a room type plus food, transport or activities, split into
/// revenue categories. The manager enters each component's share in percent; the amounts are computed here with
/// <see cref="MoneySplit.ByPercent"/>, so they always add up to exactly the package price (the database checks it too).
/// Packages are deactivated, never deleted.
/// </summary>
public sealed class PackageService(IAppDbContext db, ICurrentUser caller)
{
    public async Task<IReadOnlyList<PackageDto>> ListAsync(Guid propertyId, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct))
            throw new NotFoundAppException("Property", propertyId);
        var list = await db.Packages.Include(p => p.Type).Include(p => p.Components)
            .Where(p => p.PropertyId == propertyId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<PackageDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct) => ToDto(await LoadAsync(propertyId, id, ct));

    public async Task<PackageDto> CreateAsync(Guid propertyId, CreatePackageRequest r, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct))
            throw new NotFoundAppException("Property", propertyId);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.Packages.AnyAsync(p => p.PropertyId == propertyId && p.Code == code, ct))
            throw new ConflictAppException("PACKAGE_CODE_TAKEN", $"A package with code '{code}' already exists at this property.");

        var type = await RequireTypeAsync(propertyId, r.AccommodationTypeId, r.Adults, r.Children, ct);
        var pkg = new Package
        {
            OrganizationId = caller.RequireOrganizationId(),
            PropertyId = propertyId,
            Code = code,
            AccommodationTypeId = type.Id,
            Type = type,
        };
        Apply(pkg, r.Name, r.Description, r.Nights, r.Adults, r.Children, r.Price, r.SortOrder, isActive: true);
        SetComponents(pkg, r.Components);
        db.Packages.Add(pkg);
        await db.SaveChangesAsync(ct);
        return ToDto(pkg);
    }

    public async Task<PackageDto> UpdateAsync(Guid propertyId, Guid id, UpdatePackageRequest r, CancellationToken ct)
    {
        var pkg = await LoadAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, pkg, r.Version);

        var type = r.AccommodationTypeId == pkg.AccommodationTypeId && !r.IsActive
            ? pkg.Type  // deactivating a package whose room type was since deactivated is fine
            : await RequireTypeAsync(propertyId, r.AccommodationTypeId, r.Adults, r.Children, ct);
        pkg.AccommodationTypeId = type.Id;
        pkg.Type = type;
        Apply(pkg, r.Name, r.Description, r.Nights, r.Adults, r.Children, r.Price, r.SortOrder, r.IsActive);
        SetComponents(pkg, r.Components);
        await db.SaveChangesAsync(ct);
        return ToDto(pkg);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Package> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.Packages.Include(p => p.Type).Include(p => p.Components)
            .SingleOrDefaultAsync(p => p.Id == id && p.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Package", id);

    private async Task<AccommodationType> RequireTypeAsync(Guid propertyId, Guid typeId, short adults, short children, CancellationToken ct)
    {
        var type = await db.AccommodationTypes.SingleOrDefaultAsync(t => t.Id == typeId && t.PropertyId == propertyId, ct)
                   ?? throw RateService.Field(nameof(CreatePackageRequest.AccommodationTypeId),
                       "That room type doesn't exist at this property. Reload the page and choose again.");
        if (!type.IsActive)
            throw RateService.Field(nameof(CreatePackageRequest.AccommodationTypeId),
                $"Room type '{type.Name}' is inactive. Choose an active room type.");
        var guests = adults + children;
        if (!type.IsBedBased && guests > type.MaxOccupancy)
            throw RateService.Field(nameof(CreatePackageRequest.Adults),
                $"{type.Name} sleeps at most {type.MaxOccupancy} guests; this package includes {guests}.");
        return type;
    }

    /// <summary>Replaces the components and computes each amount from its share (largest-remainder rounding).</summary>
    private void SetComponents(Package pkg, IReadOnlyList<PackageComponentInput> input)
    {
        var amounts = MoneySplit.ByPercent(pkg.Price, input.Select(c => c.SharePercent).ToList());
        foreach (var old in pkg.Components) db.PackageComponents.Remove(old);
        pkg.Components = input.Select((c, i) => new PackageComponent
        {
            OrganizationId = pkg.OrganizationId,
            PropertyId = pkg.PropertyId,
            PackageId = pkg.Id,
            Category = c.Category,
            Description = c.Description.Trim(),
            SharePercent = c.SharePercent,
            Amount = amounts[i],
            SortOrder = i,
        }).ToList();
        foreach (var c in pkg.Components) db.PackageComponents.Add(c);
    }

    private static void Apply(Package p, string name, string? description, short nights, short adults, short children,
        decimal price, int sortOrder, bool isActive)
    {
        p.Name = name.Trim();
        p.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        p.Nights = nights;
        p.Adults = adults;
        p.Children = children;
        p.Price = price;
        p.SortOrder = sortOrder;
        p.IsActive = isActive;
    }

    internal static PackageDto ToDto(Package p)
    {
        var components = p.Components.OrderBy(c => c.SortOrder).ToList();
        var byCategory = RevenueCategories.All
            .Select(cat => (cat, sum: components.Where(c => c.Category == cat).Sum(c => c.Amount)))
            .Where(x => components.Any(c => c.Category == x.cat))
            .ToDictionary(x => x.cat, x => x.sum);
        return new PackageDto(p.Id, p.Code, p.Name, p.Description, p.AccommodationTypeId, p.Type.Name, p.Nights, p.Adults,
            p.Children, p.Price, p.SortOrder, p.IsActive,
            components.Select(c => new PackageComponentDto(c.Id, c.Category, c.Description, c.SharePercent, c.Amount, c.SortOrder)).ToList(),
            byCategory, p.Version);
    }
}
