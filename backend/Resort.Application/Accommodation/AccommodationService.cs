using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.FrontDesk;
using Resort.Domain.Housekeeping;

namespace Resort.Application.Accommodation;

/// <summary>
/// Room types, rooms/dormitories, beds and amenities for one property.
/// Every method takes the route's propertyId: the permission policy has already checked the caller's grant for that
/// property, and every query here is filtered by it, so an id from another property returns 404.
/// Nothing is deleted: types, rooms and beds are deactivated (history is kept for reservations and reports).
/// </summary>
public sealed class AccommodationService(IAppDbContext db, ICurrentUser caller, TimeProvider clock)
{
    // ------------------------------------------------------------------ amenities

    public async Task<IReadOnlyList<AmenityDto>> ListAmenitiesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        return await db.Amenities
            .Where(a => a.IsActive && (a.OrganizationId == null || a.OrganizationId == orgId))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new AmenityDto(a.Id, a.Code, a.Name, a.Category))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ accommodation types

    public async Task<IReadOnlyList<AccommodationTypeDto>> ListTypesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var types = await db.AccommodationTypes.Include(t => t.Amenities)
            .Where(t => t.PropertyId == propertyId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);
        var units = await UnitCapacityRowsAsync(propertyId, null, ct);
        return types.Select(t => ToDto(t, units)).ToList();
    }

    public async Task<AccommodationTypeDto> GetTypeAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var t = await LoadTypeAsync(propertyId, id, ct);
        return ToDto(t, await UnitCapacityRowsAsync(propertyId, id, ct));
    }

    public async Task<AccommodationTypeDto> CreateTypeAsync(Guid propertyId, CreateAccommodationTypeRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.AccommodationTypes.AnyAsync(t => t.PropertyId == propertyId && t.Code == code, ct))
            throw new ConflictAppException("TYPE_CODE_TAKEN", $"A room type with code '{code}' already exists at this property.");

        var t = new AccommodationType
        {
            OrganizationId = caller.RequireOrganizationId(),
            PropertyId = propertyId,
            Code = code,
        };
        Apply(t, r.Name, r.Description, r.InventoryMode, r.BaseOccupancy, r.MaxOccupancy, r.SizeSqm, r.SortOrder, isActive: true);
        await SetAmenitiesAsync(t, r.AmenityIds, ct);
        db.AccommodationTypes.Add(t);
        await db.SaveChangesAsync(ct);
        return ToDto(t, []);
    }

    public async Task<AccommodationTypeDto> UpdateTypeAsync(Guid propertyId, Guid id, UpdateAccommodationTypeRequest r, CancellationToken ct)
    {
        var t = await LoadTypeAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, t, r.Version);

        if (t.InventoryMode == InventoryModes.Bed && r.InventoryMode == InventoryModes.Unit
            && await db.Beds.AnyAsync(b => db.AccommodationUnits.Any(u => u.Id == b.UnitId && u.AccommodationTypeId == id), ct))
            throw new BusinessRuleAppException("TYPE_HAS_BEDS",
                "Units of this type have beds. Remove it from those units or keep it bed-based.");

        if (t.IsActive && !r.IsActive
            && await db.AccommodationUnits.AnyAsync(u => u.AccommodationTypeId == id && u.IsActive, ct))
            throw new BusinessRuleAppException("TYPE_HAS_ACTIVE_UNITS",
                "Deactivate or move the rooms of this type first.");

        Apply(t, r.Name, r.Description, r.InventoryMode, r.BaseOccupancy, r.MaxOccupancy, r.SizeSqm, r.SortOrder, r.IsActive);
        await SetAmenitiesAsync(t, r.AmenityIds, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(t, await UnitCapacityRowsAsync(propertyId, id, ct));
    }

    // ------------------------------------------------------------------ rooms

    public async Task<IReadOnlyList<RoomDto>> ListRoomsAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var rooms = await db.AccommodationUnits.Include(u => u.Type).Include(u => u.Beds)
            .Where(u => u.PropertyId == propertyId)
            .OrderBy(u => u.Type.SortOrder).ThenBy(u => u.SortOrder).ThenBy(u => u.Code)
            .ToListAsync(ct);
        return rooms.Select(ToDto).ToList();
    }

    public async Task<RoomDto> GetRoomAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        ToDto(await LoadRoomAsync(propertyId, id, ct));

    public async Task<RoomDto> CreateRoomAsync(Guid propertyId, CreateRoomRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var type = await RequireActiveTypeAsync(propertyId, r.AccommodationTypeId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.AccommodationUnits.AnyAsync(u => u.PropertyId == propertyId && u.Code == code, ct))
            throw new ConflictAppException("ROOM_CODE_TAKEN", $"A room with code '{code}' already exists at this property.");

        var u = new AccommodationUnit
        {
            OrganizationId = caller.RequireOrganizationId(),
            PropertyId = propertyId,
            AccommodationTypeId = type.Id,
            Type = type,
            Code = code,
        };
        Apply(u, r.Name, r.Floor, r.Notes, r.SortOrder, isActive: true);
        db.AccommodationUnits.Add(u);
        await db.SaveChangesAsync(ct);
        return ToDto(u);
    }

    public async Task<RoomDto> UpdateRoomAsync(Guid propertyId, Guid id, UpdateRoomRequest r, CancellationToken ct)
    {
        var u = await LoadRoomAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, u, r.Version);

        if (r.AccommodationTypeId != u.AccommodationTypeId)
        {
            var newType = await RequireActiveTypeAsync(propertyId, r.AccommodationTypeId, ct);
            if (u.Beds.Count > 0 && !newType.IsBedBased)
                throw new BusinessRuleAppException("UNIT_HAS_BEDS",
                    "This unit has beds, so it can only move to a bed-based (dormitory) type.");
            u.AccommodationTypeId = newType.Id;
            u.Type = newType;
        }
        Apply(u, r.Name, r.Floor, r.Notes, r.SortOrder, r.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(u);
    }

    // ------------------------------------------------------------------ beds

    public async Task<IReadOnlyList<BedDto>> ListBedsAsync(Guid propertyId, Guid roomId, CancellationToken ct)
    {
        var u = await LoadRoomAsync(propertyId, roomId, ct);
        return u.Beds.OrderBy(b => b.SortOrder).ThenBy(b => b.Code).Select(ToDto).ToList();
    }

    public async Task<BedDto> CreateBedAsync(Guid propertyId, Guid roomId, CreateBedRequest r, CancellationToken ct)
    {
        var u = await LoadRoomAsync(propertyId, roomId, ct);
        if (!u.Type.IsBedBased)
            throw new BusinessRuleAppException("BEDS_NOT_ALLOWED",
                $"{u.Name} is sold as a whole room. Beds can only be added to a bed-based (dormitory) type.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (u.Beds.Any(b => b.Code == code))
            throw new ConflictAppException("BED_CODE_TAKEN", $"{u.Name} already has a bed with code '{code}'.");

        var bed = new Bed
        {
            OrganizationId = u.OrganizationId,
            PropertyId = propertyId,
            UnitId = u.Id,
            Code = code,
            Name = r.Name.Trim(),
            BedType = r.BedType,
            SortOrder = r.SortOrder,
        };
        db.Beds.Add(bed);
        await db.SaveChangesAsync(ct);
        return ToDto(bed);
    }

    public async Task<BedDto> UpdateBedAsync(Guid propertyId, Guid bedId, UpdateBedRequest r, CancellationToken ct)
    {
        var bed = await db.Beds.SingleOrDefaultAsync(b => b.Id == bedId && b.PropertyId == propertyId, ct)
                  ?? throw new NotFoundAppException("Bed", bedId);
        ConcurrencyGuard.ExpectVersion(db, bed, r.Version);
        bed.Name = r.Name.Trim();
        bed.BedType = r.BedType;
        bed.SortOrder = r.SortOrder;
        bed.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(bed);
    }

    // ------------------------------------------------------------------ status board

    /// <summary>Each room's state on one property-local date (default: today at the property).</summary>
    public async Task<RoomStatusBoardDto> StatusAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        var day = date ?? property.LocalDate(clock.GetUtcNow());

        var rooms = await db.AccommodationUnits.Include(u => u.Type).Include(u => u.Beds)
            .Where(u => u.PropertyId == propertyId)
            .OrderBy(u => u.Type.SortOrder).ThenBy(u => u.SortOrder).ThenBy(u => u.Code)
            .ToListAsync(ct);
        var blocks = await db.UnitBlocks
            .Where(b => b.PropertyId == propertyId && b.Status == BlockStatuses.Active && b.StartDate <= day && day < b.EndDate)
            .ToListAsync(ct);
        // The cleaning state is "now", so it is shown on today's board only (Sprint 13).
        var isToday = day == property.LocalDate(clock.GetUtcNow());
        var housekeeping = isToday
            ? (await db.UnitHousekeepingStatus.AsNoTracking().Where(h => h.PropertyId == propertyId).ToListAsync(ct))
                .ToDictionary(h => (h.UnitId, h.BedId), h => h.Status)
            : [];
        var repairs = await db.MaintenanceTickets.AsNoTracking()
            .Where(t => t.PropertyId == propertyId && t.UnitId != null && MaintenanceStatuses.Unresolved.Contains(t.Status))
            .GroupBy(t => t.UnitId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        string? Hk(Guid unitId, Guid? bedId) => isToday ? housekeeping.GetValueOrDefault((unitId, bedId), HousekeepingStatuses.Ready) : null;

        var rows = rooms.Select(u =>
        {
            var unitBlock = blocks.FirstOrDefault(b => b.UnitId == u.Id && b.BedId == null);
            var beds = u.Beds.OrderBy(b => b.SortOrder).ThenBy(b => b.Code).Select(b =>
            {
                var bb = unitBlock ?? blocks.FirstOrDefault(x => x.BedId == b.Id);
                var status = !b.IsActive || !u.IsActive ? "INACTIVE" : bb is not null ? "BLOCKED" : "AVAILABLE";
                return new BedStatusDto(b.Id, b.Code, b.Name, b.IsActive, status, bb?.Id, Hk(u.Id, b.Id));
            }).ToList();
            var activeBeds = beds.Count(b => b.IsActive);
            var blockedBeds = beds.Count(b => b.IsActive && b.Status == "BLOCKED");

            string roomStatus;
            if (!u.IsActive) roomStatus = "INACTIVE";
            else if (unitBlock is not null) roomStatus = "BLOCKED";
            else if (u.Type.IsBedBased && blockedBeds > 0)
                roomStatus = blockedBeds == activeBeds ? "BLOCKED" : "PARTLY_BLOCKED";
            else roomStatus = "AVAILABLE";

            var shownBlock = unitBlock ?? (blockedBeds > 0 ? blocks.First(x => x.UnitId == u.Id) : null);
            return new RoomStatusDto(u.Id, u.Code, u.Name, u.Type.Name, u.Type.InventoryMode, u.IsActive, roomStatus,
                shownBlock is null ? null : BlockService.ToDto(shownBlock, u, beds.FirstOrDefault(x => x.BedId == shownBlock.BedId)?.Code, null),
                activeBeds, blockedBeds, beds,
                u.Type.IsBedBased && isToday
                    ? beds.Where(b => b.IsActive).Select(b => b.Housekeeping!).OrderBy(HousekeepingRank).FirstOrDefault() ?? HousekeepingStatuses.Ready
                    : Hk(u.Id, null),
                repairs.GetValueOrDefault(u.Id));
        }).ToList();

        return new RoomStatusBoardDto(day, rows);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Least ready first: a dormitory shows the state of its least ready bed.</summary>
    private static int HousekeepingRank(string status) => status switch
    {
        HousekeepingStatuses.OutOfOrder => 0,
        HousekeepingStatuses.Dirty => 1,
        HousekeepingStatuses.Cleaning => 2,
        HousekeepingStatuses.Clean => 3,
        HousekeepingStatuses.Inspection => 4,
        _ => 5,
    };

    private async Task RequirePropertyAsync(Guid propertyId, CancellationToken ct)
    {
        // Tenant filter applies: a property of another organisation is "not found".
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct))
            throw new NotFoundAppException("Property", propertyId);
    }

    private async Task<AccommodationType> LoadTypeAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.AccommodationTypes.Include(t => t.Amenities)
            .SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Room type", id);

    private async Task<AccommodationType> RequireActiveTypeAsync(Guid propertyId, Guid typeId, CancellationToken ct)
    {
        var type = await db.AccommodationTypes.SingleOrDefaultAsync(t => t.Id == typeId && t.PropertyId == propertyId, ct)
                   ?? throw new BusinessRuleAppException("UNKNOWN_TYPE", "That room type doesn't exist at this property.");
        if (!type.IsActive)
            throw new BusinessRuleAppException("TYPE_INACTIVE", $"Room type '{type.Name}' is inactive.");
        return type;
    }

    private async Task<AccommodationUnit> LoadRoomAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.AccommodationUnits.Include(u => u.Type).Include(u => u.Beds)
            .SingleOrDefaultAsync(u => u.Id == id && u.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Room", id);

    private sealed record UnitCapacityRow(Guid TypeId, bool UnitActive, int ActiveBeds);

    private async Task<List<UnitCapacityRow>> UnitCapacityRowsAsync(Guid propertyId, Guid? typeId, CancellationToken ct) =>
        await db.AccommodationUnits
            .Where(u => u.PropertyId == propertyId && (typeId == null || u.AccommodationTypeId == typeId))
            .Select(u => new UnitCapacityRow(u.AccommodationTypeId, u.IsActive, u.Beds.Count(b => b.IsActive)))
            .ToListAsync(ct);

    private async Task SetAmenitiesAsync(AccommodationType t, IReadOnlyList<Guid>? amenityIds, CancellationToken ct)
    {
        var wanted = (amenityIds ?? []).Distinct().ToList();
        var orgId = caller.RequireOrganizationId();
        var valid = await db.Amenities
            .Where(a => wanted.Contains(a.Id) && a.IsActive && (a.OrganizationId == null || a.OrganizationId == orgId))
            .Select(a => a.Id).ToListAsync(ct);
        if (valid.Count != wanted.Count)
            throw new BusinessRuleAppException("UNKNOWN_AMENITY", "One or more amenities don't exist.");

        t.Amenities.RemoveAll(a => !wanted.Contains(a.AmenityId));
        foreach (var id in wanted.Where(id => t.Amenities.All(a => a.AmenityId != id)))
            t.Amenities.Add(new AccommodationTypeAmenity { AccommodationTypeId = t.Id, AmenityId = id });
    }

    private static void Apply(AccommodationType t, string name, string? description, string mode, short baseOcc, short maxOcc,
        decimal? size, int sortOrder, bool isActive)
    {
        t.Name = name.Trim();
        t.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        t.InventoryMode = mode;
        t.BaseOccupancy = baseOcc;
        t.MaxOccupancy = maxOcc;
        t.SizeSqm = size;
        t.SortOrder = sortOrder;
        t.IsActive = isActive;
    }

    private static void Apply(AccommodationUnit u, string name, string? floor, string? notes, int sortOrder, bool isActive)
    {
        u.Name = name.Trim();
        u.Floor = string.IsNullOrWhiteSpace(floor) ? null : floor.Trim();
        u.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        u.SortOrder = sortOrder;
        u.IsActive = isActive;
    }

    private static AccommodationTypeDto ToDto(AccommodationType t, IEnumerable<UnitCapacityRow> allUnits)
    {
        var units = allUnits.Where(u => u.TypeId == t.Id).ToList();
        var cap = InventoryCalculator.For(t.InventoryMode, t.MaxOccupancy, units.Select(u => (u.UnitActive, u.ActiveBeds)));
        return new AccommodationTypeDto(t.Id, t.Code, t.Name, t.Description, t.InventoryMode, t.BaseOccupancy, t.MaxOccupancy,
            t.SizeSqm, t.SortOrder, t.IsActive, t.Amenities.Select(a => a.AmenityId).ToList(), units.Count,
            cap.SellableItems, cap.MaxGuests, t.Version);
    }

    internal static RoomDto ToDto(AccommodationUnit u) => new(
        u.Id, u.Code, u.Name, u.AccommodationTypeId, u.Type.Name, u.Type.InventoryMode, u.Floor, u.Notes, u.SortOrder,
        u.IsActive, u.Beds.OrderBy(b => b.SortOrder).ThenBy(b => b.Code).Select(ToDto).ToList(), u.Version);

    private static BedDto ToDto(Bed b) => new(b.Id, b.UnitId, b.Code, b.Name, b.BedType, b.SortOrder, b.IsActive, b.Version);
}
