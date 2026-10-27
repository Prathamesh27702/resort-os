using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Inventory;
using Resort.Domain.Organizations;

namespace Resort.Application.Inventory;

/// <summary>
/// Stock (Sprint 15, brief §36, §58): the items a property keeps, where they are, and every change to them. Stock changes
/// only through the ledger (<see cref="StockLedger"/>): an opening quantity, stock received, wastage, a correction, a
/// transfer between stores, a physical count's difference, or a served dish's recipe. Every change a person makes carries
/// a reason and is audited; nothing in the ledger is ever changed or deleted.
/// </summary>
public sealed class InventoryService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, StockLedger ledger)
{
    // ------------------------------------------------------------------ reading

    public async Task<InventoryDto> GetAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var units = await UnitsAsync(ct);
        var categories = await db.InventoryCategories.AsNoTracking().Where(c => c.PropertyId == propertyId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var locations = await db.StockLocations.AsNoTracking().Where(l => l.PropertyId == propertyId)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Code).ToListAsync(ct);
        var items = await db.InventoryItems.AsNoTracking().Where(i => i.PropertyId == propertyId).ToListAsync(ct);
        var levels = await db.StockLevels.AsNoTracking().Where(l => l.PropertyId == propertyId).ToListAsync(ct);
        var dtos = items
            .OrderBy(i => categories.FindIndex(c => c.Id == i.CategoryId)).ThenBy(i => i.Name)
            .Select(i => ToDto(i, categories, locations, units, levels)).ToList();
        return new InventoryDto(units, categories.Select(ToDto).ToList(), locations.Select(ToDto).ToList(), dtos,
            dtos.Where(i => i.IsActive).Sum(i => i.Value), dtos.Count(i => i.IsActive && i.Low));
    }

    /// <summary>The ledger, newest first: of one item, or of the whole property; optionally of one type and one period
    /// (property dates). At most <paramref name="take"/> rows (default 100).</summary>
    public async Task<StockMovementListDto> MovementsAsync(Guid propertyId, Guid? itemId, string? type, DateOnly? from, DateOnly? to, int? take,
        CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var q = db.StockMovements.AsNoTracking().Where(m => m.PropertyId == propertyId);
        if (itemId is { } iid) q = q.Where(m => m.ItemId == iid);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var movementType = type.Trim().ToUpperInvariant();
            q = q.Where(m => m.MovementType == movementType);
        }
        if (from is { } f)
        {
            var start = property.StartOfDay(f);
            q = q.Where(m => m.CreatedAt >= start);
        }
        if (to is { } t)
        {
            var end = property.StartOfDay(t.AddDays(1));
            q = q.Where(m => m.CreatedAt < end);
        }
        var n = Math.Clamp(take ?? 100, 1, 500);
        var rows = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(n + 1).ToListAsync(ct);
        return new StockMovementListDto(await MovementDtosAsync(rows.Take(n).ToList(), ct), rows.Count > n);
    }

    // ------------------------------------------------------------------ set-up: categories, locations, items

    public async Task<InventoryCategoryDto> SaveCategoryAsync(Guid propertyId, Guid? id, SaveInventoryCategoryRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var name = r.Name.Trim();
        var names = await db.InventoryCategories.AsNoTracking().Where(c => c.PropertyId == propertyId && c.Id != id).Select(c => c.Name).ToListAsync(ct);
        if (names.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a stock group called {name}.");
        InventoryCategory category;
        if (id is { } cid)
            category = await db.InventoryCategories.SingleOrDefaultAsync(c => c.Id == cid && c.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That stock group doesn't exist at this property. Reload the page.");
        else
        {
            category = new InventoryCategory { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.InventoryCategories.Add(category);
        }
        category.Name = name;
        category.SortOrder = r.SortOrder;
        category.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(category);
    }

    /// <summary>Saves a store. Marking it the kitchen takes the mark off the old kitchen (one per property).</summary>
    public async Task<StockLocationDto> SaveLocationAsync(Guid propertyId, Guid? id, SaveStockLocationRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.StockLocations.AnyAsync(l => l.PropertyId == propertyId && l.Code == code && l.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another store already has the code {code}.");
        if (r.IsKitchen && !r.IsActive)
            throw ReservationService.Field(nameof(r.IsActive), "The kitchen store can't be switched off: served dishes take their ingredients from it. Mark another store as the kitchen first.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockLocation location;
        if (id is { } lid)
        {
            location = await db.StockLocations.SingleOrDefaultAsync(l => l.Id == lid && l.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That store doesn't exist at this property. Reload the page.");
            if (!r.IsActive && location.IsActive && await db.StockLevels.AnyAsync(l => l.LocationId == lid && l.OnHand != 0, ct))
                throw ReservationService.Field(nameof(r.IsActive),
                    $"{location.Name} still has stock. Move it to another store (a transfer) or count it to zero before switching the store off.");
        }
        else location = new StockLocation { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
        if (r.IsKitchen && !location.IsKitchen)
        {
            var old = await db.StockLocations.Where(l => l.PropertyId == propertyId && l.IsKitchen && l.Id != location.Id).ToListAsync(ct);
            foreach (var o in old) o.IsKitchen = false;
            await db.SaveChangesAsync(ct);   // the old kitchen first: one kitchen per property (unique index)
        }
        if (id is null) db.StockLocations.Add(location);
        location.Code = code;
        location.Name = r.Name.Trim();
        location.IsKitchen = r.IsKitchen;
        location.SortOrder = r.SortOrder;
        location.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(location);
    }

    /// <summary>Creates or edits a stocked item. A new item may come with its opening stock (an OPENING movement at its
    /// unit cost, into the kitchen unless another store is given). A property without any store gets a Kitchen store.</summary>
    public async Task<InventoryItemDto> SaveItemAsync(Guid propertyId, Guid? id, SaveInventoryItemRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var category = await db.InventoryCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CategoryId && c.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.CategoryId), "That stock group doesn't exist at this property. Reload and choose again.");
        var unit = await db.InventoryUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == r.UnitId, ct)
                   ?? throw ReservationService.Field(nameof(r.UnitId), "Choose the unit the item is counted in (kg, g, l, ml, pcs or dozen).");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.InventoryItems.AnyAsync(i => i.PropertyId == propertyId && i.Code == code && i.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another stock item already has the code {code}. Codes are unique at a property.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        InventoryItem item;
        if (id is { } iid)
        {
            await ledger.LockItemsAsync(propertyId, [iid], ct);
            item = await db.InventoryItems.SingleOrDefaultAsync(i => i.Id == iid && i.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That stock item doesn't exist at this property. Reload the page.");
            if (r.Version is { } v && item.Version != v)
                throw new ConflictAppException("CONCURRENCY_CONFLICT",
                    $"Someone else changed {item.Name} a moment ago. Reload to see their change, then make yours again.");
            if (r.Version is { } ok) ConcurrencyGuard.ExpectVersion(db, item, ok);
            if (item.UnitId != unit.Id && await db.StockMovements.AnyAsync(m => m.ItemId == item.Id, ct))
                throw ReservationService.Field(nameof(r.UnitId),
                    $"{item.Name} already has stock movements in {(await db.InventoryUnits.AsNoTracking().SingleAsync(u => u.Id == item.UnitId, ct)).Code}, so its unit can't change. Add a new item for the new unit.");
            if (!r.IsActive && item.IsActive && await db.RecipeIngredients.AnyAsync(x => x.ItemId == item.Id && db.Recipes.Any(rc => rc.Id == x.RecipeId && rc.IsActive), ct))
                throw ReservationService.Field(nameof(r.IsActive), $"{item.Name} is used in a recipe. Take it out of the recipes first, then switch it off.");
            if (r.OpeningQuantity is > 0)
                throw ReservationService.Field(nameof(r.OpeningQuantity), "Opening stock is only for a new item. Record stock received instead.");
        }
        else
        {
            item = new InventoryItem { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.InventoryItems.Add(item);
        }
        item.CategoryId = category.Id;
        item.Code = code;
        item.Name = r.Name.Trim();
        item.UnitId = unit.Id;
        item.UnitCost = r.UnitCost;
        item.ReorderLevel = r.ReorderLevel;
        item.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);

        if (id is null && r.OpeningQuantity is > 0 and var opening)
        {
            var location = await LocationAsync(propertyId, r.OpeningLocationId ?? (await KitchenAsync(propertyId, ct)).Id, nameof(r.OpeningLocationId), ct);
            ledger.Add(item, location.Id, MovementTypes.Opening, opening, item.UnitCost, "Opening stock");
            audit.Reason = $"{item.Name} added with {InventoryMath.Quantity(opening, unit.Code)} in {location.Name}";
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await ItemDtoAsync(propertyId, item.Id, ct);
    }

    // ------------------------------------------------------------------ changing stock

    /// <summary>
    /// Records a change of stock with its reason (inventory.adjust; audited). Stock out (wastage, expired, damaged, a
    /// negative correction, a transfer) can't take a store below zero. Stock received at a cost moves the item's average
    /// cost.
    /// </summary>
    public async Task<StockAdjustmentDto> AdjustAsync(Guid propertyId, CreateStockAdjustmentRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var kind = r.Kind.Trim().ToUpperInvariant();
        var reason = ReservationService.Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ledger.LockItemsAsync(propertyId, [r.ItemId], ct);
        var item = await db.InventoryItems.SingleOrDefaultAsync(i => i.Id == r.ItemId && i.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.ItemId), "That stock item doesn't exist at this property. Reload and choose again.");
        if (!item.IsActive) throw ReservationService.Field(nameof(r.ItemId), $"{item.Name} is switched off. Switch it on to change its stock.");
        var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(u => u.Id == item.UnitId, ct);
        var location = await LocationAsync(propertyId, r.LocationId, nameof(r.LocationId), ct);
        StockLocation? to = null;
        if (kind == AdjustmentKinds.Transfer)
        {
            to = await LocationAsync(propertyId, r.ToLocationId ?? Guid.Empty, nameof(r.ToLocationId), ct);
            if (to.Id == location.Id) throw ReservationService.Field(nameof(r.ToLocationId), "Choose a different store to move the stock to.");
        }

        var quantity = kind switch
        {
            AdjustmentKinds.Received => r.Quantity,
            AdjustmentKinds.Correction => r.Quantity,
            _ => -r.Quantity,
        };
        var onHand = await ledger.OnHandAsync(item.Id, location.Id, ct);
        if (quantity < 0 && onHand + quantity < 0)
            throw ReservationService.Field(nameof(r.Quantity), onHand <= 0
                ? $"{location.Name} has no {item.Name} on record ({InventoryMath.Quantity(onHand, unit.Code)}), so none can be taken out. Count it first if there is some."
                : $"{location.Name} has only {InventoryMath.Quantity(onHand, unit.Code)} of {item.Name}, so {InventoryMath.Quantity(-quantity, unit.Code)} can't be taken out.");

        var cost = item.UnitCost;
        if (kind == AdjustmentKinds.Received)
        {
            cost = r.UnitCost ?? item.UnitCost;
            item.UnitCost = InventoryMath.AverageCost(await ledger.TotalOnHandAsync(item.Id, ct), item.UnitCost, quantity, cost);
            item.UpdatedAt = clock.GetUtcNow();
        }
        var adjustment = new StockAdjustment
        {
            OrganizationId = item.OrganizationId,
            PropertyId = propertyId,
            Number = await NextNumberAsync(propertyId, "stock_adjustment", "SA", ct),
            Kind = kind,
            ItemId = item.Id,
            LocationId = location.Id,
            ToLocationId = to?.Id,
            Quantity = quantity,
            UnitCost = cost,
            Reason = reason,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
        };
        db.StockAdjustments.Add(adjustment);
        var type = kind switch
        {
            AdjustmentKinds.Received => MovementTypes.Received,
            AdjustmentKinds.Correction => MovementTypes.Adjustment,
            AdjustmentKinds.Transfer => MovementTypes.TransferOut,
            _ => MovementTypes.Wastage,
        };
        var note = kind == AdjustmentKinds.Transfer ? $"To {to!.Name}: {reason}" : reason;
        ledger.Add(item, location.Id, type, quantity, cost, note, adjustmentId: adjustment.Id);
        if (to is not null) ledger.Add(item, to.Id, MovementTypes.TransferIn, -quantity, cost, $"From {location.Name}: {reason}", adjustmentId: adjustment.Id);
        audit.Reason = $"{adjustment.Number} {AdjustmentKinds.Words(kind)}: {InventoryMath.Quantity(Math.Abs(quantity), unit.Code)} {item.Name}" +
                       (to is null ? $" in {location.Name}" : $" from {location.Name} to {to.Name}") + $" — {reason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await AdjustmentDtosAsync([adjustment], ct)).Single();
    }

    /// <summary>
    /// A physical count of one store (inventory.adjust; audited): each counted item's difference from the system is posted
    /// as a COUNT movement, so on hand becomes what was counted. A line that changes stock needs a reason.
    /// </summary>
    public async Task<StockCountResultDto> CountAsync(Guid propertyId, CreateStockCountRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        if (r.Lines.Select(l => l.ItemId).Distinct().Count() != r.Lines.Count)
            throw ReservationService.Field(nameof(r.Lines), "An item is counted twice. Count each item once.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var location = await LocationAsync(propertyId, r.LocationId, nameof(r.LocationId), ct);
        await ledger.LockItemsAsync(propertyId, r.Lines.Select(l => l.ItemId), ct);
        var ids = r.Lines.Select(l => l.ItemId).ToList();
        var items = await db.InventoryItems.Where(i => ids.Contains(i.Id) && i.PropertyId == propertyId).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var now = clock.GetUtcNow();
        var counts = new List<StockCount>();
        for (var n = 0; n < r.Lines.Count; n++)
        {
            var x = r.Lines[n];
            if (!items.TryGetValue(x.ItemId, out var item))
                throw ReservationService.Field($"Lines[{n}].ItemId", "That stock item doesn't exist at this property. Reload and count again.");
            var system = await ledger.OnHandAsync(item.Id, location.Id, ct);
            var difference = x.CountedQuantity - system;
            var reason = ReservationService.Clean(x.Reason);
            if (difference != 0 && reason is null)
                throw ReservationService.Field($"Lines[{n}].Reason",
                    $"{item.Name}: the system has {InventoryMath.Quantity(system, units[item.UnitId].Code)} and you counted {InventoryMath.Quantity(x.CountedQuantity, units[item.UnitId].Code)}. Say why they differ (e.g. spillage, not recorded).");
            var count = new StockCount
            {
                OrganizationId = item.OrganizationId,
                PropertyId = propertyId,
                Number = await NextNumberAsync(propertyId, "stock_count", "SC", ct),
                ItemId = item.Id,
                LocationId = location.Id,
                SystemQuantity = system,
                CountedQuantity = x.CountedQuantity,
                Difference = difference,
                UnitCost = item.UnitCost,
                Reason = difference == 0 ? null : reason,
                CountedAt = now,
                CreatedBy = caller.UserId,
            };
            db.StockCounts.Add(count);
            counts.Add(count);
            if (difference != 0) ledger.Add(item, location.Id, MovementTypes.Count, difference, item.UnitCost, reason, countId: count.Id);
        }
        var changed = counts.Count(c => c.Difference != 0);
        audit.Reason = $"Stock count of {location.Name}: {counts.Count} item{(counts.Count == 1 ? "" : "s")} counted, {changed} changed";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var lines = counts.Select(c => new StockCountDto(c.Id, c.Number, c.ItemId, items[c.ItemId].Name, units[items[c.ItemId].UnitId].Code,
            c.SystemQuantity, c.CountedQuantity, c.Difference, InventoryMath.Value(c.Difference, c.UnitCost), c.Reason)).ToList();
        return new StockCountResultDto(location.Name, lines, lines.Sum(l => l.Value));
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    internal async Task<List<InventoryUnitDto>> UnitsAsync(CancellationToken ct) =>
        (await db.InventoryUnits.AsNoTracking().OrderBy(u => u.SortOrder).ToListAsync(ct))
        .Select(u => new InventoryUnitDto(u.Id, u.Code, u.Name, u.Dimension, u.Factor)).ToList();

    private async Task<StockLocation> LocationAsync(Guid propertyId, Guid id, string field, CancellationToken ct)
    {
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id && l.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(field, "That store doesn't exist at this property. Reload and choose again.");
        if (!location.IsActive) throw ReservationService.Field(field, $"{location.Name} is switched off. Choose another store.");
        return location;
    }

    /// <summary>The kitchen store; a property with no store at all gets one (KITCHEN).</summary>
    private async Task<StockLocation> KitchenAsync(Guid propertyId, CancellationToken ct)
    {
        var kitchen = await db.StockLocations.SingleOrDefaultAsync(l => l.PropertyId == propertyId && l.IsKitchen, ct);
        if (kitchen is not null) return kitchen;
        if (await db.StockLocations.AnyAsync(l => l.PropertyId == propertyId, ct))
            throw ReservationService.Field(nameof(SaveInventoryItemRequest.OpeningLocationId), "Choose the store the opening stock is in.");
        kitchen = new StockLocation { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId, Code = "KITCHEN", Name = "Kitchen", IsKitchen = true };
        db.StockLocations.Add(kitchen);
        await db.SaveChangesAsync(ct);
        return kitchen;
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

    private async Task<InventoryItemDto> ItemDtoAsync(Guid propertyId, Guid itemId, CancellationToken ct)
    {
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == itemId, ct);
        var categories = await db.InventoryCategories.AsNoTracking().Where(c => c.PropertyId == propertyId).ToListAsync(ct);
        var locations = await db.StockLocations.AsNoTracking().Where(l => l.PropertyId == propertyId).OrderBy(l => l.SortOrder).ThenBy(l => l.Code).ToListAsync(ct);
        var levels = await db.StockLevels.AsNoTracking().Where(l => l.ItemId == itemId).ToListAsync(ct);
        return ToDto(item, categories, locations, await UnitsAsync(ct), levels);
    }

    private static InventoryItemDto ToDto(InventoryItem i, IReadOnlyList<InventoryCategory> categories, IReadOnlyList<StockLocation> locations,
        IReadOnlyList<InventoryUnitDto> units, IReadOnlyList<StockLevel> levels)
    {
        var unit = units.Single(u => u.Id == i.UnitId);
        var at = locations.Select(l => new StockAtDto(l.Id, l.Code, levels.FirstOrDefault(x => x.ItemId == i.Id && x.LocationId == l.Id)?.OnHand ?? 0m))
            .Where(x => x.OnHand != 0 || locations.Single(l => l.Id == x.LocationId).IsActive).ToList();
        var onHand = at.Sum(x => x.OnHand);
        return new InventoryItemDto(i.Id, i.CategoryId, categories.FirstOrDefault(c => c.Id == i.CategoryId)?.Name ?? "", i.Code, i.Name, i.UnitId, unit.Code,
            unit.Dimension, i.UnitCost, i.ReorderLevel, i.IsActive, onHand, InventoryMath.Value(Math.Max(0, onHand), i.UnitCost),
            i.ReorderLevel is { } r && onHand <= r, at.Any(x => x.OnHand < 0), at, i.Version);
    }

    private static InventoryCategoryDto ToDto(InventoryCategory c) => new(c.Id, c.Name, c.SortOrder, c.IsActive);

    private static StockLocationDto ToDto(StockLocation l) => new(l.Id, l.Code, l.Name, l.IsKitchen, l.SortOrder, l.IsActive);

    private async Task<List<StockAdjustmentDto>> AdjustmentDtosAsync(IReadOnlyList<StockAdjustment> list, CancellationToken ct)
    {
        var itemIds = list.Select(a => a.ItemId).ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var locationIds = list.SelectMany(a => new[] { a.LocationId, a.ToLocationId ?? Guid.Empty }).ToList();
        var locations = await db.StockLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var ids = list.Select(a => a.Id).ToList();
        var after = await db.StockMovements.AsNoTracking().Where(m => m.AdjustmentId != null && ids.Contains(m.AdjustmentId.Value) && m.MovementType != MovementTypes.TransferIn)
            .ToDictionaryAsync(m => m.AdjustmentId!.Value, m => m.OnHandAfter, ct);
        var names = await UserNamesAsync(list.Select(a => a.CreatedBy), ct);
        return list.Select(a => new StockAdjustmentDto(a.Id, a.Number, a.Kind, a.ItemId, items[a.ItemId].Name, units[items[a.ItemId].UnitId].Code,
            a.LocationId, locations[a.LocationId].Name, a.ToLocationId, a.ToLocationId is { } t ? locations[t].Name : null, a.Quantity, a.UnitCost,
            InventoryMath.Value(a.Quantity, a.UnitCost), a.Reason, after.GetValueOrDefault(a.Id),
            a.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, a.CreatedAt)).ToList();
    }

    private async Task<List<StockMovementDto>> MovementDtosAsync(IReadOnlyList<StockMovement> rows, CancellationToken ct)
    {
        var itemIds = rows.Select(m => m.ItemId).Distinct().ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var locations = await db.StockLocations.AsNoTracking().Where(l => rows.Select(m => m.LocationId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var adjIds = rows.Where(m => m.AdjustmentId != null).Select(m => m.AdjustmentId!.Value).Distinct().ToList();
        var adjustments = await db.StockAdjustments.AsNoTracking().Where(a => adjIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        var countIds = rows.Where(m => m.CountId != null).Select(m => m.CountId!.Value).Distinct().ToList();
        var counts = await db.StockCounts.AsNoTracking().Where(c => countIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var grnItemIds = rows.Where(m => m.GrnItemId != null).Select(m => m.GrnItemId!.Value).Distinct().ToList();
        var grns = await db.GrnItems.AsNoTracking().Where(g => grnItemIds.Contains(g.Id))
            .Join(db.GoodsReceivedNotes.AsNoTracking(), g => g.GrnId, n => n.Id, (g, n) => new { g.Id, n.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var names = await UserNamesAsync(rows.Select(m => m.CreatedBy), ct);
        return rows.Select(m => new StockMovementDto(m.Id, m.CreatedAt, m.ItemId, items[m.ItemId].Name, units[items[m.ItemId].UnitId].Code,
            locations[m.LocationId].Code, m.MovementType, m.Quantity, m.UnitCost, m.Value, m.OnHandAfter,
            m.AdjustmentId is { } a ? adjustments.GetValueOrDefault(a) : m.CountId is { } c ? counts.GetValueOrDefault(c)
                : m.GrnItemId is { } g ? grns.GetValueOrDefault(g) : null,
            m.Note, m.CreatedBy is { } u ? names.GetValueOrDefault(u) : null)).ToList();
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }
}
