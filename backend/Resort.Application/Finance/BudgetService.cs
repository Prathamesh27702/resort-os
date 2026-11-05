using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Finance;
using Resort.Domain.Organizations;

namespace Resort.Application.Finance;

/// <summary>
/// Budgets and budget against actual (Sprint 22, brief §41). A property has one budget per financial year (April to March):
/// what each department plans to spend in each month, typed month by month or as a yearly amount spread evenly, or copied
/// from last year with an increase. The report adds up approved expenses (their cost amount, see
/// <see cref="ExpenseService"/>) by department, from April to the month chosen, and shows the variance and a simple
/// run-rate forecast for the year. Expenses still waiting for approval are shown beside the actuals, never in them.
/// </summary>
public sealed class BudgetService(IAppDbContext db, AuditContext audit, TimeProvider clock, ExpenseService expenses)
{
    public async Task<IReadOnlyList<BudgetSummaryDto>> ListAsync(Guid propertyId, CancellationToken ct)
    {
        await expenses.RequirePropertyAsync(propertyId, ct);
        return (await db.Budgets.AsNoTracking().Where(b => b.PropertyId == propertyId).OrderByDescending(b => b.YearStart).ToListAsync(ct))
            .Select(b => new BudgetSummaryDto(b.Id, b.YearStart.Year, FinanceMath.YearLabel(b.YearStart), b.Name, b.Total)).ToList();
    }

