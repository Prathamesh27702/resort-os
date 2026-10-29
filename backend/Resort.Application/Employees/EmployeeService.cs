using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Purchasing;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;

namespace Resort.Application.Employees;

/// <summary>
/// Employees (Sprint 17, brief §42): departments and their job titles, and employee profiles numbered E00001 … per
/// property, optionally linked to one staff account. Profiles never carry pay (see <see cref="CompensationService"/>).
/// Every change is audited. An employee who leaves keeps their record (LEFT, with the last working day); shifts after
/// that day must come off the roster first.
/// </summary>
public sealed class EmployeeService(IAppDbContext db, ICurrentUser caller, TimeProvider clock)
{
    // ------------------------------------------------------------------ departments and designations

    public async Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
        var designations = await db.Designations.AsNoTracking().Where(d => d.PropertyId == propertyId).OrderBy(d => d.Name).ToListAsync(ct);
        var active = await db.Employees.AsNoTracking().Where(e => e.PropertyId == propertyId && e.Status == EmployeeStatuses.Active)
            .Select(e => new { e.DepartmentId, e.DesignationId }).ToListAsync(ct);
        return departments.Select(d => new DepartmentDto(d.Id, d.Code, d.Name, d.SortOrder, d.IsActive, active.Count(e => e.DepartmentId == d.Id),
            designations.Where(x => x.DepartmentId == d.Id)
                .Select(x => new DesignationDto(x.Id, x.DepartmentId, x.Name, x.IsActive, active.Count(e => e.DesignationId == x.Id), x.Version)).ToList(),
            d.Version)).ToList();
    }

    public async Task<DepartmentDto> SaveDepartmentAsync(Guid propertyId, Guid? id, SaveDepartmentRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        if (await db.Departments.AnyAsync(d => d.PropertyId == propertyId && d.Code == code && d.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another department already has the code {code}.");
        var names = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId && d.Id != id).Select(d => d.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a department called {name}.");

        Department dept;
        if (id is { } did)
        {
            dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == did && d.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That department doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(dept, v, dept.Name);
            if (!r.IsActive && dept.IsActive
                && await db.Employees.CountAsync(e => e.DepartmentId == did && e.Status == EmployeeStatuses.Active, ct) is > 0 and var n)
                throw ReservationService.Field(nameof(r.IsActive),
                    $"{n} active {(n == 1 ? "employee works" : "employees work")} in {dept.Name}. Move them to another department first.");
        }
        else
        {
            dept = new Department { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.Departments.Add(dept);
        }
        dept.Code = code;
        dept.Name = name;
        dept.SortOrder = r.SortOrder;
        dept.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await DepartmentsAsync(propertyId, ct)).Single(d => d.Id == dept.Id);
    }

    public async Task<DepartmentDto> SaveDesignationAsync(Guid propertyId, Guid? id, SaveDesignationRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == r.DepartmentId && d.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.DepartmentId), "Choose a department of this property.");
        var name = r.Name.Trim();
        var names = await db.Designations.AsNoTracking().Where(d => d.DepartmentId == dept.Id && d.Id != id).Select(d => d.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"{dept.Name} already has the job title {name}.");

        Designation des;
        if (id is { } xid)
        {
            des = await db.Designations.SingleOrDefaultAsync(d => d.Id == xid && d.PropertyId == propertyId, ct)
                  ?? throw new NotFoundAppException("That job title doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(des, v, des.Name);
            if (des.DepartmentId != dept.Id && await db.Employees.AnyAsync(e => e.DesignationId == xid, ct))
                throw ReservationService.Field(nameof(r.DepartmentId),
                    $"Employees have the job title {des.Name}, so it stays in its department. Add a new title in {dept.Name} instead.");
        }
        else
        {
            des = new Designation { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.Designations.Add(des);
        }
        des.DepartmentId = dept.Id;
        des.Name = name;
        des.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await DepartmentsAsync(propertyId, ct)).Single(d => d.Id == dept.Id);
    }

    // ------------------------------------------------------------------ employees

    public async Task<IReadOnlyList<EmployeeDto>> ListAsync(Guid propertyId, string? q, Guid? departmentId, string? status, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var query = db.Employees.AsNoTracking().Where(e => e.PropertyId == propertyId);
        if (departmentId is { } d) query = query.Where(e => e.DepartmentId == d);
        var st = string.IsNullOrWhiteSpace(status) ? EmployeeStatuses.Active : status.Trim().ToUpperInvariant();
        if (st != "ALL") query = query.Where(e => e.Status == st);
        var list = await query.OrderBy(e => e.FirstName).ThenBy(e => e.LastName).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            list = list.Where(e => e.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || (e.Phone ?? "").Replace(" ", "", StringComparison.Ordinal).Contains(term.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal))
                .ToList();
        }
        return await DtosAsync(propertyId, list, ct);
    }

    public async Task<EmployeeDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var e = await LoadAsync(propertyId, id, ct);
        return (await DtosAsync(propertyId, [e], ct))[0];
    }

    /// <summary>Creates or edits an employee (employee.create / employee.update; audited).</summary>
    public async Task<EmployeeDto> SaveAsync(Guid propertyId, Guid? id, SaveEmployeeRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == r.DepartmentId && d.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.DepartmentId), "Choose the department they work in.");
        Employee? existing = null;
        if (id is { } eid)
        {
            existing = await db.Employees.SingleOrDefaultAsync(e => e.Id == eid && e.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That employee doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(existing, v, existing.FullName);
        }
        if (!dept.IsActive && existing?.DepartmentId != dept.Id)
            throw ReservationService.Field(nameof(r.DepartmentId), $"{dept.Name} is switched off. Choose another department or switch it on first.");
        if (r.DesignationId is { } desId)
        {
            var des = await db.Designations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == desId && d.PropertyId == propertyId, ct)
                      ?? throw ReservationService.Field(nameof(r.DesignationId), "Choose a job title of this property.");
            if (des.DepartmentId != dept.Id)
                throw ReservationService.Field(nameof(r.DesignationId), $"{des.Name} is a job title in another department. Choose one in {dept.Name}.");
            if (!des.IsActive && existing?.DesignationId != des.Id)
                throw ReservationService.Field(nameof(r.DesignationId), $"The job title {des.Name} is switched off. Choose another.");
        }
        if (r.JoinedOn > today.AddDays(90))
            throw ReservationService.Field(nameof(r.JoinedOn), $"The joining date can be at most 90 days ahead (until {QuoteEngine.Day(today.AddDays(90))}).");
        if (r.DateOfBirth is { } dob)
        {
            var age = r.JoinedOn.Year - dob.Year - (r.JoinedOn.DayOfYear < dob.DayOfYear ? 1 : 0);
            if (age < 18)
                throw ReservationService.Field(nameof(r.DateOfBirth),
                    $"By this date of birth they were {Math.Max(age, 0)} when they joined. Staff must be at least 18 on the joining date; check the date.");
        }
        if (existing is not null && existing.Status == EmployeeStatuses.Left && existing.LeftOn < r.JoinedOn)
            throw ReservationService.Field(nameof(r.JoinedOn), $"{existing.FullName} left on {QuoteEngine.Day(existing.LeftOn!.Value)}; the joining date can't be after that.");
        if (existing is not null && r.JoinedOn > existing.JoinedOn
            && await db.RosterShifts.AsNoTracking().Where(s => s.EmployeeId == existing.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate < r.JoinedOn)
                .OrderBy(s => s.WorkDate).Select(s => (DateOnly?)s.WorkDate).FirstOrDefaultAsync(ct) is { } early)
            throw ReservationService.Field(nameof(r.JoinedOn),
                $"{existing.FullName} is on the roster on {QuoteEngine.Day(early)}, before this joining date. Remove those shifts first or keep the earlier date.");

        if (r.UserId is { } uid)
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uid, ct)
                       ?? throw ReservationService.Field(nameof(r.UserId), "Choose a staff account of this organisation.");
            if (await db.Employees.AsNoTracking().IgnoreQueryFilters().Where(e => e.UserId == uid && e.Id != id)
                    .Select(e => new { e.FirstName, e.LastName, e.Code }).FirstOrDefaultAsync(ct) is { } other)
                throw ReservationService.Field(nameof(r.UserId),
                    $"The account {user.Email} is already linked to {other.FirstName}{(other.LastName is null ? "" : " " + other.LastName)} ({other.Code}). An account belongs to one employee.");
        }

        var emp = existing;
        if (emp is null)
        {
            emp = new Employee
            {
                OrganizationId = caller.RequireOrganizationId(),
                PropertyId = propertyId,
                Code = await NextCodeAsync(propertyId, ct),
            };
            db.Employees.Add(emp);
        }
        emp.FirstName = r.FirstName.Trim();
        emp.LastName = ReservationService.Clean(r.LastName);
        emp.Phone = ReservationService.Clean(r.Phone);
        emp.Email = ReservationService.Clean(r.Email)?.ToLowerInvariant();
        emp.DateOfBirth = r.DateOfBirth;
        emp.DepartmentId = dept.Id;
        emp.DesignationId = r.DesignationId;
        emp.EmploymentType = r.EmploymentType.Trim().ToUpperInvariant();
        emp.JoinedOn = r.JoinedOn;
        emp.WeeklyOff = r.WeeklyOff;
        emp.UserId = r.UserId;
        emp.EmergencyContactName = ReservationService.Clean(r.EmergencyContactName);
        emp.EmergencyContactPhone = ReservationService.Clean(r.EmergencyContactPhone);
        emp.Address = ReservationService.Clean(r.Address);
        emp.Note = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        return await GetAsync(propertyId, emp.Id, ct);
    }

    /// <summary>The employee leaves on LeftOn (their last working day). Refused while they are on the roster after it.</summary>
    public async Task<EmployeeDto> LeaveAsync(Guid propertyId, Guid id, EmployeeLeftRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var emp = await LoadAsync(propertyId, id, ct, track: true);
        if (r.Version is { } v) Expect(emp, v, emp.FullName);
        if (emp.Status == EmployeeStatuses.Left)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{emp.FullName} already left on {QuoteEngine.Day(emp.LeftOn!.Value)}.");
        if (r.LeftOn < emp.JoinedOn)
            throw ReservationService.Field(nameof(r.LeftOn), $"{emp.FullName} joined on {QuoteEngine.Day(emp.JoinedOn)}; the last working day can't be before that.");
        if (r.LeftOn > today.AddDays(90))
            throw ReservationService.Field(nameof(r.LeftOn), $"The last working day can be at most 90 days ahead (until {QuoteEngine.Day(today.AddDays(90))}).");
        var after = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate > r.LeftOn)
            .OrderBy(s => s.WorkDate).Select(s => s.WorkDate).ToListAsync(ct);
        if (after.Count > 0)
            throw ReservationService.Field(nameof(r.LeftOn),
                $"{emp.FullName} is on the roster after {QuoteEngine.Day(r.LeftOn)}: {string.Join(", ", after.Take(5).Select(QuoteEngine.Day))}" +
                $"{(after.Count > 5 ? $" and {after.Count - 5} more" : "")}. Remove those shifts from the roster first.");
        emp.Status = EmployeeStatuses.Left;
        emp.LeftOn = r.LeftOn;
        emp.LeavingReason = r.Reason.Trim();
        await db.SaveChangesAsync(ct);
        return await GetAsync(propertyId, id, ct);
    }

    /// <summary>Staff accounts of the organisation, with the employee each is linked to (for the link picker).</summary>
    public async Task<IReadOnlyList<LinkableAccountDto>> AccountsAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName, u.Email, u.IsActive }).ToListAsync(ct);
        var links = await db.Employees.AsNoTracking().Where(e => e.UserId != null)
            .Select(e => new { e.UserId, e.Id, e.FirstName, e.LastName, e.Code }).ToListAsync(ct);
        return users.Select(u =>
        {
            var l = links.FirstOrDefault(x => x.UserId == u.Id);
            return new LinkableAccountDto(u.Id, u.FullName, u.Email, u.IsActive, l?.Id,
                l is null ? null : $"{l.FirstName}{(l.LastName is null ? "" : " " + l.LastName)} ({l.Code})");
        }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    internal async Task<Employee> LoadAsync(Guid propertyId, Guid id, CancellationToken ct, bool track = false)
    {
        var q = track ? db.Employees : db.Employees.AsNoTracking();
        return await q.SingleOrDefaultAsync(e => e.Id == id && e.PropertyId == propertyId, ct)
               ?? throw new NotFoundAppException("That employee doesn't exist at this property. Reload the page.");
    }

    private void Expect<T>(T entity, uint version, string name) where T : class, Domain.Common.IVersioned
    {
        if (entity.Version != version)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {name} a moment ago. Reload to see their change, then make yours again.");
        ConcurrencyGuard.ExpectVersion(db, entity, version);
    }

    private Task<string> NextCodeAsync(Guid propertyId, CancellationToken ct) => PurchasingService.NextNumberAsync(db, propertyId, "employee", "E", ct);

    private async Task<List<EmployeeDto>> DtosAsync(Guid propertyId, IReadOnlyList<Employee> list, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var desIds = list.Select(e => e.DesignationId).OfType<Guid>().Distinct().ToList();
        var des = await db.Designations.AsNoTracking().Where(d => desIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var userIds = list.Select(e => e.UserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        return list.Select(e => new EmployeeDto(e.Id, e.Code, e.FirstName, e.LastName, e.FullName, e.Phone, e.Email, e.DateOfBirth, e.DepartmentId,
            depts.GetValueOrDefault(e.DepartmentId, "?"), e.DesignationId, e.DesignationId is { } d ? des.GetValueOrDefault(d) : null, e.EmploymentType,
            e.JoinedOn, e.WeeklyOff, e.UserId, e.UserId is { } u ? users.GetValueOrDefault(u) : null, e.EmergencyContactName, e.EmergencyContactPhone,
            e.Address, e.Note, e.Status, e.LeftOn, e.LeavingReason, e.Version)).ToList();
    }
}
