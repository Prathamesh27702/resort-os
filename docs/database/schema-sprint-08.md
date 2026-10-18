# Sprint 8 — schema reference (folio)

Source of truth: `database/migrations/V010__folio.sql` and `database/seed/dev/S007__folios.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).

## What changed

| Object | Change | Why |
|---|---|---|
| `folios` | new table; never deleted (trigger) | The bill of one stay. Every confirmed booking has one main folio; more are opened to split the bill. |
| `folio_items` | new table; **never updated or deleted** (triggers) | One line of a bill. Mistakes are corrected with a reversal line, never by editing (brief §85). |
| `folio_item_taxes` | new table; never updated or deleted (triggers) | The tax on a line as calculated when it was posted. Empty until the tax engine (Sprint 9). |
| `property_counters` row `folio` | per property | Folio numbers F00001, F00002 … |
| `fn_open_missing_folios()` | new function | Opens the main folio of every confirmed, in-house or checked-out booking that has none (run by the migration and by the dev seed). |

## `folios`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `reservation_id` | uuid | FK `(reservation_id, property_id)` → `reservations` |
| `number` | text | `^F[0-9]{5,}$`, unique per property |
| `name` | text | 1–100 characters: "Main" for the main folio, e.g. "Company: Acme Travels" for another |
| `is_primary` | boolean | `ux_folios_primary`: one main folio per reservation |
| `bill_to_guest_id` | uuid NULL | FK → `guests`: who pays this folio, when not the booker (used by invoices, Sprint 12) |
| `currency` | char(3) | The booking's currency (INR) |
| `created_*`, `updated_*` | | Standard audit columns |

The main folio is opened when a booking becomes CONFIRMED (booked as confirmed, confirmed later, or confirmed on the way
in at check-in), so a walk-in has one from the start. Enquiries and pending bookings have none.

## `folio_items`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `folio_id` | uuid | FK `(folio_id, property_id)` → `folios` |
| `reservation_id` | uuid | FK → `reservations`; must be the folio's reservation (trigger, `ck_folio_items_folio_reservation`) |
| `item_type` | text | `CHARGE` or `REVERSAL` |
| `category` | text | `ROOM`, `FOOD`, `BEVERAGE`, `ACTIVITY`, `TRANSPORT`, `EQUIPMENT`, `LAUNDRY`, `OTHER` |
| `description` | text | 1–200 characters, e.g. "Room night: Room 104", "Dinner at the beach shack", "Reversal: Airport transfer" |
| `service_date` | date | The property-local day the service was used (a room night is its stay date) |
| `quantity` | numeric(12,3) | > 0 for a charge, < 0 for a reversal |
| `unit_price` | numeric(12,2) | ≥ 0 |
| `amount` | numeric(12,2) | `round(quantity × unit_price, 2)` (CHECK) |
| `tax_amount` | numeric(12,2) | 0 until Sprint 9 |
| `total` | numeric(12,2) | `amount + tax_amount` (CHECK) |
| `source` | text | `MANUAL` (staff), `ROOM_NIGHT` (the stay's nights), `PACKAGE` (a package sold, one line per part) |
| `package_id` | uuid NULL | FK → `packages`; set exactly when `source = 'PACKAGE'` |
| `group_id` | uuid NULL | Shared by the lines of one package sale |
| `reverses_item_id` | uuid NULL | The charge this reversal cancels; `UNIQUE`, so a line is reversed once |
| `transferred_from_item_id` | uuid NULL | The line this one was moved from (split); `UNIQUE`, so a line is moved once |
| `reason` | text NULL | 3–500 characters; required on a reversal |
| `posted_at`, `posted_by` | | When and who (NULL = the system, e.g. the nightly job) |

**Rules the database enforces**

- `ck_folio_items_amounts`: the amount and total add up; a charge has a positive quantity and no `reverses_item_id`; a
  reversal has a negative quantity, a `reverses_item_id` and a reason.
- `fn_folio_items_check` (BEFORE INSERT): a reversal must cancel a **charge on the same folio exactly** (same quantity,
  price, amount, tax and category, negated), else `ck_folio_items_reversal`.
- `ux_folio_items_room_night (reservation_id, service_date) WHERE source = 'ROOM_NIGHT' AND item_type = 'CHARGE' AND
  transferred_from_item_id IS NULL`: **each night of a stay is charged once**, whoever posts it (check-in, the nightly
  job, check-out). A night moved to another folio carries the original's slot, so it is left out of the index.
- `trg_folio_items_no_update` / `trg_folio_items_no_delete`: "Rows in folio_items cannot be changed / deleted".

**Balance** is never stored: it is `sum(total)` of the lines (minus payments from Sprint 10). A reversed charge and its
reversal add up to 0; both stay on the bill.

## `folio_item_taxes`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `folio_item_id` | uuid | FK → `folio_items` |
| `tax_rule_id` | uuid NULL | Gets its FK when `tax_rules` is created (Sprint 9) |
| `tax_code` | text | `CGST`, `SGST`, `IGST`, `CESS` |
| `rate` | numeric(5,2) | 0–100, copied at posting time |
| `taxable_value`, `amount` | numeric(12,2) | |
| `created_at` | timestamptz | |

Copied at posting time so a later rule change never alters a posted bill (brief §86). A reversal copies the negated
tax rows of its charge.

## What each action writes

- **Confirm a booking** (or create it confirmed, or check in a pending one): a `folios` row "Main", number from the
  `folio` counter.
- **Check-in:** room nights. With `folio.room_charge_posting = "AT_CHECK_IN"` (the default) every night of the stay;
  with `"NIGHTLY"` only tonight. A night's line is the sum of all the booking's rooms that night, at the booked prices in
  `reservation_rate_breakdowns`.
- **Nightly job** (every 15 minutes, `Folio:RoomChargeJob:Enabled`): for every in-house booking, posts each night up to
  today that has no line yet. Locks the reservation row first, so it can't race a check-in or check-out.
- **Post a charge:** one `CHARGE` line (`MANUAL`), or for a package one line per component with an amount > 0, sharing a
  `group_id`. An idempotency key row (Sprint 5's table) makes a retry post once.
- **Reverse:** one `REVERSAL` line with the reason, plus negated tax rows.
- **Move lines (split):** per line, a `REVERSAL` on the old folio ("Moved to F00007 (…)") and a copy on the other one
  with `transferred_from_item_id`; a new folio first when asked.
- **Check-out:** posts any night still missing; when the guest leaves early, the released nights' room lines are
  reversed ("Left early on …: reason"). Then the balance is checked (Sprint 7's rule).

## Dev seed S007

- Main folios for the demo bookings (`fn_open_missing_folios`).
- **R00008** (Room 104, in house): its room nights, "Airport pick-up (Goa airport)" ₹1,200 and "Dinner at the beach
  shack" ₹1,450 (yesterday).
- **R00007** (Room 105, leaving today): its room night and "Laundry: shirts" 4 × ₹80.
- `frontdesk.allow_checkout_with_balance = true` on the demo property only: payments arrive in Sprint 10, so until then
  nobody with a bill could check out in the demo. Real properties keep the default (off).

Skipped when R00008 already has lines.

## Checks

`database/queries/check-sprint-08.sql`: the migration and tables, one main folio per booking, the bills and their
balances, one bill line by line, reversals that cancel exactly, one charge per night, moved lines, the no-update /
no-delete triggers, and the two settings.
