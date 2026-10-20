# Sprint 10 — schema reference (payments, refunds, cash drawer)

Source of truth: `database/migrations/V012__payments.sql` and `database/seed/dev/S009__payments.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How the locking works: [system-design.md → 4.20](../architecture/system-design.md#420-refunds-that-cant-overspend-and-a-cash-drawer-that-balances).

## What changed

| Object | Change | Why |
|---|---|---|
| `payment_status_transitions` | new table (data) | The payment status graph. A trigger refuses any move that isn't a row here. |
| `payments` | new table; never deleted; only the status and the void fields change (trigger) | Money received for a booking: cash, UPI, card (the property's terminal) or bank transfer. |
| `payment_allocations` | new table; never updated or deleted | Which bill a payment settles, and how much of it. |
| `refunds` | new table; never deleted; only a gateway refund's status moves (trigger) | Money given back from a payment, with a reason. |
| `cash_drawers` | new table; never deleted; one "Front desk" drawer seeded per property | The cash box at a desk. |
| `cash_drawer_sessions` | new table; never deleted; counted amounts never change (trigger) | One shift of a drawer, from the opening float to the count at the close. |
| `cash_drawer_movements` | new table; never updated or deleted; rows only while the shift is OPEN | The drawer's ledger: every rupee in and out. |
| `fn_payment_money_check()` | new trigger function on allocations and refunds | Locks the payment row and refuses an allocation or refund that would overspend it. |

No column holds a card number, expiry or CVV. Nothing in these tables can be deleted.

## `payments`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `number` | text | `P00001`, `P00002` … per property (`property_counters`, key `payment`); unique per property |
| `reservation_id` | uuid | The booking it is for (FK with `property_id`, so it can't point at another property's booking) |
| `guest_id` | uuid | The booking's main guest when the payment was taken |
| `method` | text | `CASH`, `UPI`, `CARD`, `BANK_TRANSFER` |
| `status` | text | `PENDING`, `AUTHORIZED`, `CAPTURED`, `FAILED`, `PARTIALLY_REFUNDED`, `REFUNDED`, `CANCELLED`. A desk payment is created `CAPTURED`; the gateway states are for Razorpay (Sprint 11) |
| `amount`, `currency` | numeric(12,2), char(3) | More than 0, at most ₹1,00,00,000 |
| `reference` | text | UTR, approval code or bank reference, 1–100 characters. `ck_payments_no_card_number`: a `CARD` reference can't hold 13 or more digits in a row |
| `note` | text | Up to 500 characters |
| `cash_drawer_session_id` | uuid | The shift that took the cash. `ck_payments_drawer`: set if and only if the method is `CASH` |
| `received_at`, `received_by`, `captured_at` | | When and by whom |
| `voided_at`, `voided_by`, `void_reason` | | A payment entered by mistake. `ck_payments_void`: all set or none, reason 3–500 characters |

`trg_payments_guard`: on insert the status must be one a payment can start in (`PENDING` or `CAPTURED`); on update only
`status`, `captured_at` and the void fields may change (anything else: SQLSTATE `insufficient_privilege`), and the status only along the graph
(`ck_payments_status_transition`):

| From | To |
|---|---|
| (new) | `PENDING`, `CAPTURED` |
| `PENDING` | `AUTHORIZED`, `CAPTURED`, `FAILED`, `CANCELLED` |
| `AUTHORIZED` | `CAPTURED`, `FAILED`, `CANCELLED` |
| `CAPTURED` | `PARTIALLY_REFUNDED`, `REFUNDED`, `CANCELLED` (void) |
| `PARTIALLY_REFUNDED` | `REFUNDED` |

Payments that count towards a bill: `CAPTURED`, `PARTIALLY_REFUNDED`, `REFUNDED` (net of their refunds). A voided payment
is `CANCELLED` and counts for nothing.

## `payment_allocations`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `payment_id` | uuid | The payment |
| `folio_id` | uuid | The bill it settles (same property) |
| `amount` | numeric(12,2) | More than 0 |
| `allocated_at`, `allocated_by` | | When and by whom |

A payment can settle several folios of its booking and a folio can have several payments. What a payment has not put on
a bill (and not refunded from account) is **on account** for the booking: an advance.

## `refunds`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `payment_id` | uuid | The payment the money came from |
| `folio_id` | uuid | The bill it is taken off; NULL = taken from what was on account |
| `amount` | numeric(12,2) | More than 0 |
| `method` | text | How it was paid back (may differ from the payment's method, e.g. a UPI payment refunded in cash) |
| `status` | text | `PROCESSED` for desk refunds; `PENDING` / `FAILED` for gateway refunds (Sprint 11) |
| `reason` | text | Required, 3–500 characters |
| `reference` | text | Optional, 1–100 characters, no 13-digit runs |
| `cash_drawer_session_id` | uuid | Set if and only if the method is `CASH` (`ck_refunds_drawer`) |
| `refunded_at`, `refunded_by` | | When and by whom |

## `fn_payment_money_check()` — refunds can't overspend

Runs `BEFORE INSERT` on `payment_allocations` and `refunds`. It takes `SELECT … FOR UPDATE` on the payment row, so two
refunds (or a refund and an allocation) on the same payment wait for each other and the second sees the first. Then:

| Rule | Constraint name |
|---|---|
| The payment is `CAPTURED` or `PARTIALLY_REFUNDED` (nothing more can come out of a refunded, voided or failed one) | `ck_payment_money_status` |
| The folio belongs to the payment's booking | `ck_payment_money_folio` |
| allocated + refunded from account ≤ amount | `ck_payment_allocations_limit` / `ck_refunds_limit` |
| refunded ≤ amount (captured − already refunded) | `ck_refunds_limit` |
| refunded from a folio ≤ what the payment put on that folio | `ck_refunds_limit` |

The API checks the same rules first, under the same lock, so a person sees the reason under the Amount field; the
trigger is the backstop.

## `cash_drawers`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `name` | text | 1–60 characters, unique per property. V012 creates "Front desk" for every property; the API creates it on first use for new ones |
| `is_active` | boolean | |

## `cash_drawer_sessions`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id`, `cash_drawer_id` | uuid | |
| `status` | text | `OPEN`, `VARIANCE_PENDING` (closed short or over, waiting for approval), `CLOSED` |
| `opened_at`, `opened_by`, `opening_float`, `open_note` | | The float is the cash counted into the drawer (0 – ₹1,00,00,000) |
| `closed_at`, `closed_by`, `expected_amount`, `counted_amount`, `variance`, `close_note` | | `ck_cash_drawer_sessions_close`: set together when the shift closes; `variance = counted − expected` (negative = short); a variance needs a note |
| `approved_at`, `approved_by`, `approval_note` | | `ck_cash_drawer_sessions_approve`: only for a shift with a variance |

