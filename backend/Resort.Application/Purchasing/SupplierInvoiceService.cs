using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Payments;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Inventory;
using Resort.Domain.Payments;
using Resort.Domain.Purchasing;
using Resort.Domain.Rates;
using Resort.Domain.Tax;

namespace Resort.Application.Purchasing;

/// <summary>
/// Supplier invoices and payments (Sprint 16, brief §38).
/// <list type="bullet">
/// <item>A supplier's bill is recorded against an order, line by line, for no more than has come in and not been billed
/// (goods received before they are billed). The supplier's price may differ from the order's; the line says so.</item>
/// <item>GST is the input tax: CGST + SGST when the supplier is in the property's state, IGST otherwise; an unregistered
/// supplier's bill carries none. It can be claimed (ItcEligible) when the property was a regular GST registrant on the
/// invoice date. Once saved, a bill never changes; only what has been paid on it moves.</item>
/// <item>A payment is no more than the balance. Cash comes out of the open cash drawer (a PAY_OUT movement), and only what
/// the drawer should hold can be paid from it.</item>
/// </list>
/// </summary>
public sealed class SupplierInvoiceService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, CashDrawerService drawers)
{
    public async Task<SupplierInvoiceListDto> ListAsync(Guid propertyId, string? status, Guid? supplierId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct)) throw new NotFoundAppException("Property", propertyId);
        if (from is { } f && to is { } t && t < f) throw ReservationService.Field("to", "The end date is before the start date.");
        var q = db.SupplierInvoices.AsNoTracking().Include(i => i.Items).Where(i => i.PropertyId == propertyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToUpperInvariant();
            q = s == "OPEN" ? q.Where(i => i.Status != SupplierInvoiceStatuses.Paid) : q.Where(i => i.Status == s);
        }
        if (supplierId is { } sid) q = q.Where(i => i.SupplierId == sid);
        if (from is { } a) q = q.Where(i => i.InvoiceDate >= a);
        if (to is { } b) q = q.Where(i => i.InvoiceDate <= b);
        var list = await DtosAsync(await q.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.CreatedAt).Take(300).ToListAsync(ct), ct);
        return new SupplierInvoiceListDto(list, list.Sum(i => i.Balance), list.Where(i => i.Overdue).Sum(i => i.Balance),
            list.Where(i => i.ItcEligible).Sum(i => i.InputTax));
    }

    internal async Task<List<SupplierInvoiceDto>> ForOrderAsync(Guid orderId, CancellationToken ct) =>
        await DtosAsync(await db.SupplierInvoices.AsNoTracking().Include(i => i.Items).Where(i => i.OrderId == orderId).OrderBy(i => i.CreatedAt).ToListAsync(ct), ct);

    /// <summary>
    /// Records the supplier's bill against an order (supplier_invoice.manage; audited). Locks the order, so two people
    /// can't bill the same delivery twice.
    /// </summary>
    public async Task<SupplierInvoiceDto> RecordAsync(Guid propertyId, Guid orderId, RecordSupplierInvoiceRequest r, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
        if (r.Lines.Select(l => l.OrderItemId).Distinct().Count() != r.Lines.Count)
            throw ReservationService.Field(nameof(r.Lines), "A line is billed twice. Enter each line once.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM purchase_orders WHERE id = {orderId} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        var order = await db.PurchaseOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == orderId && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That purchase order doesn't exist at this property. Reload the list.");
        if (!PurchaseOrderStatuses.Billable.Contains(order.Status))
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", order.Status == PurchaseOrderStatuses.Cancelled
                ? $"{order.Number} was cancelled, so it can't be billed."
                : $"Nothing has come in on {order.Number} yet. Record the goods received first, then the supplier's bill for them.");
        var supplier = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == order.SupplierId, ct);
        var number = r.SupplierNumber.Trim();
        var recorded = await db.SupplierInvoices.AsNoTracking().Where(i => i.SupplierId == supplier.Id)
            .Select(i => new { i.SupplierNumber, i.Number }).ToListAsync(ct);
        if (recorded.FirstOrDefault(i => string.Equals(i.SupplierNumber.Trim(), number, StringComparison.OrdinalIgnoreCase)) is { } already)
            throw ReservationService.Field(nameof(r.SupplierNumber),
                $"{supplier.Name}'s bill {already.SupplierNumber} is already recorded as {already.Number}. A bill is recorded once.");
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.InvoiceDate > today)
            throw ReservationService.Field(nameof(r.InvoiceDate), $"{QuoteEngine.Day(r.InvoiceDate)} is in the future. Enter the date printed on the bill.");
        if (r.InvoiceDate < order.OrderDate)
            throw ReservationService.Field(nameof(r.InvoiceDate), $"{order.Number} was raised on {QuoteEngine.Day(order.OrderDate)}, so a bill for it can't be dated before that.");
        var due = r.DueDate ?? r.InvoiceDate.AddDays(supplier.PaymentTermsDays);
        if (due < r.InvoiceDate)
            throw ReservationService.Field(nameof(r.DueDate), "The due date can't be before the bill's date.");

        var registration = await db.GstRegistrations.AsNoTracking()
            .Where(x => x.PropertyId == propertyId && x.EffectiveFrom <= r.InvoiceDate && (x.EffectiveTo == null || x.EffectiveTo >= r.InvoiceDate))
            .FirstOrDefaultAsync(ct);
        var buyerState = registration?.StateCode ?? property.GstStateCode;
        var intra = supplier.StateCode == (buyerState ?? supplier.StateCode);
        var items = await db.InventoryItems.AsNoTracking().Where(i => order.Items.Select(x => x.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        var now = clock.GetUtcNow();
        var invoice = new SupplierInvoice
        {
            OrganizationId = order.OrganizationId,
            PropertyId = propertyId,
            Number = await PurchasingService.NextNumberAsync(db, propertyId, "supplier_invoice", "SI", ct),
            SupplierId = supplier.Id,
            OrderId = order.Id,
            SupplierNumber = number,
            InvoiceDate = r.InvoiceDate,
            DueDate = due,
            SupplierGstin = supplier.Gstin,
            SupplierStateCode = supplier.StateCode,
            BuyerStateCode = buyerState,
            IntraState = intra,
            ItcEligible = supplier.Gstin is not null && registration?.RegistrationType == RegistrationTypes.Regular,
            Note = ReservationService.Clean(r.Note),
        };
        for (var n = 0; n < r.Lines.Count; n++)
        {
            var x = r.Lines[n];
            var line = order.Items.SingleOrDefault(i => i.Id == x.OrderItemId)
                       ?? throw ReservationService.Field($"Lines[{n}].OrderItemId", $"That line isn't on {order.Number}. Reload the order and enter the bill again.");
            var item = items[line.ItemId];
            var unit = units[item.UnitId];
            var toBill = line.ReceivedQuantity - line.BilledQuantity;
            if (x.Quantity > toBill)
                throw ReservationService.Field($"Lines[{n}].Quantity", toBill == 0
                    ? $"All of the {item.Name} received on {order.Number} ({InventoryMath.Quantity(line.ReceivedQuantity, unit)}) is already billed."
                    : $"Only {InventoryMath.Quantity(toBill, unit)} of {item.Name} has come in and not been billed on {order.Number}. " +
                      "Bill what was received; record the goods received first if more came in.");
            if (supplier.Gstin is null && x.GstRate != 0)
                throw ReservationService.Field($"Lines[{n}].GstRate", $"{supplier.Name} has no GSTIN, so their bill can't carry GST. Enter 0 %, or add their GSTIN in Suppliers first.");
            var taxable = PurchaseMath.Taxable(x.Quantity, x.UnitPrice);
            var (cgst, sgst, igst) = PurchaseMath.Tax(taxable, x.GstRate, intra);
            invoice.Items.Add(new SupplierInvoiceItem
            {
                OrganizationId = invoice.OrganizationId,
                InvoiceId = invoice.Id,
                OrderItemId = line.Id,
                ItemId = item.Id,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                GstRate = x.GstRate,
                TaxableValue = taxable,
                Cgst = cgst,
                Sgst = sgst,
                Igst = igst,
                Total = taxable + cgst + sgst + igst,
                CreatedAt = now,
            });
            line.BilledQuantity += x.Quantity;
        }
        invoice.TaxableValue = invoice.Items.Sum(i => i.TaxableValue);
        invoice.Cgst = invoice.Items.Sum(i => i.Cgst);
        invoice.Sgst = invoice.Items.Sum(i => i.Sgst);
        invoice.Igst = invoice.Items.Sum(i => i.Igst);
        invoice.TaxTotal = invoice.Cgst + invoice.Sgst + invoice.Igst;
        invoice.Total = invoice.TaxableValue + invoice.TaxTotal;
        invoice.Status = PurchaseMath.InvoiceStatus(invoice.Total, 0m);
        db.SupplierInvoices.Add(invoice);
        audit.Reason = $"{invoice.Number}: {supplier.Name}'s bill {number} of {QuoteEngine.Day(r.InvoiceDate)} on {order.Number}, {Money.Rupees(invoice.Total)} " +
                       $"({Money.Rupees(invoice.TaxTotal)} GST{(invoice.ItcEligible ? ", input tax claimable" : "")}), due {QuoteEngine.Day(due)}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync([invoice], ct)).Single();
    }

    /// <summary>
    /// Pays (part of) a supplier invoice (supplier_invoice.manage; audited; Idempotency-Key optional). Locks the invoice: two
    /// payments at once can't pay more than the balance.
    /// </summary>
    public async Task<(SupplierInvoiceDto Invoice, bool Replayed)> PayAsync(Guid propertyId, Guid id, string? idempotencyKey, PaySupplierRequest r,
        CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/purchasing/invoices/{id}/payments";
        var key = idempotencyKey is null ? null : Idempotency.RequireKey(idempotencyKey);
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
        var method = r.Method.Trim().ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (key is not null && await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "supplier payment", ct) is { } stored)
            return (JsonSerializer.Deserialize<SupplierInvoiceDto>(stored, Idempotency.Json)!, true);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM supplier_invoices WHERE id = {id} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        var invoice = await db.SupplierInvoices.SingleOrDefaultAsync(i => i.Id == id && i.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("That supplier invoice doesn't exist at this property. Reload the list.");
        if (r.Version is { } v && invoice.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT",
                $"Someone else changed {invoice.Number} a moment ago (perhaps a payment). Reload it to see the balance, then pay again.");
        if (r.Version is { } ok) ConcurrencyGuard.ExpectVersion(db, invoice, ok);
        var supplier = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == invoice.SupplierId, ct);
        var balance = invoice.Total - invoice.AmountPaid;
        if (balance <= 0) throw new BusinessRuleAppException("INVOICE_PAID", $"{invoice.Number} ({supplier.Name}'s bill {invoice.SupplierNumber}) is already paid in full.");
        if (r.Amount > balance)
            throw ReservationService.Field(nameof(r.Amount), $"{invoice.Number} has {Money.Rupees(balance)} left to pay, so {Money.Rupees(r.Amount)} is too much.");
        var today = property.LocalDate(clock.GetUtcNow());
        var on = r.PaidOn ?? today;
        if (on > today) throw ReservationService.Field(nameof(r.PaidOn), $"{QuoteEngine.Day(on)} is in the future. Record a payment once it is made.");
        if (on < invoice.InvoiceDate)
            throw ReservationService.Field(nameof(r.PaidOn), $"The bill is dated {QuoteEngine.Day(invoice.InvoiceDate)}; a payment against it can't be earlier.");

        var payment = new SupplierPayment
        {
            OrganizationId = invoice.OrganizationId,
            PropertyId = propertyId,
            Number = await PurchasingService.NextNumberAsync(db, propertyId, "supplier_payment", "SP", ct),
            SupplierId = supplier.Id,
            InvoiceId = invoice.Id,
            PaidOn = on,
            Amount = r.Amount,
            Method = method,
            Reference = ReservationService.Clean(r.Reference),
            Note = ReservationService.Clean(r.Note),
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
        };
        CashDrawerSession? session = null;
        if (method == SupplierPaymentMethods.Cash)
        {
            session = await drawers.RequireOpenSessionAsync(property, nameof(r.Method), "a supplier payment", ct);
            var cash = await drawers.ExpectedAsync(session.Id, ct);
            if (cash < r.Amount)
                throw ReservationService.Field(nameof(r.Amount),
                    $"The cash drawer should hold {Money.Rupees(cash)}, so {Money.Rupees(r.Amount)} can't be paid from it. Pay part in cash, or pay by bank transfer, UPI or cheque.");
            payment.CashDrawerSessionId = session.Id;
        }
        db.SupplierPayments.Add(payment);
        invoice.AmountPaid += r.Amount;
        invoice.Status = PurchaseMath.InvoiceStatus(invoice.Total, invoice.AmountPaid);
        audit.Reason = $"{payment.Number}: paid {supplier.Name} {Money.Rupees(r.Amount)} by {SupplierPaymentMethods.Words(method)}" +
                       (payment.Reference is null ? "" : $" ({payment.Reference})") + $" on {invoice.Number}, {Money.Rupees(invoice.Total - invoice.AmountPaid)} left";
        await db.SaveChangesAsync(ct);   // the payment first: the drawer movement points at it
        if (session is not null)
        {
            drawers.AddMovement(session, DrawerMovementTypes.PayOut, -r.Amount, $"{payment.Number} · {supplier.Name} · {invoice.SupplierNumber}", null, null,
                supplierPaymentId: payment.Id);
        }
        await db.SaveChangesAsync(ct);
        var dto = (await DtosAsync([invoice], ct)).Single();
        if (key is not null) await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, payment.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<List<SupplierInvoiceDto>> DtosAsync(IReadOnlyList<SupplierInvoice> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var propertyId = list[0].PropertyId;
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var ids = list.Select(i => i.Id).ToList();
        var supplierIds = list.Select(i => i.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var orderIds = list.Select(i => i.OrderId).Distinct().ToList();
        var orders = await db.PurchaseOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var lineIds = list.SelectMany(i => i.Items.Select(x => x.OrderItemId)).ToList();
        var orderLines = await db.PurchaseOrderItems.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var orderPrices = orderLines.ToDictionary(x => x.Key, x => x.Value.UnitPrice);
        var itemIds = list.SelectMany(i => i.Items.Select(x => x.ItemId)).Distinct().ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        var payments = await db.SupplierPayments.AsNoTracking().Where(p => ids.Contains(p.InvoiceId)).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var userIds = list.Select(i => i.CreatedBy).Concat(payments.Select(p => p.CreatedBy)).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return list.Select(i =>
        {
            var balance = i.Total - i.AmountPaid;
            return new SupplierInvoiceDto(i.Id, i.Number, i.SupplierId, suppliers.GetValueOrDefault(i.SupplierId, ""), i.SupplierGstin, i.OrderId,
                orders.GetValueOrDefault(i.OrderId, ""), i.SupplierNumber, i.InvoiceDate, i.DueDate, i.IntraState, i.ItcEligible, i.TaxableValue, i.Cgst, i.Sgst,
                i.Igst, i.TaxTotal, i.Total, i.AmountPaid, balance, i.Status, balance > 0 && i.DueDate < today, i.Note,
                i.Items.OrderBy(x => orderLines.TryGetValue(x.OrderItemId, out var l) ? l.SortOrder : 0).Select(x =>
                {
                    var orderPrice = orderPrices.GetValueOrDefault(x.OrderItemId);
                    return new SupplierInvoiceLineDto(x.OrderItemId, items[x.ItemId].Name, units[items[x.ItemId].UnitId], x.Quantity, x.UnitPrice, orderPrice,
                        x.UnitPrice != orderPrice, x.GstRate, x.TaxableValue, x.Cgst, x.Sgst, x.Igst, x.Total);
                }).ToList(),
                payments.Where(p => p.InvoiceId == i.Id).Select(p => new SupplierPaymentDto(p.Id, p.Number, p.PaidOn, p.Amount, p.Method, p.Reference, p.Note,
                    p.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, p.CreatedAt)).ToList(),
                i.CreatedBy is { } c ? names.GetValueOrDefault(c) : null, i.CreatedAt, i.Version);
        }).ToList();
    }
}
