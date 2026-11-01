using System.Globalization;
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
/// Leave set-up and balances (Sprint 19, brief §45).
/// <list type="bullet">
/// <item>Leave types with a policy each (days a year, earned monthly or yearly, a maximum balance). A property gets Casual,
/// Sick, Earned and Leave without pay the first time its leave is opened; HR changes them on the Leave page.</item>
/// <item>A balance is the sum of its ledger: every accrual, leave taken, leave given back and adjustment is a ledger
/// entry that is never changed, and a database trigger adds each one to leave_balances, which can't go below zero.</item>
/// <item>Holidays: one per date. Leave over a holiday doesn't count it.</item>
/// </list>
/// </summary>
public sealed class LeaveService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, EmployeeService employees,
    LeaveAccrual accrual)
{
    // ------------------------------------------------------------------ leave types and policies

    public async Task<IReadOnlyList<LeaveTypeDto>> TypesAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        return await TypeDtosAsync(property, ct);
    }

    public async Task<LeaveTypeDto> SaveTypeAsync(Guid propertyId, Guid? id, SaveLeaveTypeRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await EnsureDefaultsAsync(property, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        if (await db.LeaveTypes.AnyAsync(t => t.PropertyId == propertyId && t.Code == code && t.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another leave type already has the code {code}.");
        var names = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == propertyId && t.Id != id).Select(t => t.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a leave type called {name}.");
        if (r.NeedsBalance && !r.IsPaid)
            throw ReservationService.Field(nameof(r.NeedsBalance), "Unpaid leave isn't taken from a balance. Switch off \"needs a balance\", or make it paid.");

        LeaveType t;
        if (id is { } tid)
        {
            t = await db.LeaveTypes.SingleOrDefaultAsync(x => x.Id == tid && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That leave type doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(t, v, $"the {t.Name} leave type");
            if (t.NeedsBalance && !r.NeedsBalance && await db.LeaveBalances.AnyAsync(b => b.LeaveTypeId == t.Id && b.Balance > 0, ct))
                throw ReservationService.Field(nameof(r.NeedsBalance),
                    $"People still have {t.Name} days in their balance. It must keep needing a balance; switch the type off instead.");
        }
        else
        {
            t = new LeaveType { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.LeaveTypes.Add(t);
        }
        t.Code = code;
        t.Name = name;
        t.IsPaid = r.IsPaid;
        t.NeedsBalance = r.NeedsBalance;
        t.AllowHalfDay = r.AllowHalfDay;
        t.SortOrder = r.SortOrder;
        t.IsActive = r.IsActive;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        var policy = await db.LeavePolicies.SingleOrDefaultAsync(p => p.LeaveTypeId == t.Id && p.EmploymentType == null, ct);
        if (policy is null)
        {
            policy = new LeavePolicy { OrganizationId = property.OrganizationId, PropertyId = propertyId, LeaveTypeId = t.Id };
            db.LeavePolicies.Add(policy);
        }
        policy.DaysPerYear = r.DaysPerYear;
        policy.Accrual = r.Accrual.Trim().ToUpperInvariant();
        policy.MaxBalance = r.MaxBalance;
        policy.IsActive = true;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await TypeDtosAsync(property, ct)).Single(x => x.Id == t.Id);
    }

    /// <summary>Sets a leave type's policy for one employment type (or everyone).</summary>
    public async Task<LeaveTypeDto> SavePolicyAsync(Guid propertyId, SaveLeavePolicyRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var t = await db.LeaveTypes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.LeaveTypeId && x.PropertyId == propertyId, ct)
                ?? throw ReservationService.Field(nameof(r.LeaveTypeId), "Choose a leave type of this property.");
        var type = string.IsNullOrWhiteSpace(r.EmploymentType) ? null : r.EmploymentType.Trim().ToUpperInvariant();
        var policy = await db.LeavePolicies.SingleOrDefaultAsync(p => p.LeaveTypeId == t.Id && p.EmploymentType == type, ct);
        if (policy is null)
        {
            policy = new LeavePolicy { OrganizationId = property.OrganizationId, PropertyId = propertyId, LeaveTypeId = t.Id, EmploymentType = type };
            db.LeavePolicies.Add(policy);
        }
        else if (r.Version is { } v) Expect(policy, v, $"the {t.Name} policy");
        if (type is null && !r.IsActive)
            throw ReservationService.Field(nameof(r.IsActive), "The policy for everyone can't be switched off. Set its days to 0 instead.");
        policy.DaysPerYear = r.DaysPerYear;
        policy.Accrual = r.Accrual.Trim().ToUpperInvariant();
        policy.MaxBalance = r.MaxBalance;
        policy.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await TypeDtosAsync(property, ct)).Single(x => x.Id == t.Id);
    }

    // ------------------------------------------------------------------ holidays

    public async Task<IReadOnlyList<HolidayDto>> HolidaysAsync(Guid propertyId, int? year, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var y = year ?? property.LocalDate(clock.GetUtcNow()).Year;
        var from = new DateOnly(y, 1, 1);
        var to = new DateOnly(y, 12, 31);
        return await db.Holidays.AsNoTracking().Where(h => h.PropertyId == propertyId && h.HolidayDate >= from && h.HolidayDate <= to)
            .OrderBy(h => h.HolidayDate).ThenByDescending(h => h.IsActive)
            .Select(h => new HolidayDto(h.Id, h.HolidayDate, h.Name, h.IsActive, h.Version)).ToListAsync(ct);
    }

    /// <summary>Adds or changes a holiday. Leave already asked for keeps the days it was given; new requests count the change.</summary>
    public async Task<HolidayDto> SaveHolidayAsync(Guid propertyId, Guid? id, SaveHolidayRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        if (r.IsActive && await db.Holidays.AsNoTracking().FirstOrDefaultAsync(h => h.PropertyId == propertyId && h.HolidayDate == r.Date && h.IsActive && h.Id != id, ct)
                is { } other)
            throw ReservationService.Field(nameof(r.Date), $"{QuoteEngine.Day(r.Date)} is already a holiday: {other.Name}.");
        Holiday h;
        if (id is { } hid)
        {
            h = await db.Holidays.SingleOrDefaultAsync(x => x.Id == hid && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That holiday doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(h, v, $"the {h.Name} holiday");
        }
        else
        {
            h = new Holiday { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.Holidays.Add(h);
        }
        h.HolidayDate = r.Date;
        h.Name = r.Name.Trim();
        h.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return new HolidayDto(h.Id, h.HolidayDate, h.Name, h.IsActive, h.Version);
    }

    // ------------------------------------------------------------------ balances

    /// <summary>Everyone's balances (leave.read): employees working here now, by department.</summary>
    public async Task<IReadOnlyList<EmployeeLeaveBalancesDto>> BalancesAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var emps = await db.Employees.AsNoTracking()
            .Where(e => e.PropertyId == propertyId && e.Status == EmployeeStatuses.Active && e.JoinedOn <= today.AddDays(31)).ToListAsync(ct);
        return await BalanceSetsAsync(property, emps, ct);
    }

    /// <summary>One person's ledger with the running balance (leave.read, or the person themselves).</summary>
    public async Task<LeaveLedgerDto> LedgerAsync(Guid propertyId, Guid employeeId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        if (emp.UserId != caller.UserId && !(await access.GetAsync(ct)).Has(Perm.LeaveRead, propertyId))
            throw new ForbiddenAppException("You can see your own leave only.");
        return await LedgerDtoAsync(property, emp, ct);
    }

    /// <summary>HR adds or takes off days by hand with the reason (leave.manage_balances; not their own).</summary>
    public async Task<LeaveLedgerDto> AdjustAsync(Guid propertyId, AdjustLeaveRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == r.EmployeeId && e.PropertyId == propertyId, ct)
                  ?? throw ReservationService.Field(nameof(r.EmployeeId), "Choose an employee of this property.");
        if (emp.UserId is { } u && u == caller.UserId)
            throw new BusinessRuleAppException("OWN_LEAVE", "You can't change your own leave balance. Another HR person or the owner does it.");
        var type = await db.LeaveTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == r.LeaveTypeId && t.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.LeaveTypeId), "Choose a leave type of this property.");
        if (!type.NeedsBalance)
            throw ReservationService.Field(nameof(r.LeaveTypeId), $"{type.Name} isn't taken from a balance, so it has none to change.");
        if (r.Opening && r.Days <= 0)
            throw ReservationService.Field(nameof(r.Days), "An opening balance is the days brought in, more than 0.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var balance = await LockBalanceAsync(property, emp.Id, type.Id, ct);
        if (balance + r.Days < 0)
            throw ReservationService.Field(nameof(r.Days),
                $"{emp.FullName} has {LeaveMath.Days(balance)} of {type.Name}. You can take off at most {LeaveMath.D(balance)}.");
        db.LeaveLedger.Add(new LeaveLedgerEntry
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            EmployeeId = emp.Id,
            LeaveTypeId = type.Id,
            EntryType = r.Opening ? LeaveEntryTypes.Opening : LeaveEntryTypes.Adjustment,
            Days = r.Days,
            EntryDate = property.LocalDate(clock.GetUtcNow()),
            Reason = r.Reason.Trim(),
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.RequireUserId(),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LedgerDtoAsync(property, emp, ct);
    }

    /// <summary>Posts a month's accrual by hand (leave.manage_balances). The job does the current month by itself.</summary>
    public async Task<AccrualResultDto> RunAccrualAsync(Guid propertyId, RunAccrualRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var (year, month) = ParseMonth(r.Month, nameof(r.Month));
        var today = property.LocalDate(clock.GetUtcNow());
        if (new DateOnly(year, month, 1) > today)
            throw ReservationService.Field(nameof(r.Month), $"{MonthName(year, month)} hasn't started yet. Leave is earned on the 1st of the month.");
        if (new DateOnly(year, month, 1) < new DateOnly(today.Year - 1, 1, 1))
            throw ReservationService.Field(nameof(r.Month), $"Accruals go back to January {today.Year - 1}. Add older days as an opening balance.");
        await EnsureDefaultsAsync(property, ct);
        return await accrual.RunAsync(property, year, month, caller.UserId, ct);
    }

    // ------------------------------------------------------------------ used by requests and payroll

    internal async Task<List<LeaveTypeDto>> TypeDtosAsync(Property property, CancellationToken ct)
    {
        await EnsureDefaultsAsync(property, ct);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == property.Id).ToListAsync(ct);
        var policies = await db.LeavePolicies.AsNoTracking().Where(p => p.PropertyId == property.Id).ToListAsync(ct);
        return types.OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LeaveTypeDto(t.Id, t.Code, t.Name, t.IsPaid, t.NeedsBalance, t.AllowHalfDay, t.SortOrder, t.IsActive,
                policies.Where(p => p.LeaveTypeId == t.Id).OrderBy(p => p.EmploymentType is null ? 0 : 1).ThenBy(p => p.EmploymentType)
                    .Select(p => new LeavePolicyDto(p.Id, p.EmploymentType, p.DaysPerYear, p.Accrual, p.MaxBalance, p.IsActive, p.Version)).ToList(),
                t.Version))
            .ToList();
    }

    /// <summary>The property's holidays between two dates (active ones), by date.</summary>
    internal async Task<Dictionary<DateOnly, string>> HolidayMapAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Holidays.AsNoTracking().Where(h => h.PropertyId == propertyId && h.IsActive && h.HolidayDate >= from && h.HolidayDate <= to)
            .ToDictionaryAsync(h => h.HolidayDate, h => h.Name, ct);

    internal async Task<List<EmployeeLeaveBalancesDto>> BalanceSetsAsync(Property property, IReadOnlyCollection<Employee> emps, CancellationToken ct)
    {
        if (emps.Count == 0) return [];
        var types = (await TypeDtosAsync(property, ct)).Where(t => t.IsActive || t.NeedsBalance).ToList();
        var ids = emps.Select(e => e.Id).ToList();
        var balances = await db.LeaveBalances.AsNoTracking().Where(b => ids.Contains(b.EmployeeId)).ToListAsync(ct);
        var pending = await db.LeaveRequests.AsNoTracking().Where(q => ids.Contains(q.EmployeeId) && q.Status == LeaveRequestStatuses.Pending)
            .GroupBy(q => new { q.EmployeeId, q.LeaveTypeId }).Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Days = g.Sum(x => x.Days) })
            .ToListAsync(ct);
        var year = property.LocalDate(clock.GetUtcNow()).Year;
        var yStart = new DateOnly(year, 1, 1);
        var yEnd = new DateOnly(year, 12, 31);
        var taken = await db.LeaveRequests.AsNoTracking()
            .Where(q => ids.Contains(q.EmployeeId) && q.Status == LeaveRequestStatuses.Approved && q.FromDate >= yStart && q.FromDate <= yEnd)
            .GroupBy(q => new { q.EmployeeId, q.LeaveTypeId }).Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Days = g.Sum(x => x.Days) })
            .ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToListAsync(ct);
        var order = depts.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return emps.OrderBy(e => order.GetValueOrDefault(e.DepartmentId, 999)).ThenBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new EmployeeLeaveBalancesDto(e.Id, e.Code, e.FullName, depts.Single(d => d.Id == e.DepartmentId).Name, e.EmploymentType,
                types.Where(t => t.IsActive).Select(t =>
                {
                    var bal = balances.SingleOrDefault(b => b.EmployeeId == e.Id && b.LeaveTypeId == t.Id)?.Balance ?? 0;
                    var pend = pending.SingleOrDefault(p => p.EmployeeId == e.Id && p.LeaveTypeId == t.Id)?.Days ?? 0;
                    var took = taken.SingleOrDefault(p => p.EmployeeId == e.Id && p.LeaveTypeId == t.Id)?.Days ?? 0;
                    return new LeaveBalanceDto(t.Id, t.Code, t.Name, t.NeedsBalance, bal, pend, bal - pend, took);
                }).ToList()))
            .ToList();
    }

    /// <summary>The balance row locked FOR UPDATE (created at 0 if there isn't one), so two approvals or adjustments for the
    /// same person and leave type happen one after the other.</summary>
    internal async Task<decimal> LockBalanceAsync(Property property, Guid employeeId, Guid leaveTypeId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO leave_balances (id, organization_id, property_id, employee_id, leave_type_id, balance)
             VALUES ({Guid.CreateVersion7()}, {property.OrganizationId}, {property.Id}, {employeeId}, {leaveTypeId}, 0)
             ON CONFLICT (employee_id, leave_type_id) DO NOTHING
             """, ct);
        return (await db.Database.SqlQuery<decimal>(
            $"SELECT balance AS \"Value\" FROM leave_balances WHERE employee_id = {employeeId} AND leave_type_id = {leaveTypeId} FOR UPDATE").ToListAsync(ct)).Single();
    }

    /// <summary>Casual, Sick, Earned and Leave without pay, the first time a property's leave is opened.</summary>
    internal async Task EnsureDefaultsAsync(Property property, CancellationToken ct)
    {
        if (await db.LeaveTypes.AnyAsync(t => t.PropertyId == property.Id, ct)) return;
        var outer = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"leave-defaults:" + property.Id}))", ct);
            if (await db.LeaveTypes.AnyAsync(t => t.PropertyId == property.Id, ct)) return;
            (string Code, string Name, bool Paid, bool Balance, bool Half, decimal Days, string Accrual, decimal? Max)[] defaults =
            [
                ("CL", "Casual leave", true, true, true, 12, LeaveAccruals.Monthly, 12),
                ("SL", "Sick leave", true, true, true, 6, LeaveAccruals.Yearly, 12),
                ("EL", "Earned leave", true, true, false, 15, LeaveAccruals.Monthly, 45),
                ("LOP", "Leave without pay", false, false, true, 0, LeaveAccruals.None, null),
            ];
            var i = 0;
            var policies = new List<LeavePolicy>();
            foreach (var d in defaults)
            {
                var t = new LeaveType
                {
                    OrganizationId = property.OrganizationId,
                    PropertyId = property.Id,
                    Code = d.Code,
                    Name = d.Name,
                    IsPaid = d.Paid,
                    NeedsBalance = d.Balance,
                    AllowHalfDay = d.Half,
                    SortOrder = (++i) * 10,
                };
                db.LeaveTypes.Add(t);
                policies.Add(new LeavePolicy
                {
                    OrganizationId = property.OrganizationId,
                    PropertyId = property.Id,
                    LeaveTypeId = t.Id,
                    DaysPerYear = d.Days,
                    Accrual = d.Accrual,
                    MaxBalance = d.Max,
                });
            }
            // The model has no navigations, so EF doesn't order the inserts: the types go in first.
            await db.SaveChangesAsync(ct);
            db.LeavePolicies.AddRange(policies);
            await db.SaveChangesAsync(ct);
            if (outer is not null) await outer.CommitAsync(ct);
        }
        finally
        {
            if (outer is not null) await outer.DisposeAsync();
        }
    }

    internal static (int Year, int Month) ParseMonth(string? s, string field)
    {
        if (s is not null && DateOnly.TryParseExact(s.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year >= 2000)
            return (d.Year, d.Month);
        throw ReservationService.Field(field, "Choose the month, e.g. 2026-11.");
    }

    internal static string MonthName(int year, int month) => new DateOnly(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private async Task<LeaveLedgerDto> LedgerDtoAsync(Property property, Employee emp, CancellationToken ct)
    {
        var set = (await BalanceSetsAsync(property, [emp], ct))[0];
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == property.Id).ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var entries = await db.LeaveLedger.AsNoTracking().Where(l => l.EmployeeId == emp.Id).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToListAsync(ct);
        var reqIds = entries.Select(e => e.LeaveRequestId).OfType<Guid>().Distinct().ToList();
        var codes = await db.LeaveRequests.AsNoTracking().Where(q => reqIds.Contains(q.Id)).ToDictionaryAsync(q => q.Id, q => q.Code, ct);
        var users = await UserNamesAsync(db, entries.Select(e => e.CreatedBy), ct);
        var running = new Dictionary<Guid, decimal>();
        var dtos = entries.Select(e =>
        {
            running[e.LeaveTypeId] = running.GetValueOrDefault(e.LeaveTypeId) + e.Days;
            return new LeaveLedgerEntryDto(e.Id, types.GetValueOrDefault(e.LeaveTypeId, "?"), e.EntryType, e.Days, running[e.LeaveTypeId], e.EntryDate,
                e.Period, e.LeaveRequestId is { } q ? codes.GetValueOrDefault(q) : null, e.Reason, e.CreatedAt,
                e.CreatedBy is { } b ? users.GetValueOrDefault(b) : null);
        }).Reverse().ToList();
        return new LeaveLedgerDto(set, dtos);
    }

    internal static async Task<Dictionary<Guid, string>> UserNamesAsync(IAppDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private void Expect<T>(T entity, uint version, string name) where T : class, Domain.Common.IVersioned
    {
        if (entity.Version != version)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {name} a moment ago. Reload to see their change, then try again.");
        ConcurrencyGuard.ExpectVersion(db, entity, version);
    }
}

/// <summary>
/// Posts leave accruals for one property and month. Monthly policies: a twelfth of the year's days for each person who
/// worked here on the 1st. Yearly policies: the year's days (a part for someone who joined in the year) the first month
/// they are here. Never above the policy's maximum balance. One entry per person, leave type and period (a unique index),
/// so it is safe to run again or from the job and by hand at once.
/// </summary>
public sealed class LeaveAccrual(IAppDbContext db, TimeProvider clock)
{
    public async Task<AccrualResultDto> RunAsync(Property property, int year, int month, Guid? userId, CancellationToken ct)
    {
        var first = new DateOnly(year, month, 1);
        var label = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var outer = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"leave-accrual:" + property.Id}))", ct);
            var types = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.PropertyId == property.Id && t.IsActive && t.NeedsBalance).ToListAsync(ct);
            var policies = await db.LeavePolicies.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PropertyId == property.Id && p.IsActive).ToListAsync(ct);
            var emps = await db.Employees.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.PropertyId == property.Id && e.JoinedOn <= first && (e.LeftOn == null || e.LeftOn >= first)).ToListAsync(ct);
            var ids = emps.Select(e => e.Id).ToList();
            var yearLabel = year.ToString(CultureInfo.InvariantCulture);
            var done = (await db.LeaveLedger.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => ids.Contains(l.EmployeeId) && l.EntryType == LeaveEntryTypes.Accrual && (l.Period == label || l.Period == yearLabel))
                    .Select(l => new { l.EmployeeId, l.LeaveTypeId, l.Period }).ToListAsync(ct))
                .Select(x => (x.EmployeeId, x.LeaveTypeId, x.Period!)).ToHashSet();
            var balances = await db.LeaveBalances.IgnoreQueryFilters().AsNoTracking().Where(b => ids.Contains(b.EmployeeId))
                .ToDictionaryAsync(b => (b.EmployeeId, b.LeaveTypeId), b => b.Balance, ct);

            int posted = 0, already = 0;
            decimal total = 0;
            var notes = new List<string>();
            foreach (var e in emps.OrderBy(e => e.Code))
                foreach (var t in types.OrderBy(t => t.SortOrder))
                {
                    var policy = policies.SingleOrDefault(p => p.LeaveTypeId == t.Id && p.EmploymentType == e.EmploymentType)
                                 ?? policies.SingleOrDefault(p => p.LeaveTypeId == t.Id && p.EmploymentType == null);
                    if (policy is null || policy.Accrual == LeaveAccruals.None || policy.DaysPerYear == 0) continue;
                    var period = policy.Accrual == LeaveAccruals.Monthly ? label : yearLabel;
                    if (done.Contains((e.Id, t.Id, period))) { already++; continue; }
                    var amount = policy.Accrual == LeaveAccruals.Monthly ? LeaveMath.Monthly(policy.DaysPerYear) : LeaveMath.Yearly(policy.DaysPerYear, year, e.JoinedOn);
                    var balance = balances.GetValueOrDefault((e.Id, t.Id));
                    var capped = LeaveMath.Capped(amount, balance, policy.MaxBalance);
                    if (capped < amount)
                        notes.Add($"{e.FullName}: {t.Code} is at its maximum of {LeaveMath.Days(policy.MaxBalance!.Value)}, so {LeaveMath.D(capped)} of {LeaveMath.D(amount)} was added.");
                    db.LeaveLedger.Add(new LeaveLedgerEntry
                    {
                        OrganizationId = property.OrganizationId,
                        PropertyId = property.Id,
                        EmployeeId = e.Id,
                        LeaveTypeId = t.Id,
                        EntryType = LeaveEntryTypes.Accrual,
                        Days = capped,
                        EntryDate = first,
                        Period = period,
                        CreatedAt = clock.GetUtcNow(),
                        CreatedBy = userId,
                    });
                    balances[(e.Id, t.Id)] = balance + capped;
                    posted++;
                    total += capped;
                }
            await db.SaveChangesAsync(ct);
            if (outer is not null) await outer.CommitAsync(ct);
            return new AccrualResultDto(label, posted, already, total, notes);
        }
        finally
        {
            if (outer is not null) await outer.DisposeAsync();
        }
    }
}
