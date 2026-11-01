using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Employees;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Leave;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;

namespace Resort.Application.Leave;

/// <summary>
/// Leave requests (Sprint 19, brief §45, §74).
/// <list type="bullet">
/// <item>An employee asks for leave for themselves (leave.create); a manager can ask for someone else (leave.approve). The
/// days that count leave out holidays and the person's weekly day off; half days count 0.5 (<see cref="LeaveMath"/>).</item>
/// <item>Checks: the balance (what is left after other requests still waiting), the person's other leave (no two pending
/// or approved requests share a day: checked under a lock on the employee, with an EXCLUDE constraint behind it), and the
/// roster's cover (warnings: a shift that would be short of people, someone else in the department who is off).</item>
/// <item>PENDING → APPROVED (leave.approve; never one's own): the days are taken off the balance in the ledger under a
/// lock on the balance row (which can't go below zero), and the person's shifts on full days of leave come off the roster.
/// → REJECTED (leave.reject) with the reason. → CANCELLED with the reason: pending any time; approved only before it
/// starts, and the days go back to the balance. Every change is audited.</item>
/// </list>
/// </summary>
public sealed class LeaveRequestService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, EmployeeService employees,
    LeaveService leave)
{
    public const int BackDays = 30;
    public const int AheadDays = 365;

    // ------------------------------------------------------------------ asking

    /// <summary>Works a request out without saving it: the days, the balance after and the warnings.</summary>
    public async Task<LeavePreviewDto> PreviewAsync(Guid propertyId, CreateLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await WhoAsync(propertyId, r.EmployeeId, ct);
        var (type, days, balance, available) = await EvaluateAsync(property, emp, r, null, ct);
        var warnings = await WarningsAsync(property, emp, r.FromDate, r.ToDate, r.HalfDayStart, r.HalfDayEnd, days.Counted, null, ct);
        return new LeavePreviewDto(days.Days, days.Counted, days.Skipped.Select(s => $"{Short(s.Date)}: {s.Why}").ToList(),
            type.NeedsBalance ? balance : null, type.NeedsBalance ? available : null, type.NeedsBalance ? available - days.Days : null, warnings);
    }

    public async Task<LeaveRequestDto> CreateAsync(Guid propertyId, CreateLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await WhoAsync(propertyId, r.EmployeeId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One person's requests are made one at a time: the overlap and balance checks can't miss one made at the same moment.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM employees WHERE id = {emp.Id} FOR UPDATE", ct);
        var (type, days, _, _) = await EvaluateAsync(property, emp, r, null, ct);
        var q = new LeaveRequest
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            Code = await Purchasing.PurchasingService.NextNumberAsync(db, propertyId, "leave_request", "LV", ct),
            EmployeeId = emp.Id,
            LeaveTypeId = type.Id,
            FromDate = r.FromDate,
            ToDate = r.ToDate,
            HalfDayStart = r.HalfDayStart,
            HalfDayEnd = r.HalfDayEnd,
            Days = days.Days,
            CountedDates = [.. days.Counted],
            Reason = ReservationService.Clean(r.Reason),
            RequestedBy = caller.RequireUserId(),
        };
        db.LeaveRequests.Add(q);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [q], ct))[0];
    }

    // ------------------------------------------------------------------ deciding

    public async Task<LeaveRequestDto> ApproveAsync(Guid propertyId, Guid id, ApproveLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var q = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(q, v);
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == q.EmployeeId, ct);
        RequireNotOwn(emp, "approve");
        if (q.Status != LeaveRequestStatuses.Pending)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"Only leave waiting for approval can be approved; {q.Code} is {Words(q.Status)}.");
        var type = await db.LeaveTypes.AsNoTracking().SingleAsync(t => t.Id == q.LeaveTypeId, ct);
        if (type.NeedsBalance)
        {
            var balance = await leave.LockBalanceAsync(property, emp.Id, type.Id, ct);
            if (balance < q.Days)
                throw new BusinessRuleAppException("INSUFFICIENT_LEAVE_BALANCE",
                    $"{emp.FullName} has {LeaveMath.Days(balance)} of {type.Name} left and {q.Code} is {LeaveMath.Days(q.Days)}. " +
                    "Send it back so they can ask for fewer days or Leave without pay, or ask HR to adjust the balance.");
            db.LeaveLedger.Add(new LeaveLedgerEntry
            {
                OrganizationId = property.OrganizationId,
                PropertyId = propertyId,
                EmployeeId = emp.Id,
                LeaveTypeId = type.Id,
                EntryType = LeaveEntryTypes.Taken,
                Days = -q.Days,
                EntryDate = today,
                LeaveRequestId = q.Id,
                CreatedAt = clock.GetUtcNow(),
                CreatedBy = caller.RequireUserId(),
            });
        }

        // Off the roster on the full days of leave (a half day keeps the shift).
        var fullDays = q.CountedDates.Where(d => q.DayFraction(d) == 1m).ToList();
        var shifts = await db.RosterShifts
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && fullDays.Contains(s.WorkDate)).ToListAsync(ct);
        foreach (var s in shifts)
        {
            s.Status = RosterShiftStatuses.Cancelled;
            s.CancelReason = $"On leave: {q.Code} ({type.Name})";
        }
        q.Status = LeaveRequestStatuses.Approved;
        q.DecidedAt = clock.GetUtcNow();
        q.DecidedBy = caller.RequireUserId();
        q.DecisionNote = ReservationService.Clean(r.Note);
        q.ShiftsCancelled = (short)shifts.Count;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [q], ct))[0];
    }

    public async Task<LeaveRequestDto> RejectAsync(Guid propertyId, Guid id, RejectLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var q = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(q, v);
        RequireNotOwn(await db.Employees.AsNoTracking().SingleAsync(e => e.Id == q.EmployeeId, ct), "refuse");
        if (q.Status != LeaveRequestStatuses.Pending)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"Only leave waiting for approval can be refused; {q.Code} is {Words(q.Status)}.");
        q.Status = LeaveRequestStatuses.Rejected;
        q.DecidedAt = clock.GetUtcNow();
        q.DecidedBy = caller.RequireUserId();
        q.DecisionNote = r.Reason.Trim();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [q], ct))[0];
    }

    /// <summary>Cancels a request (the person themselves, or a manager with leave.approve). Approved leave only before it
    /// starts; its days go back to the balance. Shifts taken off the roster for it are not put back.</summary>
    public async Task<LeaveRequestDto> CancelAsync(Guid propertyId, Guid id, CancelLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var q = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(q, v);
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == q.EmployeeId, ct);
        var own = emp.UserId is { } u && u == caller.UserId;
        if (!own && !(await access.GetAsync(ct)).Has(Perm.LeaveApprove, propertyId))
            throw new ForbiddenAppException("You can cancel your own leave only. A manager cancels someone else's.");
        if (q.Status is not (LeaveRequestStatuses.Pending or LeaveRequestStatuses.Approved))
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{q.Code} is {Words(q.Status)}, so there is nothing to cancel.");
        if (q.Status == LeaveRequestStatuses.Approved && q.FromDate <= today)
            throw new BusinessRuleAppException("LEAVE_STARTED",
                $"{q.Code} began on {QuoteEngine.Day(q.FromDate)}, so it can't be cancelled now. If {(own ? "you" : emp.FirstName)} came back early, HR adjusts the balance.");
        if (q.Status == LeaveRequestStatuses.Approved
            && await db.LeaveLedger.AnyAsync(l => l.LeaveRequestId == q.Id && l.EntryType == LeaveEntryTypes.Taken, ct))
        {
            var type = await db.LeaveTypes.AsNoTracking().SingleAsync(t => t.Id == q.LeaveTypeId, ct);
            await leave.LockBalanceAsync(property, emp.Id, type.Id, ct);
            db.LeaveLedger.Add(new LeaveLedgerEntry
            {
                OrganizationId = property.OrganizationId,
                PropertyId = propertyId,
                EmployeeId = emp.Id,
                LeaveTypeId = type.Id,
                EntryType = LeaveEntryTypes.Returned,
                Days = q.Days,
                EntryDate = today,
                LeaveRequestId = q.Id,
                CreatedAt = clock.GetUtcNow(),
                CreatedBy = caller.RequireUserId(),
            });
        }
        q.Status = LeaveRequestStatuses.Cancelled;
        q.CancelledAt = clock.GetUtcNow();
        q.CancelledBy = caller.RequireUserId();
        q.CancelReason = r.Reason.Trim();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [q], ct))[0];
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Leave at the property (leave.read). Status: one status (default all); From/To: leave that touches those dates
    /// (default the last 30 days to 6 months ahead).</summary>
    public async Task<LeaveListDto> ListAsync(Guid propertyId, string? status, DateOnly? from, DateOnly? to, Guid? employeeId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var f = from ?? today.AddDays(-30);
        var t = to ?? today.AddDays(180);
        if (t < f) throw ReservationService.Field("To", "The end date is before the start date.");
        var q = db.LeaveRequests.AsNoTracking().Where(x => x.PropertyId == propertyId && x.FromDate <= t && x.ToDate >= f);
        if (employeeId is { } eid) q = q.Where(x => x.EmployeeId == eid);
        var all = await q.ToListAsync(ct);
        var counts = LeaveRequestStatuses.All.ToDictionary(s => s, s => all.Count(x => x.Status == s));
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToUpperInvariant();
            if (!LeaveRequestStatuses.All.Contains(s))
                throw ReservationService.Field("Status", $"'{status}' isn't a leave status. Use {string.Join(", ", LeaveRequestStatuses.All)}.");
            all = all.Where(x => x.Status == s).ToList();
        }
        var list = all.OrderBy(x => x.Status == LeaveRequestStatuses.Pending ? 0 : 1).ThenBy(x => x.FromDate).ThenBy(x => x.Code).Take(300).ToList();
        return new LeaveListDto(await DtosAsync(property, list, ct), counts);
    }

    public async Task<LeaveRequestDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var q = await db.LeaveRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That leave request doesn't exist at this property. Reload the page.");
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == q.EmployeeId, ct);
        if (emp.UserId != caller.UserId && !(await access.GetAsync(ct)).Has(Perm.LeaveRead, propertyId))
            throw new ForbiddenAppException("You can see your own leave only.");
        return (await DtosAsync(property, [q], ct))[0];
    }

    /// <summary>The caller's own leave (leave.create).</summary>
    public async Task<MyLeaveDto> MineAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await MyEmployeeAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var types = await leave.TypeDtosAsync(property, ct);
        var balances = (await leave.BalanceSetsAsync(property, [emp], ct))[0].Balances;
        var requests = await db.LeaveRequests.AsNoTracking().Where(x => x.EmployeeId == emp.Id).OrderByDescending(x => x.FromDate).ThenByDescending(x => x.Code)
            .Take(50).ToListAsync(ct);
        var holidays = await db.Holidays.AsNoTracking()
            .Where(h => h.PropertyId == propertyId && h.IsActive && h.HolidayDate >= today && h.HolidayDate <= today.AddDays(365))
            .OrderBy(h => h.HolidayDate).Select(h => new HolidayDto(h.Id, h.HolidayDate, h.Name, h.IsActive, h.Version)).ToListAsync(ct);
        return new MyLeaveDto(emp.Id, emp.FullName, today, balances, await DtosAsync(property, requests, ct), holidays,
            types.Where(t => t.IsActive).ToList());
    }

    // ------------------------------------------------------------------ the checks

    /// <summary>The field checks, the days, the overlap with the person's other leave and the balance. Throws under the
    /// field it is about.</summary>
    private async Task<(LeaveType Type, LeaveDays Days, decimal Balance, decimal Available)> EvaluateAsync(Property property, Employee emp,
        CreateLeaveRequest r, Guid? exceptId, CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        await leave.EnsureDefaultsAsync(property, ct);
        var type = await db.LeaveTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == r.LeaveTypeId && t.PropertyId == property.Id, ct)
                   ?? throw ReservationService.Field(nameof(r.LeaveTypeId), "Choose a kind of leave of this property.");
        if (!type.IsActive) throw ReservationService.Field(nameof(r.LeaveTypeId), $"{type.Name} is switched off. Choose another kind of leave.");
        if (r.FromDate < today.AddDays(-BackDays))
            throw ReservationService.Field(nameof(r.FromDate),
                $"Leave can be asked for up to {BackDays} days back (from {QuoteEngine.Day(today.AddDays(-BackDays))}). For older days ask HR.");
        if (r.ToDate > today.AddDays(AheadDays))
            throw ReservationService.Field(nameof(r.ToDate), $"Leave can be asked for up to a year ahead (until {QuoteEngine.Day(today.AddDays(AheadDays))}).");
        if (r.FromDate < emp.JoinedOn)
            throw ReservationService.Field(nameof(r.FromDate), $"{emp.FullName} joined on {QuoteEngine.Day(emp.JoinedOn)}, after {QuoteEngine.Day(r.FromDate)}.");
        if (emp.LeftOn is { } left && r.ToDate > left)
            throw ReservationService.Field(nameof(r.ToDate), $"{emp.FullName} left on {QuoteEngine.Day(left)}, before {QuoteEngine.Day(r.ToDate)}.");
        if ((r.HalfDayStart || r.HalfDayEnd) && !type.AllowHalfDay)
            throw ReservationService.Field(r.HalfDayStart ? nameof(r.HalfDayStart) : nameof(r.HalfDayEnd), $"{type.Name} is taken in whole days only.");

        var holidays = await leave.HolidayMapAsync(property.Id, r.FromDate, r.ToDate, ct);
        var days = LeaveMath.Count(r.FromDate, r.ToDate, r.HalfDayStart, r.HalfDayEnd, holidays, emp.WeeklyOff);
        string Why(DateOnly d) => holidays.TryGetValue(d, out var n) ? $"a holiday ({n})" : "the weekly day off";
        if (days.Counted.Count == 0)
            throw ReservationService.Field(nameof(r.FromDate), r.FromDate == r.ToDate
                ? $"{QuoteEngine.Day(r.FromDate)} is {Why(r.FromDate)}, so there is no leave to take."
                : $"Every day from {Short(r.FromDate)} to {Short(r.ToDate)} is a holiday or the weekly day off, so there is no leave to take.");
        if (r.HalfDayStart && days.Counted[0] != r.FromDate)
            throw ReservationService.Field(nameof(r.HalfDayStart), $"{Short(r.FromDate)} is {Why(r.FromDate)}, so it can't be a half day.");
        if (r.HalfDayEnd && days.Counted[^1] != r.ToDate)
            throw ReservationService.Field(nameof(r.HalfDayEnd), $"{Short(r.ToDate)} is {Why(r.ToDate)}, so it can't be a half day.");

        var clash = await db.LeaveRequests.AsNoTracking()
            .Where(x => x.EmployeeId == emp.Id && x.Id != exceptId && (x.Status == LeaveRequestStatuses.Pending || x.Status == LeaveRequestStatuses.Approved)
                        && x.FromDate <= r.ToDate && x.ToDate >= r.FromDate)
            .OrderBy(x => x.FromDate).FirstOrDefaultAsync(ct);
        if (clash is not null)
        {
            var ct2 = await db.LeaveTypes.AsNoTracking().Where(t => t.Id == clash.LeaveTypeId).Select(t => t.Name).SingleAsync(ct);
            var own = emp.UserId == caller.UserId;
            throw new ConflictAppException("LEAVE_OVERLAP",
                $"{(own ? "You" : emp.FullName)} already {(own ? "have" : "has")} {ct2} {clash.Code} on {Range(clash.FromDate, clash.ToDate)} " +
                $"({Words(clash.Status)}). Leave can't overlap: choose other dates, or cancel {clash.Code} first.", new { requestId = clash.Id });
        }

        decimal balance = 0, available = 0;
        if (type.NeedsBalance)
        {
            balance = await db.LeaveBalances.AsNoTracking().Where(b => b.EmployeeId == emp.Id && b.LeaveTypeId == type.Id).Select(b => b.Balance)
                .SingleOrDefaultAsync(ct);
            var pending = await db.LeaveRequests.AsNoTracking()
                .Where(x => x.EmployeeId == emp.Id && x.LeaveTypeId == type.Id && x.Status == LeaveRequestStatuses.Pending && x.Id != exceptId)
                .SumAsync(x => x.Days, ct);
            available = balance - pending;
            if (days.Days > available)
            {
                var own = emp.UserId == caller.UserId;
                var have = $"{(own ? "You have" : $"{emp.FirstName} has")} {LeaveMath.Days(balance)} of {type.Name}";
                var waiting = pending > 0 ? $" ({LeaveMath.D(pending)} already asked for and waiting, so {LeaveMath.D(Math.Max(0, available))} left)" : "";
                throw ReservationService.Field(nameof(r.LeaveTypeId),
                    $"{have}{waiting}. These dates are {LeaveMath.Days(days.Days)}. Ask for fewer days, or choose Leave without pay.");
            }
        }
        return (type, days, balance, available);
    }

    /// <summary>Cover warnings: shifts the leave takes off the roster, shifts that would then be short of people, and others
    /// in the department who are off on the same days.</summary>
    private async Task<List<LeaveWarningDto>> WarningsAsync(Property property, Employee emp, DateOnly from, DateOnly to, bool halfStart, bool halfEnd,
        IReadOnlyList<DateOnly> counted, Guid? requestId, CancellationToken ct)
    {
        var warnings = new List<LeaveWarningDto>();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(property.Timezone);
        var full = counted.Where(d => LeaveMath.Fraction(d, from, to, halfStart, halfEnd) == 1m).ToList();
        var mine = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && full.Contains(s.WorkDate)).OrderBy(s => s.StartsAt)
            .ToListAsync(ct);
        var templateIds = mine.Select(s => s.TemplateId).OfType<Guid>().Distinct().ToList();
        var templates = await db.ShiftTemplates.AsNoTracking().Where(t => templateIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var others = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == property.Id && s.EmployeeId != emp.Id && s.Status == RosterShiftStatuses.Scheduled
                        && s.TemplateId != null && templateIds.Contains(s.TemplateId.Value) && full.Contains(s.WorkDate))
            .Select(s => new { s.TemplateId, s.WorkDate }).ToListAsync(ct);
        foreach (var s in mine)
        {
            var label = $"{depts.GetValueOrDefault(s.DepartmentId, "?")}{(s.TemplateId is { } t ? " · " + templates[t].Name : "")} " +
                        $"{T(s.StartsAt, tz)}–{T(s.EndsAt, tz)}";
            warnings.Add(new LeaveWarningDto("ROSTERED", s.WorkDate, $"{Short(s.WorkDate)}: {emp.FirstName}'s shift {label} comes off the roster when this is approved."));
            if (s.TemplateId is { } tid && templates[tid].MinStaff > 0)
            {
                var left = others.Count(o => o.TemplateId == tid && o.WorkDate == s.WorkDate);
                if (left < templates[tid].MinStaff)
                    warnings.Add(new LeaveWarningDto("SHORT_STAFFED", s.WorkDate,
                        $"{Short(s.WorkDate)}: {depts.GetValueOrDefault(s.DepartmentId, "?")} · {templates[tid].Name} would have {left} of the {templates[tid].MinStaff} people it needs."));
            }
        }

        var off = await db.LeaveRequests.AsNoTracking()
            .Where(x => x.PropertyId == property.Id && x.EmployeeId != emp.Id && x.Id != requestId
                        && (x.Status == LeaveRequestStatuses.Pending || x.Status == LeaveRequestStatuses.Approved) && x.FromDate <= to && x.ToDate >= from)
            .ToListAsync(ct);
        if (off.Count > 0)
        {
            var offIds = off.Select(o => o.EmployeeId).Distinct().ToList();
            var colleagues = await db.Employees.AsNoTracking().Where(e => offIds.Contains(e.Id) && e.DepartmentId == emp.DepartmentId).ToDictionaryAsync(e => e.Id, ct);
            var typeNames = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == property.Id).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            foreach (var o in off.Where(o => colleagues.ContainsKey(o.EmployeeId)).OrderBy(o => o.FromDate))
            {
                var shared = o.CountedDates.Where(counted.Contains).Order().ToList();
                if (shared.Count == 0) continue;
                warnings.Add(new LeaveWarningDto("ALSO_OFF", shared[0],
                    $"{colleagues[o.EmployeeId].FullName} ({depts.GetValueOrDefault(emp.DepartmentId)}) is also off on {string.Join(", ", shared.Take(4).Select(Short))}" +
                    $"{(shared.Count > 4 ? $" and {shared.Count - 4} more" : "")}: {typeNames.GetValueOrDefault(o.LeaveTypeId)} {o.Code}" +
                    $"{(o.Status == LeaveRequestStatuses.Pending ? ", not decided yet" : "")}."));
            }
        }
        return warnings.OrderBy(w => w.Date).ThenBy(w => w.Kind).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Employee> WhoAsync(Guid propertyId, Guid? employeeId, CancellationToken ct)
    {
        var uid = caller.RequireUserId();
        var mine = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == uid && e.PropertyId == propertyId, ct);
        if (employeeId is null || employeeId == Guid.Empty || employeeId == mine?.Id)
            return mine ?? throw new NotFoundAppException(
                "Your account isn't linked to an employee record at this property. Ask HR to link it on your employee profile.");
        if (!(await access.GetAsync(ct)).Has(Perm.LeaveApprove, propertyId))
            throw new ForbiddenAppException("You can ask for leave for yourself only. A manager asks for someone else.");
        return await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.PropertyId == propertyId, ct)
               ?? throw ReservationService.Field("EmployeeId", "Choose an employee of this property.");
    }

    private async Task<Employee> MyEmployeeAsync(Guid propertyId, CancellationToken ct)
    {
        var uid = caller.RequireUserId();
        return await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == uid && e.PropertyId == propertyId, ct)
               ?? throw new NotFoundAppException("Your account isn't linked to an employee record at this property. Ask HR to link it on your employee profile.");
    }

    private void RequireNotOwn(Employee emp, string verb)
    {
        if (emp.UserId is { } u && u == caller.UserId)
            throw new BusinessRuleAppException("OWN_LEAVE", $"You can't {verb} your own leave. Another manager or HR does it.");
    }

    private async Task<LeaveRequest> LoadAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM leave_requests WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        return await db.LeaveRequests.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
               ?? throw new NotFoundAppException("That leave request doesn't exist at this property. Reload the page.");
    }

    private async Task<List<LeaveRequestDto>> DtosAsync(Property property, List<LeaveRequest> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var today = property.LocalDate(clock.GetUtcNow());
        var empIds = list.Select(x => x.EmployeeId).Distinct().ToList();
        var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == property.Id).ToDictionaryAsync(t => t.Id, ct);
        var balances = await db.LeaveBalances.AsNoTracking().Where(b => empIds.Contains(b.EmployeeId))
            .ToDictionaryAsync(b => (b.EmployeeId, b.LeaveTypeId), b => b.Balance, ct);
        var users = await LeaveService.UserNamesAsync(db, list.SelectMany(x => new[] { x.RequestedBy, x.DecidedBy, x.CancelledBy }), ct);
        var a = await access.GetAsync(ct);
        var result = new List<LeaveRequestDto>();
        foreach (var x in list)
        {
            var e = emps[x.EmployeeId];
            var t = types[x.LeaveTypeId];
            var own = e.UserId is { } u && u == caller.UserId;
            var actions = new List<string>();
            if (x.Status == LeaveRequestStatuses.Pending && !own)
            {
                if (a.Has(Perm.LeaveApprove, property.Id)) actions.Add("approve");
                if (a.Has(Perm.LeaveReject, property.Id)) actions.Add("reject");
            }
            if ((own || a.Has(Perm.LeaveApprove, property.Id))
                && (x.Status == LeaveRequestStatuses.Pending || (x.Status == LeaveRequestStatuses.Approved && x.FromDate > today)))
                actions.Add("cancel");
            var warnings = x.Status == LeaveRequestStatuses.Pending
                ? await WarningsAsync(property, e, x.FromDate, x.ToDate, x.HalfDayStart, x.HalfDayEnd, x.CountedDates, x.Id, ct)
                : [];
            result.Add(new LeaveRequestDto(x.Id, x.Code, e.Id, e.Code, e.FullName, depts.GetValueOrDefault(e.DepartmentId, "?"), t.Id, t.Code, t.Name, t.IsPaid,
                x.FromDate, x.ToDate, x.HalfDayStart, x.HalfDayEnd, x.Days, x.CountedDates.Order().ToList(), x.Reason, x.Status, x.CreatedAt,
                x.RequestedBy is { } rb ? users.GetValueOrDefault(rb) : null, x.DecidedAt, x.DecidedBy is { } db2 ? users.GetValueOrDefault(db2) : null,
                x.DecisionNote, x.ShiftsCancelled, x.CancelledAt, x.CancelledBy is { } cb ? users.GetValueOrDefault(cb) : null, x.CancelReason,
                t.NeedsBalance ? balances.GetValueOrDefault((e.Id, t.Id)) : null, warnings, actions, x.Version));
        }
        return result;
    }

    private void Expect(LeaveRequest q, uint version)
    {
        if (q.Version != version)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {q.Code} a moment ago. Reload to see their change, then try again.");
        ConcurrencyGuard.ExpectVersion(db, q, version);
    }

    internal static string Words(string status) => status switch
    {
        LeaveRequestStatuses.Pending => "waiting for approval",
        LeaveRequestStatuses.Approved => "approved",
        LeaveRequestStatuses.Rejected => "refused",
        _ => "cancelled",
    };

    internal static string Short(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

    internal static string Range(DateOnly from, DateOnly to) => from == to ? Short(from) : $"{Short(from)} – {Short(to)}";

    private static string T(DateTimeOffset at, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(at, tz).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
