using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Activities;
using Resort.Domain.Organizations;
using Resort.Domain.Reservations;

namespace Resort.Application.Activities;

/// <summary>
/// Equipment (Sprint 20, brief §49): kinds and items, reserving and issuing them to a booking, a stay or a name, and taking
/// them back good, damaged or lost.
/// <list type="bullet">
/// <item>Reserving or issuing locks the items' rows (SELECT … ORDER BY id FOR UPDATE) and checks each is available, so two
/// people can't hand out the same kayak; a unique index (one open assignment per item) stands behind it.</item>
/// <item>An item's moves are in <see cref="EquipmentStatuses.Transitions"/>, checked here first with the reason and again
/// by a database trigger. A damaged item goes to maintenance and back to available; a lost one can be found.</item>
/// </list>
/// </summary>
public sealed class EquipmentService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock,
    ActivityService activities)
{
    public const int MaxIssue = 50;

    // ------------------------------------------------------------------ reading

    public async Task<EquipmentBoardDto> BoardAsync(Guid propertyId, CancellationToken ct)
    {
        await activities.RequirePropertyAsync(propertyId, ct);
        var types = await db.EquipmentTypes.AsNoTracking().Where(t => t.PropertyId == propertyId).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var items = await db.EquipmentItems.AsNoTracking().Where(e => e.PropertyId == propertyId).ToListAsync(ct);
        var open = await db.EquipmentAssignments.AsNoTracking()
            .Where(x => x.PropertyId == propertyId && AssignmentStatuses.Open.Contains(x.Status)).ToListAsync(ct);
        var order = types.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var typeDtos = types.Select(t =>
        {
            var mine = items.Where(e => e.EquipmentTypeId == t.Id && e.Status != EquipmentStatuses.Retired).ToList();
            return new EquipmentTypeDto(t.Id, t.Code, t.Name, t.SortOrder, t.IsActive, mine.Count, mine.Count(e => e.Status == EquipmentStatuses.Available),
                mine.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()), t.Version);
        }).ToList();
        var itemDtos = ItemDtos(items.OrderBy(e => order.GetValueOrDefault(e.EquipmentTypeId)).ThenBy(e => e.Tag, StringComparer.Ordinal).ToList(),
            types.ToDictionary(t => t.Id, t => t.Name), open);
        var outDtos = (await AssignmentDtosAsync(open, ct)).OrderBy(x => x.Overdue ? 0 : 1).ThenBy(x => x.DueBackAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.IssuedTo).ThenBy(x => x.Tag, StringComparer.Ordinal).ToList();
        return new EquipmentBoardDto(typeDtos, itemDtos, outDtos);
    }

    public async Task<EquipmentItemHistoryDto> ItemAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await activities.RequirePropertyAsync(propertyId, ct);
        var item = await db.EquipmentItems.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That item doesn't exist at this property. Reload the page.");
        var history = await db.EquipmentAssignments.AsNoTracking().Where(x => x.EquipmentId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        return new EquipmentItemHistoryDto(await ItemDtoAsync(item, ct), await AssignmentDtosAsync(history, ct));
    }

    /// <summary>What was reserved or issued for each booking (released ones left out).</summary>
    internal async Task<Dictionary<Guid, List<EquipmentAssignmentDto>>> ForBookingsAsync(IReadOnlyCollection<Guid> bookingIds,
        CancellationToken ct)
    {
        var list = await db.EquipmentAssignments.AsNoTracking()
            .Where(x => x.ActivityBookingId != null && bookingIds.Contains(x.ActivityBookingId.Value) && x.Status != AssignmentStatuses.Released)
            .ToListAsync(ct);
        if (list.Count == 0) return [];
        return (await AssignmentDtosAsync(list, ct)).OrderBy(x => x.TypeName).ThenBy(x => x.Tag, StringComparer.Ordinal)
            .GroupBy(x => x.ActivityBookingId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    // ------------------------------------------------------------------ kinds and items

    public async Task<EquipmentTypeDto> SaveTypeAsync(Guid propertyId, Guid? id, SaveEquipmentTypeRequest r, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        EquipmentType t;
        if (id is { } existing)
        {
            t = await db.EquipmentTypes.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That kind of equipment doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, t, v);
        }
        else
        {
            t = new EquipmentType { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.EquipmentTypes.Add(t);
        }
        var others = db.EquipmentTypes.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != t.Id);
        if (await others.Where(x => x.Code == code).Select(x => x.Name).FirstOrDefaultAsync(ct) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode} already has the code {code}. Choose another code.");
        if ((await others.Select(x => x.Name).ToListAsync(ct)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a kind of equipment called {name}.");
        t.Code = code;
        t.Name = name;
        t.SortOrder = r.SortOrder;
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await BoardAsync(propertyId, ct)).Types.Single(x => x.Id == t.Id);
    }

    /// <summary>Adds one item with its tag, or Count items numbered after the highest tag with Prefix (LJ-07, LJ-08 …).</summary>
    public async Task<IReadOnlyList<EquipmentItemDto>> AddItemsAsync(Guid propertyId, AddEquipmentRequest r, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var type = await db.EquipmentTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == r.EquipmentTypeId && t.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.EquipmentTypeId), "Choose a kind of equipment of this property.");
        var existing = (await db.EquipmentItems.AsNoTracking().Where(e => e.PropertyId == propertyId).Select(e => e.Tag).ToListAsync(ct)).ToHashSet();
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Tag))
        {
            var tag = r.Tag.Trim().ToUpperInvariant();
            if (existing.Contains(tag)) throw ReservationService.Field(nameof(r.Tag), $"{tag} is already used by another item. Tags are unique at a property.");
            tags.Add(tag);
        }
        else
        {
            var prefix = r.Prefix!.Trim().ToUpperInvariant();
            var pattern = new Regex($"^{Regex.Escape(prefix)}-([0-9]+)$");
            var last = existing.Select(x => pattern.Match(x)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0).Max();
            for (var i = 1; i <= r.Count; i++) tags.Add($"{prefix}-{last + i:D2}");
        }
        var added = tags.Select(tag => new EquipmentItem
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            EquipmentTypeId = type.Id,
            Tag = tag,
            Note = ReservationService.Clean(r.Note),
            StatusChangedAt = clock.GetUtcNow(),
        }).ToList();
        db.EquipmentItems.AddRange(added);
        audit.Reason = $"{type.Name}: {string.Join(", ", tags)} added";
        await db.SaveChangesAsync(ct);
        return ItemDtos(added, new Dictionary<Guid, string> { [type.Id] = type.Name }, []);
    }

    /// <summary>Sets an item's status by hand (damaged, to maintenance, back in use, lost, retired). Reserved and issued items
    /// change only by being released, issued or returned.</summary>
    public async Task<EquipmentItemDto> ChangeStatusAsync(Guid propertyId, Guid id, ChangeEquipmentStatusRequest r, CancellationToken ct)
    {
        await activities.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM equipment WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        var item = await db.EquipmentItems.SingleOrDefaultAsync(e => e.Id == id && e.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That item doesn't exist at this property. Reload the page.");
        if (r.Version is { } v && item.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"{item.Tag} changed a moment ago. Reload to see it, then try again.");
        var to = r.Status.Trim().ToUpperInvariant();
        if (to == item.Status) throw ReservationService.Field(nameof(r.Status), $"{item.Tag} is already {EquipmentStatuses.Words(to)}.");
        if (item.Status is EquipmentStatuses.Reserved or EquipmentStatuses.Issued)
        {
            var with = await db.EquipmentAssignments.AsNoTracking()
                .Where(x => x.EquipmentId == id && AssignmentStatuses.Open.Contains(x.Status)).Select(x => x.IssuedTo).FirstOrDefaultAsync(ct);
            throw new BusinessRuleAppException("EQUIPMENT_OUT", item.Status == EquipmentStatuses.Issued
                ? $"{item.Tag} is issued to {with}. Take it back first (Return, and say if it is damaged or lost)."
                : $"{item.Tag} is reserved for {with}. Release it first.");
        }
        if (!EquipmentStatuses.ByHand.Contains(to) || !EquipmentStatuses.CanMove(item.Status, to))
        {
            var can = EquipmentStatuses.Transitions[item.Status].Where(EquipmentStatuses.ByHand.Contains).Select(EquipmentStatuses.Words).ToList();
            throw ReservationService.Field(nameof(r.Status), can.Count == 0
                ? $"{item.Tag} is {EquipmentStatuses.Words(item.Status)}, so its status can't change any more."
                : $"{item.Tag} is {EquipmentStatuses.Words(item.Status)}; it can be set to {string.Join(", ", can)}.");
        }
        audit.Reason = $"{item.Tag}: {EquipmentStatuses.Words(item.Status)} → {EquipmentStatuses.Words(to)}" + (r.Note is { } n && n.Trim().Length > 0 ? $" ({n.Trim()})" : "");
        item.Status = to;
        item.StatusNote = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemDtoAsync(await db.EquipmentItems.AsNoTracking().SingleAsync(e => e.Id == id, ct), ct);
    }

    // ------------------------------------------------------------------ reserving, issuing, returning

    public async Task<IReadOnlyList<EquipmentAssignmentDto>> IssueAsync(Guid propertyId, IssueEquipmentRequest r, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // For whom.
        Guid? bookingId = null, stayId = null;
        string issuedTo;
        DateTimeOffset? due = r.DueBackAt;
        if (r.ActivityBookingId is { } bid)
        {
            var b = await db.ActivityBookings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bid && x.PropertyId == propertyId, ct)
                    ?? throw ReservationService.Field(nameof(r.ActivityBookingId), "That activity booking isn't at this property. Reload and choose again.");
            var s = await db.ActivitySchedules.AsNoTracking().SingleAsync(x => x.Id == b.ScheduleId, ct);
            if (b.Status != ActivityBookingStatuses.Booked)
                throw ReservationService.Field(nameof(r.ActivityBookingId), $"{b.Code} is cancelled, so nothing can be issued for it.");
            if (now >= s.EndsAt)
                throw ReservationService.Field(nameof(r.ActivityBookingId), $"The session of {b.Code} is over. Issue it to the guest by name instead.");
            bookingId = b.Id;
            stayId = b.ReservationId;
            issuedTo = b.GuestName;
            due ??= s.EndsAt;
        }
        else if (r.ReservationId is { } rid)
        {
            var stay = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rid && x.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.ReservationId), "That stay isn't at this property. Reload and choose again.");
            if (stay.Status is not (ReservationStatuses.CheckedIn or ReservationStatuses.Confirmed))
                throw ReservationService.Field(nameof(r.ReservationId),
                    $"{stay.Reference} is {ReservationStatuses.Words(stay.Status).ToLowerInvariant()}. Issue to a stay that is confirmed or checked in, or to a name.");
            stayId = stay.Id;
            issuedTo = ReservationService.Clean(r.IssuedTo)
                       ?? await db.Guests.AsNoTracking().Where(g => g.Id == stay.PrimaryGuestId).Select(g => g.FullName).SingleAsync(ct);
        }
        else issuedTo = ReservationService.Clean(r.IssuedTo) ?? throw ReservationService.Field(nameof(r.IssuedTo), "Say who it is for: a booking, a stay or a name.");
        if (due is { } d && d <= now) throw ReservationService.Field(nameof(r.DueBackAt), "The time it is due back has already passed.");

        // Which items, under their row locks (fixed order: no deadlocks).
        List<EquipmentItem> items;
        if (r.EquipmentIds is { Count: > 0 } ids)
        {
            var arr = ids.Distinct().Order().ToArray();
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM equipment WHERE id = ANY({arr}) AND property_id = {propertyId} ORDER BY id FOR UPDATE", ct);
            items = await db.EquipmentItems.Where(e => arr.Contains(e.Id) && e.PropertyId == propertyId).ToListAsync(ct);
            if (items.Count != arr.Length)
                throw ReservationService.Field(nameof(r.EquipmentIds), "One of the items isn't at this property any more. Reload and choose again.");
            var busy = items.Where(e => e.Status != EquipmentStatuses.Available).OrderBy(e => e.Tag, StringComparer.Ordinal).ToList();
            if (busy.Count > 0)
            {
                var withs = await WithAsync(busy.Select(e => e.Id).ToList(), ct);
                throw new ConflictAppException("EQUIPMENT_UNAVAILABLE",
                    string.Join(" ", busy.Select(e => withs.TryGetValue(e.Id, out var w)
                        ? $"{e.Tag} is {EquipmentStatuses.Words(e.Status)} ({w})."
                        : $"{e.Tag} is {EquipmentStatuses.Words(e.Status)}.")) + " Choose other items.");
            }
        }
        else
        {
            var type = await db.EquipmentTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == r.EquipmentTypeId && t.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.EquipmentTypeId), "Choose the kind of equipment.");
            var typeId = type.Id;
            // Locks every available item of the kind; PostgreSQL re-checks the status of a row it waited for.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM equipment WHERE equipment_type_id = {typeId} AND status = 'AVAILABLE' ORDER BY id FOR UPDATE", ct);
            var free = (await db.EquipmentItems.Where(e => e.EquipmentTypeId == typeId && e.Status == EquipmentStatuses.Available).ToListAsync(ct))
                .OrderBy(e => e.Tag, StringComparer.Ordinal).ToList();
            var qty = r.Quantity ?? 0;
            if (free.Count < qty)
                throw new ConflictAppException("EQUIPMENT_UNAVAILABLE", free.Count == 0
                    ? $"No {type.Name.ToLowerInvariant()} is available now. See the equipment page for who has them."
                    : $"Only {free.Count} of {type.Name.ToLowerInvariant()} {(free.Count == 1 ? "is" : "are")} available and {qty} {(qty == 1 ? "was" : "were")} asked for.",
                    new { available = free.Count });
            items = free.Take(qty).ToList();
        }

        var userId = caller.RequireUserId();
        var created = items.OrderBy(e => e.Tag, StringComparer.Ordinal).Select(e => new EquipmentAssignment
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            EquipmentId = e.Id,
            ActivityBookingId = bookingId,
            ReservationId = stayId,
            IssuedTo = issuedTo,
            Status = r.ReserveOnly ? AssignmentStatuses.Reserved : AssignmentStatuses.Issued,
            ReservedAt = r.ReserveOnly ? now : null,
            ReservedBy = r.ReserveOnly ? userId : null,
            IssuedAt = r.ReserveOnly ? null : now,
            IssuedBy = r.ReserveOnly ? null : userId,
            DueBackAt = due,
        }).ToList();
        foreach (var e in items) e.Status = r.ReserveOnly ? EquipmentStatuses.Reserved : EquipmentStatuses.Issued;
        db.EquipmentAssignments.AddRange(created);
        audit.Reason = $"{(r.ReserveOnly ? "Reserved" : "Issued")} {string.Join(", ", items.Select(e => e.Tag).Order(StringComparer.Ordinal))} for {issuedTo}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await AssignmentDtosAsync(created, ct);
    }

    /// <summary>Hands over a reserved item.</summary>
    public async Task<EquipmentAssignmentDto> HandOverAsync(Guid propertyId, Guid id, AssignmentStepRequest r, CancellationToken ct) =>
        await StepAsync(propertyId, id, r.Version, AssignmentStatuses.Reserved, (x, item) =>
        {
            x.Status = AssignmentStatuses.Issued;
            x.IssuedAt = clock.GetUtcNow();
            x.IssuedBy = caller.RequireUserId();
            item.Status = EquipmentStatuses.Issued;
            return $"{item.Tag} issued to {x.IssuedTo}";
        }, ct);

    /// <summary>Puts a reserved item back on the shelf.</summary>
    public async Task<EquipmentAssignmentDto> ReleaseAsync(Guid propertyId, Guid id, AssignmentStepRequest r, CancellationToken ct) =>
        await StepAsync(propertyId, id, r.Version, AssignmentStatuses.Reserved, (x, item) =>
        {
            Close(x, AssignmentStatuses.Released);
            item.Status = EquipmentStatuses.Available;
            return $"{item.Tag} released (was reserved for {x.IssuedTo})";
        }, ct);

    /// <summary>Takes an issued item back: good (available), damaged (out of use until repaired) or lost.</summary>
    public async Task<EquipmentAssignmentDto> ReturnAsync(Guid propertyId, Guid id, ReturnEquipmentRequest r, CancellationToken ct)
    {
        var condition = r.Condition.Trim().ToUpperInvariant();
        return await StepAsync(propertyId, id, r.Version, AssignmentStatuses.Issued, (x, item) =>
        {
            var note = ReservationService.Clean(r.Note);
            Close(x, condition == ReturnConditions.Lost ? AssignmentStatuses.Lost : AssignmentStatuses.Returned);
            x.ReturnCondition = condition;
            x.ReturnNote = note;
            item.Status = condition switch
            {
                ReturnConditions.Damaged => EquipmentStatuses.Damaged,
                ReturnConditions.Lost => EquipmentStatuses.Lost,
                _ => EquipmentStatuses.Available,
            };
            item.StatusNote = condition == ReturnConditions.Good ? null : note;
            return condition switch
            {
                ReturnConditions.Damaged => $"{item.Tag} returned damaged by {x.IssuedTo}: {note}",
                ReturnConditions.Lost => $"{item.Tag} lost by {x.IssuedTo}: {note}",
                _ => $"{item.Tag} returned by {x.IssuedTo}",
            };
        }, ct);
    }

    /// <summary>Releases what is reserved for these bookings (they were cancelled). Issued items stay out until returned.
    /// Runs inside the caller's transaction; not saved.</summary>
    internal async Task ReleaseForBookingsAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        var reserved = await db.EquipmentAssignments
            .Where(x => x.ActivityBookingId != null && bookingIds.Contains(x.ActivityBookingId.Value) && x.Status == AssignmentStatuses.Reserved).ToListAsync(ct);
        if (reserved.Count == 0) return;
        var itemIds = reserved.Select(x => x.EquipmentId).Distinct().Order().ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM equipment WHERE id = ANY({itemIds}) ORDER BY id FOR UPDATE", ct);
        var items = await db.EquipmentItems.Where(e => itemIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var x in reserved)
        {
            Close(x, AssignmentStatuses.Released);
            items[x.EquipmentId].Status = EquipmentStatuses.Available;
        }
    }

    private async Task<EquipmentAssignmentDto> StepAsync(Guid propertyId, Guid id, uint? version, string from,
        Func<EquipmentAssignment, EquipmentItem, string> step, CancellationToken ct)
    {
        await activities.RequirePropertyAsync(propertyId, ct);
        var itemId = await db.EquipmentAssignments.AsNoTracking().Where(x => x.Id == id && x.PropertyId == propertyId).Select(x => (Guid?)x.EquipmentId)
                         .SingleOrDefaultAsync(ct)
                     ?? throw new NotFoundAppException("That equipment record doesn't exist at this property. Reload the page.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM equipment WHERE id = {itemId} FOR UPDATE", ct);
        var x = await db.EquipmentAssignments.SingleAsync(a => a.Id == id, ct);
        var item = await db.EquipmentItems.SingleAsync(e => e.Id == itemId, ct);
        if (version is { } v && x.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"{item.Tag} ({x.IssuedTo}) changed a moment ago. Reload to see it, then try again.");
        if (x.Status != from)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", x.Status switch
            {
                AssignmentStatuses.Reserved => $"{item.Tag} is only reserved for {x.IssuedTo}; hand it over first.",
                AssignmentStatuses.Issued => $"{item.Tag} is already issued to {x.IssuedTo}.",
                AssignmentStatuses.Returned => $"{item.Tag} was already returned by {x.IssuedTo}.",
                AssignmentStatuses.Lost => $"{item.Tag} was already recorded as lost by {x.IssuedTo}.",
                _ => $"{item.Tag} was released; it isn't reserved for {x.IssuedTo} any more.",
            });
        audit.Reason = step(x, item);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await AssignmentDtosAsync([await db.EquipmentAssignments.AsNoTracking().SingleAsync(a => a.Id == id, ct)], ct))[0];
    }

    private void Close(EquipmentAssignment x, string status)
    {
        x.Status = status;
        x.ClosedAt = clock.GetUtcNow();
        x.ClosedBy = caller.RequireUserId();
    }

    // ------------------------------------------------------------------ DTOs

    /// <summary>"Asha Rao, AB00004" per item that is reserved or issued now.</summary>
    private async Task<Dictionary<Guid, string>> WithAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var open = await db.EquipmentAssignments.AsNoTracking()
            .Where(x => itemIds.Contains(x.EquipmentId) && AssignmentStatuses.Open.Contains(x.Status)).ToListAsync(ct);
        return open.ToDictionary(x => x.EquipmentId, x => x.IssuedTo);
    }

    private async Task<EquipmentItemDto> ItemDtoAsync(EquipmentItem item, CancellationToken ct)
    {
        var type = await db.EquipmentTypes.AsNoTracking().Where(t => t.Id == item.EquipmentTypeId).Select(t => t.Name).SingleAsync(ct);
        var open = await db.EquipmentAssignments.AsNoTracking().Where(x => x.EquipmentId == item.Id && AssignmentStatuses.Open.Contains(x.Status)).ToListAsync(ct);
        return ItemDtos([item], new Dictionary<Guid, string> { [item.EquipmentTypeId] = type }, open)[0];
    }

    private static List<EquipmentItemDto> ItemDtos(IReadOnlyList<EquipmentItem> items, IReadOnlyDictionary<Guid, string> types,
        IReadOnlyCollection<EquipmentAssignment> open)
    {
        var with = open.ToDictionary(x => x.EquipmentId, x => x.IssuedTo);
        return items.Select(e => new EquipmentItemDto(e.Id, e.EquipmentTypeId, types.GetValueOrDefault(e.EquipmentTypeId, ""), e.Tag, e.Note, e.Status, e.StatusNote,
            e.StatusChangedAt, with.GetValueOrDefault(e.Id),
            EquipmentStatuses.Transitions[e.Status].Where(EquipmentStatuses.ByHand.Contains).ToList(), e.Version)).ToList();
    }

    private async Task<List<EquipmentAssignmentDto>> AssignmentDtosAsync(List<EquipmentAssignment> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var itemIds = list.Select(x => x.EquipmentId).Distinct().ToList();
        var items = await db.EquipmentItems.AsNoTracking().Where(e => itemIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var typeIds = items.Values.Select(e => e.EquipmentTypeId).Distinct().ToList();
        var types = await db.EquipmentTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var bookingIds = list.Select(x => x.ActivityBookingId).OfType<Guid>().Distinct().ToList();
        var codes = await db.ActivityBookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var stayIds = list.Select(x => x.ReservationId).OfType<Guid>().Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => stayIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var users = await activities.UserNamesAsync(list.SelectMany(x => new[] { x.IssuedBy, x.ClosedBy }), ct);
        var now = clock.GetUtcNow();
        return list.Select(x =>
        {
            var e = items[x.EquipmentId];
            return new EquipmentAssignmentDto(x.Id, e.Id, e.Tag, types.GetValueOrDefault(e.EquipmentTypeId, ""), x.IssuedTo, x.ActivityBookingId,
                x.ActivityBookingId is { } b ? codes.GetValueOrDefault(b) : null, x.ReservationId, x.ReservationId is { } r ? refs.GetValueOrDefault(r) : null,
                x.Status, x.ReservedAt, x.IssuedAt, x.IssuedBy is { } ib ? users.GetValueOrDefault(ib) : null, x.DueBackAt,
                x.Status == AssignmentStatuses.Issued && x.DueBackAt is { } due && now > due, x.ClosedAt,
                x.ClosedBy is { } cb ? users.GetValueOrDefault(cb) : null, x.ReturnCondition, x.ReturnNote, x.Version);
        }).ToList();
    }
}
