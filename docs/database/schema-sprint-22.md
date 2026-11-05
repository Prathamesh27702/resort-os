# Sprint 22 — schema reference (expenses and budgets)

Source of truth: `database/migrations/V024__expenses.sql`. Dev seed: `database/seed/dev/S019__expenses.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.32](../architecture/system-design.md#432-expenses-approval-and-budget-against-actual).

Every expense waits for approval. The budget report counts **approved** expenses only, at their **cost** (the taxable
value when the GST can be claimed back as input credit, else what was paid). Once an expense is approved, rejected or
cancelled the database freezes it (`trg_expenses_frozen`) and refuses new receipts on it (`trg_expense_attachments_open`),
so what was counted can't change behind the report. Nothing here is deleted.

## What changed

| Object | Change | Why |
|---|---|---|
| `expense_categories` | new table; never deleted (switched off instead) | What money is spent on; whether its GST can be claimed back. |
| `cost_centres` | new table; never deleted (switched off instead) | The departments that spend money and have a budget. |
| `expenses` | new table; never deleted; frozen once decided (`trg_expenses_frozen`) | Money paid out, its GST, its cost, and the approval. |
| `expense_attachments` | new table; append-only; only while the expense waits | Receipt photos or PDFs, encrypted in the file store. |
| `budgets` | new table; never deleted; one per property and financial year | The year's plan and its total. |
| `budget_lines` | new table; never deleted (set to ₹0 instead); month inside the budget's year | A department's amount for one month. |
| `settings` | `expenses.receipt_required_over` = 500 | An expense over ₹500 needs a receipt before it can be approved. |

## `expense_categories` and `cost_centres`

Both have `code` (`^[A-Z][A-Z0-9]{0,9}$`, unique per property), `name` (2–60, unique per property in any case:
`ux_expense_categories_name`, `ux_cost_centres_name`), `sort_order`, `is_active`. A category also has `gst_claimable`
(false for staff food, which CGST section 17(5) blocks). The app makes the defaults the first time the Expenses screen is
opened: categories UTIL, REPAIR, FUEL, SUPPLY, MKT, OFFICE, TRAVEL, STAFFW, BANK, LICENCE, OTHER and departments FO, HK,
FB, MNT, ACT, TRN, SALES, ADMIN.

## `expenses`

| Column | Notes |
|---|---|
| `code` | `EX00001` … per property (`property_counters` name `expense`) |
| `expense_date` | The local day the money was spent; the API takes up to 400 days back, never the future |
| `category_id`, `cost_centre_id` | Same property (composite FKs) |
| `description` (3–200), `payee` (2–100) | What it was for, who was paid |
| `payment_method`, `payment_reference` | `CASH`, `UPI`, `CARD`, `BANK_TRANSFER`, `CHEQUE`; reference up to 60 |
| `amount` | What was paid, with GST; above 0, up to ₹1,00,00,000 |
| `vendor_gstin`, `bill_number` | For a GST bill: 15 characters (the API also checks the check digit), bill up to 30. `ux_expenses_bill`: one bill of a vendor is recorded once (bill number in any case) among expenses that wait or are approved |
| `gst_amount`, `taxable_value`, `cgst`, `sgst`, `igst` | `ck_expenses_gst`: GST below the amount and needs the GSTIN and bill; `taxable_value = amount − gst_amount`; `cgst + sgst + igst = gst_amount`; IGST alone or CGST + SGST |
| `itc_eligible` | The GST can be claimed back (a regular GST registrant on the day, a claimable category, GST above 0) |
| `cost_amount` | What counts in the budget: `taxable_value` when `itc_eligible`, else `amount` (`ck_expenses_cost`) |
| `note` | Up to 500 |
| `status` | `SUBMITTED` → `APPROVED`, `REJECTED` or `CANCELLED`; `decided_at` and `decided_by` set exactly when it is not `SUBMITTED` (`ck_expenses_decided`); `decision_reason` 3–300 |

`trg_expenses_frozen` refuses any change to an expense that is no longer `SUBMITTED`, and changing its number, property or
who recorded it (`ck_expenses_frozen`). `ix_expenses_actuals` (approved, by department and day) serves the report; `ix_expenses_waiting` the waiting count.

## `expense_attachments`

`expense_id`, `file_name` (1–120), `content_type` (`image/jpeg`, `image/png`, `image/webp`, `application/pdf`; the API
sniffs it from the bytes), `size_bytes` (1 byte to 2 MB), `storage_key` (unique; the file is encrypted at rest). Append-only;
`trg_expense_attachments_open` refuses a receipt on an expense that is no longer waiting (`ck_expense_attachments_open`).
The API allows 4 per expense.

## `budgets` and `budget_lines`

`budgets`: `year_start` (1 April, `ck_budgets_year`; `ux_budgets_year` one per property and year), `name` (2–60), `note`
(up to 500), `total` (kept by the API as the sum of the lines).

`budget_lines`: `budget_id`, `cost_centre_id` (same property), `month` (the first of a month, inside the budget's year:
`ck_budget_lines_in_year` from `trg_budget_lines_month`), `amount` (0 to ₹1,00,00,000). `ux_budget_lines_month`: one line
per department and month. The screen spreads a yearly amount as whole rupees a month, March taking what is left.

## Dev seed (S019)

The default categories and departments; the budget for this financial year, the same every month: Front office ₹15,000,
Housekeeping ₹40,000, Food and beverage ₹60,000, Maintenance ₹25,000, Activities ₹10,000, Transport ₹20,000, Sales and
marketing ₹15,000, Administration ₹20,000 (₹24,60,000 a year). Ten approved expenses this year so far (EX00001–EX00010):
electricity, repairs, laundry soap, ads, diesel on a GST bill from Malvan Fuels (made-up GSTIN 30MALVF5521K1ZB; ₹11,800
with ₹1,800 GST, so ₹10,000 counts), staff food and printer paper. Two wait for approval: EX00011 bed sheets ₹1,200 with no
receipt yet (it can't be approved until one is added), EX00012 tea ₹350. Skipped when the property already has a budget.
