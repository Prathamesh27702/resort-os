using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.FrontDesk;
using Resort.Domain.Housekeeping;
using Resort.Domain.Organizations;

namespace Resort.Application.Housekeeping;

/// <summary>
/// The one place a room's (or dorm bed's) cleaning state changes, and the rule that keeps its housekeeping task in step.
///
/// Every change is appended to unit_housekeeping_history. When a change comes from outside the board — a check-out, a
/// room move, the front desk by hand, a repair ticket — <see cref="ChangeAsync"/> also makes, moves or closes the room's
/// task: a DIRTY change becomes a task in the same transaction and is marked consumed, and the task keeps the change's id
/// (UNIQUE), so a retried check-out can never make a second task. A room has at most one open task (partial unique index).
///
/// Callers hold a transaction. The room's row is locked first (SELECT … FOR UPDATE on accommodation_units), so a check-out
/// and a housekeeper's tap on the same room run one after the other.
/// </summary>
public sealed class HousekeepingState(IAppDbContext db, ICurrentUser caller, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Locks a room's row for the rest of the transaction.</summary>
    public async Task LockRoomAsync(Guid propertyId, Guid unitId, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accommodation_units WHERE id = {unitId} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);

    /// <summary>The cleaning state now (no row = READY).</summary>
    public async Task<string> StatusOfAsync(Guid unitId, Guid? bedId, CancellationToken ct) =>
        await db.UnitHousekeepingStatus.Where(h => h.UnitId == unitId && h.BedId == bedId).Select(h => h.Status).SingleOrDefaultAsync(ct)
        ?? HousekeepingStatuses.Ready;

    /// <summary>Upserts the state and appends the change; returns it, or null when nothing changed. A check-out is always
    /// recorded (it is the event that makes the cleaning task), other changes only when the state actually changes.</summary>
    public async Task<UnitHousekeepingChange?> SetAsync(Guid orgId, Guid propertyId, Guid unitId, Guid? bedId, string status, string source,
        Guid? reservationId, string? reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = db.UnitHousekeepingStatus.Local.FirstOrDefault(h => h.UnitId == unitId && h.BedId == bedId)
                  ?? await db.UnitHousekeepingStatus.SingleOrDefaultAsync(h => h.UnitId == unitId && h.BedId == bedId, ct);
        var from = row?.Status ?? HousekeepingStatuses.Ready;
        if (from == status && source != HousekeepingSources.CheckOut) return null;
        if (row is null)
        {
            row = new UnitHousekeepingStatus { OrganizationId = orgId, PropertyId = propertyId, UnitId = unitId, BedId = bedId };
            db.UnitHousekeepingStatus.Add(row);
        }
        row.Status = status;
        row.Reason = reason;
        row.ChangedAt = now;
        row.ChangedBy = caller.UserId;
        var change = new UnitHousekeepingChange
        {
            OrganizationId = orgId,
            PropertyId = propertyId,
            UnitId = unitId,
            BedId = bedId,
            FromStatus = from,
            ToStatus = status,
            Source = source,
            ReservationId = reservationId,
            Reason = reason,
            ChangedAt = now,
            ChangedBy = caller.UserId,
        };
        db.UnitHousekeepingHistory.Add(change);
        return change;
    }

    /// <summary>A change from outside the housekeeping board: sets the state, then makes, moves or closes the room's task
    /// to match (DIRTY → an OPEN task, READY → the task is done, OUT_OF_ORDER → the task is cancelled).</summary>
    public async Task ChangeAsync(Property property, Guid unitId, Guid? bedId, string status, string source, Guid? reservationId,
        Guid? ticketId, string? reason, CancellationToken ct)
    {
        var change = await SetAsync(property.OrganizationId, property.Id, unitId, bedId, status, source, reservationId, reason, ct);
        var now = clock.GetUtcNow();
        var open = db.HousekeepingTasks.Local.FirstOrDefault(t => t.UnitId == unitId && t.BedId == bedId && t.IsActive)
                   ?? await db.HousekeepingTasks.SingleOrDefaultAsync(
                       t => t.UnitId == unitId && t.BedId == bedId && HousekeepingTaskStatuses.Active.Contains(t.Status), ct);
        var target = HousekeepingFlow.TaskStatusFor(status);
        if (change is not null && status == HousekeepingStatuses.Dirty) change.ConsumedAt = now;

        if (target is null)
        {
            if (open is null) return;
            open.Status = status == HousekeepingStatuses.Ready ? HousekeepingTaskStatuses.Done : HousekeepingTaskStatuses.Cancelled;
            open.ClosedAt = now;
            open.CloseReason = status == HousekeepingStatuses.Ready
                ? reason ?? "Marked ready outside the housekeeping board"
                : $"Out of order{(reason is null ? "" : ": " + reason)}";
            return;
        }

        if (open is null)
        {
            var type = source == HousekeepingSources.Maintenance ? HousekeepingTaskTypes.AfterRepair : HousekeepingTaskTypes.Departure;
            open = await NewTaskAsync(property, unitId, bedId, type, TaskSource(source), ct);
            open.SourceChangeId = change?.Id;
            open.ReservationId = reservationId;
            open.MaintenanceTicketId = ticketId;
            open.Note = source == HousekeepingSources.Manual ? reason : null;
            db.HousekeepingTasks.Add(open);
        }
        else if (status == HousekeepingStatuses.Dirty && open.Status != HousekeepingTaskStatuses.Open)
        {
            // Dirty again (another guest left, or the front desk said so): the cleaning starts over.
            open.StartedAt = open.FinishedAt = null;
            open.StartedBy = open.FinishedBy = null;
        }
        open.Status = target;
        if (target != HousekeepingTaskStatuses.Open && open.StartedAt is null) { open.StartedAt = now; open.StartedBy = caller.UserId; }
        if (target == HousekeepingTaskStatuses.Cleaned && open.FinishedAt is null) { open.FinishedAt = now; open.FinishedBy = caller.UserId; }
    }

    /// <summary>A new OPEN task with the property's checklist for its type (and the room's type, when it has its own).</summary>
    public async Task<HousekeepingTask> NewTaskAsync(Property property, Guid unitId, Guid? bedId, string taskType, string source, CancellationToken ct)
    {
        var typeId = await db.AccommodationUnits.Where(u => u.Id == unitId).Select(u => u.AccommodationTypeId).SingleAsync(ct);
        var templates = await db.HousekeepingChecklistTemplates.AsNoTracking().Include(t => t.Items)
            .Where(t => t.PropertyId == property.Id && t.IsActive && (t.TaskType == taskType || t.TaskType == HousekeepingTaskTypes.Departure)
                        && (t.AccommodationTypeId == null || t.AccommodationTypeId == typeId))
            .ToListAsync(ct);
        // This task type for this room type, then for every room type, then the departure checklist.
        var template = templates.OrderBy(t => t.TaskType == taskType ? 0 : 1).ThenBy(t => t.AccommodationTypeId == null ? 1 : 0).FirstOrDefault();
        var lines = template?.Items.OrderBy(i => i.SortOrder).Select(i => new ChecklistLine(i.Id, i.Label, i.IsRequired, false)).ToList()
                    ?? (await db.HousekeepingChecklistTemplates.AnyAsync(t => t.PropertyId == property.Id, ct)
                        ? []
                        : DefaultChecklist.Lines.Select(l => new ChecklistLine(Guid.CreateVersion7(), l, false, false)).ToList());
        return new HousekeepingTask
        {
            OrganizationId = property.OrganizationId,
            PropertyId = property.Id,
            UnitId = unitId,
            BedId = bedId,
            TaskType = taskType,
            Source = source,
            TemplateId = template?.Id,
            Checklist = WriteChecklist(lines),
            BusinessDate = property.LocalDate(clock.GetUtcNow()),
        };
    }

    public static List<ChecklistLine> ReadChecklist(string json) => JsonSerializer.Deserialize<List<ChecklistLine>>(json, Json) ?? [];

    public static string WriteChecklist(IEnumerable<ChecklistLine> lines) => JsonSerializer.Serialize(lines, Json);

    private static string TaskSource(string changeSource) => changeSource switch
    {
        HousekeepingSources.CheckOut => HousekeepingTaskSources.CheckOut,
        HousekeepingSources.RoomMove => HousekeepingTaskSources.RoomMove,
        HousekeepingSources.Maintenance => HousekeepingTaskSources.Maintenance,
        _ => HousekeepingTaskSources.Manual,
    };
}
