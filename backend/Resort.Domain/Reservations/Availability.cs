using Resort.Domain.Accommodation;

namespace Resort.Domain.Reservations;

/// <summary>A room or dormitory as the availability engine sees it.</summary>
public sealed record InventoryUnit(
    Guid UnitId, Guid TypeId, string Code, string Name, int SortOrder, bool IsActive, string Mode, IReadOnlyList<InventoryBed> Beds);

public sealed record InventoryBed(Guid BedId, string Code, string Name, int SortOrder, bool IsActive);

/// <summary>Something that takes a room or bed for [Start, End): a held reservation unit or an active block.
/// BedId null = the whole room (for a dorm: every bed in it). Label says what it is, for messages
/// ("booked on R00012", "blocked: AC repair").</summary>
public sealed record Occupation(Guid UnitId, Guid? BedId, DateOnly Start, DateOnly End, string Label)
{
    public bool Overlaps(DateOnly from, DateOnly to) => Start < to && from < End;
}

/// <summary>What is free for a whole stay: rooms (UNIT mode) and beds (BED mode).</summary>
public sealed record FreeInventory(IReadOnlyList<InventoryUnit> FreeRooms, IReadOnlyList<(InventoryUnit Unit, InventoryBed Bed)> FreeBeds)
{
    public int FreeCount(Guid typeId, string mode) =>
        mode == InventoryModes.Bed ? FreeBeds.Count(b => b.Unit.TypeId == typeId) : FreeRooms.Count(u => u.TypeId == typeId);
}

/// <summary>One requested room line: a room type, optionally a specific room, and who sleeps there. For a dormitory
/// every guest takes one bed.</summary>
public sealed record StayLine(Guid TypeId, string TypeName, string Mode, Guid? UnitId, int Adults, int Children)
{
    public int Guests => Adults + Children;
}

public sealed record Allocation(int Line, Guid TypeId, Guid UnitId, Guid? BedId, int Adults, int Children);

public sealed record AllocationResult(IReadOnlyList<Allocation> Allocations, string? ProblemMessage, int? ProblemLine)
{
    public bool Ok => ProblemMessage is null;
}

/// <summary>
/// The availability engine. Pure: no database, no clock — the caller loads the rooms, the held reservation units and
/// the active blocks (under row locks when booking) and these functions decide. Every rule is unit-tested.
///
/// Rules (stays are [from, to): the departure day is not a night)
///  * A room is free if it is active and no held reservation and no active block on it overlaps the stay.
///  * A dorm bed is free if the bed and its dorm are active, no held reservation on that bed overlaps the stay, and no
///    active block on that bed or on the whole dorm overlaps it.
///  * Allocation: a line naming a room gets that room or fails; otherwise rooms are taken in the property's order
///    (sort order, then code), preferring the ones in <c>preferred</c> (a changed booking keeps its rooms if it can).
///    Dorm lines take one bed per guest, filling one dorm before the next.
/// </summary>
public static class AvailabilityEngine
{
    public static FreeInventory Free(IEnumerable<InventoryUnit> units, IEnumerable<Occupation> taken, DateOnly from, DateOnly to)
    {
        var overlapping = taken.Where(o => o.Overlaps(from, to)).ToList();
        var wholeTaken = overlapping.Where(o => o.BedId is null).Select(o => o.UnitId).ToHashSet();
        var bedTaken = overlapping.Where(o => o.BedId is not null).Select(o => o.BedId!.Value).ToHashSet();

        var ordered = units.Where(u => u.IsActive).OrderBy(u => u.SortOrder).ThenBy(u => u.Code, StringComparer.Ordinal).ToList();
        var rooms = ordered.Where(u => u.Mode == InventoryModes.Unit && !wholeTaken.Contains(u.UnitId)).ToList();
        var beds = ordered.Where(u => u.Mode == InventoryModes.Bed && !wholeTaken.Contains(u.UnitId))
            .SelectMany(u => u.Beds.Where(b => b.IsActive && !bedTaken.Contains(b.BedId))
                .OrderBy(b => b.SortOrder).ThenBy(b => b.Code, StringComparer.Ordinal)
                .Select(b => (u, b)))
            .ToList();
        return new FreeInventory(rooms, beds);
    }

