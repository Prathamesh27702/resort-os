using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Rates;

namespace Resort.Application.Employees;

/// <summary>
/// Employee pay (Sprint 17): a separate table behind its own endpoints, readable only with employee_salary.read (a
/// MANAGER or GENERAL_MANAGER can see the profile and the roster but never the pay). Entries are never changed: a raise or
/// a correction is a new entry; the latest entry on or before a date is the pay for that date, and for one date the
/// latest recorded wins. Amounts are masked in the audit trail.
/// </summary>
public sealed class CompensationService(IAppDbContext db, TimeProvider clock, EmployeeService employees)
{
    public async Task<EmployeeCompensationDto> GetAsync(Guid propertyId, Guid employeeId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        var rows = await db.EmployeeCompensation.AsNoTracking().Where(c => c.EmployeeId == emp.Id).ToListAsync(ct);
        var ordered = rows.OrderByDescending(c => c.EffectiveFrom).ThenByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList();
        var today = property.LocalDate(clock.GetUtcNow());
        var current = ordered.FirstOrDefault(c => c.EffectiveFrom <= today);
        var names = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => rows.Select(r => r.CreatedBy).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var history = ordered.Select(c => new CompensationDto(c.Id, c.EffectiveFrom, c.PayBasis, c.Basic, c.Allowances, c.Basic + c.Allowances,
            c.PfApplicable, c.EsiApplicable, c.Note, c.Id == current?.Id,
            ordered.Any(o => o.EffectiveFrom == c.EffectiveFrom && (o.CreatedAt > c.CreatedAt || (o.CreatedAt == c.CreatedAt && o.Id.CompareTo(c.Id) > 0))),
            c.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, c.CreatedAt)).ToList();
        return new EmployeeCompensationDto(emp.Id, emp.FullName, history.FirstOrDefault(h => h.IsCurrent), history);
    }

    /// <summary>Records pay from a date (employee_salary.read and employee.update; audited with the amounts masked).</summary>
    public async Task<EmployeeCompensationDto> RecordAsync(Guid propertyId, Guid employeeId, RecordCompensationRequest r, CancellationToken ct)
    {
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        if (r.EffectiveFrom < emp.JoinedOn)
            throw ReservationService.Field(nameof(r.EffectiveFrom), $"{emp.FullName} joined on {QuoteEngine.Day(emp.JoinedOn)}; pay can start on that day or later.");
        if (emp.LeftOn is { } left && r.EffectiveFrom > left)
            throw ReservationService.Field(nameof(r.EffectiveFrom), $"{emp.FullName} left on {QuoteEngine.Day(left)}; pay can't start after that.");
        db.EmployeeCompensation.Add(new EmployeeCompensation
        {
            OrganizationId = emp.OrganizationId,
            EmployeeId = emp.Id,
            EffectiveFrom = r.EffectiveFrom,
            PayBasis = r.PayBasis.Trim().ToUpperInvariant(),
            Basic = r.Basic,
            Allowances = r.Allowances,
            PfApplicable = r.PfApplicable,
            EsiApplicable = r.EsiApplicable,
            Note = ReservationService.Clean(r.Note),
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(propertyId, employeeId, ct);
    }
}
