using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Resort.Application.Common;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Compliance;
using Resort.Domain.Inventory;
using Resort.Domain.Leave;
using Resort.Domain.Notifications;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Notifications;

/// <summary>
/// The scheduled jobs (Sprint 24, brief §70) and the scheduler that runs them. Every job is idempotent: it finds its work
/// (the bookings arriving tomorrow, today's low stock…), and each piece of work has a business key, such as
/// <c>&lt;reservation&gt;:&lt;arrival date&gt;</c> or the day. For each, in one transaction under an advisory lock on the key, it
/// checks job_runs, queues the messages through the <see cref="Outbox"/> and records the key in job_runs (UNIQUE on
/// property, job and key). Running a job again, by hand or on a second API instance, finds the key and queues nothing:
/// a guest gets one reminder however often the job runs. The outbox's own dedupe key is a second guard.
///
/// The scheduler (<see cref="RunDueAsync"/>, every minute) is PostgreSQL only (no Redis, brief §76): scheduled_jobs holds
/// each job's local time and next_run_at per property. An instance claims a due job with a compare-and-set UPDATE that moves
/// next_run_at to the next day; the one whose UPDATE changed the row runs it. A job missed while the API was down runs as
/// soon as it is back (next_run_at is in the past), once.
/// </summary>
public sealed class NotificationJobs(IAppDbContext db, Outbox outbox, TimeProvider clock, ILogger<NotificationJobs> log)
{
    private sealed record Work(string Key, Guid? ReservationId, string About, Func<Guid, Task<IReadOnlyList<Enqueued>>> Enqueue);

    // ------------------------------------------------------------------ the scheduler

