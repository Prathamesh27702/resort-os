# Sprint 9 — schema reference (GST and the tax engine)

Source of truth: `database/migrations/V011__tax.sql` and `database/seed/dev/S008__gst.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How the rules are used: [GST.md](../GST.md) (worked examples).

## What changed

| Object | Change | Why |
|---|---|---|
| `gst_registrations` | new table; never deleted (trigger) | The GSTIN a property bills under, from/to a date. No registration on a day = no GST that day. |
| `tax_categories` | new table; never deleted (trigger) | What is taxed (SAC/HSN code) and which folio charge category it covers. |
| `tax_rules` | new table; never deleted; only the end date and "verified" change (trigger) | A category's GST from one date to another, with the notification it comes from. |
| `tax_rule_slabs` | new table; never updated or deleted (triggers) | The rates of a rule by value of one unit. |
| `folio_item_taxes.tax_rule_id` | FK → `tax_rules` added | Each tax row of a bill line names the rule it used. |
| `fn_seed_tax_defaults(org)` | new function, run for every organisation | Seeds accommodation and restaurant GST as data (unverified). |
| `trg_organizations_seed_tax` | new trigger on `organizations` | A new organisation gets the same defaults. |

No GST rate, threshold or SAC code is in the code: the only numbers are these rows.

## `gst_registrations`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `gstin` | char(15) | Format `^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$`; the API also checks the check digit (mod-36) |
| `legal_name`, `trade_name` | text | Legal name 1–200 characters (printed on invoices, Sprint 12) |
| `state_code` | char(2) | Always the GSTIN's first two digits (`ck_gst_registrations_state`); must match the property's GST state |
| `registration_type` | text | `REGULAR` (charges GST) or `COMPOSITION` (issues a bill of supply, charges none) |
| `effective_from`, `effective_to` | date | Both days included; `effective_to` NULL = until further notice |
| `created_*`, `updated_*` | | Standard audit columns |

`ex_gst_registrations_overlap`: a property has one registration on any day (EXCLUDE on the date range).

## `tax_categories`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | uuid | Per organisation (all its properties share the rules) |
| `code` | text | `^[A-Z][A-Z0-9_]{1,29}$`, unique per organisation, e.g. `ACCOMMODATION` |
| `name` | text | 1–100 characters |
| `hsn_sac` | text | 4, 6 or 8 digits, e.g. `996311` |
| `charge_category` | text NULL | The folio charge category it taxes (`ROOM`, `FOOD` …). At most one tax category per charge category (`ux_tax_categories_charge`). NULL = used only by the preview. |
| `description` | text NULL | |

A folio charge in a category no tax category covers carries no GST.

## `tax_rules`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | uuid | |
| `tax_category_id` | uuid | FK → `tax_categories` |
| `effective_from`, `effective_to` | date | Both days included; NULL end = until further notice. `ex_tax_rules_overlap`: one rule per category on any day |
| `rule_type` | text | `FLAT` (one rate) or `SLAB` (the rate depends on the value of one unit for one night) |
| `source` | text | 3–500 characters: the notification the rule comes from |
| `notes` | text NULL | |
| `verified_at`, `verified_by` | NULL | Set together (`ck_tax_rules_verified`) when the accountant presses Verify |
| `created_*`, `updated_*` | | Standard audit columns |

`fn_tax_rules_guard` (BEFORE UPDATE): every column except `effective_to`, `verified_*` and `updated_*` is frozen
("A tax rule can't be changed once saved: add a new rule from the date it should change"). The end date changes only
when the API closes the running rule because a new one starts.

## `tax_rule_slabs`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | uuid | |
| `tax_rule_id` | uuid | FK → `tax_rules` |
| `up_to` | numeric(12,2) NULL | Upper limit, **included** (₹7,500.00 falls in the "up to ₹7,500" slab); NULL = no upper limit |
| `rate` | numeric(5,2) | 0–100 and a multiple of 0.02, so CGST and SGST are each a whole number of hundredths |
| `input_tax_credit` | boolean | Whether the supplier can claim input tax credit at this rate (shown; used by accounting later) |
| `sort_order` | integer | Ascending `up_to`, the open-ended one last |

`ux_tax_rule_slabs_up_to` (NULLS NOT DISTINCT): one slab per limit, so exactly one open-ended slab. The API also
checks that a FLAT rule has one slab and a SLAB rule at least two.

## Seeded rules (`fn_seed_tax_defaults`)

Entered from public CBIC notifications and marked **unverified**: the owner's accountant must check each one and press
Verify in Settings → GST before go-live.

| Category | SAC | Charges | From → to | Rates |
|---|---|---|---|---|
| ACCOMMODATION | 996311 | ROOM | 2022-07-18 → 2025-09-21 | ≤ ₹7,500: 12% (ITC) · above: 18% (ITC) |
| ACCOMMODATION | 996311 | ROOM | 2025-09-22 → open | ≤ ₹7,500: 5% (no ITC) · above: 18% (ITC) |
| RESTAURANT | 996331 | FOOD | 2017-11-15 → open | 5% (no ITC) |

Idempotent: a category that already exists is left alone with its rules.

## What each action writes

- **Add a registration:** a `gst_registrations` row (the property row is locked first). If the property had no GST state
  yet, it is set from the GSTIN. A registration that runs on past the new one's start is ended the day before.
- **Add a category:** a `tax_categories` row (the organisation row is locked, so two people can't take the same code).
- **Add a rule:** a `tax_rules` row and its `tax_rule_slabs` (the category row is locked). It starts today (India) or
  later; the rule running on past that day gets `effective_to = start − 1`. Any other overlap is refused.
- **Verify a rule:** `verified_at`, `verified_by`.
- **Post a charge** (Sprint 8's actions): `folio_items.tax_amount` and its `folio_item_taxes` rows: CGST + SGST at half
  the rate each when the place of supply is the property's state, else IGST, each with `tax_rule_id` and the rate. A
  reversal or a moved line copies them (Sprint 8). Every change above writes `audit_logs` rows.

## Dev seed S008

The demo property is registered in Goa from 2017-07-01 with the made-up GSTIN `30AABCT1234C1Z9` (valid check digit;
not a real business). Charges posted from then on carry GST; S007's demo lines were posted before the engine and keep
none. Skipped when the property already has a registration.

## Checks

`database/queries/check-sprint-09.sql`: the migration and tables, the registrations, every rule with its rates, no
overlapping rules, slab shape, a line's tax equals its tax rows (each naming a rule), recent tax rows, CGST = SGST, and
the audit trail.
