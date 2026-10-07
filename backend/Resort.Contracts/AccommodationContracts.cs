namespace Resort.Contracts;

// ---------------------------------------------------------------- amenities
public sealed record AmenityDto(Guid Id, string Code, string Name, string Category);

// ---------------------------------------------------------------- accommodation types
/// <summary>SellableItems = active units (UNIT mode) or active beds in active units (BED mode).</summary>
public sealed record AccommodationTypeDto(
    Guid Id, string Code, string Name, string? Description, string InventoryMode,
    short BaseOccupancy, short MaxOccupancy, decimal? SizeSqm, int SortOrder, bool IsActive,
    IReadOnlyList<Guid> AmenityIds, int UnitCount, int SellableItems, int MaxGuests, uint Version);

public sealed record CreateAccommodationTypeRequest(
    string Code, string Name, string? Description, string InventoryMode,
    short BaseOccupancy, short MaxOccupancy, decimal? SizeSqm, int SortOrder, IReadOnlyList<Guid>? AmenityIds);

/// <summary>Code can't change after creation (later sprints map it to rate plans and OTA room codes).</summary>
public sealed record UpdateAccommodationTypeRequest(
    string Name, string? Description, string InventoryMode,
    short BaseOccupancy, short MaxOccupancy, decimal? SizeSqm, int SortOrder, IReadOnlyList<Guid>? AmenityIds,
    bool IsActive, uint Version);

// ---------------------------------------------------------------- rooms (accommodation units)
public sealed record BedDto(Guid Id, Guid UnitId, string Code, string Name, string BedType, int SortOrder, bool IsActive, uint Version);

public sealed record RoomDto(
    Guid Id, string Code, string Name, Guid AccommodationTypeId, string AccommodationTypeName, string InventoryMode,
    string? Floor, string? Notes, int SortOrder, bool IsActive, IReadOnlyList<BedDto> Beds, uint Version);

public sealed record CreateRoomRequest(string Code, string Name, Guid AccommodationTypeId, string? Floor, string? Notes, int SortOrder);

/// <summary>Code can't change after creation (reservations and housekeeping refer to it).</summary>
public sealed record UpdateRoomRequest(
    string Name, Guid AccommodationTypeId, string? Floor, string? Notes, int SortOrder, bool IsActive, uint Version);

public sealed record CreateBedRequest(string Code, string Name, string BedType, int SortOrder);

public sealed record UpdateBedRequest(string Name, string BedType, int SortOrder, bool IsActive, uint Version);

// ---------------------------------------------------------------- blocks
/// <summary>Dates are the property's local calendar dates; EndDate is exclusive (like a check-out date).</summary>
public sealed record UnitBlockDto(
    Guid Id, Guid UnitId, string UnitCode, string UnitName, Guid? BedId, string? BedCode,
    string BlockType, DateOnly StartDate, DateOnly EndDate, int Nights, string Reason, string Status,
    DateTimeOffset CreatedAt, string? CreatedByName, DateTimeOffset? ReleasedAt, string? ReleaseReason);

public sealed record CreateBlockRequest(Guid UnitId, Guid? BedId, string BlockType, DateOnly StartDate, DateOnly EndDate, string Reason);

public sealed record ReleaseBlockRequest(string Reason);

// ---------------------------------------------------------------- room status board (read-only in Sprint 2)
/// <summary>Status: AVAILABLE, BLOCKED, PARTLY_BLOCKED (some dorm beds blocked) or INACTIVE.
/// Occupancy and housekeeping states are added by Sprints 5–7 and 13.</summary>
public sealed record RoomStatusBoardDto(DateOnly Date, IReadOnlyList<RoomStatusDto> Rooms);

public sealed record RoomStatusDto(
    Guid UnitId, string Code, string Name, string TypeName, string InventoryMode, bool IsActive, string Status,
    UnitBlockDto? Block, int ActiveBeds, int BlockedBeds, IReadOnlyList<BedStatusDto> Beds);

public sealed record BedStatusDto(Guid BedId, string Code, string Name, bool IsActive, string Status, Guid? BlockId);
