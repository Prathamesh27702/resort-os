# Sprint 14 — schema reference (menu, restaurant POS and KOT)

Source of truth: `database/migrations/V016__restaurant.sql`. Dev seed: `database/seed/dev/S011__restaurant.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.24](../architecture/system-design.md#424-restaurant-orders-two-waiters-one-order-and-the-room-charge).

The restaurant reuses what earlier sprints built: a bill charged to a room is one line on the guest's main folio (Sprint 8,
`folio_items` with source `RESTAURANT`), its GST comes from the tax engine (Sprint 9, category `FOOD`), and cash taken at
the restaurant goes into the open cash drawer (Sprint 10, `cash_drawer_movements` type `POS_SALE`). Numbers come from
`property_counters` (`restaurant_order` → `O00001` …, `restaurant_kot` → `K00001` …).

## What changed

| Object | Change | Why |
|---|---|---|
| `menu_categories`, `menu_items`, `menu_item_variants`, `modifier_groups`, `modifiers` | new tables; never deleted (switched off instead) | The menu: sections, dishes with a price before GST, portions with their own price, and choices that add to it. Old orders keep pointing at what was ordered. |
| `restaurant_tables` | new table; never deleted | Tables by code with seats and an area. |
| `orders` | new table; never deleted; final once settled or voided (trigger) | A bill: dine-in, room service or takeaway; its money, discount, how it was settled. |
| `order_items` | new table; a line sent to the kitchen can only be voided (trigger) | The lines, with a copy of the name, portion, choices and price at the time. |
| `kots`, `kot_items` | new tables; never deleted; ticket lines only voided (trigger) | Kitchen order tickets as the kitchen reads them. |
| `cash_drawer_movements` | `order_id` (unique when set) and type `POS_SALE` (amount > 0, needs the order) | Cash paid at the restaurant counts in the drawer like a desk payment. |
| `folio_items` | `source` adds `RESTAURANT` | The room charge's line says where it came from. |

## The menu

| Table | Key columns | Rules |
|---|---|---|
| `menu_categories` | `name` (2–60), `sort_order`, `is_active` | Name unique per property, any case (`ux_menu_categories_name`) |
| `menu_items` | `category_id`, `code` (`^[A-Z0-9][A-Z0-9_-]{0,19}$`, unique per property), `name` (2–80), `description` (≤ 300), `food_type` (`VEG`, `NON_VEG`, `EGG`), `price` (₹0–₹10,00,000, paise), `is_available` (false = sold out for now), `is_active` | The section must be at the same property (composite FK) |
| `menu_item_variants` | `menu_item_id`, `name` (1–40), `price`, `sort_order`, `is_active` | Active names unique per dish. A dish with active portions is ordered by portion; its own `price` is then only shown as "from" |
| `modifier_groups` | `menu_item_id`, `name` (2–40), `min_select`, `max_select` (1–20, min ≤ max) | "Spice level" 1..1 must be picked; "Add-ons" 0..2 optional |
| `modifiers` | `group_id`, `name` (1–40), `price_delta` (₹0–₹1,00,000) | What one choice adds to the line's unit price |

Saving a dish sends every portion, group and choice: ones sent with an id are kept and updated, new ones are added, ones
left out are switched off (`is_active = false`). Editing the menu never changes an order already taken, because
`order_items` copies the name and price.

## `restaurant_tables`

`code` (`^[A-Z0-9][A-Z0-9_-]{0,9}$`, unique per property), `seats` (1–50), `area` (≤ 40, e.g. Garden), `sort_order`,
`is_active`. A table with an open order can't be switched off (the service says which order).

## `orders`

| Column | Type | Notes |
|---|---|---|
| `number` | text | `O00001` … per property |
| `order_type` | text | `DINE_IN` (needs `table_id`), `ROOM_SERVICE` (needs `unit_id` and the in-house `reservation_id`), `TAKEAWAY` |
| `status` | text | `OPEN` → `SETTLED` or `VOIDED`. A non-open order can't be updated (trigger `fn_orders_final`) |
| `covers`, `note` | | Guests at the table (1–99); a note (≤ 300) |
| `business_date` | date | The property's date when it was opened; the day the order is listed under |
| `subtotal` | numeric(12,2) | Sum of the live lines (not voided) |
| `discount_kind`, `discount_value`, `discount_amount`, `discount_reason`, `discounted_by`, `discounted_at` | | `PERCENT` (0–100) or `AMOUNT`; the amount is never more than the subtotal; a reason is required (`pos.discount`) |
| `tax_amount` | numeric(12,2) | GST on subtotal − discount, from the tax engine (category `FOOD`, the order's business date) |
| `total` | numeric(12,2) | `subtotal − discount_amount + tax_amount` (CHECK) |
| `settlement` | text NULL | `PAID` (method `CASH`, `UPI`, `CARD` or `BANK_TRANSFER`; cash with a total > 0 needs the open `cash_drawer_session_id`) or `ROOM_CHARGE` (`reservation_id`, `folio_id`, `folio_item_id`) |
| `payment_reference` | text NULL | ≤ 100; 13 or more digits in a row are refused (no card numbers, brief §3) |
| `settled_at`/`by`, `voided_at`/`by`, `void_reason` | | A voided order has a reason (`pos_order.void`) |
| `opened_by` | uuid | Who opened it |
| `xmin` | | The version every change sends back (409 when someone else changed it first) |

Indexes: `ux_orders_open_table` (one open order per table), `ux_orders_folio_item` (a folio line is one order's charge),
`ix_orders_list` (day lists), `ix_orders_open`.

## `order_items`

`order_id`, `menu_item_id`, `variant_id`; copies `name`, `variant_name`, `food_type`, `modifiers` (jsonb array of
`{ id, group, name, price }`); `quantity` (1–99), `unit_price` (portion + choices), `amount` (= unit price × quantity),
`note` (≤ 200, for the kitchen), `status`:

- `NEW`: not sent yet (`kot_id` NULL). The waiter may change the quantity or note, or remove it (DELETE allowed only now).
- `SENT`: on a ticket (`kot_id` set). Only voiding changes it (trigger `fn_order_items_guard`).
- `VOIDED`: after it was sent, with `voided_at`, `voided_by` and `void_reason` (`pos_order.void`).

## `kots` and `kot_items`

`kots`: `number` (`K00001` …), `order_id`, `status` `NEW` → `PREPARING` (kitchen: Start) → `READY` (kitchen: Ready) →
`SERVED` (waiter); `CANCELLED` when every line on it is voided. `sent_at`/`by`, `started_at`, `ready_at`, `served_at`/`by`,
`cancelled_at`, `note`. The kitchen display reads `ix_kots_kitchen` (tickets still `NEW`, `PREPARING` or `READY`).

`kot_items`: one per order line (`ux_kot_items_line`), the name as the kitchen reads it ("Paneer Tikka (Full)"),
`quantity`, `modifiers` (as text, "Less spicy"), `note`; `voided_at` and `void_reason` when the line is voided after it was
sent, so the kitchen sees it struck through. Nothing else on a ticket line ever changes (trigger `fn_kot_items_guard`).

## Dev seed (S011)

Five sections (Starters, Mains, Breads and rice, Desserts, Drinks) with 14 dishes; **Paneer Tikka** has Half ₹160 / Full
₹280, *Spice level* (pick 1) and *Add-ons* (up to 2: Extra cheese +₹40, Mint chutney +₹20); **Masala Chai** is sold out.
Tables T1–T4 (Garden) and B1, B2 (Beach). One open order, **O00001** at T2, sent to the kitchen as **K00001** (still
New): Paneer Tikka (Full, less spicy), Goan Fish Curry, 2 Butter Naan; ₹850 before GST.

## Checks

`database/queries/check-sprint-14.sql`: totals that don't add up, settled orders without their folio line or drawer
movement, open tables with two orders, lines sent but not on a ticket, tickets with every line voided but not cancelled,
and gaps in the numbers.