    /// <summary>Runs every job that is due at <paramref name="p"/>. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(Property p, CancellationToken ct)
    {
        await outbox.EnsureAsync(p, ct);
        var now = clock.GetUtcNow();
        var due = await db.ScheduledJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.PropertyId == p.Id && j.Enabled && j.NextRunAt <= now).OrderBy(j => j.NextRunAt).ToListAsync(ct);
        var ran = 0;
        foreach (var j in due)
        {
            var next = JobClock.Next(p, j.RunAt, now);
            var claimed = await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE scheduled_jobs SET next_run_at = {next}, last_started_at = {now}, updated_at = {now}
                  WHERE id = {j.Id} AND enabled AND next_run_at = {j.NextRunAt}
                 """, ct);
            if (claimed != 1) continue;   // another instance has it
            ran++;
            try
            {
                var result = await RunAsync(p, j.Job, null, ct);
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE scheduled_jobs SET last_finished_at = {clock.GetUtcNow()}, last_outcome = {Cut(result.Message)}, last_error = NULL WHERE id = {j.Id}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                (db as DbContext)?.ChangeTracker.Clear();
                log.LogError(ex, "Job {Job} at {Property} failed; it runs again at {Next}", j.Job, p.Code, next);
                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE scheduled_jobs SET last_finished_at = {clock.GetUtcNow()},
                            last_error = {Cut($"{ex.GetType().Name}: {ex.Message}. It runs again at its next time; Run now tries again.")}
                      WHERE id = {j.Id}
                     """, CancellationToken.None);
            }
        }
        return ran;
    }

    // ------------------------------------------------------------------ one job

    /// <summary>Runs one job at the property now. <paramref name="userId"/>: who pressed Run now (null: the scheduler).</summary>
    public async Task<JobRunResultDto> RunAsync(Property p, string job, Guid? userId, CancellationToken ct)
    {
        var def = NotificationCatalog.Job(job);
        await outbox.EnsureAsync(p, ct);
        var now = clock.GetUtcNow();
        var today = p.LocalDate(now);
        var trigger = userId is null ? JobTriggers.Schedule : JobTriggers.Manual;
        var work = job switch
        {
            JobCodes.ReservationReminder => await ArrivalsAsync(p, today.AddDays(1), MessageCodes.ReservationReminder, ct),
            JobCodes.PreArrival => await ArrivalsAsync(p, today.AddDays(await PreArrivalDaysAsync(p, ct)), MessageCodes.PreArrival, ct),
            JobCodes.CheckoutReminder => await DeparturesAsync(p, today, ct),
            JobCodes.PaymentReminder => await UnpaidAsync(p, today, now, ct),
            JobCodes.InventoryAlert => await LowStockAsync(p, today, ct),
            JobCodes.LeaveReminder => await LeaveWaitingAsync(p, today, now, ct),
            JobCodes.FormCReminder => await FormCWaitingAsync(p, today, ct),
            _ => [],
        };

        int queued = 0, skipped = 0, already = 0;
        var notes = new List<string>();
        foreach (var w in work)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({$"job:{p.Id}:{job}:{w.Key}"}))", ct);
            if (await db.JobRuns.IgnoreQueryFilters().AnyAsync(r => r.PropertyId == p.Id && r.Job == job && r.BusinessKey == w.Key, ct))
            {
                already++;
                continue;
            }
            var runId = Guid.CreateVersion7();
            var results = await w.Enqueue(runId);
            var q = results.Count(r => r.Result == MessageStatuses.Queued);
            var s = results.Where(r => r.Result == MessageStatuses.Skipped).ToList();
            queued += q;
            skipped += s.Count;
            var outcome = results.Count == 0 ? $"{w.About}: nobody to send it to."
                : $"{w.About}: " + string.Join("; ", results.GroupBy(r => r.Recipient).Select(g =>
                    $"{g.Key}: {string.Join(", ", g.Select(r => MessageChannels.Words(r.Channel) + " " + (r.Result switch
                    {
                        MessageStatuses.Queued => "queued",
                        MessageStatuses.Skipped => "not sent (" + r.Reason + ")",
                        _ => "already queued",
                    })))}"));
            if (s.Count > 0) notes.AddRange(s.Select(r => $"{w.About}, {MessageChannels.Words(r.Channel)} to {r.Recipient}: {r.Reason}"));
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO job_runs (id, organization_id, property_id, job, business_key, reservation_id, triggered_by, run_by, outcome, created_at)
                 VALUES ({runId}, {p.OrganizationId}, {p.Id}, {job}, {w.Key}, {w.ReservationId}, {trigger}, {userId}, {Cut(outcome)}, {now})
                 """, ct);
            await tx.CommitAsync(ct);
        }
        var message = work.Count == 0 ? $"{def.Name}: nothing to send today."
            : $"{def.Name}: {work.Count} to do, {queued} message{(queued == 1 ? "" : "s")} queued" +
              (skipped > 0 ? $", {skipped} not sent (see why below)" : "") +
              (already > 0 ? $", {already} already done earlier (nothing sent twice)" : "") + ".";
        if (work.Count > 0 && already == work.Count) message = $"{def.Name}: all {work.Count} already done earlier, so nothing was sent twice.";
        log.LogInformation("Job {Job} at {Property}: {Message}", job, p.Code, message);
        return new JobRunResultDto(job, def.Name, work.Count, queued, skipped, already, message, notes);
    }

    // ------------------------------------------------------------------ what each job finds

    private async Task<List<Work>> ArrivalsAsync(Property p, DateOnly day, string code, CancellationToken ct)
    {
        var list = await db.Reservations.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.PropertyId == p.Id && r.Status == ReservationStatuses.Confirmed && r.CheckIn == day)
            .OrderBy(r => r.Reference).ToListAsync(ct);
        return list.Select(r =>
        {
            var key = $"{r.Id:N}:{r.CheckIn:yyyyMMdd}";
            return new Work(key, r.Id, $"{r.Reference} arriving {QuoteEngine.Day(r.CheckIn)}",
                runId => outbox.GuestAsync(p, r, code, $"{code}:{key}", MessageSources.Job, runId, null, ct));
        }).ToList();
    }

    private async Task<List<Work>> DeparturesAsync(Property p, DateOnly today, CancellationToken ct)
    {
        var list = await db.Reservations.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.PropertyId == p.Id && r.Status == ReservationStatuses.CheckedIn && r.CheckOut == today)
            .OrderBy(r => r.Reference).ToListAsync(ct);
        return list.Select(r =>
        {
            var key = $"{r.Id:N}:{r.CheckOut:yyyyMMdd}";
            return new Work(key, r.Id, $"{r.Reference} leaving today",
                runId => outbox.GuestAsync(p, r, MessageCodes.CheckoutReminder, $"{MessageCodes.CheckoutReminder}:{key}", MessageSources.Job, runId, null, ct));
        }).ToList();
    }

    /// <summary>Bookings still waiting for payment (PENDING, arriving today or later) with an open payment link made more
    /// than a day ago: the link again, once per link.</summary>
    private async Task<List<Work>> UnpaidAsync(Property p, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var dayAgo = now.AddHours(-24);
        var rows = await db.PaymentGatewayOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PropertyId == p.Id && l.Status == GatewayOrderStatuses.Created && l.CancelledAt == null && l.CreatedAt <= dayAgo)
            .Join(db.Reservations.IgnoreQueryFilters().Where(r => r.Status == ReservationStatuses.Pending && r.CheckIn >= today),
                l => l.ReservationId, r => r.Id, (l, r) => new { Link = l, Reservation = r })
            .OrderBy(x => x.Reservation.Reference).ThenBy(x => x.Link.CreatedAt).ToListAsync(ct);
        return rows.Select(x =>
        {
            var key = x.Link.Id.ToString("N", CultureInfo.InvariantCulture);
            var extra = new Dictionary<string, string>
            {
                ["amount"] = Money.Rupees(x.Link.Amount),
                ["pay_link"] = outbox.PublicUrl($"/pay/{x.Link.PublicToken}"),
            };
            return new Work(key, x.Reservation.Id, $"{x.Reservation.Reference} waiting for {Money.Rupees(x.Link.Amount)}",
                runId => outbox.GuestAsync(p, x.Reservation, MessageCodes.PaymentReminder, $"{MessageCodes.PaymentReminder}:{key}", MessageSources.Job,
                    runId, extra, ct));
        }).ToList();
    }

    /// <summary>Items at or below their reorder level (all stores together): one alert a day to the staff who manage stock.</summary>
    private async Task<List<Work>> LowStockAsync(Property p, DateOnly today, CancellationToken ct)
    {
        var items = await db.InventoryItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.PropertyId == p.Id && i.IsActive && i.ReorderLevel != null)
            .Select(i => new
            {
                i.Name,
                i.ReorderLevel,
                Unit = db.InventoryUnits.Where(u => u.Id == i.UnitId).Select(u => u.Code).FirstOrDefault(),
                OnHand = db.StockLevels.IgnoreQueryFilters().Where(s => s.ItemId == i.Id).Sum(s => (decimal?)s.OnHand) ?? 0m,
            })
            .ToListAsync(ct);
        var low = items.Where(i => i.OnHand <= i.ReorderLevel!.Value).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        if (low.Count == 0) return [];
        var text = string.Join("; ", low.Select(i => $"{i.Name} {Qty(i.OnHand)} {i.Unit} (reorder at {Qty(i.ReorderLevel!.Value)} {i.Unit})"));
        var values = new Dictionary<string, string>
        {
            ["property_name"] = p.Name,
            ["count"] = low.Count.ToString(CultureInfo.InvariantCulture),
            ["items"] = text,
        };
        var key = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return
        [
            new Work(key, null, $"{low.Count} item{(low.Count == 1 ? "" : "s")} low on {QuoteEngine.Day(today)}",
                runId => outbox.StaffAsync(p, MessageCodes.InventoryAlert, Perm.InventoryManage, $"{MessageCodes.InventoryAlert}:{key}", values,
                    "/inventory", MessageSources.Job, runId, ct)),
        ];
    }

    /// <summary>Leave requests waiting more than a day for a decision: one reminder a day to the staff who approve leave.</summary>
    private async Task<List<Work>> LeaveWaitingAsync(Property p, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var dayAgo = now.AddHours(-24);
        var waiting = await db.LeaveRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PropertyId == p.Id && l.Status == LeaveRequestStatuses.Pending && l.CreatedAt <= dayAgo)
            .Join(db.Employees.IgnoreQueryFilters(), l => l.EmployeeId, e => e.Id, (l, e) => new { l, e.FirstName, e.LastName })
            .Join(db.LeaveTypes.IgnoreQueryFilters(), x => x.l.LeaveTypeId, t => t.Id, (x, t) => new { x.l, x.FirstName, x.LastName, Type = t.Name })
            .OrderBy(x => x.l.FromDate).ThenBy(x => x.l.Code).ToListAsync(ct);
        if (waiting.Count == 0) return [];
        var text = string.Join("; ", waiting.Select(x =>
            $"{(string.IsNullOrWhiteSpace(x.LastName) ? x.FirstName : x.FirstName + " " + x.LastName)}: {x.Type}, {Qty(x.l.Days)} day{(x.l.Days == 1 ? "" : "s")} from {QuoteEngine.Day(x.l.FromDate)}"));
        var values = new Dictionary<string, string>
        {
            ["property_name"] = p.Name,
            ["count"] = waiting.Count.ToString(CultureInfo.InvariantCulture),
            ["requests"] = text,
        };
        var key = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return
        [
            new Work(key, null, $"{waiting.Count} leave request{(waiting.Count == 1 ? "" : "s")} waiting on {QuoteEngine.Day(today)}",
                runId => outbox.StaffAsync(p, MessageCodes.LeaveReminder, Perm.LeaveApprove, $"{MessageCodes.LeaveReminder}:{key}", values,
                    "/leave", MessageSources.Job, runId, ct)),
        ];
    }

    /// <summary>Form C sheets still to file (Sprint 30): one reminder a day to the staff who check guests in, with each
    /// guest's deadline, the ones already past it first.</summary>
    private async Task<List<Work>> FormCWaitingAsync(Property p, DateOnly today, CancellationToken ct)
    {
        var waiting = await db.FormCRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.PropertyId == p.Id && f.Status == FormCStatuses.Pending)
            .Join(db.Guests.IgnoreQueryFilters(), f => f.GuestId, g => g.Id, (f, g) => new { f, g.FirstName, g.LastName })
            .Join(db.Reservations.IgnoreQueryFilters(), x => x.f.ReservationId, r => r.Id, (x, r) => new { x.f, x.FirstName, x.LastName, r.Reference })
            .OrderBy(x => x.f.DueAt).ToListAsync(ct);
        if (waiting.Count == 0) return [];
        var tz = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        var now = clock.GetUtcNow();
        var text = string.Join("; ", waiting.Select(x =>
        {
            var name = string.IsNullOrWhiteSpace(x.LastName) ? x.FirstName : x.FirstName + " " + x.LastName;
            var due = TimeZoneInfo.ConvertTime(x.f.DueAt, tz).ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture);
            return x.f.DueAt <= now ? $"{name} ({x.Reference}), overdue since {due}" : $"{name} ({x.Reference}), to file by {due}";
        }));
        var values = new Dictionary<string, string>
        {
            ["property_name"] = p.Name,
            ["count"] = waiting.Count.ToString(CultureInfo.InvariantCulture),
            ["guests"] = text,
        };
        var key = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return
        [
            new Work(key, null, $"{waiting.Count} Form C to file on {QuoteEngine.Day(today)}",
                runId => outbox.StaffAsync(p, MessageCodes.FormCReminder, Perm.FrontDeskCheckIn, $"{MessageCodes.FormCReminder}:{key}", values,
                    "/form-c", MessageSources.Job, runId, ct)),
        ];
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>notifications.pre_arrival_days (default 3, 2–30).</summary>
    public async Task<int> PreArrivalDaysAsync(Property p, CancellationToken ct)
    {
        var json = await db.PropertySettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.PropertyId == p.Id && s.Key == NotificationCatalog.PreArrivalDaysSetting).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (json is null) return NotificationCatalog.DefaultPreArrivalDays;
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("days", out var d) ? d : doc.RootElement;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && v is >= 2 and <= 30 ? v : NotificationCatalog.DefaultPreArrivalDays;
    }

    private static string Qty(decimal q) => q.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Cut(string s) => s.Length <= 1000 ? s : s[..999] + "…";
}
