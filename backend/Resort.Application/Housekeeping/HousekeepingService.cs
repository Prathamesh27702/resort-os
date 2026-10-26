using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.FrontDesk;
using Resort.Domain.Housekeeping;
using Resort.Domain.Organizations;
using Resort.Domain.Reservations;

namespace Resort.Application.Housekeeping;

/// <summary>
/// Housekeeping (Sprint 13, brief §33 and §53): the board of every room and dorm bed, and the cleaning tasks on it.
/// A housekeeper finishes a room in two taps (Start, Done); a supervisor inspects it and marks it READY, or sends it back
/// with a note. Tasks are made by check-outs, room moves and repairs (<see cref="HousekeepingState"/>) or by hand.
///
/// Every step locks the room's row first, then the task, so two people tapping the same room run one after the other:
/// the second sees what the first did ("Room 101 is already being cleaned by Asha …").
/// </summary>
public sealed class HousekeepingService(
    IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock, HousekeepingState state)
{
    public const string InspectionRequired = "housekeeping.inspection_required";

    private static readonly string[] BoardOrder =
        [HousekeepingStatuses.Dirty, HousekeepingStatuses.Cleaning, HousekeepingStatuses.Clean, HousekeepingStatuses.Inspection,
         HousekeepingStatuses.OutOfOrder, HousekeepingStatuses.Ready];

    // ------------------------------------------------------------------ board

    public async Task<HousekeepingBoardDto> BoardAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var me = caller.UserId;
        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
            .Where(u => u.PropertyId == propertyId && u.IsActive).OrderBy(u => u.SortOrder).ThenBy(u => u.Code).ToListAsync(ct);
        var statuses = (await db.UnitHousekeepingStatus.AsNoTracking().Where(h => h.PropertyId == propertyId).ToListAsync(ct))
            .ToDictionary(h => (h.UnitId, h.BedId), h => h.Status);
        var tasks = (await db.HousekeepingTasks.AsNoTracking()
                .Where(t => t.PropertyId == propertyId && HousekeepingTaskStatuses.Active.Contains(t.Status)).ToListAsync(ct))
            .ToDictionary(t => (t.UnitId, t.BedId));
        var repairs = await db.MaintenanceTickets.AsNoTracking()
            .Where(t => t.PropertyId == propertyId && t.UnitId != null && MaintenanceStatuses.Unresolved.Contains(t.Status))
            .OrderByDescending(t => t.OutOfOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var stays = await StaysTodayAsync(property, today, ct);
        var ctx = await DtoContextAsync(propertyId, tasks.Values, ct);

        var rows = new List<HousekeepingRoomDto>();
        foreach (var u in units)
        {
            var places = u.Type.InventoryMode == InventoryModes.Bed
                ? u.Beds.Where(b => b.IsActive).OrderBy(b => b.SortOrder).ThenBy(b => b.Code).Select(b => ((Guid?)b.Id, (string?)b.Code)).ToList()
                : [(null, null)];
            foreach (var (bedId, bedCode) in places)
            {
                var status = statuses.GetValueOrDefault((u.Id, bedId), HousekeepingStatuses.Ready);
                var task = tasks.GetValueOrDefault((u.Id, bedId));
                var stay = stays.Where(s => s.UnitId == u.Id && (s.BedId == bedId || s.BedId == null)).ToList();
                var occupied = stay.Any(s => s.InHouse);
                var departure = stay.Any(s => s.InHouse && s.CheckOut <= today) || stay.Any(s => s.LeftToday);
                var arrival = stay.Any(s => s.Arriving);
                var repair = repairs.FirstOrDefault(t => t.UnitId == u.Id && (t.BedId == null || t.BedId == bedId));
                var place = HousekeepingFlow.Place(u.Name, bedCode);
                rows.Add(new HousekeepingRoomDto(u.Id, u.Code, u.Name, bedId, bedCode, u.Type.Name, place, status, occupied, departure, arrival,
                    arrival && status != HousekeepingStatuses.Ready && status != HousekeepingStatuses.OutOfOrder,
                    task is null ? null : ToDto(task, place, status, ctx),
                    repair is null ? null : new RepairBriefDto(repair.Id, repair.Number, repair.Title, repair.OutOfOrder, repair.ExpectedReadyOn)));
            }
        }

        rows = rows.OrderByDescending(r => r.Rush).ThenBy(r => Array.IndexOf(BoardOrder, r.Status)).ToList();
        var summary = new HousekeepingSummaryDto(
            rows.Count(r => r.Status == HousekeepingStatuses.Dirty),
            rows.Count(r => r.Status == HousekeepingStatuses.Cleaning),
            rows.Count(r => r.Status is HousekeepingStatuses.Clean or HousekeepingStatuses.Inspection),
            rows.Count(r => r.Status == HousekeepingStatuses.Ready),
            rows.Count(r => r.Status == HousekeepingStatuses.OutOfOrder),
            rows.Count(r => r.Task?.AssignedTo is { } a && a == me),
            rows.Count(r => r.Rush));
        return new HousekeepingBoardDto(today, await InspectionRequiredAsync(propertyId, ct), summary, rows);
    }

    public async Task<HousekeepingTaskDto> GetTaskAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var task = await db.HousekeepingTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That housekeeping task doesn't exist at this property. Reload the board.");
        return await ToDtoAsync(task, ct);
    }

    /// <summary>People who can be given cleaning work here (housekeeping.update at this property), with their open tasks.</summary>
    public async Task<IReadOnlyList<StaffOptionDto>> StaffAsync(Guid propertyId, CancellationToken ct)
    {
        var users = await StaffQuery.WithPermissionAsync(db, propertyId, Perm.HousekeepingUpdate, ct);
        var open = await db.HousekeepingTasks.AsNoTracking()
            .Where(t => t.PropertyId == propertyId && t.AssignedTo != null && HousekeepingTaskStatuses.Active.Contains(t.Status))
            .GroupBy(t => t.AssignedTo!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.Select(u => new StaffOptionDto(u.Id, u.Name, open.GetValueOrDefault(u.Id))).ToList();
    }

    // ------------------------------------------------------------------ tasks

    /// <summary>A task made by hand (a stay-over or deep clean). The room becomes DIRTY.</summary>
    public async Task<HousekeepingTaskDto> CreateTaskAsync(Guid propertyId, CreateTaskRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await state.LockRoomAsync(propertyId, r.UnitId, ct);
        var unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
                       .SingleOrDefaultAsync(u => u.Id == r.UnitId && u.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.UnitId), "That room doesn't exist at this property. Reload and choose again.");
        var bed = RequirePlace(unit, r.BedId, nameof(r.BedId));
        var place = HousekeepingFlow.Place(unit.Name, bed?.Code);
        if (!unit.IsActive) throw ReservationService.Field(nameof(r.UnitId), $"{unit.Name} is not in use; switch it back on in Rooms first.");

        var status = await state.StatusOfAsync(unit.Id, bed?.Id, ct);
        if (status == HousekeepingStatuses.OutOfOrder)
            throw new BusinessRuleAppException("ROOM_OUT_OF_ORDER", $"{place} is out of order. It gets a cleaning task by itself when its repair is resolved.");
        var bedId = bed?.Id;
        var existing = await db.HousekeepingTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.UnitId == unit.Id && t.BedId == bedId && HousekeepingTaskStatuses.Active.Contains(t.Status), ct);
        if (existing is not null)
            throw new ConflictAppException("TASK_EXISTS",
                $"{place} already has an open task ({HousekeepingTaskTypes.Words(existing.TaskType).ToLowerInvariant()}, " +
                $"room {HousekeepingStatuses.Words(status)}). Finish or inspect that one instead.", new { taskId = existing.Id });

        (Guid Id, string Name)? assignee = r.AssignTo is { } uid ? await RequireCleanerAsync(propertyId, uid, nameof(r.AssignTo), ct) : null;
        var task = await state.NewTaskAsync(property, unit.Id, bed?.Id, r.TaskType, HousekeepingTaskSources.Manual, ct);
        task.Note = ReservationService.Clean(r.Note);
        if (assignee is not null) { task.AssignedTo = assignee.Value.Id; task.AssignedAt = clock.GetUtcNow(); }
        var change = await state.SetAsync(property.OrganizationId, propertyId, unit.Id, bed?.Id, HousekeepingStatuses.Dirty,
            HousekeepingSources.Manual, null, $"{HousekeepingTaskTypes.Words(r.TaskType)} requested", ct);
        if (change is not null) { task.SourceChangeId = change.Id; change.ConsumedAt = clock.GetUtcNow(); }
        db.HousekeepingTasks.Add(task);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(task, ct);
    }

    public async Task<HousekeepingTaskDto> AssignAsync(Guid propertyId, Guid id, AssignTaskRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (task, place, roomStatus) = await LockTaskAsync(propertyId, id, r.Version, ct);
        if (!task.IsActive) throw new BusinessRuleAppException("TASK_CLOSED", HousekeepingFlow.Problem("assign", place, task.Status, roomStatus)!);
        if (r.UserId is { } uid)
        {
            var who = await RequireCleanerAsync(propertyId, uid, nameof(r.UserId), ct);
            task.AssignedTo = who.Id;
            task.AssignedAt = clock.GetUtcNow();
            audit.Reason = $"Assigned to {who.Name}";
        }
        else
        {
            task.AssignedTo = null;
            task.AssignedAt = null;
            audit.Reason = "Unassigned";
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(task, ct);
    }

    /// <summary>Tap 1: the room is being cleaned. Whoever starts an unassigned task takes it.</summary>
    public async Task<HousekeepingTaskDto> StartAsync(Guid propertyId, Guid id, StartTaskRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (task, place, roomStatus) = await LockTaskAsync(propertyId, id, r.Version, ct);
        if (HousekeepingFlow.Problem(HousekeepingFlow.Start, place, task.Status, roomStatus) is { } why)
            throw new ConflictAppException(task.Status == HousekeepingTaskStatuses.InProgress ? "TASK_ALREADY_STARTED" : "TASK_NOT_STARTABLE",
                task.Status == HousekeepingTaskStatuses.InProgress ? $"{place} is already being cleaned{await ByAsync(task.StartedBy, task.StartedAt, propertyId, ct)}." : why);
        var me = caller.RequireUserId();
        if (task.AssignedTo is { } a && a != me && !await CanAsync(Perm.HousekeepingAssign, propertyId, ct))
            throw new ConflictAppException("TASK_ASSIGNED_TO_SOMEONE_ELSE",
                $"{place} is assigned to {await NameAsync(a, ct)}. Ask your supervisor to give it to you.");

        var now = clock.GetUtcNow();
        if (task.AssignedTo is null) { task.AssignedTo = me; task.AssignedAt = now; }
        task.Status = HousekeepingTaskStatuses.InProgress;
        task.StartedAt = now;
        task.StartedBy = me;
        await state.SetAsync(task.OrganizationId, propertyId, task.UnitId, task.BedId, HousekeepingStatuses.Cleaning,
            HousekeepingSources.Housekeeping, task.ReservationId, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(task, ct);
    }

    /// <summary>Tap 2: cleaning done. The room waits for inspection (CLEAN), or is READY when the property doesn't inspect.</summary>
    public async Task<HousekeepingTaskDto> FinishAsync(Guid propertyId, Guid id, FinishTaskRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (task, place, roomStatus) = await LockTaskAsync(propertyId, id, r.Version, ct);
        if (HousekeepingFlow.Problem(HousekeepingFlow.Finish, place, task.Status, roomStatus) is { } why)
            throw new ConflictAppException(task.Status == HousekeepingTaskStatuses.Cleaned ? "TASK_ALREADY_FINISHED" : "TASK_NOT_STARTED", why);
        var me = caller.RequireUserId();
        if (task.StartedBy != me && task.AssignedTo != me && !await CanAsync(Perm.HousekeepingAssign, propertyId, ct))
            throw new ConflictAppException("TASK_ASSIGNED_TO_SOMEONE_ELSE",
                $"{await NameAsync(task.StartedBy ?? task.AssignedTo, ct)} is cleaning {place}; only they or a supervisor can finish it.");

        var (lines, missing) = HousekeepingFlow.Complete(HousekeepingState.ReadChecklist(task.Checklist), r.Checked);
        if (r.Checked is not null && r.Checked.Except(lines.Select(l => l.Id)).Any())
            throw ReservationService.Field(nameof(r.Checked), "Some ticked lines aren't on this room's checklist. Reload and tick again.");
        if (missing.Count > 0)
            throw ReservationService.Field(nameof(r.Checked), $"Tick every required line before finishing: {string.Join(", ", missing)}.");

        var now = clock.GetUtcNow();
        task.Checklist = HousekeepingState.WriteChecklist(lines);
        task.FinishedAt = now;
        task.FinishedBy = me;
        if (ReservationService.Clean(r.Note) is { } note) task.Note = note;
        var inspect = await InspectionRequiredAsync(propertyId, ct);
        if (inspect) task.Status = HousekeepingTaskStatuses.Cleaned;
        else { task.Status = HousekeepingTaskStatuses.Done; task.ClosedAt = now; }
        await state.SetAsync(task.OrganizationId, propertyId, task.UnitId, task.BedId,
            inspect ? HousekeepingStatuses.Clean : HousekeepingStatuses.Ready, HousekeepingSources.Housekeeping, task.ReservationId, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(task, ct);
    }

    /// <summary>A supervisor's check: PASS makes the room READY; FAIL sends it back (DIRTY) with what to redo; START shows the
    /// room as being inspected.</summary>
    public async Task<HousekeepingTaskDto> InspectAsync(Guid propertyId, Guid id, InspectTaskRequest r, CancellationToken ct)
    {
        var result = r.Result.Trim().ToUpperInvariant();
        var note = ReservationService.Clean(r.Note);
        if (result == "FAIL" && note is not { Length: >= 3 })
            throw ReservationService.Field(nameof(r.Note), "Say what needs redoing (at least 3 characters), e.g. Bathroom mirror smeared.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (task, place, roomStatus) = await LockTaskAsync(propertyId, id, r.Version, ct);
        if (HousekeepingFlow.Problem(HousekeepingFlow.Inspect, place, task.Status, roomStatus) is { } why)
            throw new ConflictAppException(task.Status == HousekeepingTaskStatuses.Done ? "ALREADY_READY" : "TASK_NOT_FINISHED", why);

        var now = clock.GetUtcNow();
        var me = caller.RequireUserId();
        switch (result)
        {
            case "START":
                await state.SetAsync(task.OrganizationId, propertyId, task.UnitId, task.BedId, HousekeepingStatuses.Inspection,
                    HousekeepingSources.Housekeeping, task.ReservationId, null, ct);
                // Touch the task so its version moves and others reload it.
                task.InspectedBy = me;
                break;
            case "PASS":
                task.Status = HousekeepingTaskStatuses.Done;
                task.InspectedAt = now;
                task.InspectedBy = me;
                task.InspectionNote = note;
                task.ClosedAt = now;
                audit.Reason = note;
                await state.SetAsync(task.OrganizationId, propertyId, task.UnitId, task.BedId, HousekeepingStatuses.Ready,
                    HousekeepingSources.Housekeeping, task.ReservationId, note, ct);
                break;
            default: // FAIL
                task.Status = HousekeepingTaskStatuses.Open;
                task.InspectedAt = now;
                task.InspectedBy = me;
                task.InspectionNote = note;
                task.FailedInspections++;
                task.StartedAt = task.FinishedAt = null;
                task.StartedBy = task.FinishedBy = null;
                task.Checklist = HousekeepingState.WriteChecklist(HousekeepingState.ReadChecklist(task.Checklist).Select(l => l with { Done = false }));
                audit.Reason = note;
                await state.SetAsync(task.OrganizationId, propertyId, task.UnitId, task.BedId, HousekeepingStatuses.Dirty,
                    HousekeepingSources.Housekeeping, task.ReservationId, $"Inspection failed: {note}", ct);
                break;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(task, ct);
    }

    // ------------------------------------------------------------------ checklists

    public async Task<IReadOnlyList<ChecklistTemplateDto>> TemplatesAsync(Guid propertyId, CancellationToken ct)
    {
        var list = await db.HousekeepingChecklistTemplates.AsNoTracking().Include(t => t.Items).Where(t => t.PropertyId == propertyId)
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.TaskType).ThenBy(t => t.Name).ToListAsync(ct);
        var typeIds = list.Where(t => t.AccommodationTypeId != null).Select(t => t.AccommodationTypeId!.Value).Distinct().ToList();
        var types = await db.AccommodationTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return list.Select(t => ToDto(t, types)).ToList();
    }

    public async Task<ChecklistTemplateDto> SaveTemplateAsync(Guid propertyId, Guid? id, SaveChecklistTemplateRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        string? typeName = null;
        if (r.AccommodationTypeId is { } typeId)
            typeName = await db.AccommodationTypes.AsNoTracking().Where(t => t.Id == typeId && t.PropertyId == propertyId).Select(t => t.Name)
                           .SingleOrDefaultAsync(ct)
                       ?? throw ReservationService.Field(nameof(r.AccommodationTypeId), "That room type doesn't exist at this property. Reload and choose again.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        HousekeepingChecklistTemplate template;
        if (id is { } tid)
        {
            template = await db.HousekeepingChecklistTemplates.Include(t => t.Items).SingleOrDefaultAsync(t => t.Id == tid && t.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That checklist doesn't exist at this property. Reload the page.");
            db.HousekeepingChecklistItems.RemoveRange(template.Items);
            template.Items.Clear();
        }
        else
        {
            template = new HousekeepingChecklistTemplate { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.HousekeepingChecklistTemplates.Add(template);
        }
        if (r.IsActive)
        {
            var clash = await db.HousekeepingChecklistTemplates.AsNoTracking()
                .Where(t => t.PropertyId == propertyId && t.IsActive && t.Id != template.Id && t.TaskType == r.TaskType
                            && t.AccommodationTypeId == r.AccommodationTypeId).Select(t => t.Name).FirstOrDefaultAsync(ct);
            if (clash is not null)
                throw ReservationService.Field(nameof(r.TaskType),
                    $"\"{clash}\" is already the {HousekeepingTaskTypes.Words(r.TaskType).ToLowerInvariant()} checklist for " +
                    $"{typeName ?? "every room type"}. Switch that one off first, or edit it instead.");
        }
        template.Name = r.Name.Trim();
        template.TaskType = r.TaskType;
        template.AccommodationTypeId = r.AccommodationTypeId;
        template.IsActive = r.IsActive;
        var now = clock.GetUtcNow();
        for (var i = 0; i < r.Items.Count; i++)
        {
            var item = new HousekeepingChecklistItem
            {
                OrganizationId = property.OrganizationId,
                TemplateId = template.Id,
                Label = r.Items[i].Label.Trim(),
                IsRequired = r.Items[i].IsRequired,
                SortOrder = i + 1,
                CreatedAt = now,
            };
            template.Items.Add(item);
            db.HousekeepingChecklistItems.Add(item);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(template, typeName is null ? [] : new Dictionary<Guid, string> { [r.AccommodationTypeId!.Value] = typeName });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Locks the task's room, then loads the task for update. Returns the task, "Room 101" and the room's state.</summary>
    private async Task<(HousekeepingTask Task, string Place, string RoomStatus)> LockTaskAsync(Guid propertyId, Guid id, uint? version, CancellationToken ct)
    {
        var unitId = await db.HousekeepingTasks.AsNoTracking().Where(t => t.Id == id && t.PropertyId == propertyId).Select(t => (Guid?)t.UnitId)
                         .SingleOrDefaultAsync(ct)
                     ?? throw new NotFoundAppException("That housekeeping task doesn't exist at this property. Reload the board.");
        await state.LockRoomAsync(propertyId, unitId, ct);
        var task = await db.HousekeepingTasks.SingleAsync(t => t.Id == id, ct);
        if (version is { } v) ConcurrencyGuard.ExpectVersion(db, task, v);
        var unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).SingleAsync(u => u.Id == unitId, ct);
        var place = HousekeepingFlow.Place(unit.Name, task.BedId is { } b ? unit.Beds.FirstOrDefault(x => x.Id == b)?.Code : null);
        return (task, place, await state.StatusOfAsync(task.UnitId, task.BedId, ct));
    }

    private static Bed? RequirePlace(AccommodationUnit unit, Guid? bedId, string field)
    {
        if (unit.Type.InventoryMode == InventoryModes.Bed)
        {
            if (bedId is null) throw ReservationService.Field(field, $"{unit.Name} is a dormitory; choose the bed.");
            return unit.Beds.FirstOrDefault(b => b.Id == bedId)
                   ?? throw ReservationService.Field(field, $"That bed isn't in {unit.Name}. Reload and choose again.");
        }
        if (bedId is not null) throw ReservationService.Field(field, $"{unit.Name} is a private room; it has no beds to choose.");
        return null;
    }

    private async Task<(Guid Id, string Name)> RequireCleanerAsync(Guid propertyId, Guid userId, string field, CancellationToken ct)
    {
        var staff = await StaffQuery.WithPermissionAsync(db, propertyId, Perm.HousekeepingUpdate, ct);
        var who = staff.FirstOrDefault(s => s.Id == userId);
        if (who.Id == Guid.Empty)
        {
            var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).SingleOrDefaultAsync(ct);
            throw ReservationService.Field(field, name is null
                ? "That person isn't on the staff list. Reload and choose again."
                : $"{name} can't clean rooms at this property (no housekeeping role here). Choose someone from housekeeping.");
        }
        return (who.Id, who.Name);
    }

    private async Task<bool> CanAsync(string permission, Guid propertyId, CancellationToken ct) =>
        (await access.GetAsync(ct)).Has(permission, propertyId);

    private Task<bool> InspectionRequiredAsync(Guid propertyId, CancellationToken ct) =>
        PropertySettingsReader.BoolAsync(db, propertyId, InspectionRequired, true, ct);

    private async Task<string> NameAsync(Guid? userId, CancellationToken ct) =>
        userId is null ? "someone else"
            : await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).SingleOrDefaultAsync(ct) ?? "someone else";

    /// <summary>" by Asha Rao (started 10:05)" in property time.</summary>
    private async Task<string> ByAsync(Guid? userId, DateTimeOffset? at, Guid propertyId, CancellationToken ct)
    {
        if (userId is null) return "";
        var property = await LoadPropertyAsync(propertyId, ct);
        var time = at is { } t ? $" (started {TimeZoneInfo.ConvertTime(t, TimeZoneInfo.FindSystemTimeZoneById(property.Timezone)):HH:mm})" : "";
        return $" by {await NameAsync(userId, ct)}{time}";
    }

    /// <summary>Who is in, leaving or arriving today, per room (and bed). No guest names.</summary>
    private sealed record StayToday(Guid UnitId, Guid? BedId, bool InHouse, bool Arriving, bool LeftToday, DateOnly CheckOut);

    private async Task<List<StayToday>> StaysTodayAsync(Property property, DateOnly today, CancellationToken ct)
    {
        var rows = await db.ReservationUnits.AsNoTracking()
            .Where(u => u.SupersededAt == null && u.StartDate <= today && today <= u.EndDate)
            .Join(db.Reservations.Where(r => r.PropertyId == property.Id), u => u.ReservationId, r => r.Id,
                (u, r) => new { u.UnitId, u.BedId, u.StartDate, u.EndDate, r.Status, r.CheckIn, r.CheckOut, r.CheckedOutAt })
            .ToListAsync(ct);
        return rows.Select(x => new StayToday(x.UnitId, x.BedId,
                InHouse: x.Status == ReservationStatuses.CheckedIn && x.EndDate == x.CheckOut,
                Arriving: x.Status is ReservationStatuses.Pending or ReservationStatuses.Confirmed && x.StartDate <= today && today < x.EndDate,
                LeftToday: x.Status == ReservationStatuses.CheckedOut && x.CheckedOutAt is { } at && property.LocalDate(at) == today,
                x.CheckOut))
            .Where(s => s.InHouse || s.Arriving || s.LeftToday).ToList();
    }

    private sealed record DtoContext(
        Dictionary<Guid, string> Users, Dictionary<Guid, string> References, bool CanUpdate, bool CanAssign, bool CanInspect, Guid? Me);

    private async Task<DtoContext> DtoContextAsync(Guid propertyId, IEnumerable<HousekeepingTask> tasks, CancellationToken ct)
    {
        var list = tasks.ToList();
        var userIds = list.SelectMany(t => new[] { t.AssignedTo, t.StartedBy, t.FinishedBy, t.InspectedBy }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var resIds = list.Where(t => t.ReservationId != null).Select(t => t.ReservationId!.Value).Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var a = await access.GetAsync(ct);
        return new DtoContext(users, refs, a.Has(Perm.HousekeepingUpdate, propertyId), a.Has(Perm.HousekeepingAssign, propertyId),
            a.Has(Perm.HousekeepingInspect, propertyId), caller.UserId);
    }

    private async Task<HousekeepingTaskDto> ToDtoAsync(HousekeepingTask task, CancellationToken ct)
    {
        var unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).SingleAsync(u => u.Id == task.UnitId, ct);
        var place = HousekeepingFlow.Place(unit.Name, task.BedId is { } b ? unit.Beds.FirstOrDefault(x => x.Id == b)?.Code : null);
        var status = await state.StatusOfAsync(task.UnitId, task.BedId, ct);
        return ToDto(task, place, status, await DtoContextAsync(task.PropertyId, [task], ct));
    }

    private static HousekeepingTaskDto ToDto(HousekeepingTask t, string place, string roomStatus, DtoContext c)
    {
        string? Name(Guid? id) => id is { } x ? c.Users.GetValueOrDefault(x) : null;
        var actions = new List<string>();
        var ooo = roomStatus == HousekeepingStatuses.OutOfOrder;
        if (!ooo && c.CanUpdate && t.Status == HousekeepingTaskStatuses.Open && (t.AssignedTo is null || t.AssignedTo == c.Me || c.CanAssign))
            actions.Add(HousekeepingFlow.Start);
        if (!ooo && c.CanUpdate && t.Status == HousekeepingTaskStatuses.InProgress && (t.StartedBy == c.Me || t.AssignedTo == c.Me || c.CanAssign))
            actions.Add(HousekeepingFlow.Finish);
        if (!ooo && c.CanInspect && t.Status == HousekeepingTaskStatuses.Cleaned) actions.Add(HousekeepingFlow.Inspect);
        if (c.CanAssign && t.IsActive) actions.Add("assign");
        return new HousekeepingTaskDto(t.Id, t.UnitId, t.BedId, place, t.TaskType, t.Status, t.Source,
            t.ReservationId is { } r ? c.References.GetValueOrDefault(r) : null,
            t.AssignedTo, Name(t.AssignedTo), t.StartedAt, Name(t.StartedBy), t.FinishedAt, Name(t.FinishedBy), t.InspectedAt,
            t.InspectedAt is null ? null : Name(t.InspectedBy), t.InspectionNote, t.FailedInspections, t.Note,
            HousekeepingState.ReadChecklist(t.Checklist).Select(l => new ChecklistLineDto(l.Id, l.Label, l.Required, l.Done)).ToList(),
            t.BusinessDate, t.CreatedAt, actions, t.Version);
    }

    private static ChecklistTemplateDto ToDto(HousekeepingChecklistTemplate t, IReadOnlyDictionary<Guid, string> types) => new(
        t.Id, t.Name, t.TaskType, t.AccommodationTypeId, t.AccommodationTypeId is { } id ? types.GetValueOrDefault(id) : null, t.IsActive,
        t.Items.OrderBy(i => i.SortOrder).Select(i => new ChecklistItemDto(i.Id, i.Label, i.IsRequired, i.SortOrder)).ToList());

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
}

/// <summary>Active users who hold a permission at a property (org-wide or for that property).</summary>
internal static class StaffQuery
{
    public static async Task<List<(Guid Id, string Name)>> WithPermissionAsync(IAppDbContext db, Guid propertyId, string permission, CancellationToken ct)
    {
        var roleIds = db.RolePermissions.Where(rp => rp.PermissionCode == permission).Select(rp => rp.RoleId);
        var userIds = db.UserRoles.Where(ur => roleIds.Contains(ur.RoleId) && (ur.PropertyId == null || ur.PropertyId == propertyId)).Select(ur => ur.UserId);
        return (await db.Users.AsNoTracking().Where(u => u.IsActive && userIds.Contains(u.Id)).OrderBy(u => u.FullName)
                .Select(u => new { u.Id, u.FullName }).ToListAsync(ct))
            .Select(u => (u.Id, u.FullName)).ToList();
    }
}
