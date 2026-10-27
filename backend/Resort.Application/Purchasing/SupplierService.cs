using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Purchasing;
using Resort.Domain.Tax;

namespace Resort.Application.Purchasing;

/// <summary>
/// Suppliers (Sprint 16, brief §38): who a property buys from, with their GSTIN (checked, check digit included) and the
/// state they supply from, which decides CGST + SGST or IGST on their bills. An unregistered supplier has no GSTIN and
/// charges no GST.
/// </summary>
public sealed class SupplierService(IAppDbContext db, ICurrentUser caller, TimeProvider clock)
{
    public async Task<IReadOnlyList<SupplierDto>> ListAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
        var today = property.LocalDate(clock.GetUtcNow());
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.PropertyId == propertyId).OrderBy(s => s.Name).ToListAsync(ct);
        var owed = await db.SupplierInvoices.AsNoTracking().Where(i => i.PropertyId == propertyId && i.Status != SupplierInvoiceStatuses.Paid)
            .Select(i => new { i.SupplierId, Balance = i.Total - i.AmountPaid, i.DueDate }).ToListAsync(ct);
        return suppliers.Select(s => ToDto(s, owed.Where(o => o.SupplierId == s.Id).Sum(o => o.Balance),
            owed.Where(o => o.SupplierId == s.Id && o.DueDate < today).Sum(o => o.Balance))).ToList();
    }

    public async Task<SupplierDto> SaveAsync(Guid propertyId, Guid? id, SaveSupplierRequest r, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct)) throw new NotFoundAppException("Property", propertyId);
        var code = r.Code.Trim().ToUpperInvariant();
        var gstin = string.IsNullOrWhiteSpace(r.Gstin) ? null : Gstin.Normalize(r.Gstin);
        string state;
        if (gstin is not null)
        {
            if (Gstin.Problem(gstin) is { } problem) throw ReservationService.Field(nameof(r.Gstin), problem);
            state = gstin[..2];
            if (!string.IsNullOrWhiteSpace(r.StateCode) && r.StateCode.Trim() != state)
                throw ReservationService.Field(nameof(r.StateCode),
                    $"The GSTIN is registered in {GstStates.Words(state)}, not {GstStates.Words(r.StateCode.Trim())}. Leave the state to the GSTIN or check the GSTIN.");
        }
        else
        {
            state = r.StateCode?.Trim() ?? "";
            if (!GstStates.Names.ContainsKey(state))
                throw ReservationService.Field(nameof(r.StateCode),
                    "Choose the state the supplier supplies from. Without a GSTIN it decides nothing on tax (they charge no GST) but is kept for the records.");
        }
        if (await db.Suppliers.AnyAsync(s => s.PropertyId == propertyId && s.Code == code && s.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another supplier already has the code {code}.");
        if (gstin is not null && await db.Suppliers.AsNoTracking().Where(s => s.PropertyId == propertyId && s.Gstin == gstin && s.Id != id)
                .Select(s => s.Name).FirstOrDefaultAsync(ct) is { } other)
            throw ReservationService.Field(nameof(r.Gstin), $"{other} already has the GSTIN {gstin}. A GSTIN belongs to one supplier.");

        Supplier supplier;
        if (id is { } sid)
        {
            supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == sid && s.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That supplier doesn't exist at this property. Reload the page.");
            if (r.Version is { } v && supplier.Version != v)
                throw new ConflictAppException("CONCURRENCY_CONFLICT",
                    $"Someone else changed {supplier.Name} a moment ago. Reload to see their change, then make yours again.");
            // A changed GSTIN applies to new bills only: recorded invoices keep the GSTIN they were billed under.
            if (r.Version is { } ok) ConcurrencyGuard.ExpectVersion(db, supplier, ok);
        }
        else
        {
            supplier = new Supplier { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.Suppliers.Add(supplier);
        }
        supplier.Code = code;
        supplier.Name = r.Name.Trim();
        supplier.Gstin = gstin;
        supplier.StateCode = state;
        supplier.ContactName = ReservationService.Clean(r.ContactName);
        supplier.Phone = ReservationService.Clean(r.Phone);
        supplier.Email = ReservationService.Clean(r.Email)?.ToLowerInvariant();
        supplier.Address = ReservationService.Clean(r.Address);
        supplier.PaymentTermsDays = r.PaymentTermsDays;
        supplier.Note = ReservationService.Clean(r.Note);
        supplier.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await ListAsync(propertyId, ct)).Single(s => s.Id == supplier.Id);
    }

    internal static SupplierDto ToDto(Supplier s, decimal outstanding, decimal overdue) =>
        new(s.Id, s.Code, s.Name, s.Gstin, s.StateCode, GstStates.Words(s.StateCode), s.ContactName, s.Phone, s.Email, s.Address, s.PaymentTermsDays,
            s.Note, s.IsActive, outstanding, overdue, s.Version);
}
