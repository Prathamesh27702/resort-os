using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Restaurant;

namespace Resort.Application.Restaurant;

/// <summary>
/// The restaurant's menu and tables (Sprint 14). Nothing here is deleted: a section, dish, portion, choice or table that
/// is no longer offered is switched off, because old orders point to it. Orders copy a dish's name and price when it is
/// ordered, so editing the menu never changes an order already taken.
/// </summary>
public sealed class MenuService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock)
{
    /// <summary>The menu by section. <paramref name="all"/>: also switched-off sections and items (the menu editor).</summary>
    public async Task<MenuDto> GetAsync(Guid propertyId, bool all, CancellationToken ct)
    {
        var categories = await db.MenuCategories.AsNoTracking().Where(c => c.PropertyId == propertyId && (all || c.IsActive))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Variants).Include(i => i.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Where(i => i.PropertyId == propertyId && (all || i.IsActive)).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return new MenuDto(categories.Select(c => new MenuCategoryDto(c.Id, c.Name, c.SortOrder, c.IsActive,
            items.Where(i => i.CategoryId == c.Id).Select(ToDto).ToList())).ToList());
    }

    public async Task<MenuCategoryDto> SaveCategoryAsync(Guid propertyId, Guid? id, SaveMenuCategoryRequest r, CancellationToken ct)
    {
        var name = r.Name.Trim();
        var names = await db.MenuCategories.AsNoTracking().Where(c => c.PropertyId == propertyId && c.Id != id).Select(c => c.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"The menu already has a section called {name}.");
        MenuCategory category;
        if (id is { } cid)
            category = await db.MenuCategories.SingleOrDefaultAsync(c => c.Id == cid && c.PropertyId == propertyId, ct)
                       ?? throw new NotFoundAppException("That menu section doesn't exist at this property. Reload the menu.");
        else
        {
            category = new MenuCategory { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.MenuCategories.Add(category);
        }
        category.Name = name;
        category.SortOrder = r.SortOrder;
        category.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Variants).Include(i => i.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Where(i => i.CategoryId == category.Id).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return new MenuCategoryDto(category.Id, category.Name, category.SortOrder, category.IsActive, items.Select(ToDto).ToList());
    }

    /// <summary>Creates or replaces a dish with its portions and choices. Ones sent with an Id are kept; ones left out are
    /// switched off.</summary>
    public async Task<MenuItemDto> SaveItemAsync(Guid propertyId, Guid? id, SaveMenuItemRequest r, CancellationToken ct)
    {
        var category = await db.MenuCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CategoryId && c.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.CategoryId), "That menu section doesn't exist at this property. Reload and choose again.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.MenuItems.AnyAsync(i => i.PropertyId == propertyId && i.Code == code && i.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another dish already has the code {code}. Codes are unique on the menu.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        MenuItem item;
        if (id is { } iid)
        {
            item = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups).ThenInclude(g => g.Modifiers)
                       .SingleOrDefaultAsync(i => i.Id == iid && i.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That dish doesn't exist at this property. Reload the menu.");
            if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, item, v);
        }
        else
        {
            item = new MenuItem { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.MenuItems.Add(item);
        }
        item.CategoryId = category.Id;
        item.Code = code;
        item.Name = r.Name.Trim();
        item.Description = ReservationService.Clean(r.Description);
        item.FoodType = r.FoodType.Trim().ToUpperInvariant();
        item.Price = r.Price;
        item.IsActive = r.IsActive;
        item.SortOrder = r.SortOrder;
        var now = clock.GetUtcNow();

        var variants = r.Variants ?? [];
        foreach (var v in item.Variants.Where(v => v.IsActive && !variants.Any(x => x.Id == v.Id))) v.IsActive = false;
        for (var n = 0; n < variants.Count; n++)
        {
            var x = variants[n];
            var variant = x.Id is { } vid
                ? item.Variants.FirstOrDefault(v => v.Id == vid) ?? throw ReservationService.Field($"Variants[{n}].Id", $"That portion isn't on {item.Name}. Reload and try again.")
                : Add(item.Variants, new MenuItemVariant { OrganizationId = item.OrganizationId, MenuItemId = item.Id, CreatedAt = now });
            if (x.Id is null) db.MenuItemVariants.Add(variant);
            variant.Name = x.Name.Trim();
            variant.Price = x.Price;
            variant.SortOrder = n;
            variant.IsActive = true;
        }

        var groups = r.ModifierGroups ?? [];
        foreach (var g in item.ModifierGroups.Where(g => g.IsActive && !groups.Any(x => x.Id == g.Id))) g.IsActive = false;
        for (var n = 0; n < groups.Count; n++)
        {
            var x = groups[n];
            var group = x.Id is { } gid
                ? item.ModifierGroups.FirstOrDefault(g => g.Id == gid) ?? throw ReservationService.Field($"ModifierGroups[{n}].Id", $"That group of choices isn't on {item.Name}. Reload and try again.")
                : Add(item.ModifierGroups, new ModifierGroup { OrganizationId = item.OrganizationId, MenuItemId = item.Id, CreatedAt = now });
            if (x.Id is null) db.ModifierGroups.Add(group);
            group.Name = x.Name.Trim();
            group.MinSelect = x.MinSelect;
            group.MaxSelect = x.MaxSelect;
            group.SortOrder = n;
            group.IsActive = true;
            foreach (var m in group.Modifiers.Where(m => m.IsActive && !x.Modifiers.Any(y => y.Id == m.Id))) m.IsActive = false;
            for (var k = 0; k < x.Modifiers.Count; k++)
            {
                var y = x.Modifiers[k];
                var modifier = y.Id is { } mid
                    ? group.Modifiers.FirstOrDefault(m => m.Id == mid) ?? throw ReservationService.Field($"ModifierGroups[{n}].Modifiers[{k}].Id", $"That choice isn't in {group.Name}. Reload and try again.")
                    : Add(group.Modifiers, new Modifier { OrganizationId = item.OrganizationId, GroupId = group.Id, CreatedAt = now });
                if (y.Id is null) db.Modifiers.Add(modifier);
                modifier.Name = y.Name.Trim();
                modifier.PriceDelta = y.PriceDelta;
                modifier.SortOrder = k;
                modifier.IsActive = true;
            }
        }
        item.UpdatedAt = now;   // a change to a portion or choice moves the dish's version on too
        audit.Reason = id is null ? $"{item.Name} added to the menu" : null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(item);
    }