`ux_cash_drawer_sessions_open`: one `OPEN` shift per drawer (partial unique index). `trg_cash_drawer_sessions_guard`:
`OPEN → CLOSED | VARIANCE_PENDING`, `VARIANCE_PENDING → CLOSED`; the float and the counted amounts never change.

## `cash_drawer_movements`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id`, `session_id` | uuid | |
| `movement_type` | text | `OPENING_FLOAT` (+), `PAYMENT` (+), `REFUND` (−), `VOID` (−), `PAY_IN` (+), `PAY_OUT` (−); the sign is checked |
| `amount` | numeric(12,2) | Signed: + into the drawer, − out |
| `payment_id`, `refund_id` | uuid | The payment or refund it came from (`ux_cash_drawer_movements_payment`: a payment comes in once and is voided out once) |
| `description`, `moved_at`, `moved_by` | | |

What the drawer should hold = the sum of its movements. `trg_cash_drawer_movements_check` refuses a row unless its shift
is `OPEN` (`ck_cash_drawer_movements_open`), so a closed shift's count can't drift.

## What each action writes

| Action | Rows |
|---|---|
| Take a payment on a bill | `payments` (CAPTURED) + `payment_allocations`; cash: + a `PAYMENT` movement |
| Take a payment on account | `payments` only |
| Put an advance on a bill | `payment_allocations` |
| Refund | `refunds`, then the payment's status → `PARTIALLY_REFUNDED` or `REFUNDED`; cash: + a `REFUND` movement (−) |
| Void | the payment's status → `CANCELLED` with the reason; cash: + a `VOID` movement (−) while its shift is open |
| Open the drawer | `cash_drawer_sessions` (OPEN) + an `OPENING_FLOAT` movement |
| Close the drawer | the session → `CLOSED` (count matches) or `VARIANCE_PENDING` with expected, counted, variance and note |
| Approve a variance | the session → `CLOSED` with the approver and note |

Each of these is audited (`audit_logs`), with the reason where there is one.

## Dev seed S009

- Yesterday's closed shift on the Front desk drawer: float ₹5,000, R00007 paid ₹500 cash (P00001, on its main bill),
  counted ₹5,500, no variance.
- R00002 paid a ₹2,000 UPI advance (P00002), on account.
- The payment counter is at 2; no shift is left open, so the review starts by opening one.

## Checks

`database/queries/check-sprint-10.sql`: money never over the payment, refunds from a bill within what the payment put on
it, statuses that agree with the refunds, no card numbers, cash always with a shift, each shift's ledger and variance,
one open shift per drawer, and the audit trail.
