using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Payments;
using Resort.Application.Reservations;
using Resort.Application.Tax;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Reservations;
using Resort.Domain.Restaurant;
using Resort.Domain.Tax;

namespace Resort.Application.Restaurant;

/// <summary>
/// Restaurant orders (Sprint 14, brief §35, §55): a waiter opens an order at a table (or for room service, or takeaway),
/// adds dishes, sends them to the kitchen as a ticket (KOT), serves what the kitchen marks ready, and settles the bill —
/// paid at the restaurant, or charged to an in-house guest's room as one line on the main folio.
///
/// Two waiters on one order: every change locks the order row (SELECT … FOR UPDATE) and compares the version the waiter
/// last saw (xmin); someone who saved in between makes it 409 CONCURRENCY_CONFLICT, so nobody overwrites the other's
/// lines. Every change rewrites the order's totals, so every change moves its version on.
///
/// Money: menu prices are before GST. GST is the tax engine's (Sprint 9) for restaurant service (the FOOD tax category) on
/// the order's date, on the subtotal less any discount — the same calculation the folio makes when the bill is charged to
/// a room, so the order and the folio line always agree.
/// </summary>
public sealed class OrderService(
    IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock, TaxCalculator tax,
    FolioPosting posting, CashDrawerService drawers)
{
    /// <summary>Restaurant service is taxed as one supply under the FOOD category (food and soft drinks alike).</summary>
    public const string TaxCategory = ChargeCategories.Food;

    // ------------------------------------------------------------------ reading

    public async Task<FloorDto> FloorAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var tables = await db.RestaurantTables.AsNoTracking().Where(t => t.PropertyId == propertyId && t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct);
        var open = await db.Orders.AsNoTracking().Where(o => o.PropertyId == propertyId && o.Status == OrderStatuses.Open)
            .OrderBy(o => o.CreatedAt).ToListAsync(ct);
        var summaries = await SummariesAsync(open, ct);
        return new FloorDto(property.LocalDate(clock.GetUtcNow()),
            tables.Select(t => new FloorTableDto(t.Id, t.Code, t.Seats, t.Area, summaries.FirstOrDefault(s => open.Single(o => o.Id == s.Id).TableId == t.Id))).ToList(),
            summaries.Where(s => open.Single(o => o.Id == s.Id).TableId is null).ToList());
    }

    /// <summary>Orders of one day (default today), newest first. Status: OPEN, SETTLED, VOIDED or ALL (default).</summary>
    public async Task<OrderListDto> ListAsync(Guid propertyId, DateOnly? date, string? status, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var day = date ?? property.LocalDate(clock.GetUtcNow());
        var q = db.Orders.AsNoTracking().Where(o => o.PropertyId == propertyId && o.BusinessDate == day);
        var s = string.IsNullOrWhiteSpace(status) ? "ALL" : status.Trim().ToUpperInvariant();
        if (s != "ALL")
        {
            if (!OrderStatuses.All.Contains(s))
                throw ReservationService.Field("Status", $"'{status}' isn't an order status. Use ALL or one of {string.Join(", ", OrderStatuses.All)}.");
            q = q.Where(o => o.Status == s);
        }
        var orders = await q.OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct);
        var items = await SummariesAsync(orders, ct);
        return new OrderListDto(items,
            orders.Where(o => o.Status == OrderStatuses.Settled).Sum(o => o.Total),
            orders.Where(o => o.Status == OrderStatuses.Open).Sum(o => o.Total));
    }

    public async Task<OrderDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That order doesn't exist at this property. Reload the list.");
        return await ToDtoAsync(order, ct);
    }

    /// <summary>The stays checked in now, room by room, that a bill can be charged to.</summary>
    public async Task<IReadOnlyList<InHouseRoomDto>> InHouseRoomsAsync(Guid propertyId, CancellationToken ct)
    {
        var stays = await db.Reservations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && r.Status == ReservationStatuses.CheckedIn).ToListAsync(ct);
        var ids = stays.Select(s => s.Id).ToList();
        var rows = await db.ReservationUnits.AsNoTracking().Where(u => ids.Contains(u.ReservationId) && u.SupersededAt == null).ToListAsync(ct);
        var unitIds = rows.Select(r => r.UnitId).Distinct().ToList();
        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var guests = await GuestNamesAsync(stays.Select(s => s.PrimaryGuestId), ct);
        return rows
            .Select(r =>
            {
                var stay = stays.Single(s => s.Id == r.ReservationId);
                var unit = units[r.UnitId];
                var name = r.BedId is { } b ? $"{unit.Name} bed {unit.Beds.First(x => x.Id == b).Code}" : unit.Name;
                return new InHouseRoomDto(unit.Id, unit.Code, name, stay.Id, stay.Reference, guests.GetValueOrDefault(stay.PrimaryGuestId, ""), stay.CheckOut);
            })
            .DistinctBy(r => (r.UnitId, r.ReservationId, r.UnitName))
            .OrderBy(r => units[r.UnitId].SortOrder).ThenBy(r => r.UnitCode, StringComparer.Ordinal).ThenBy(r => r.UnitName, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ taking the order

    public async Task<(OrderDto Order, bool Replayed)> CreateAsync(Guid propertyId, string? idempotencyKey, CreateOrderRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/orders";
        var key = idempotencyKey is null ? null : Idempotency.RequireKey(idempotencyKey);
        var type = r.OrderType.Trim().ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (key is not null && await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "order", ct) is { } stored)
            return (JsonSerializer.Deserialize<OrderDto>(stored, Idempotency.Json)!, true);

        var order = new Order
        {
            OrganizationId = orgId,
            PropertyId = propertyId,
            OrderType = type,
            Covers = r.Covers,
            Note = ReservationService.Clean(r.Note),
            BusinessDate = property.LocalDate(clock.GetUtcNow()),
            OpenedBy = caller.UserId,
        };
        if (type == OrderTypes.DineIn)
        {
            var table = await TableAsync(propertyId, r.TableId, nameof(r.TableId), ct);
            await RequireTableFreeAsync(table, null, ct);
            order.TableId = table.Id;
        }
        else if (type == OrderTypes.RoomService)
        {
            var room = (await InHouseRoomsAsync(propertyId, ct)).FirstOrDefault(x => x.UnitId == r.UnitId)
                       ?? throw ReservationService.Field(nameof(r.UnitId), await NotInHouseAsync(propertyId, r.UnitId, ct));
            order.UnitId = room.UnitId;
            order.ReservationId = room.ReservationId;
        }
        order.Number = await NextNumberAsync(propertyId, "restaurant_order", "O", ct);
        db.Orders.Add(order);

        var lines = await BuildLinesAsync(propertyId, order, r.Items ?? [], ct);
        await RecalculateAsync(order, lines, ct);
        audit.Reason = $"{order.Number} opened";
        await db.SaveChangesAsync(ct);
        var dto = await ToDtoAsync(order, ct);
        if (key is not null) await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, order.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    public async Task<OrderDto> AddItemsAsync(Guid propertyId, Guid id, AddOrderItemsRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        await BuildLinesAsync(propertyId, order, r.Items, ct);
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>Changes the quantity or note of a line not sent to the kitchen yet.</summary>
    public async Task<OrderDto> UpdateItemAsync(Guid propertyId, Guid id, Guid itemId, UpdateOrderItemRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        var line = await UnsentLineAsync(order, itemId, "changed", ct);
        line.Quantity = r.Quantity;
        line.Amount = RestaurantMath.LineAmount(line.Quantity, line.UnitPrice);
        line.Note = ReservationService.Clean(r.Note);
        line.UpdatedAt = clock.GetUtcNow();
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>Takes off a line not sent to the kitchen yet (nothing to void: the kitchen never saw it).</summary>
    public async Task<OrderDto> RemoveItemAsync(Guid propertyId, Guid id, Guid itemId, uint? version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, version, ct);
        var line = await UnsentLineAsync(order, itemId, "removed", ct);
        db.OrderItems.Remove(line);
        return await SaveAsync(order, tx, ct);
    }

    public async Task<OrderDto> UpdateAsync(Guid propertyId, Guid id, UpdateOrderRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        if (r.TableId is { } tid && tid != order.TableId)
        {
            if (order.OrderType != OrderTypes.DineIn)
                throw ReservationService.Field(nameof(r.TableId), $"{order.Number} is {TypeWords(order.OrderType)}, not at a table.");
            var table = await TableAsync(propertyId, tid, nameof(r.TableId), ct);
            await RequireTableFreeAsync(table, order.Id, ct);
            audit.Reason = $"{order.Number} moved to table {table.Code}";
            order.TableId = table.Id;
        }
        order.Covers = r.Covers;
        order.Note = ReservationService.Clean(r.Note);
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>Sends every new line to the kitchen as one ticket.</summary>
    public async Task<OrderDto> SendAsync(Guid propertyId, Guid id, SendToKitchenRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        var fresh = await db.OrderItems.Where(i => i.OrderId == order.Id && i.Status == OrderItemStatuses.New).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        if (fresh.Count == 0)
            throw new BusinessRuleAppException("NOTHING_TO_SEND", $"Everything on {order.Number} is already with the kitchen. Add a dish first.");
        var now = clock.GetUtcNow();
        var kot = new Kot
        {
            OrganizationId = order.OrganizationId,
            PropertyId = order.PropertyId,
            OrderId = order.Id,
            Number = await NextNumberAsync(propertyId, "restaurant_kot", "K", ct),
            Note = ReservationService.Clean(r.Note),
            SentAt = now,
            SentBy = caller.UserId,
        };
        db.Kots.Add(kot);
        foreach (var (line, n) in fresh.Select((l, n) => (l, n)))
        {
            line.Status = OrderItemStatuses.Sent;
            line.KotId = kot.Id;
            line.UpdatedAt = now;
            db.KotItems.Add(new KotItem
            {
                OrganizationId = order.OrganizationId,
                KotId = kot.Id,
                OrderItemId = line.Id,
                Name = RestaurantMath.Label(line.Name, line.VariantName),
                Quantity = line.Quantity,
                Modifiers = ModifierText(line.Modifiers),
                Note = line.Note,
                CreatedAt = now.AddMicroseconds(n),
            });
        }
        audit.Reason = $"{kot.Number} sent to the kitchen";
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>The waiter takes a ready ticket to the table. Doesn't change the order's version: serving one ticket never
    /// gets in the way of a waiter adding to the order.</summary>
    public async Task<OrderDto> ServeAsync(Guid propertyId, Guid id, Guid kotId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That order doesn't exist at this property. Reload the list.");
        var kot = await LockKotAsync(propertyId, kotId, ct);
        if (kot.OrderId != order.Id) throw new NotFoundAppException($"That kitchen ticket isn't on {order.Number}. Reload the order.");
        if (RestaurantFlow.KotProblem("serve", kot.Number, kot.Status) is { } why)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", why, new { from = kot.Status, action = "serve" });
        kot.Status = KotStatuses.Served;
        kot.ServedAt = clock.GetUtcNow();
        kot.ServedBy = caller.UserId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>Voids a line already sent to the kitchen (pos_order.void, reason). Its ticket line is struck through; a
    /// ticket with nothing left on it is cancelled.</summary>
    public async Task<OrderDto> VoidItemAsync(Guid propertyId, Guid id, Guid itemId, VoidRequest r, CancellationToken ct)
    {
        var reason = ReservationService.Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        var line = await db.OrderItems.SingleOrDefaultAsync(i => i.Id == itemId && i.OrderId == order.Id, ct)
                   ?? throw new NotFoundAppException($"That line isn't on {order.Number}. Reload the order.");
        var label = RestaurantMath.Label(line.Name, line.VariantName);
        if (line.Status == OrderItemStatuses.Voided)
            throw new BusinessRuleAppException("ALREADY_VOIDED", $"{label} on {order.Number} is already voided ({line.VoidReason}).");
        if (line.Status == OrderItemStatuses.New)
            throw new BusinessRuleAppException("NOT_SENT", $"{label} hasn't gone to the kitchen yet, so there is nothing to void: remove it from the order instead.");
        await VoidLineAsync(order, line, reason, ct);
        audit.Reason = $"Voided {line.Quantity}× {label}: {reason}";
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>A discount on the whole order (pos.discount, reason). Kind null takes it off.</summary>
    public async Task<OrderDto> DiscountAsync(Guid propertyId, Guid id, DiscountRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        if (r.Kind is null)
        {
            order.DiscountKind = null;
            order.DiscountValue = null;
            order.DiscountReason = null;
            order.DiscountedBy = null;
            order.DiscountedAt = null;
            audit.Reason = ReservationService.Clean(r.Reason) ?? "Discount taken off";
        }
        else
        {
            var kind = r.Kind.Trim().ToUpperInvariant();
            var subtotal = await LiveSubtotalAsync(order.Id, ct);
            if (kind == DiscountKinds.Amount && r.Value > subtotal)
                throw ReservationService.Field(nameof(r.Value),
                    $"{order.Number} comes to {Money.Rupees(subtotal)} before GST, so the discount can be at most that.");
            order.DiscountKind = kind;
            order.DiscountValue = r.Value;
            order.DiscountReason = ReservationService.Clean(r.Reason);
            order.DiscountedBy = caller.UserId;
            order.DiscountedAt = clock.GetUtcNow();
            audit.Reason = $"Discount {(kind == DiscountKinds.Percent ? $"{r.Value:0.##}%" : Money.Rupees(r.Value ?? 0))}: {order.DiscountReason}";
        }
        return await SaveAsync(order, tx, ct);
    }

    /// <summary>Voids the whole order (pos_order.void, reason): every line, and every ticket the kitchen hasn't served.</summary>
    public async Task<OrderDto> VoidAsync(Guid propertyId, Guid id, VoidRequest r, CancellationToken ct)
    {
        var reason = ReservationService.Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        var lines = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync(ct);
        foreach (var line in lines.Where(l => l.Status == OrderItemStatuses.New)) db.OrderItems.Remove(line);
        foreach (var line in lines.Where(l => l.Status == OrderItemStatuses.Sent)) await VoidLineAsync(order, line, reason, ct);
        await db.SaveChangesAsync(ct);   // the lines first: the order is final once voided
        await RecalculateAsync(order, null, ct);
        order.Status = OrderStatuses.Voided;
        order.VoidedAt = clock.GetUtcNow();
        order.VoidedBy = caller.UserId;
        order.VoidReason = reason;
        audit.Reason = $"{order.Number} voided: {reason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(order, ct);
    }

    // ------------------------------------------------------------------ settling the bill

    /// <summary>Paid at the restaurant. Cash goes into the property's open cash drawer.</summary>
    public async Task<(OrderDto Order, bool Replayed)> PayAsync(Guid propertyId, Guid id, string? idempotencyKey, PayOrderRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/orders/{id}/pay";
        var key = idempotencyKey is null ? null : Idempotency.RequireKey(idempotencyKey);
        var method = r.Method.Trim().ToUpperInvariant();
        var property = await LoadPropertyAsync(propertyId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (key is not null && await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "restaurant bill", ct) is { } stored)
            return (JsonSerializer.Deserialize<OrderDto>(stored, Idempotency.Json)!, true);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        await RequireSettleableAsync(order, ct);

        var now = clock.GetUtcNow();
        order.Status = OrderStatuses.Settled;
        order.Settlement = Settlements.Paid;
        order.PaymentMethod = method;
        order.PaymentReference = ReservationService.Clean(r.Reference);
        order.SettledAt = now;
        order.SettledBy = caller.UserId;
        if (method == PaymentMethods.Cash && order.Total > 0)
        {
            var session = await drawers.RequireOpenSessionAsync(property, nameof(r.Method), "a restaurant bill", ct);
            order.CashDrawerSessionId = session.Id;
            drawers.AddSaleMovement(session, order.Total, $"{order.Number} · {await WhereAsync(order, ct)}", order.Id);
        }
        audit.Reason = $"{order.Number} paid by {PaymentMethods.Words(method)}: {Money.Rupees(order.Total)}";
        await db.SaveChangesAsync(ct);
        var dto = await ToDtoAsync(order, ct);
        if (key is not null) await Idempotency.CompleteAsync(db, orgId, scope, key, 200, dto, order.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>
    /// Charges the bill to an in-house guest's room: one line on the stay's main folio (category FOOD, source RESTAURANT,
    /// the order's net as its price and its GST from the tax engine, served on the order's date). Locks the order, then
    /// the booking (as every folio writer does), so the guest can't be checked out halfway through.
    /// </summary>
    public async Task<(OrderDto Order, bool Replayed)> ChargeToRoomAsync(Guid propertyId, Guid id, string? idempotencyKey, ChargeToRoomRequest r,
        CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/orders/{id}/charge-to-room";
        var key = idempotencyKey is null ? null : Idempotency.RequireKey(idempotencyKey);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (key is not null && await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "room charge", ct) is { } stored)
            return (JsonSerializer.Deserialize<OrderDto>(stored, Idempotency.Json)!, true);
        var order = await LockAsync(propertyId, id, r.Version, ct);
        var reservationId = r.ReservationId ?? order.ReservationId
                            ?? throw ReservationService.Field(nameof(r.ReservationId), "Choose the room to charge it to.");
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM reservations WHERE id = {reservationId} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        var stay = await db.Reservations.SingleOrDefaultAsync(x => x.Id == reservationId && x.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.ReservationId), "That booking isn't at this property. Reload and choose the room again.");
        if (stay.Status != ReservationStatuses.CheckedIn)
            throw ReservationService.Field(nameof(r.ReservationId), stay.Status == ReservationStatuses.CheckedOut
                ? $"{stay.Reference} has checked out, so nothing more can go on the room's bill. Take the payment at the restaurant instead."
                : $"{stay.Reference} isn't checked in ({ReservationStatuses.Words(stay.Status)}), so the bill can't go on the room. Take the payment at the restaurant instead.");
        await RequireSettleableAsync(order, ct);

        var room = order.UnitId is { } u && order.ReservationId == stay.Id ? u
            : await db.ReservationUnits.AsNoTracking().Where(x => x.ReservationId == stay.Id && x.SupersededAt == null)
                .OrderBy(x => x.CreatedAt).Select(x => x.UnitId).FirstAsync(ct);
        var folio = await posting.EnsureMainFolioAsync(stay, ct);
        var lines = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == order.Id && i.Status != OrderItemStatuses.Voided)
            .OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var net = order.Subtotal - order.DiscountAmount;
        var item = await posting.ChargeAsync(folio, new FolioItem
        {
            Category = TaxCategory,
            Description = RestaurantMath.FolioDescription(order.Number, order.TableId is null ? null : await WhereAsync(order, ct),
                lines.Select(l => (l.Quantity, RestaurantMath.Label(l.Name, l.VariantName)))),
            ServiceDate = order.BusinessDate,
            Quantity = 1,
            UnitPrice = net,
            Source = FolioItemSources.Restaurant,
        }, null, ct);
        if (item.TaxAmount != order.TaxAmount)
            throw new InvalidOperationException($"{order.Number}: the folio's GST {item.TaxAmount} differs from the order's {order.TaxAmount}.");

        order.Status = OrderStatuses.Settled;
        order.Settlement = Settlements.RoomCharge;
        order.ReservationId = stay.Id;
        order.UnitId = room;
        order.FolioId = folio.Id;
        order.FolioItemId = item.Id;
        order.SettledAt = clock.GetUtcNow();
        order.SettledBy = caller.UserId;
        audit.Reason = $"{order.Number} charged to {stay.Reference} ({folio.Number}): {Money.Rupees(order.Total)}";
        await db.SaveChangesAsync(ct);
        var dto = await ToDtoAsync(order, ct);
        if (key is not null) await Idempotency.CompleteAsync(db, orgId, scope, key, 200, dto, order.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ kitchen

    /// <summary>Tickets still in the kitchen, oldest first. The display asks again every 10 seconds.</summary>
    public async Task<KitchenDto> KitchenAsync(Guid propertyId, CancellationToken ct)
    {
        var kots = await db.Kots.AsNoTracking().Where(k => k.PropertyId == propertyId && KotStatuses.Active.Contains(k.Status))
            .OrderBy(k => k.SentAt).Take(200).ToListAsync(ct);
        return new KitchenDto(clock.GetUtcNow(), await KotDtosAsync(kots, ct));
    }

    /// <summary>The kitchen moves a ticket on: start (preparing) or ready (the waiter is told to serve it).</summary>
    public async Task<KotDto> KotStepAsync(Guid propertyId, Guid kotId, string action, KotStepRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var kot = await LockKotAsync(propertyId, kotId, ct);
        if (RestaurantFlow.KotProblem(action, kot.Number, kot.Status) is { } why)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", why, new { from = kot.Status, action });
        if (r.Version is { } v && kot.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {kot.Number} a moment ago. The screen reloads to show it; tap again if it still needs doing.");
        if (r.Version is { } ok) ConcurrencyGuard.ExpectVersion(db, kot, ok);
        var now = clock.GetUtcNow();
        if (action == "start")
        {
            kot.Status = KotStatuses.Preparing;
            kot.StartedAt = now;
        }
        else
        {
            kot.Status = KotStatuses.Ready;
            kot.StartedAt ??= now;
            kot.ReadyAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await KotDtosAsync([kot], ct)).Single();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Locks the order row, checks it is still open and the caller saw its latest version.</summary>
    private async Task<Order> LockAsync(Guid propertyId, Guid id, uint? version, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM orders WHERE id = {id} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That order doesn't exist at this property. Reload the list.");
        if (RestaurantFlow.OrderProblem(order.Number, order.Status) is { } why)
            throw new BusinessRuleAppException(order.Status == OrderStatuses.Settled ? "ORDER_SETTLED" : "ORDER_VOIDED", why);
        if (version is { } v && order.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT",
                $"Someone else changed {order.Number} a moment ago (another waiter, or the kitchen). Reload it to see their change, then make yours again.");
        if (version is { } ok) ConcurrencyGuard.ExpectVersion(db, order, ok);
        return order;
    }

    private async Task<Kot> LockKotAsync(Guid propertyId, Guid kotId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM kots WHERE id = {kotId} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        return await db.Kots.SingleOrDefaultAsync(k => k.Id == kotId && k.PropertyId == propertyId, ct)
               ?? throw new NotFoundAppException("That kitchen ticket doesn't exist at this property. Reload.");
    }

    /// <summary>Recalculates the totals, saves, commits and returns the order as it is now.</summary>
    private async Task<OrderDto> SaveAsync(Order order, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await RecalculateAsync(order, null, ct);
        order.UpdatedAt = clock.GetUtcNow();   // every change moves the version on, even one that leaves the totals alone
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>Subtotal of the live lines (or <paramref name="lines"/> when they aren't saved yet), the discount on it,
    /// the GST on what is left, and the total.</summary>
    private async Task RecalculateAsync(Order order, IReadOnlyList<OrderItem>? lines, CancellationToken ct)
    {
        order.Subtotal = lines?.Where(l => l.Status != OrderItemStatuses.Voided).Sum(l => l.Amount) ?? await LiveSubtotalAsync(order.Id, ct);
        order.DiscountAmount = RestaurantMath.Discount(order.DiscountKind, order.DiscountValue, order.Subtotal);
        var outcome = await TaxAsync(order, ct);
        order.TaxAmount = outcome.Total;
        order.Total = order.Subtotal - order.DiscountAmount + order.TaxAmount;
    }

    private async Task<TaxOutcome> TaxAsync(Order order, CancellationToken ct)
    {
        var net = order.Subtotal - order.DiscountAmount;
        return await tax.CalculateAsync(order.PropertyId, TaxCategory, null, order.BusinessDate, [new TaxablePart(net, net)], null, ct);
    }

    private async Task<decimal> LiveSubtotalAsync(Guid orderId, CancellationToken ct) =>
        await db.OrderItems.Where(i => i.OrderId == orderId && i.Status != OrderItemStatuses.Voided).SumAsync(i => (decimal?)i.Amount, ct) ?? 0m;

    /// <summary>A bill is settled once nothing is waiting: every line sent, every ticket served, something left to bill.</summary>
    private async Task RequireSettleableAsync(Order order, CancellationToken ct)
    {
        await RecalculateAsync(order, null, ct);   // a GST rule added since the last change counts
        var lines = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).ToListAsync(ct);
        var unsent = lines.Where(l => l.Status == OrderItemStatuses.New).ToList();
        if (unsent.Count > 0)
            throw new BusinessRuleAppException("ORDER_NOT_SENT",
                $"{Count(unsent.Count, "line")} on {order.Number} {(unsent.Count == 1 ? "hasn't" : "haven't")} gone to the kitchen " +
                $"({string.Join(", ", unsent.Select(l => RestaurantMath.Label(l.Name, l.VariantName)))}). Send {(unsent.Count == 1 ? "it" : "them")} or remove {(unsent.Count == 1 ? "it" : "them")} first.");
        if (lines.All(l => l.Status == OrderItemStatuses.Voided))
            throw new BusinessRuleAppException("ORDER_EMPTY", $"{order.Number} has nothing on it to bill. Add a dish, or void the order.");
        var waiting = await db.Kots.AsNoTracking()
            .Where(k => k.OrderId == order.Id && KotStatuses.Active.Contains(k.Status)).OrderBy(k => k.SentAt).ToListAsync(ct);
        if (waiting.Count > 0)
            throw new BusinessRuleAppException("ORDER_IN_KITCHEN",
                string.Join(" ", waiting.Select(k => $"{k.Number} is {KotStatuses.Words(k.Status)}.")) +
                (waiting.All(k => k.Status == KotStatuses.Ready) ? " Mark it served first." : " Bill the order once everything is served (or void what isn't wanted)."));
        var outcome = await TaxAsync(order, ct);
        if (outcome.RuleMissing)
            throw new BusinessRuleAppException("TAX_RULE_MISSING", $"{outcome.NoTaxReason} Until then {order.Number} can't be billed.");
    }

    private async Task<OrderItem> UnsentLineAsync(Order order, Guid itemId, string what, CancellationToken ct)
    {
        var line = await db.OrderItems.SingleOrDefaultAsync(i => i.Id == itemId && i.OrderId == order.Id, ct)
                   ?? throw new NotFoundAppException($"That line isn't on {order.Number}. Reload the order.");
        if (line.Status != OrderItemStatuses.New)
            throw new BusinessRuleAppException("ALREADY_SENT", line.Status == OrderItemStatuses.Voided
                ? $"{RestaurantMath.Label(line.Name, line.VariantName)} is voided."
                : $"{RestaurantMath.Label(line.Name, line.VariantName)} has gone to the kitchen, so it can't be {what}. A manager can void it, with a reason.");
        return line;
    }

    private async Task VoidLineAsync(Order order, OrderItem line, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        line.Status = OrderItemStatuses.Voided;
        line.VoidedAt = now;
        line.VoidedBy = caller.UserId;
        line.VoidReason = reason;
        line.UpdatedAt = now;
        var kotLine = await db.KotItems.SingleAsync(k => k.OrderItemId == line.Id, ct);
        kotLine.VoidedAt = now;
        kotLine.VoidReason = reason;
        await db.SaveChangesAsync(ct);
        var kot = await LockKotAsync(order.PropertyId, line.KotId!.Value, ct);
        if (kot.Status is KotStatuses.Served or KotStatuses.Cancelled) return;
        if (await db.KotItems.AllAsync(i => i.KotId != kot.Id || i.VoidedAt != null, ct))
        {
            kot.Status = KotStatuses.Cancelled;
            kot.CancelledAt = now;
        }
    }

    /// <summary>Order lines for <paramref name="requests"/>, priced from the menu now (not saved).</summary>
    private async Task<List<OrderItem>> BuildLinesAsync(Guid propertyId, Order order, IReadOnlyList<OrderLineRequest> requests, CancellationToken ct)
    {
        var ids = requests.Select(x => x.MenuItemId).Distinct().ToList();
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Variants).Include(i => i.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Where(i => ids.Contains(i.Id) && i.PropertyId == propertyId).ToDictionaryAsync(i => i.Id, ct);
        var now = clock.GetUtcNow();
        var lines = new List<OrderItem>();
        for (var n = 0; n < requests.Count; n++)
        {
            var x = requests[n];
            var f = $"Items[{n}]";
            if (!items.TryGetValue(x.MenuItemId, out var item))
                throw ReservationService.Field($"{f}.MenuItemId", "That dish isn't on this property's menu. Reload the menu and choose again.");
            if (!item.IsActive) throw ReservationService.Field($"{f}.MenuItemId", $"{item.Name} has been taken off the menu.");
            if (!item.IsAvailable) throw ReservationService.Field($"{f}.MenuItemId", $"{item.Name} is sold out for now. The kitchen switches it back on when it has more.");
            var variants = item.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).ToList();
            MenuItemVariant? variant = null;
            if (x.VariantId is { } vid)
                variant = variants.FirstOrDefault(v => v.Id == vid)
                          ?? throw ReservationService.Field($"{f}.VariantId", $"That portion of {item.Name} isn't offered any more. Choose {Join(variants.Select(v => v.Name))}.");
            else if (variants.Count > 0)
                throw ReservationService.Field($"{f}.VariantId", $"Choose the portion of {item.Name}: {Join(variants.Select(v => v.Name))}.");

            var picked = (x.ModifierIds ?? []).Distinct().ToList();
            var groups = item.ModifierGroups.Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToList();
            var chosen = new List<ChosenModifier>();
            foreach (var mid in picked)
            {
                var g = groups.FirstOrDefault(g => g.Modifiers.Any(m => m.Id == mid && m.IsActive))
                        ?? throw ReservationService.Field($"{f}.ModifierIds", $"One of the choices isn't offered for {item.Name} any more. Reload the menu and choose again.");
                var m = g.Modifiers.Single(m => m.Id == mid);
                chosen.Add(new ChosenModifier(m.Id, g.Name, m.Name, m.PriceDelta));
            }
            foreach (var g in groups)
            {
                var count = chosen.Count(c => g.Modifiers.Any(m => m.Id == c.Id));
                if (count < g.MinSelect)
                    throw ReservationService.Field($"{f}.ModifierIds", g.MinSelect == 1
                        ? $"Choose the {g.Name.ToLowerInvariant()} for {item.Name}."
                        : $"Choose at least {g.MinSelect} of {g.Name.ToLowerInvariant()} for {item.Name}.");
                if (count > g.MaxSelect)
                    throw ReservationService.Field($"{f}.ModifierIds", g.MaxSelect == 1
                        ? $"Choose one {g.Name.ToLowerInvariant()} for {item.Name}, not {count}."
                        : $"Choose at most {g.MaxSelect} of {g.Name.ToLowerInvariant()} for {item.Name}.");
            }
            var unitPrice = RestaurantMath.UnitPrice(variant?.Price ?? item.Price, chosen.Select(c => c.Price));
            var line = new OrderItem
            {
                OrganizationId = order.OrganizationId,
                OrderId = order.Id,
                MenuItemId = item.Id,
                VariantId = variant?.Id,
                Name = item.Name,
                VariantName = variant?.Name,
                FoodType = item.FoodType,
                Modifiers = JsonSerializer.Serialize(chosen, Idempotency.Json),
                Quantity = x.Quantity,
                UnitPrice = unitPrice,
                Amount = RestaurantMath.LineAmount(x.Quantity, unitPrice),
                Note = ReservationService.Clean(x.Note),
                AddedBy = caller.UserId,
                CreatedAt = now.AddMicroseconds(n),   // keeps the order they were added in (timestamps are to the microsecond)
                UpdatedAt = now,
            };
            db.OrderItems.Add(line);
            lines.Add(line);
        }
        return lines;
    }

    private async Task<RestaurantTable> TableAsync(Guid propertyId, Guid? tableId, string field, CancellationToken ct)
    {
        var table = tableId is { } id ? await db.RestaurantTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct) : null;
        if (table is null) throw ReservationService.Field(field, tableId is null ? "Choose the table." : "That table isn't at this property. Reload and choose again.");
        if (!table.IsActive) throw ReservationService.Field(field, $"Table {table.Code} is switched off in the restaurant's tables.");
        return table;
    }

    /// <summary>A table has one open order at a time (also a unique index): say whose it is.</summary>
    private async Task RequireTableFreeAsync(RestaurantTable table, Guid? except, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM restaurant_tables WHERE id = {table.Id} FOR UPDATE", ct);
        var busy = await db.Orders.AsNoTracking()
            .Where(o => o.TableId == table.Id && o.Status == OrderStatuses.Open && o.Id != except).FirstOrDefaultAsync(ct);
        if (busy is null) return;
        var who = await NameAsync(busy.OpenedBy, ct);
        throw new ConflictAppException("TABLE_HAS_ORDER",
            $"Table {table.Code} already has open order {busy.Number}{(who is null ? "" : $" (opened by {who})")}. Add the dishes to that order, or settle it first.",
            new { orderId = busy.Id, number = busy.Number });
    }

    private async Task<string> NotInHouseAsync(Guid propertyId, Guid? unitId, CancellationToken ct)
    {
        if (unitId is null) return "Choose the room the order goes to.";
        var unit = await db.AccommodationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == unitId && u.PropertyId == propertyId, ct);
        return unit is null
            ? "That room isn't at this property. Reload and choose again."
            : $"Nobody is checked in to {unit.Name}, so it can't order room service. Choose an occupied room.";
    }

    private async Task<string> NextNumberAsync(Guid propertyId, string counter, string prefix, CancellationToken ct)
    {
        var n = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({propertyId}, {counter}, 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        return $"{prefix}{n:D5}";
    }

    private static string? ModifierText(string json)
    {
        var mods = JsonSerializer.Deserialize<List<ChosenModifier>>(json, Idempotency.Json) ?? [];
        return mods.Count == 0 ? null : string.Join(", ", mods.Select(m => m.Name));
    }

    private static List<string> ModifierNames(string json) =>
        (JsonSerializer.Deserialize<List<ChosenModifier>>(json, Idempotency.Json) ?? [])
        .Select(m => m.Price > 0 ? $"{m.Name} (+{Money.Rupees(m.Price)})" : m.Name).ToList();

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} or {list[^1]}";
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string TypeWords(string type) => type switch
    {
        OrderTypes.RoomService => "room service",
        OrderTypes.Takeaway => "a takeaway",
        _ => "at a table",
    };

    private async Task<string> WhereAsync(Order o, CancellationToken ct)
    {
        if (o.TableId is { } t)
            return "Table " + await db.RestaurantTables.AsNoTracking().Where(x => x.Id == t).Select(x => x.Code).SingleAsync(ct);
        if (o.UnitId is { } u && o.OrderType == OrderTypes.RoomService)
            return await db.AccommodationUnits.AsNoTracking().Where(x => x.Id == u).Select(x => x.Name).SingleAsync(ct);
        return o.OrderType == OrderTypes.Takeaway ? "Takeaway" : "Room service";
    }

    private async Task<Dictionary<Guid, string>> GuestNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return (await db.Guests.AsNoTracking().Where(g => list.Contains(g.Id)).Select(g => new { g.Id, g.FirstName, g.LastName }).ToListAsync(ct))
            .ToDictionary(g => g.Id, g => string.IsNullOrWhiteSpace(g.LastName) ? g.FirstName : $"{g.FirstName} {g.LastName}");
    }

    private async Task<string?> NameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).SingleOrDefaultAsync(ct);

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private async Task<List<OrderSummaryDto>> SummariesAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var ids = orders.Select(o => o.Id).ToList();
        var lines = await db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).Select(i => new { i.OrderId, i.Status }).ToListAsync(ct);
        var kots = await db.Kots.AsNoTracking().Where(k => ids.Contains(k.OrderId) && KotStatuses.Active.Contains(k.Status))
            .Select(k => new { k.OrderId, k.Status }).ToListAsync(ct);
        var tableIds = orders.Where(o => o.TableId != null).Select(o => o.TableId!.Value).Distinct().ToList();
        var tables = await db.RestaurantTables.AsNoTracking().Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var unitIds = orders.Where(o => o.UnitId != null).Select(o => o.UnitId!.Value).Distinct().ToList();
        var units = await db.AccommodationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var stayIds = orders.Where(o => o.ReservationId != null).Select(o => o.ReservationId!.Value).Distinct().ToList();
        var stays = await db.Reservations.AsNoTracking().Where(r => stayIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.PrimaryGuestId, ct);
        var guests = await GuestNamesAsync(stays.Values, ct);
        var users = await UserNamesAsync(orders.Select(o => o.OpenedBy), ct);
        return orders.Select(o => new OrderSummaryDto(o.Id, o.Number, o.OrderType, o.Status,
                Where(o, o.TableId is { } t ? tables.GetValueOrDefault(t) : null, o.UnitId is { } u ? units.GetValueOrDefault(u) : null),
                o.ReservationId is { } r && stays.TryGetValue(r, out var g) ? guests.GetValueOrDefault(g) : null,
                o.Covers, lines.Count(l => l.OrderId == o.Id && l.Status != OrderItemStatuses.Voided),
                lines.Count(l => l.OrderId == o.Id && l.Status == OrderItemStatuses.New),
                kots.Count(k => k.OrderId == o.Id && k.Status != KotStatuses.Ready), kots.Count(k => k.OrderId == o.Id && k.Status == KotStatuses.Ready),
                o.Total, o.Settlement, o.OpenedBy is { } ob ? users.GetValueOrDefault(ob) : null, o.CreatedAt, o.SettledAt))
            .ToList();
    }

    /// <summary>"Table T4", "Room 104" (room service), "Takeaway". A dine-in order charged to a room still shows its table.</summary>
    private static string Where(Order o, string? table, string? unit) => o.OrderType switch
    {
        OrderTypes.DineIn => $"Table {table}",
        OrderTypes.RoomService => unit ?? "Room service",
        _ => "Takeaway",
    };

    private async Task<List<KotDto>> KotDtosAsync(IReadOnlyList<Kot> kots, CancellationToken ct)
    {
        var ids = kots.Select(k => k.Id).ToList();
        var lines = await db.KotItems.AsNoTracking().Where(i => ids.Contains(i.KotId)).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var lineIds = lines.Select(l => l.OrderItemId).ToList();
        var foodTypes = await db.OrderItems.AsNoTracking().Where(i => lineIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.FoodType, ct);
        var orderIds = kots.Select(k => k.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
        var summaries = (await SummariesAsync(orders, ct)).ToDictionary(s => s.Id);
        var users = await UserNamesAsync(kots.Select(k => k.SentBy), ct);
        return kots.Select(k => new KotDto(k.Id, k.Number, k.OrderId, summaries[k.OrderId].Number, summaries[k.OrderId].Where, k.Status, k.SentAt,
                k.SentBy is { } s ? users.GetValueOrDefault(s) : null, k.StartedAt, k.ReadyAt, k.Note,
                lines.Where(l => l.KotId == k.Id).Select(l => new KotItemDto(l.Id, l.Name, l.Quantity, l.Modifiers, l.Note,
                    foodTypes.GetValueOrDefault(l.OrderItemId, FoodTypes.Veg), l.VoidedAt != null, l.VoidReason)).ToList(),
                k.Version))
            .ToList();
    }

    private async Task<OrderDto> ToDtoAsync(Order o, CancellationToken ct)
    {
        var lines = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(ct);
        var kots = await db.Kots.AsNoTracking().Where(k => k.OrderId == o.Id).OrderBy(k => k.SentAt).ToListAsync(ct);
        var table = o.TableId is { } t ? await db.RestaurantTables.AsNoTracking().Where(x => x.Id == t).Select(x => x.Code).SingleAsync(ct) : null;
        var unit = o.UnitId is { } u
            ? await db.AccommodationUnits.AsNoTracking().Where(x => x.Id == u).Select(x => x.Name).SingleAsync(ct) : null;
        var stay = o.ReservationId is { } rid ? await db.Reservations.AsNoTracking().SingleAsync(x => x.Id == rid, ct) : null;
        var guest = stay is null ? null : (await GuestNamesAsync([stay.PrimaryGuestId], ct)).GetValueOrDefault(stay.PrimaryGuestId);
        var folio = o.FolioId is { } fid ? await db.Folios.AsNoTracking().Where(f => f.Id == fid).Select(f => f.Number).SingleAsync(ct) : null;
        var users = await UserNamesAsync(new[] { o.OpenedBy, o.SettledBy, o.VoidedBy, o.DiscountedBy }.Concat(lines.Select(l => l.VoidedBy)), ct);
        string? User(Guid? id) => id is { } x ? users.GetValueOrDefault(x) : null;

        var outcome = await TaxAsync(o, ct);
        var taxes = outcome.Lines.Select(l => new DocumentTaxDto(l.Tax.Code, l.Tax.Rate, l.Tax.TaxableValue, l.Tax.Amount)).ToList();

        var a = await access.GetAsync(ct);
        var actions = new List<string>();
        if (o.Status == OrderStatuses.Open)
        {
            var live = lines.Where(l => l.Status != OrderItemStatuses.Voided).ToList();
            if (a.Has(Perm.PosOrderUpdate, o.PropertyId))
            {
                actions.Add("add");
                actions.Add("edit");
                if (live.Any(l => l.Status == OrderItemStatuses.New)) actions.Add("send");
                if (kots.Any(k => k.Status == KotStatuses.Ready)) actions.Add("serve");
            }
            if (a.Has(Perm.PosDiscount, o.PropertyId) && live.Count > 0) actions.Add("discount");
            var settleable = live.Count > 0 && live.All(l => l.Status == OrderItemStatuses.Sent) && kots.All(k => !KotStatuses.Active.Contains(k.Status));
            if (settleable && a.Has(Perm.PaymentCreate, o.PropertyId)) actions.Add("pay");
            if (settleable && a.Has(Perm.FolioPostCharge, o.PropertyId)) actions.Add("charge");
            if (a.Has(Perm.PosOrderVoid, o.PropertyId)) actions.Add("void");
        }

        return new OrderDto(o.Id, o.Number, o.OrderType, o.Status, Where(o, table, unit), o.TableId, table, o.UnitId, unit,
            o.ReservationId, stay?.Reference, guest, o.Covers, o.Note, o.BusinessDate,
            lines.Select(l =>
            {
                var k = l.KotId is { } kid ? kots.Single(x => x.Id == kid) : null;
                return new OrderItemDto(l.Id, l.MenuItemId, l.Name, l.VariantName, l.FoodType, ModifierNames(l.Modifiers), l.Quantity, l.UnitPrice,
                    l.Amount, l.Note, l.Status, l.KotId, k?.Number, k?.Status, l.VoidReason, User(l.VoidedBy));
            }).ToList(),
            kots.Select(k => new OrderKotDto(k.Id, k.Number, k.Status, k.SentAt, k.ReadyAt, k.ServedAt, lines.Count(l => l.KotId == k.Id))).ToList(),
            o.Subtotal, o.DiscountKind, o.DiscountValue, o.DiscountAmount, o.DiscountReason, User(o.DiscountedBy),
            o.Subtotal - o.DiscountAmount, taxes, o.TaxAmount, o.Total, outcome.NoTaxReason,
            o.Settlement, o.PaymentMethod, o.PaymentReference, o.FolioId, folio, o.SettledAt, User(o.SettledBy),
            o.VoidedAt, User(o.VoidedBy), o.VoidReason, User(o.OpenedBy), o.CreatedAt, actions, o.Version);
    }

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
}
