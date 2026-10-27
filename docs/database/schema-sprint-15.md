# Sprint 15 — schema reference (inventory and recipe costing)

Source of truth: `database/migrations/V017__inventory.sql`. Dev seed: `database/seed/dev/S012__inventory.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.25](../architecture/system-design.md#425-the-stock-ledger-recipes-and-food-cost).

Inventory reuses the restaurant of Sprint 14: a recipe belongs to a dish (`menu_items`) or one of its portions
(`menu_item_variants`), and when a kitchen ticket is served (`kots.status = SERVED`) each of its live lines uses its recipe
from the kitchen store. Numbers come from `property_counters` (`stock_adjustment` → `SA00001` …, `stock_count` → `SC00001` …).
Every person's change is written to `audit_logs` with its reason (brief §58).

## What changed

| Object | Change | Why |
|---|---|---|
| `inventory_units` | new table; a fixed list (trigger refuses changes) | kg, g, l, ml, pcs, dozen with their dimension and size, so a recipe can say 200 g of an item kept in kg. |
| `inventory_categories`, `stock_locations`, `inventory_items` | new tables; never deleted (switched off instead) | Groups, stores (one is the kitchen) and the items stocked, with a unit cost and reorder level. |
| `stock_movements` | new table; append-only (trigger) | The ledger: every change of stock is one row. |
| `stock_levels` | new table; kept only by the trigger on `stock_movements` | On hand per item and store, always the sum of the ledger. Not in the plan's list; added so on hand is read in one row. |
| `stock_adjustments`, `stock_counts` | new tables; append-only (trigger) | A person's change of stock and a physical count, each with its reason. |
| `recipes`, `recipe_ingredients` | new tables; recipes never deleted | What one portion of a dish uses. |

## Units, groups, stores and items

| Table | Key columns | Rules |
|---|---|---|
| `inventory_units` | `code` (`kg`, `g`, `l`, `ml`, `pcs`, `dozen`), `name`, `dimension` (`MASS`, `VOLUME`, `COUNT`), `factor` (base units in one: kg = 1000, dozen = 12) | One list for every property. A quantity converts only within its dimension. |
| `inventory_categories` | `name` (2–60), `sort_order`, `is_active` | Name unique per property, any case |
| `stock_locations` | `code` (`^[A-Z0-9][A-Z0-9_-]{0,19}$`, unique per property), `name` (2–60), `is_kitchen`, `is_active` | At most one kitchen per property (`ux_stock_locations_kitchen`); the kitchen can't be switched off |
| `inventory_items` | `category_id`, `code` (unique per property), `name` (2–80), `unit_id`, `unit_cost` (₹ per unit before GST, 4 decimals, 0–₹10,00,000), `reorder_level` (NULL = never flagged) | The group must be at the same property (composite FK). `unit_cost` is a moving average, updated when stock is received |

## `stock_movements` (the ledger)

| Column | Type | Notes |
|---|---|---|
| `item_id`, `location_id` | uuid | Same property (composite FKs) |
| `movement_type` | text | `OPENING`, `RECEIVED`, `TRANSFER_IN` (> 0); `CONSUMPTION`, `WASTAGE`, `TRANSFER_OUT` (< 0); `ADJUSTMENT`, `COUNT` (either sign) |
| `quantity` | numeric(14,3) | Signed, in the item's unit; never 0 |
| `unit_cost`, `value` | | The item's cost at the time; `value = round(quantity × unit_cost, 2)` (CHECK) |
| `on_hand_after` | numeric(14,3) | Set by the trigger: the store's level after this row |
| `note` | text | For consumption: `O00012 · K00015 · 3× Paneer Tikka (Full)` |
| `adjustment_id`, `count_id`, `order_item_id`, `kot_id` | uuid NULL | What caused it. `CONSUMPTION` ⇔ `order_item_id` set; `COUNT` needs `count_id`; received, wastage, corrections and transfers need `adjustment_id` |

`ux_stock_movements_consumption` (`order_item_id`, `item_id`) where `CONSUMPTION`: a served line uses each ingredient
once, even if serving were retried. Indexes `ix_stock_movements_item` and `ix_stock_movements_property` serve the ledger
screen, newest first.

## `stock_levels`

`item_id` + `location_id` (primary key), `on_hand`, `updated_at`. The BEFORE INSERT trigger `fn_stock_movements_level`
adds every movement to its level with one `INSERT … ON CONFLICT DO UPDATE SET on_hand = on_hand + …` and writes the
result into the movement's `on_hand_after`, so two changes at the same moment both count. The app never writes this
table. The service also locks the `inventory_items` rows it changes (`FOR UPDATE`, in id order) before checking that stock
taken out isn't more than the store has; only consumption by a served dish may take a level below zero (the dish has
been served; the ledger shows the shortfall).

## `stock_adjustments`

`number` (`SA00001` …), `kind`, `item_id`, `location_id`, `to_location_id` (only for `TRANSFER`, not the same store),
`quantity` (as applied to `location_id`), `unit_cost`, `reason` (3–300, required), `created_by`.

| Kind | Sign | Movement(s) |
|---|---|---|
| `RECEIVED` | + | `RECEIVED`; the item's `unit_cost` becomes the moving average of what was on hand and what came in |
| `WASTAGE`, `EXPIRED`, `DAMAGED` | − | `WASTAGE` (the kind is in the note) |
| `CORRECTION` | ± | `ADJUSTMENT` |
| `TRANSFER` | − | `TRANSFER_OUT` from `location_id` and `TRANSFER_IN` to `to_location_id` |

## `stock_counts`

One row per counted item: `number` (`SC00001` …), `system_quantity` (what the ledger said, read under the row lock),
`counted_quantity` (≥ 0), `difference` (= counted − system, CHECK), `unit_cost`, `reason` (NULL only when nothing
differs, else 3–300). A difference posts one `COUNT` movement. Counting several items in one go gives each its own number.

## `recipes` and `recipe_ingredients`

`recipes`: `menu_item_id` (same property), `variant_id` (NULL = the dish's recipe), `note` (≤ 500), `is_active`. One
recipe per dish (`ux_recipes_item`) and one per portion (`ux_recipes_variant`); a portion's own recipe wins over the dish's.

`recipe_ingredients`: `recipe_id`, `item_id`, `quantity` (0 < q ≤ 100,000), `unit_id` (any unit of the item's dimension;
the service refuses others), `sort_order`. Each item once per recipe. Saving a recipe sends all its lines; the old lines
are replaced. A dish's choices (Sprint 14 modifiers) don't change what it uses.

**Costing** (prices before GST): cost = Σ quantity converted to the item's unit (3 decimals) × the item's `unit_cost`,
rounded to the paisa; food cost % = cost ÷ price × 100; contribution = price − cost; margin % = contribution ÷ price ×
100. Paneer Tikka (Full): ₹105 of ₹280 → 37.5 % food cost, ₹175 contribution, 62.5 % margin.

## Dev seed (S012)

Stores `STORE` (Main store) and `KITCHEN` (the kitchen); 21 items in 6 groups (Dairy, Vegetables and fruit, Meat and fish,
Dry goods and spices, Oils and fats, Beverages) with opening stock, fresh cream at its reorder level of 2 l; recipes for 8 dishes (9
recipes: Paneer Tikka Half and Full); one wastage, `SA00001`, of onion and capsicum. Skipped when the property already
has a kitchen store.

## Checks

`database/queries/check-sprint-15.sql`: on hand equals the sum of the ledger; each `on_hand_after` follows the one before;
every consumption points at a served ticket's live line; every adjustment has its reason in the audit log; recipe cost
per dish; no recipe unit of the wrong kind.
