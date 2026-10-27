using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Inventory;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Inventory;
using Resort.Domain.Organizations;
using Resort.Domain.Purchasing;
using Resort.Domain.Rates;
using Resort.Domain.Tax;

namespace Resort.Application.Purchasing;

/// <summary>
/// Purchasing (Sprint 16, brief §38): purchase requests, purchase orders and their approval, and goods received.
/// <list type="bullet">
/// <item>An order is drafted, then submitted. At or under the property's approval limit (purchasing.approval_limit,
/// default ₹10,000 with GST) it is approved on submit; over it, someone with purchase.approve who did not raise it must
/// approve it (or reject it back to draft, with a reason).</item>
/// <item>Goods come in against an approved order, line by line, never more than is still to come. Each line is a RECEIVED
/// movement in the stock ledger at the order's price before GST (which moves the item's average cost), so on hand and
/// the order's received quantities change together, in one transaction, under a lock on the order.</item>
/// <item>Every change is audited with what changed; deliveries are never changed (a mistake is corrected in stock).</item>
/// </list>
/// </summary>
public sealed class PurchasingService(
    IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, StockLedger ledger, SupplierInvoiceService invoices)
{
    public const string ApprovalLimitSetting = "purchasing.approval_limit";

    // ------------------------------------------------------------------ purchase requests

    public async Task<IReadOnlyList<PurchaseRequestDto>> ListRequestsAsync(Guid propertyId, string? status, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var q = db.PurchaseRequests.AsNoTracking().Include(r => r.Items).Where(r => r.PropertyId == propertyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToUpperInvariant();
            q = q.Where(r => r.Status == s);
        }
        return await RequestDtosAsync(await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct), ct);
    }

    /// <summary>A store asks for stock (purchase.create; audited). Quantities in each item's unit.</summary>
    public async Task<PurchaseRequestDto> CreateRequestAsync(Guid propertyId, CreatePurchaseRequestRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var location = await LocationAsync(propertyId, r.LocationId, nameof(r.LocationId), ct);
        if (r.NeededBy is { } by && by < property.LocalDate(clock.GetUtcNow()))
            throw ReservationService.Field(nameof(r.NeededBy), $"{QuoteEngine.Day(by)} has passed. Choose today or a later day, or leave it empty.");
        var items = await ItemsAsync(propertyId, r.Items.Select(i => i.ItemId).ToList(), "Items", ct);
        var request = new PurchaseRequest
        {
            OrganizationId = caller.RequireOrganizationId(),
            PropertyId = propertyId,
            Number = await NextNumberAsync(db, propertyId, "purchase_request", "PR", ct),
            LocationId = location.Id,
            NeededBy = r.NeededBy,
            Note = ReservationService.Clean(r.Note),
        };
        var now = clock.GetUtcNow();
        for (var n = 0; n < r.Items.Count; n++)
            request.Items.Add(new PurchaseRequestItem
            {
                OrganizationId = request.OrganizationId,
                ItemId = r.Items[n].ItemId,
                Quantity = r.Items[n].Quantity,
                Note = ReservationService.Clean(r.Items[n].Note),
                SortOrder = n,
                CreatedAt = now,
            });
        db.PurchaseRequests.Add(request);
        audit.Reason = $"{request.Number} for {location.Name}: " + string.Join(", ", r.Items.Select(i => $"{Qty(i.Quantity, items[i.ItemId])} {items[i.ItemId].Item.Name}"));
        await db.SaveChangesAsync(ct);
        return (await RequestDtosAsync([request], ct)).Single();
    }

    public async Task<PurchaseRequestDto> CancelRequestAsync(Guid propertyId, Guid id, PurchaseReasonRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM purchase_requests WHERE id = {id} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        var request = await db.PurchaseRequests.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("That purchase request doesn't exist at this property. Reload the list.");
        Expect(request, r.Version, request.Number);
        if (request.Status != PurchaseRequestStatuses.Open)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", request.Status == PurchaseRequestStatuses.Ordered
                ? $"{request.Number} is already on a purchase order, so it can't be cancelled. Cancel the order instead."
                : $"{request.Number} is already cancelled.");
        request.Status = PurchaseRequestStatuses.Cancelled;
        request.CancelReason = ReservationService.Clean(r.Reason);
        audit.Reason = $"{request.Number} cancelled — {request.CancelReason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await RequestDtosAsync([request], ct)).Single();
    }

    // ------------------------------------------------------------------ purchase orders

    public async Task<PurchaseOrderListDto> ListOrdersAsync(Guid propertyId, string? status, Guid? supplierId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var limit = await ApprovalLimitAsync(propertyId, ct);
        var q = db.PurchaseOrders.AsNoTracking().Include(o => o.Items).Where(o => o.PropertyId == propertyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToUpperInvariant();
            q = q.Where(o => o.Status == s);
        }
        if (supplierId is { } sid) q = q.Where(o => o.SupplierId == sid);
        var orders = await q.OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.PropertyId == propertyId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var locations = await db.StockLocations.AsNoTracking().Where(l => l.PropertyId == propertyId).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var names = await UserNamesAsync(orders.Select(o => o.CreatedBy), ct);
        var rows = orders.Select(o =>
        {
            var ordered = o.Items.Sum(i => i.TaxableValue);
            var received = o.Items.Sum(i => PurchaseMath.Taxable(i.ReceivedQuantity, i.UnitPrice));
            return new PurchaseOrderSummaryDto(o.Id, o.Number, o.SupplierId, suppliers.GetValueOrDefault(o.SupplierId, ""), locations.GetValueOrDefault(o.LocationId, ""),
                o.OrderDate, o.ExpectedDate, o.Status, o.Total, PurchaseMath.NeedsApproval(o.Total, o.ApprovalLimit ?? limit),
                ordered == 0 ? 0 : Math.Round(received / ordered * 100m, 0), o.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, o.CreatedAt);
        }).ToList();
        var waiting = await db.PurchaseOrders.CountAsync(o => o.PropertyId == propertyId && o.Status == PurchaseOrderStatuses.PendingApproval, ct);
        return new PurchaseOrderListDto(rows, limit, waiting);
    }

    public async Task<PurchaseOrderDto> GetOrderAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That purchase order doesn't exist at this property. Reload the list.");
        return await ToDtoAsync(order, ct);
    }

    /// <summary>
    /// Creates a draft order (optionally from an open request, which becomes ORDERED) or edits a draft. Lines replace the
    /// draft's lines; totals are worked out here, GST per line (CGST + SGST inside the property's state, IGST across).
    /// </summary>
    public async Task<PurchaseOrderDto> SaveOrderAsync(Guid propertyId, Guid? id, SavePurchaseOrderRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == r.SupplierId && s.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.SupplierId), "That supplier doesn't exist at this property. Reload and choose again.");
        if (!supplier.IsActive) throw ReservationService.Field(nameof(r.SupplierId), $"{supplier.Name} is switched off. Switch them on in Suppliers, or choose another supplier.");
        var location = await LocationAsync(propertyId, r.LocationId, nameof(r.LocationId), ct);
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.ExpectedDate is { } ex && ex < today)
            throw ReservationService.Field(nameof(r.ExpectedDate), $"{QuoteEngine.Day(ex)} has passed. Choose today or a later day for the delivery, or leave it empty.");
        var items = await ItemsAsync(propertyId, r.Lines.Select(l => l.ItemId).ToList(), "Lines", ct);
        if (supplier.Gstin is null)
            for (var n = 0; n < r.Lines.Count; n++)
                if (r.Lines[n].GstRate != 0)
                    throw ReservationService.Field($"Lines[{n}].GstRate",
                        $"{supplier.Name} has no GSTIN, so they can't charge GST. Set the GST to 0 %, or add their GSTIN in Suppliers.");
        var intra = supplier.StateCode == (await BuyerStateAsync(property, today, ct) ?? supplier.StateCode);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        PurchaseOrder order;
        PurchaseRequest? request = null;
        if (id is { } oid)
        {
            order = await LockOrderAsync(propertyId, oid, r.Version, ct);
            if (order.Status != PurchaseOrderStatuses.Draft)
                throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION",
                    $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}, so it can't be changed. Only a draft can be edited (a rejected order goes back to draft).");
            db.PurchaseOrderItems.RemoveRange(order.Items);
            order.Items.Clear();
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (r.RequestId is { } rid)
            {
                await db.Database.ExecuteSqlAsync($"SELECT 1 FROM purchase_requests WHERE id = {rid} AND property_id = {propertyId} FOR UPDATE", ct);
                request = await db.PurchaseRequests.SingleOrDefaultAsync(x => x.Id == rid && x.PropertyId == propertyId, ct)
                          ?? throw ReservationService.Field(nameof(r.RequestId), "That purchase request doesn't exist at this property. Reload and choose again.");
                if (request.Status != PurchaseRequestStatuses.Open)
                    throw ReservationService.Field(nameof(r.RequestId), request.Status == PurchaseRequestStatuses.Ordered
                        ? $"{request.Number} is already on {await OrderNumberAsync(request.PurchaseOrderId, ct)}. Open that order instead."
                        : $"{request.Number} was cancelled ({request.CancelReason}).");
            }
            order = new PurchaseOrder
            {
                OrganizationId = caller.RequireOrganizationId(),
                PropertyId = propertyId,
                Number = await NextNumberAsync(db, propertyId, "purchase_order", "PO", ct),
                OrderDate = today,
                RequestId = request?.Id,
            };
            db.PurchaseOrders.Add(order);
        }
        order.SupplierId = supplier.Id;
        order.LocationId = location.Id;
        order.ExpectedDate = r.ExpectedDate;
        order.Note = ReservationService.Clean(r.Note);
        order.IntraState = intra;
        var now = clock.GetUtcNow();
        for (var n = 0; n < r.Lines.Count; n++)
        {
            var l = r.Lines[n];
            var taxable = PurchaseMath.Taxable(l.Quantity, l.UnitPrice);
            var (cgst, sgst, igst) = PurchaseMath.Tax(taxable, l.GstRate, intra);
            order.Items.Add(new PurchaseOrderItem
            {
                OrganizationId = order.OrganizationId,
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                GstRate = l.GstRate,
                TaxableValue = taxable,
                Tax = cgst + sgst + igst,
                SortOrder = n,
                CreatedAt = now,
            });
        }
        order.Subtotal = order.Items.Sum(i => i.TaxableValue);
        order.TaxTotal = order.Items.Sum(i => i.Tax);
        order.Total = order.Subtotal + order.TaxTotal;
        audit.Reason = $"{order.Number} {(id is null ? "drafted" : "changed")} for {supplier.Name}: {order.Items.Count} line{(order.Items.Count == 1 ? "" : "s")}, {Money.Rupees(order.Total)} with GST" +
                       (request is null ? "" : $", from {request.Number}");
        await db.SaveChangesAsync(ct);
        if (request is not null)
        {
            // The order first: the request points at it.
            request.Status = PurchaseRequestStatuses.Ordered;
            request.PurchaseOrderId = order.Id;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, order.Id, ct);
    }

    /// <summary>Sends a draft for approval. At or under the approval limit it is approved straight away.</summary>
    public async Task<PurchaseOrderDto> SubmitAsync(Guid propertyId, Guid id, PurchaseOrderStepRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, r.Version, ct);
        if (order.Status != PurchaseOrderStatuses.Draft)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}; only a draft can be submitted.");
        var limit = await ApprovalLimitAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        order.SubmittedAt = now;
        order.SubmittedBy = caller.UserId;
        order.ApprovalLimit = limit;
        if (PurchaseMath.NeedsApproval(order.Total, limit))
        {
            order.Status = PurchaseOrderStatuses.PendingApproval;
            audit.Reason = $"{order.Number} submitted for approval: {Money.Rupees(order.Total)} is over the {Money.Rupees(limit)} limit" + Note(r.Note);
        }
        else
        {
            order.Status = PurchaseOrderStatuses.Approved;
            order.ApprovedAt = now;
            order.ApprovalNote = ReservationService.Clean(r.Note);
            order.RejectedReason = null;
            audit.Reason = $"{order.Number} approved on submit: {Money.Rupees(order.Total)} is within the {Money.Rupees(limit)} limit" + Note(r.Note);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    /// <summary>Approves an order waiting for approval (purchase.approve). Not by the person who raised or submitted it.</summary>
    public async Task<PurchaseOrderDto> ApproveAsync(Guid propertyId, Guid id, PurchaseOrderStepRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, r.Version, ct);
        RequirePending(order, "approved");
        if (caller.UserId is { } me && (order.SubmittedBy == me || order.CreatedBy == me))
            throw new BusinessRuleAppException("SELF_APPROVAL",
                $"You raised {order.Number}, so you can't approve it yourself. Orders over {Money.Rupees(order.ApprovalLimit ?? 0)} need a second person: " +
                "ask someone else with purchase approval (owner or general manager) to approve it.");
        order.Status = PurchaseOrderStatuses.Approved;
        order.ApprovedAt = clock.GetUtcNow();
        order.ApprovedBy = caller.UserId;
        order.ApprovalNote = ReservationService.Clean(r.Note);
        order.RejectedReason = null;
        audit.Reason = $"{order.Number} approved: {Money.Rupees(order.Total)}" + Note(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    /// <summary>Sends an order waiting for approval back to draft with the reason (purchase.approve).</summary>
    public async Task<PurchaseOrderDto> RejectAsync(Guid propertyId, Guid id, PurchaseReasonRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, r.Version, ct);
        RequirePending(order, "rejected");
        order.Status = PurchaseOrderStatuses.Draft;
        order.RejectedReason = ReservationService.Clean(r.Reason);
        order.SubmittedAt = null;
        order.SubmittedBy = null;
        order.ApprovalLimit = null;
        audit.Reason = $"{order.Number} rejected back to draft — {order.RejectedReason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    /// <summary>Cancels an order nothing has come in on, with the reason. Its request (if any) is open again.</summary>
    public async Task<PurchaseOrderDto> CancelAsync(Guid propertyId, Guid id, PurchaseReasonRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, r.Version, ct);
        if (order.Status is not (PurchaseOrderStatuses.Draft or PurchaseOrderStatuses.PendingApproval or PurchaseOrderStatuses.Approved))
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", order.Status == PurchaseOrderStatuses.PartlyReceived
                ? $"Goods have already come in on {order.Number}, so it can't be cancelled. Close it instead if the rest is not coming."
                : $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}, so it can't be cancelled.");
        order.Status = PurchaseOrderStatuses.Cancelled;
        order.EndReason = ReservationService.Clean(r.Reason);
        if (order.RequestId is { } rid && await db.PurchaseRequests.SingleOrDefaultAsync(x => x.Id == rid, ct) is { } request)
        {
            request.Status = PurchaseRequestStatuses.Open;
            request.PurchaseOrderId = null;
        }
        audit.Reason = $"{order.Number} cancelled — {order.EndReason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    /// <summary>Closes a partly received order: the rest is not coming. With the reason.</summary>
    public async Task<PurchaseOrderDto> CloseAsync(Guid propertyId, Guid id, PurchaseReasonRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, r.Version, ct);
        if (order.Status != PurchaseOrderStatuses.PartlyReceived)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", order.Status == PurchaseOrderStatuses.Approved
                ? $"Nothing has come in on {order.Number} yet. Cancel it instead if it is not needed."
                : $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}; only a partly received order can be closed.");
        order.Status = PurchaseOrderStatuses.Closed;
        order.EndReason = ReservationService.Clean(r.Reason);
        var units = await UnitCodesAsync(ct);
        var items = await db.InventoryItems.AsNoTracking().Where(i => order.Items.Select(x => x.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var missing = order.Items.Where(i => i.ReceivedQuantity < i.Quantity)
            .Select(i => $"{InventoryMath.Quantity(i.Quantity - i.ReceivedQuantity, units[items[i.ItemId].UnitId])} {items[i.ItemId].Name}");
        audit.Reason = $"{order.Number} closed, not coming: {string.Join(", ", missing)} — {order.EndReason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    // ------------------------------------------------------------------ goods received

    /// <summary>
    /// Goods received against an approved order (grn.create; audited). Locks the order, so two deliveries recorded at once
    /// can't both take the last of a line; then each line's stock goes into the store as a RECEIVED movement at the order's
    /// price, which moves the item's average cost. The order becomes PARTLY_RECEIVED, or RECEIVED when every line is in.
    /// </summary>
    public async Task<PurchaseOrderDto> ReceiveAsync(Guid propertyId, Guid id, ReceiveGoodsRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        if (r.Lines.Select(l => l.OrderItemId).Distinct().Count() != r.Lines.Count)
            throw ReservationService.Field(nameof(r.Lines), "A line is received twice. Enter each line once.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(propertyId, id, null, ct);
        if (!PurchaseOrderStatuses.Receivable.Contains(order.Status))
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", order.Status switch
            {
                PurchaseOrderStatuses.Draft => $"{order.Number} is a draft. Submit it (and get it approved if it is over the limit) before goods come in.",
                PurchaseOrderStatuses.PendingApproval => $"{order.Number} is waiting for approval. Goods can be received once it is approved.",
                PurchaseOrderStatuses.Received => $"Everything on {order.Number} has already come in.",
                _ => $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}, so nothing more can come in on it.",
            });
        var today = property.LocalDate(clock.GetUtcNow());
        var on = r.ReceivedOn ?? today;
        if (on > today) throw ReservationService.Field(nameof(r.ReceivedOn), $"{QuoteEngine.Day(on)} is in the future. Record goods on the day they came in.");
        if (on < order.OrderDate)
            throw ReservationService.Field(nameof(r.ReceivedOn), $"{order.Number} was raised on {QuoteEngine.Day(order.OrderDate)}; goods can't have come in before that.");
        var location = await LocationAsync(propertyId, r.LocationId ?? order.LocationId, nameof(r.LocationId), ct);

        await ledger.LockItemsAsync(propertyId, order.Items.Select(i => i.ItemId), ct);
        var items = await db.InventoryItems.Where(i => order.Items.Select(x => x.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await UnitCodesAsync(ct);
        var now = clock.GetUtcNow();
        var grn = new GoodsReceivedNote
        {
            OrganizationId = order.OrganizationId,
            PropertyId = propertyId,
            Number = await NextNumberAsync(db, propertyId, "goods_received", "GRN", ct),
            OrderId = order.Id,
            SupplierId = order.SupplierId,
            LocationId = location.Id,
            ReceivedOn = on,
            ChallanNumber = ReservationService.Clean(r.ChallanNumber),
            Note = ReservationService.Clean(r.Note),
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.GoodsReceivedNotes.Add(grn);
        var words = new List<string>();
        for (var n = 0; n < r.Lines.Count; n++)
        {
            var x = r.Lines[n];
            var line = order.Items.SingleOrDefault(i => i.Id == x.OrderItemId)
                       ?? throw ReservationService.Field($"Lines[{n}].OrderItemId", $"That line isn't on {order.Number}. Reload the order and enter the delivery again.");
            var item = items[line.ItemId];
            var unit = units[item.UnitId];
            var remaining = PurchaseMath.Remaining(line.Quantity, line.ReceivedQuantity);
            if (x.Quantity > remaining)
                throw ReservationService.Field($"Lines[{n}].Quantity", remaining == 0
                    ? $"All {InventoryMath.Quantity(line.Quantity, unit)} of {item.Name} on {order.Number} has already come in."
                    : $"Only {InventoryMath.Quantity(remaining, unit)} of {item.Name} is still to come on {order.Number} " +
                      $"({InventoryMath.Quantity(line.ReceivedQuantity, unit)} of {InventoryMath.Quantity(line.Quantity, unit)} already in). " +
                      "Record the extra on a new order, or as stock received in Inventory with the reason.");
            var g = new GrnItem
            {
                OrganizationId = grn.OrganizationId,
                GrnId = grn.Id,
                OrderItemId = line.Id,
                ItemId = item.Id,
                Quantity = x.Quantity,
                UnitCost = line.UnitPrice,
                Value = InventoryMath.Value(x.Quantity, line.UnitPrice),
                CreatedAt = now,
            };
            grn.Items.Add(g);
            item.UnitCost = InventoryMath.AverageCost(await ledger.TotalOnHandAsync(item.Id, ct), item.UnitCost, x.Quantity, line.UnitPrice);
            item.UpdatedAt = now;
            line.ReceivedQuantity += x.Quantity;
            words.Add($"{InventoryMath.Quantity(x.Quantity, unit)} {item.Name}");
        }
        grn.Value = grn.Items.Sum(i => i.Value);
        audit.Reason = $"{grn.Number} on {order.Number} into {location.Name}: {string.Join(", ", words)} ({Money.Rupees(grn.Value)} before GST)" +
                       (grn.ChallanNumber is null ? "" : $", challan {grn.ChallanNumber}");
        await db.SaveChangesAsync(ct);   // the delivery's lines first: each stock movement points at its line
        var supplierName = await db.Suppliers.AsNoTracking().Where(s => s.Id == order.SupplierId).Select(s => s.Name).SingleAsync(ct);
        foreach (var g in grn.Items)
            ledger.Add(items[g.ItemId], location.Id, MovementTypes.Received, g.Quantity, g.UnitCost, $"{order.Number} · {supplierName}", grnItemId: g.Id);
        order.Status = PurchaseMath.StatusAfterReceipt(order.Items.Select(i => (i.Quantity, i.ReceivedQuantity)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrderAsync(propertyId, id, ct);
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<PurchaseOrderDto> ToDtoAsync(PurchaseOrder o, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == o.SupplierId, ct);
        var location = await db.StockLocations.AsNoTracking().SingleAsync(l => l.Id == o.LocationId, ct);
        var itemIds = o.Items.Select(i => i.ItemId).ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await UnitCodesAsync(ct);
        var grns = await db.GoodsReceivedNotes.AsNoTracking().Include(g => g.Items).Where(g => g.OrderId == o.Id).OrderBy(g => g.CreatedAt).ToListAsync(ct);
        var grnLocations = await db.StockLocations.AsNoTracking().Where(l => grns.Select(g => g.LocationId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var names = await UserNamesAsync(new[] { o.CreatedBy, o.SubmittedBy, o.ApprovedBy }.Concat(grns.Select(g => g.CreatedBy)), ct);
        var request = o.RequestId is { } rid ? await db.PurchaseRequests.AsNoTracking().Where(x => x.Id == rid).Select(x => x.Number).SingleOrDefaultAsync(ct) : null;
        var limit = o.ApprovalLimit ?? await ApprovalLimitAsync(o.PropertyId, ct);
        string Name(Guid? u) => u is { } x ? names.GetValueOrDefault(x) ?? "" : "";
        var lines = o.Items.OrderBy(i => i.SortOrder).Select(i =>
        {
            var unit = units[items[i.ItemId].UnitId];
            return new PurchaseOrderLineDto(i.Id, i.ItemId, items[i.ItemId].Code, items[i.ItemId].Name, unit, i.Quantity, i.UnitPrice, i.GstRate, i.TaxableValue,
                i.Tax, i.TaxableValue + i.Tax, i.ReceivedQuantity, PurchaseMath.Remaining(i.Quantity, i.ReceivedQuantity), i.BilledQuantity,
                i.ReceivedQuantity - i.BilledQuantity);
        }).ToList();
        var sortOrder = o.Items.ToDictionary(i => i.Id, i => i.SortOrder);   // a delivery's lines in the order's order, not the database's
        var receipts = grns.Select(g => new GrnDto(g.Id, g.Number, g.ReceivedOn, grnLocations.GetValueOrDefault(g.LocationId, ""), g.ChallanNumber, g.Note, g.Value,
            g.Items.OrderBy(x => sortOrder.GetValueOrDefault(x.OrderItemId)).Select(x => new GrnLineDto(x.OrderItemId, items[x.ItemId].Name, units[items[x.ItemId].UnitId], x.Quantity, x.UnitCost, x.Value)).ToList(),
            g.CreatedBy is null ? null : Name(g.CreatedBy), g.CreatedAt)).ToList();
        var bills = await invoices.ForOrderAsync(o.Id, ct);
        return new PurchaseOrderDto(o.Id, o.Number, supplier.Id, supplier.Name, supplier.Gstin, GstStates.Words(supplier.StateCode), o.RequestId, request,
            location.Id, location.Name, o.OrderDate, o.ExpectedDate, o.Status, o.IntraState, o.Note, o.Subtotal, o.TaxTotal, o.Total, limit,
            PurchaseMath.NeedsApproval(o.Total, limit), o.SubmittedAt, o.SubmittedBy is null ? null : Name(o.SubmittedBy), o.SubmittedBy, o.ApprovedAt,
            o.ApprovedBy is null ? null : Name(o.ApprovedBy), o.ApprovalNote, o.RejectedReason, o.EndReason, lines, receipts, bills,
            o.CreatedBy is null ? null : Name(o.CreatedBy), o.CreatedAt, o.Version);
    }

    private async Task<List<PurchaseRequestDto>> RequestDtosAsync(IReadOnlyList<PurchaseRequest> list, CancellationToken ct)
    {
        var itemIds = list.SelectMany(r => r.Items.Select(i => i.ItemId)).Distinct().ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await UnitCodesAsync(ct);
        var levels = await db.StockLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync(ct);
        var locIds = list.Select(r => r.LocationId).Distinct().ToList();
        var locations = await db.StockLocations.AsNoTracking().Where(l => locIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var orderIds = list.Where(r => r.PurchaseOrderId != null).Select(r => r.PurchaseOrderId!.Value).ToList();
        var orders = await db.PurchaseOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var names = await UserNamesAsync(list.Select(r => r.CreatedBy), ct);
        return list.Select(r => new PurchaseRequestDto(r.Id, r.Number, r.LocationId, locations.GetValueOrDefault(r.LocationId, ""), r.NeededBy, r.Note, r.Status,
            r.PurchaseOrderId, r.PurchaseOrderId is { } po ? orders.GetValueOrDefault(po) : null, r.CancelReason,
            r.Items.OrderBy(i => i.SortOrder).Select(i => new PurchaseRequestItemDto(i.Id, i.ItemId, items[i.ItemId].Code, items[i.ItemId].Name, units[items[i.ItemId].UnitId],
                i.Quantity, i.Note, levels.Where(l => l.ItemId == i.ItemId && l.LocationId == r.LocationId).Sum(l => l.OnHand), items[i.ItemId].UnitCost)).ToList(),
            r.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, r.CreatedAt, r.Version)).ToList();
    }

    private async Task<PurchaseOrder> LockOrderAsync(Guid propertyId, Guid id, uint? version, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM purchase_orders WHERE id = {id} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        var order = await db.PurchaseOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That purchase order doesn't exist at this property. Reload the list.");
        Expect(order, version, order.Number);
        return order;
    }

    private void Expect<T>(T entity, uint? version, string number) where T : class, Domain.Common.IVersioned
    {
        if (version is { } v && entity.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT",
                $"Someone else changed {number} a moment ago. Reload it to see their change, then make yours again.");
        if (version is { } ok) ConcurrencyGuard.ExpectVersion(db, entity, ok);
    }

    private static void RequirePending(PurchaseOrder order, string what)
    {
        if (order.Status != PurchaseOrderStatuses.PendingApproval)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION",
                $"{order.Number} is {PurchaseOrderStatuses.Words(order.Status)}, not waiting for approval, so it can't be {what}.");
    }

    private async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private Task<decimal> ApprovalLimitAsync(Guid propertyId, CancellationToken ct) =>
        PropertySettingsReader.DecimalAsync(db, propertyId, ApprovalLimitSetting, PurchaseMath.DefaultApprovalLimit, ct);

    /// <summary>The state the property buys in: its GST registration's on <paramref name="day"/>, else its own state.</summary>
    internal async Task<string?> BuyerStateAsync(Property property, DateOnly day, CancellationToken ct) =>
        await db.GstRegistrations.AsNoTracking().Where(r => r.PropertyId == property.Id && r.EffectiveFrom <= day && (r.EffectiveTo == null || r.EffectiveTo >= day))
            .Select(r => r.StateCode).FirstOrDefaultAsync(ct) ?? property.GstStateCode;

    private async Task<StockLocation> LocationAsync(Guid propertyId, Guid id, string field, CancellationToken ct)
    {
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id && l.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(field, "That store doesn't exist at this property. Reload and choose again.");
        if (!location.IsActive) throw ReservationService.Field(field, $"{location.Name} is switched off. Choose another store.");
        return location;
    }

    /// <summary>The stock items of the lines, each once and switched on; errors under Lines[n].ItemId.</summary>
    private async Task<Dictionary<Guid, (InventoryItem Item, string Unit)>> ItemsAsync(Guid propertyId, List<Guid> ids, string field, CancellationToken ct)
    {
        var found = await db.InventoryItems.AsNoTracking().Where(i => ids.Contains(i.Id) && i.PropertyId == propertyId).ToDictionaryAsync(i => i.Id, ct);
        var units = await UnitCodesAsync(ct);
        for (var n = 0; n < ids.Count; n++)
        {
            if (!found.TryGetValue(ids[n], out var item))
                throw ReservationService.Field($"{field}[{n}].ItemId", "That stock item doesn't exist at this property. Reload and choose again.");
            if (!item.IsActive)
                throw ReservationService.Field($"{field}[{n}].ItemId", $"{item.Name} is switched off in Inventory. Switch it on to buy it.");
            if (ids.Take(n).Contains(ids[n]))
                throw ReservationService.Field($"{field}[{n}].ItemId", $"{item.Name} is on two lines. Put the whole quantity on one line.");
        }
        return found.ToDictionary(x => x.Key, x => (x.Value, units[x.Value.UnitId]));
    }

    private static string Qty(decimal quantity, (InventoryItem Item, string Unit) item) => InventoryMath.Quantity(quantity, item.Unit);

    private static string Note(string? note) => ReservationService.Clean(note) is { } n ? $" — {n}" : "";

    private async Task<string?> OrderNumberAsync(Guid? id, CancellationToken ct) =>
        id is { } x ? await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == x).Select(o => o.Number).SingleOrDefaultAsync(ct) : null;

    private async Task<Dictionary<Guid, string>> UnitCodesAsync(CancellationToken ct) =>
        await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);

    internal async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    /// <summary>The next number of a per-property series (PR00001, PO00001, GRN00001, SI00001, SP00001).</summary>
    internal static async Task<string> NextNumberAsync(IAppDbContext db, Guid propertyId, string counter, string prefix, CancellationToken ct)
    {
        var n = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({propertyId}, {counter}, 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        return $"{prefix}{n:D5}";
    }
}