    /// <summary>Sold out, or back on. The waiters' menu shows it at once; dishes already ordered are not affected.</summary>
    public async Task<MenuItemDto> SetAvailabilityAsync(Guid propertyId, Guid id, SetAvailabilityRequest r, CancellationToken ct)
    {
        var item = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups).ThenInclude(g => g.Modifiers)
                       .SingleOrDefaultAsync(i => i.Id == id && i.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("That dish doesn't exist at this property. Reload the menu.");
        if (item.IsAvailable != r.IsAvailable)
        {
            item.IsAvailable = r.IsAvailable;
            audit.Reason = r.IsAvailable ? $"{item.Name} back on" : $"{item.Name} sold out";
            await db.SaveChangesAsync(ct);
        }
        return ToDto(item);
    }

    // ------------------------------------------------------------------ tables

    public async Task<IReadOnlyList<RestaurantTableDto>> TablesAsync(Guid propertyId, CancellationToken ct) =>
        (await db.RestaurantTables.AsNoTracking().Where(t => t.PropertyId == propertyId).OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<RestaurantTableDto> SaveTableAsync(Guid propertyId, Guid? id, SaveRestaurantTableRequest r, CancellationToken ct)
    {
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.RestaurantTables.AnyAsync(t => t.PropertyId == propertyId && t.Code == code && t.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"There is already a table {code}.");
        RestaurantTable table;
        if (id is { } tid)
        {
            table = await db.RestaurantTables.SingleOrDefaultAsync(t => t.Id == tid && t.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That table doesn't exist at this property. Reload.");
            if (!r.IsActive && table.IsActive
                && await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.TableId == tid && o.Status == OrderStatuses.Open, ct) is { } open)
                throw ReservationService.Field(nameof(r.IsActive), $"Table {table.Code} has open order {open.Number}. Settle or move it before switching the table off.");
        }
        else
        {
            table = new RestaurantTable { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.RestaurantTables.Add(table);
        }
        table.Code = code;
        table.Seats = r.Seats;
        table.Area = ReservationService.Clean(r.Area);
        table.SortOrder = r.SortOrder;
        table.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(table);
    }

    private static T Add<T>(List<T> list, T value)
    {
        list.Add(value);
        return value;
    }

    internal static MenuItemDto ToDto(MenuItem i) => new(i.Id, i.CategoryId, i.Code, i.Name, i.Description, i.FoodType, i.Price, i.IsAvailable,
        i.IsActive, i.SortOrder,
        i.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => new MenuVariantDto(v.Id, v.Name, v.Price)).ToList(),
        i.ModifierGroups.Where(g => g.IsActive).OrderBy(g => g.SortOrder).Select(g => new ModifierGroupDto(g.Id, g.Name, g.MinSelect, g.MaxSelect,
            g.Modifiers.Where(m => m.IsActive).OrderBy(m => m.SortOrder).Select(m => new ModifierDto(m.Id, m.Name, m.PriceDelta)).ToList())).ToList(),
        i.Version);

    private static RestaurantTableDto ToDto(RestaurantTable t) => new(t.Id, t.Code, t.Seats, t.Area, t.SortOrder, t.IsActive);
}
