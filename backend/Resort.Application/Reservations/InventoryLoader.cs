using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Accommodation;
using Resort.Domain.Reservations;

namespace Resort.Application.Reservations;

/// <summary>Loads what the <see cref="AvailabilityEngine"/> needs: rooms with their beds, and what takes them for a stay.</summary>
internal static class InventoryLoader
{
    public static async Task<List<InventoryUnit>> UnitsAsync(IAppDbContext db, Guid propertyId, IReadOnlyCollection<Guid>? typeIds, CancellationToken ct)
    {
        var q = db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds).Where(u => u.PropertyId == propertyId);
        if (typeIds is not null) q = q.Where(u => typeIds.Contains(u.AccommodationTypeId));
        var units = await q.ToListAsync(ct);
        return units.Select(u => new InventoryUnit(u.Id, u.AccommodationTypeId, u.Code, u.Name, u.SortOrder, u.IsActive && u.Type.IsActive,
            u.Type.InventoryMode, u.Beds.Select(b => new InventoryBed(b.Id, b.Code, b.Name, b.SortOrder, b.IsActive)).ToList())).ToList();
    }

    /// <summary>Held reservation units and active blocks on these rooms that overlap [from, to). A reservation being
    /// changed is left out (<paramref name="excludeReservation"/>), so it can keep its own rooms.</summary>
    public static async Task<List<Occupation>> TakenAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> unitIds, DateOnly from, DateOnly to, Guid? excludeReservation, CancellationToken ct)
    {
        var held = await db.ReservationUnits.AsNoTracking()
            .Where(u => unitIds.Contains(u.UnitId) && u.HoldsInventory && u.StartDate < to && from < u.EndDate
                        && u.ReservationId != excludeReservation)
            .Join(db.Reservations, u => u.ReservationId, r => r.Id, (u, r) => new { u.UnitId, u.BedId, u.StartDate, u.EndDate, r.Reference })
            .ToListAsync(ct);
        var blocks = await db.UnitBlocks.AsNoTracking()
            .Where(b => unitIds.Contains(b.UnitId) && b.Status == BlockStatuses.Active && b.StartDate < to && from < b.EndDate)
            .ToListAsync(ct);
        return held.Select(h => new Occupation(h.UnitId, h.BedId, h.StartDate, h.EndDate, $"it is booked on {h.Reference}"))
            .Concat(blocks.Select(b => new Occupation(b.UnitId, b.BedId, b.StartDate, b.EndDate, $"it is blocked ({b.Reason})")))
            .ToList();
    }
}