    /// <param name="stayText">"for the nights of 7 – 8 Jan 2027", used in messages</param>
    /// <param name="preferred">rooms and beds to use first (a changed booking's current ones)</param>
    public static AllocationResult Allocate(
        IReadOnlyList<StayLine> lines, FreeInventory free, IReadOnlyList<InventoryUnit> units, IReadOnlyList<Occupation> taken,
        DateOnly from, DateOnly to, string stayText, IReadOnlySet<Guid>? preferred = null)
    {
        preferred ??= new HashSet<Guid>();
        var usedRooms = new HashSet<Guid>();
        var usedBeds = new HashSet<Guid>();
        var result = new List<Allocation>();

        // Lines naming a room first, so "any room" lines don't take a room someone asked for by name.
        foreach (var i in Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].UnitId is null ? 1 : 0))
        {
            var line = lines[i];
            if (line.Mode == InventoryModes.Unit)
            {
                InventoryUnit? room;
                if (line.UnitId is { } wanted)
                {
                    var unit = units.FirstOrDefault(u => u.UnitId == wanted);
                    room = free.FreeRooms.FirstOrDefault(u => u.UnitId == wanted);
                    if (room is null || usedRooms.Contains(wanted))
                    {
                        var name = unit?.Name ?? "That room";
                        var why = unit is { IsActive: false } ? "it is inactive"
                            : usedRooms.Contains(wanted) ? "this booking already has it on another line"
                            : taken.FirstOrDefault(o => o.UnitId == wanted && o.Overlaps(from, to))?.Label ?? "it is taken";
                        return Fail($"{name} isn't free {stayText}: {why}. Choose another room.", i);
                    }
                }
                else
                {
                    var candidates = free.FreeRooms.Where(u => u.TypeId == line.TypeId && !usedRooms.Contains(u.UnitId)).ToList();
                    room = candidates.FirstOrDefault(u => preferred.Contains(u.UnitId)) ?? candidates.FirstOrDefault();
                    if (room is null)
                    {
                        var need = lines.Count(l => l.TypeId == line.TypeId);
                        var have = free.FreeCount(line.TypeId, line.Mode);
                        return Fail(have == 0
                            ? $"No {line.TypeName} is free {stayText}. Choose another room type or other dates."
                            : $"Only {have} {line.TypeName}{(have == 1 ? " is" : "s are")} free {stayText}; this booking needs {need}.", i);
                    }
                }
                usedRooms.Add(room.UnitId);
                result.Add(new Allocation(i, line.TypeId, room.UnitId, null, line.Adults, line.Children));
            }
            else
            {
                var candidates = free.FreeBeds.Where(b => b.Unit.TypeId == line.TypeId && !usedBeds.Contains(b.Bed.BedId)
                                                          && (line.UnitId is null || b.Unit.UnitId == line.UnitId)).ToList();
                if (candidates.Count < line.Guests)
                {
                    var where = line.UnitId is { } u ? units.FirstOrDefault(x => x.UnitId == u)?.Name ?? line.TypeName : line.TypeName;
                    return Fail(candidates.Count == 0
                        ? $"No bed is free in {where} {stayText}. Choose another room type or other dates."
                        : $"Only {candidates.Count} bed{(candidates.Count == 1 ? " is" : "s are")} free in {where} {stayText}; " +
                          $"this booking needs {line.Guests} (one per guest).", i);
                }
                // Keep a group together: preferred beds first, then the dorm with the most free beds, in bed order.
                var picked = candidates
                    .OrderByDescending(b => preferred.Contains(b.Bed.BedId))
                    .ThenByDescending(b => candidates.Count(c => c.Unit.UnitId == b.Unit.UnitId))
                    .ThenBy(b => candidates.IndexOf(b))
                    .Take(line.Guests).ToList();
                for (var g = 0; g < picked.Count; g++)
                {
                    var isAdult = g < line.Adults;
                    usedBeds.Add(picked[g].Bed.BedId);
                    result.Add(new Allocation(i, line.TypeId, picked[g].Unit.UnitId, picked[g].Bed.BedId, isAdult ? 1 : 0, isAdult ? 0 : 1));
                }
            }
        }
        return new AllocationResult(result.OrderBy(a => a.Line).ToList(), null, null);

        static AllocationResult Fail(string message, int line) => new([], message, line);
    }
}