    public async Task<BudgetDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await expenses.RequirePropertyAsync(propertyId, ct);
        var b = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That budget doesn't exist at this property. Reload the page.");
        return await DtoAsync(property, b, ct);
    }

    /// <summary>Starts the budget for a financial year, empty or copied from another year's with an increase.</summary>
    public async Task<BudgetDto> CreateAsync(Guid propertyId, CreateBudgetRequest r, CancellationToken ct)
    {
        var property = await expenses.RequirePropertyAsync(propertyId, ct);
        await expenses.EnsureDefaultsAsync(property, ct);
        var start = new DateOnly(r.Year, 4, 1);
        var label = FinanceMath.YearLabel(start);
        var thisYear = FinanceMath.YearStart(property.LocalDate(clock.GetUtcNow()));
        if (start < thisYear.AddYears(-1) || start > thisYear.AddYears(1))
            throw ReservationService.Field(nameof(r.Year),
                $"A budget can be made for last year, this year or next year ({FinanceMath.YearLabel(thisYear.AddYears(-1))} to {FinanceMath.YearLabel(thisYear.AddYears(1))}).");
        if (await db.Budgets.AsNoTracking().AnyAsync(b => b.PropertyId == propertyId && b.YearStart == start, ct))
            throw ReservationService.Field(nameof(r.Year), $"There is already a budget for {label}. Open it to change it.");
        Budget? source = null;
        if (r.CopyFromId is { } from)
            source = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(b => b.Id == from && b.PropertyId == propertyId, ct)
                     ?? throw ReservationService.Field(nameof(r.CopyFromId), "Choose a budget of this property to copy.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = new Budget
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            YearStart = start,
            Name = ReservationService.Clean(r.Name) ?? $"Budget {label}",
            Note = ReservationService.Clean(r.Note),
        };
        db.Budgets.Add(b);
        // The model has no navigations, so EF doesn't order the inserts: the budget goes in before its lines.
        await db.SaveChangesAsync(ct);
        if (source is not null)
        {
            var factor = 1 + r.IncreasePercent / 100m;
            var lines = await db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == source.Id).ToListAsync(ct);
            foreach (var l in lines)
                db.BudgetLines.Add(new BudgetLine
                {
                    OrganizationId = b.OrganizationId,
                    PropertyId = propertyId,
                    BudgetId = b.Id,
                    CostCentreId = l.CostCentreId,
                    Month = start.AddMonths((l.Month.Year - source.YearStart.Year) * 12 + l.Month.Month - source.YearStart.Month),
                    Amount = decimal.Round(l.Amount * factor, 0, MidpointRounding.AwayFromZero),
                });
            b.Total = db.BudgetLines.Local.Where(l => l.BudgetId == b.Id).Sum(l => l.Amount);
            audit.Reason = $"Budget {label} copied from {FinanceMath.YearLabel(source.YearStart)}" + (r.IncreasePercent != 0 ? $" with {r.IncreasePercent:0.##} % added" : "");
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await DtoAsync(property, b, ct);
    }

    /// <summary>Sets one department's twelve months. The budget's row is locked first, so two people saving at once queue,
    /// and the second is told someone else changed it (Version).</summary>
    public async Task<BudgetDto> SaveRowAsync(Guid propertyId, Guid id, SaveBudgetRowRequest r, CancellationToken ct)
    {
        var property = await expenses.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM budgets WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        var b = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That budget doesn't exist at this property. Reload the page.");
        var label = FinanceMath.YearLabel(b.YearStart);
        if (r.Version is { } v && b.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed the budget {label} a moment ago. Reload to see it, then try again.");
        var centre = await db.CostCentres.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CostCentreId && c.PropertyId == propertyId, ct)
                     ?? throw ReservationService.Field(nameof(r.CostCentreId), "Choose a department of this property.");

        var lines = await db.BudgetLines.Where(l => l.BudgetId == b.Id && l.CostCentreId == centre.Id).ToListAsync(ct);
        var months = FinanceMath.Months(b.YearStart);
        for (var i = 0; i < 12; i++)
        {
            var line = lines.SingleOrDefault(l => l.Month == months[i]);
            if (line is null)
            {
                if (r.Months[i] == 0) continue;
                db.BudgetLines.Add(new BudgetLine
                {
                    OrganizationId = b.OrganizationId,
                    PropertyId = propertyId,
                    BudgetId = b.Id,
                    CostCentreId = centre.Id,
                    Month = months[i],
                    Amount = r.Months[i],
                });
            }
            else line.Amount = r.Months[i];
        }
        var others = await db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == b.Id && l.CostCentreId != centre.Id).SumAsync(l => (decimal?)l.Amount, ct) ?? 0;
        var rowTotal = r.Months.Sum();
        b.Total = others + rowTotal;
        audit.Reason = $"Budget {label}: {centre.Name} set to {Money.Rupees(rowTotal)} for the year";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, b, ct);
    }

    // ------------------------------------------------------------------ the report

    /// <summary>Budget against actual for the financial year starting in April of <paramref name="year"/> (this year when
    /// not given), from April to the end of <paramref name="through"/> (this month, or March for a past year).</summary>
    public async Task<BudgetReportDto> ReportAsync(Guid propertyId, int? year, DateOnly? through, CancellationToken ct)
    {
        var property = await expenses.RequirePropertyAsync(propertyId, ct);
        await expenses.EnsureDefaultsAsync(property, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var start = year is { } y ? new DateOnly(y, 4, 1) : FinanceMath.YearStart(today);
        if (start.Year is < 2000 or > 2100) throw ReservationService.Field("Year", "Choose a financial year, e.g. 2026 for 2026-27.");
        var end = start.AddYears(1).AddDays(-1);
        var months = FinanceMath.Months(start);
        var month = through is { } t ? new DateOnly(t.Year, t.Month, 1)
            : today < start ? start
            : today > end ? months[11]
            : new DateOnly(today.Year, today.Month, 1);
        if (month < start || month > months[11])
            throw ReservationService.Field("Through", $"Choose a month of {FinanceMath.YearLabel(start)} (April {start.Year} to March {start.Year + 1}).");
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var asOf = monthEnd < today ? monthEnd : today;

        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(b => b.PropertyId == propertyId && b.YearStart == start, ct);
        var lines = budget is null ? [] : await db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == budget.Id).ToListAsync(ct);
        // Only approved expenses are actuals; waiting ones are shown beside them.
        var spent = await db.Expenses.AsNoTracking()
            .Where(e => e.PropertyId == propertyId && e.ExpenseDate >= start && e.ExpenseDate <= end
                        && (e.Status == ExpenseStatuses.Approved || e.Status == ExpenseStatuses.Submitted))
            .Select(e => new { e.CostCentreId, e.CategoryId, e.ExpenseDate, e.CostAmount, e.Status }).ToListAsync(ct);
        var actual = spent.Where(e => e.Status == ExpenseStatuses.Approved).ToList();
        var waiting = spent.Where(e => e.Status == ExpenseStatuses.Submitted && e.ExpenseDate <= monthEnd).ToList();
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var categories = await db.ExpenseCategories.AsNoTracking().Where(c => c.PropertyId == propertyId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        BudgetVarianceRowDto Row(Guid id, string code, string name, Func<Guid, bool> mine)
        {
            var l = lines.Where(x => mine(x.CostCentreId)).ToList();
            var a = actual.Where(x => mine(x.CostCentreId)).ToList();
            var w = waiting.Where(x => mine(x.CostCentreId)).ToList();
            var monthBudget = l.Where(x => x.Month == month).Sum(x => x.Amount);
            var monthActual = a.Where(x => x.ExpenseDate >= month && x.ExpenseDate <= monthEnd).Sum(x => x.CostAmount);
            var budgetToDate = l.Where(x => x.Month <= month).Sum(x => x.Amount);
            var actualToDate = a.Where(x => x.ExpenseDate <= monthEnd).Sum(x => x.CostAmount);
            var annual = l.Sum(x => x.Amount);
            var forecast = FinanceMath.Forecast(actualToDate, start, asOf);
            var variance = budgetToDate - actualToDate;
            return new BudgetVarianceRowDto(id, code, name, monthBudget, monthActual, budgetToDate, actualToDate, variance,
                budgetToDate == 0 ? null : decimal.Round(variance * 100 / budgetToDate, 1),
                annual, forecast, forecast is { } f ? annual - f : null, FinanceMath.Standing(budgetToDate, actualToDate, annual, forecast),
                w.Sum(x => x.CostAmount), w.Count,
                a.Where(x => x.ExpenseDate <= monthEnd).GroupBy(x => x.CategoryId)
                    .Select(g => new BudgetCategoryActualDto(g.Key, categories.GetValueOrDefault(g.Key, ""), g.Sum(x => x.CostAmount)))
                    .OrderByDescending(x => x.Actual).ThenBy(x => x.Name).ToList());
        }

        // Every active department, and a switched-off one that still has a plan or spending this year.
        var used = lines.Select(l => l.CostCentreId).Concat(spent.Select(e => e.CostCentreId)).ToHashSet();
        var rows = centres.Where(c => c.IsActive || used.Contains(c.Id)).Select(c => Row(c.Id, c.Code, c.Name, x => x == c.Id)).ToList();
        var total = Row(Guid.Empty, "ALL", "All departments", _ => true);
        var byMonth = months.Select(m => new BudgetMonthDto(m, lines.Where(l => l.Month == m).Sum(l => l.Amount),
            actual.Where(e => e.ExpenseDate >= m && e.ExpenseDate < m.AddMonths(1)).Sum(e => e.CostAmount))).ToList();
        return new BudgetReportDto(start.Year, FinanceMath.YearLabel(start), budget?.Id, month, asOf, rows, total, byMonth);
    }

    private async Task<BudgetDto> DtoAsync(Property property, Budget b, CancellationToken ct)
    {
        var lines = await db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == b.Id).ToListAsync(ct);
        var used = lines.Select(l => l.CostCentreId).ToHashSet();
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == property.Id && (c.IsActive || used.Contains(c.Id)))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var months = FinanceMath.Months(b.YearStart);
        var rows = centres.Select(c =>
        {
            var amounts = months.Select(m => lines.Where(l => l.CostCentreId == c.Id && l.Month == m).Sum(l => l.Amount)).ToList();
            return new BudgetRowDto(c.Id, c.Code, c.Name, amounts, amounts.Sum());
        }).ToList();
        return new BudgetDto(b.Id, b.YearStart.Year, FinanceMath.YearLabel(b.YearStart), b.Name, b.Note, b.Total, months, rows, b.Version);
    }

}
