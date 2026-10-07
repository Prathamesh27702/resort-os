using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Accommodation;

namespace Resort.Application.Accommodation;

/// <summary>
/// Blocks take a room, a whole dormitory or a single dorm bed out of sale for [StartDate, EndDate) in the property's
/// local dates. A reason is required to create and to release one, and is written to the audit trail.
///
/// Concurrency: creating a block locks the unit row (SELECT … FOR UPDATE) inside a transaction, then checks for
/// overlaps. A second request for the same unit waits for the first to commit and then sees its block, so it gets
/// 409 BLOCK_OVERLAP. The database enforces the same rule independently (EXCLUDE constraints + trigger in V004),
/// which maps to 409 OVERLAP if anything ever bypasses this service.
/// </summary>
public sealed class BlockService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock)
{
    public sealed record BlockQuery(DateOnly? From, DateOnly? To, Guid? UnitId, bool IncludeReleased = false);

    /// <summary>Blocks overlapping [From, To). Default From = property's today, so past blocks drop off the list.</summary>
    public async Task<IReadOnlyList<UnitBlockDto>> ListAsync(Guid propertyId, BlockQuery q, CancellationToken ct)
    {
        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        var from = q.From ?? property.LocalDate(clock.GetUtcNow());
        var to = q.To ?? DateOnly.MaxValue;

        var rows = db.UnitBlocks.Where(b => b.PropertyId == propertyId && b.EndDate > from && b.StartDate < to);
        if (!q.IncludeReleased) rows = rows.Where(b => b.Status == BlockStatuses.Active);
        if (q.UnitId is { } uid) rows = rows.Where(b => b.UnitId == uid);
        var blocks = await rows.OrderBy(b => b.StartDate).ThenBy(b => b.CreatedAt).ToListAsync(ct);
        return await ToDtosAsync(blocks, ct);
    }

    public async Task<UnitBlockDto> CreateAsync(Guid propertyId, CreateBlockRequest r, CancellationToken ct)
    {
        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.EndDate <= today)
            throw new BusinessRuleAppException("BLOCK_IN_PAST", "A block must end after today (property time).");

        var unit = await db.AccommodationUnits.Include(u => u.Type).Include(u => u.Beds)
                       .SingleOrDefaultAsync(u => u.Id == r.UnitId && u.PropertyId == propertyId, ct)
                   ?? throw new BusinessRuleAppException("UNKNOWN_ROOM", "That room doesn't exist at this property.");
        if (!unit.IsActive)
            throw new BusinessRuleAppException("ROOM_INACTIVE", $"{unit.Name} is inactive.");

        string? bedCode = null;
        if (r.BedId is { } bedId)
        {
            if (!unit.Type.IsBedBased)
                throw new BusinessRuleAppException("BEDS_NOT_ALLOWED", $"{unit.Name} is sold as a whole room; block the room instead.");
            var bed = unit.Beds.SingleOrDefault(b => b.Id == bedId)
                      ?? throw new BusinessRuleAppException("UNKNOWN_BED", $"That bed isn't in {unit.Name}.");
            if (!bed.IsActive)
                throw new BusinessRuleAppException("BED_INACTIVE", $"Bed {bed.Code} is inactive.");
            bedCode = bed.Code;
        }

        var reason = r.Reason.Trim();
        audit.Reason = reason;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialise block changes per unit (same lock the database trigger takes).
        var orgId = caller.RequireOrganizationId();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accommodation_units WHERE id = {unit.Id} AND organization_id = {orgId} FOR UPDATE", ct);

        var clash = await db.UnitBlocks
            .Where(b => b.UnitId == unit.Id && b.Status == BlockStatuses.Active
                        && b.StartDate < r.EndDate && r.StartDate < b.EndDate
                        // whole-unit blocks clash with everything in the unit; bed blocks clash with the same bed
                        && (r.BedId == null || b.BedId == null || b.BedId == r.BedId))
            .OrderBy(b => b.StartDate)
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
            throw new ConflictAppException("BLOCK_OVERLAP",
                $"{unit.Name}{(clash.BedId is null ? "" : " (a bed)")} is already blocked {NightsText(clash.StartDate, clash.EndDate)} ({clash.Reason}).");

        var block = new UnitBlock
        {
            OrganizationId = unit.OrganizationId,
            PropertyId = propertyId,
            UnitId = unit.Id,
            BedId = r.BedId,
            BlockType = r.BlockType,
            StartDate = r.StartDate,
            EndDate = r.EndDate,
            Reason = reason,
        };
        db.UnitBlocks.Add(block);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToDto(block, unit, bedCode, await UserNameAsync(block.CreatedBy, ct));
    }

    public async Task<UnitBlockDto> ReleaseAsync(Guid propertyId, Guid blockId, ReleaseBlockRequest r, CancellationToken ct)
    {
        var block = await db.UnitBlocks.SingleOrDefaultAsync(b => b.Id == blockId && b.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("Block", blockId);
        if (!block.IsActive)
            throw new BusinessRuleAppException("BLOCK_NOT_ACTIVE", "This block has already been released.");

        var reason = r.Reason.Trim();
        audit.Reason = reason;
        block.Status = BlockStatuses.Released;
        block.ReleasedAt = clock.GetUtcNow();
        block.ReleasedBy = caller.UserId;
        block.ReleaseReason = reason;
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([block], ct))[0];
    }

    private async Task<List<UnitBlockDto>> ToDtosAsync(List<UnitBlock> blocks, CancellationToken ct)
    {
        var unitIds = blocks.Select(b => b.UnitId).Distinct().ToList();
        var units = await db.AccommodationUnits.Include(u => u.Beds).Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var userIds = blocks.Where(b => b.CreatedBy != null).Select(b => b.CreatedBy!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return blocks.Select(b =>
        {
            var u = units[b.UnitId];
            var bedCode = b.BedId is null ? null : u.Beds.FirstOrDefault(x => x.Id == b.BedId)?.Code;
            return ToDto(b, u, bedCode, b.CreatedBy is { } id && names.TryGetValue(id, out var n) ? n : null);
        }).ToList();
    }

    private async Task<string?> UserNameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.IgnoreQueryFilters().Where(u => u.Id == id).Select(u => u.FullName).SingleOrDefaultAsync(ct);

    internal static UnitBlockDto ToDto(UnitBlock b, Domain.Accommodation.AccommodationUnit u, string? bedCode, string? createdByName) => new(
        b.Id, b.UnitId, u.Code, u.Name, b.BedId, bedCode, b.BlockType, b.StartDate, b.EndDate,
        b.EndDate.DayNumber - b.StartDate.DayNumber, b.Reason, b.Status, b.CreatedAt, createdByName, b.ReleasedAt, b.ReleaseReason);

    /// <summary>"the night of 7 Oct 2026" or "the nights of 7 Oct – 8 Oct 2026" (the end date itself is not blocked).</summary>
    internal static string NightsText(DateOnly start, DateOnly endExclusive)
    {
        var last = endExclusive.AddDays(-1);
        var c = CultureInfo.InvariantCulture;
        return last == start
            ? string.Create(c, $"for the night of {start:d MMM yyyy}")
            : start.Year == last.Year
                ? string.Create(c, $"for the nights of {start:d MMM} – {last:d MMM yyyy}")
                : string.Create(c, $"for the nights of {start:d MMM yyyy} – {last:d MMM yyyy}");
    }
}
