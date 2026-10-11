namespace Resort.Contracts;

// ---------------------------------------------------------------- reservation sources
public sealed record ReservationSourceDto(Guid Id, string Code, string Name, string Channel, decimal? CommissionPercent);

// ---------------------------------------------------------------- rate plans
/// <summary>MealPlan: EP (room only), CP (+ breakfast), MAP (+ breakfast and one meal), AP (all meals).
/// IsDefault: the plan a quote uses when none is named; one per property.</summary>
public sealed record RatePlanDto(
    Guid Id, string Code, string Name, string? Description, string MealPlan, bool IsDefault, int SortOrder, bool IsActive, uint Version);

public sealed record CreateRatePlanRequest(string Code, string Name, string? Description, string MealPlan, bool IsDefault, int SortOrder);

/// <summary>Code can't change after creation (OTA rate mappings refer to it from Sprint 22).</summary>
public sealed record UpdateRatePlanRequest(
    string Name, string? Description, string MealPlan, bool IsDefault, int SortOrder, bool IsActive, uint Version);

// ---------------------------------------------------------------- rate grid
/// <summary>One cell of the grid. Price is per room per night, or per bed per night for a dormitory type.
/// ExtraAdult/ExtraChild are per guest per night above the type's standard occupancy (rooms only).
/// MinStay applies to stays arriving on this date.</summary>
public sealed record RatePriceDto(
    Guid AccommodationTypeId, DateOnly Date, decimal Price, decimal ExtraAdult, decimal ExtraChild, short MinStay,
    DateTimeOffset UpdatedAt);

/// <summary>Sets the same values on every night in [From, To) — To is not included, like a check-out date —
/// for each listed type. DaysOfWeek (e.g. ["Friday","Saturday"]) limits it to those nights; empty = every night.</summary>
public sealed record SetRatePricesRequest(
    IReadOnlyList<Guid> AccommodationTypeIds, DateOnly From, DateOnly To, IReadOnlyList<DayOfWeek>? DaysOfWeek,
    decimal Price, decimal ExtraAdult, decimal ExtraChild, short MinStay);

public sealed record SetRatePricesResult(int Created, int Changed, int Unchanged);

// ---------------------------------------------------------------- quote
/// <summary>A price for one room (or the beds for a group in a dormitory) for every night of a stay. Before tax.</summary>
public sealed record RateQuoteDto(
    Guid RatePlanId, string RatePlanCode, string RatePlanName, string MealPlan,
    Guid AccommodationTypeId, string AccommodationTypeName, string InventoryMode, string? RoomCode,
    DateOnly From, DateOnly To, int Nights, int Adults, int Children, int Units,
    IReadOnlyList<RateQuoteNightDto> Breakdown, decimal Total, string Currency);

public sealed record RateQuoteNightDto(
    DateOnly Date, int Units, decimal UnitPrice, decimal BaseAmount,
    int ExtraAdults, decimal ExtraAdultRate, decimal ExtraAdultAmount,
    int ExtraChildren, decimal ExtraChildRate, decimal ExtraChildAmount, decimal Total);

// ---------------------------------------------------------------- packages
public sealed record PackageComponentDto(Guid Id, string Category, string Description, decimal SharePercent, decimal Amount, int SortOrder);

/// <summary>Price is for the whole package (all nights, all included guests), before tax. The components' amounts always
/// add up to exactly the price. RevenueByCategory totals them per category (ROOM, FOOD, TRANSPORT, ACTIVITY).</summary>
public sealed record PackageDto(
    Guid Id, string Code, string Name, string? Description, Guid AccommodationTypeId, string AccommodationTypeName,
    short Nights, short Adults, short Children, decimal Price, int SortOrder, bool IsActive,
    IReadOnlyList<PackageComponentDto> Components, IReadOnlyDictionary<string, decimal> RevenueByCategory, uint Version);

/// <summary>SharePercent: this component's share of the package price; all shares must add up to exactly 100.</summary>
public sealed record PackageComponentInput(string Category, string Description, decimal SharePercent);

public sealed record CreatePackageRequest(
    string Code, string Name, string? Description, Guid AccommodationTypeId, short Nights, short Adults, short Children,
    decimal Price, int SortOrder, IReadOnlyList<PackageComponentInput> Components);

/// <summary>Code can't change after creation. Components replace the existing set.</summary>
public sealed record UpdatePackageRequest(
    string Name, string? Description, Guid AccommodationTypeId, short Nights, short Adults, short Children,
    decimal Price, int SortOrder, IReadOnlyList<PackageComponentInput> Components, bool IsActive, uint Version);
