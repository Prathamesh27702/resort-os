# Sprint 11 — schema reference (Razorpay)

Source of truth: `database/migrations/V013__razorpay.sql`. No new dev seed: a payment link needs a real order in Razorpay
(or in the fake Razorpay's memory), so links are made in the demo instead; the seeded booking R00003 (PENDING, "Will send
the advance by UPI") is the one to send a link for.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.21](../architecture/system-design.md#421-razorpay-the-webhook-is-the-only-writer) and
[RAZORPAY.md](../RAZORPAY.md).

## What changed

| Object | Change | Why |
|---|---|---|
| `payment_gateway_orders` | new table; never deleted; only status, attempts, the last error, the callback time, the payment and the cancel fields change (trigger) | A payment link: a Razorpay order for a booking and an amount, and the guest's private page. |
| `webhook_events` | new table; never deleted; only a `FAILED` row changes (trigger) | Every verified Razorpay delivery, once per event id, with what it did. |
| `payments` | method `ONLINE`; new `gateway_order_id`, `gateway_method`; unique Razorpay payment id; new status moves for ONLINE only | Online payments are created by the webhook. |
| `payment_status_transitions` | + `(new) → AUTHORIZED`, `REFUNDED → PARTIALLY_REFUNDED / CAPTURED`, `PARTIALLY_REFUNDED → CAPTURED` | Razorpay authorises before it captures; a failed gateway refund gives the money back. The guard allows these for `ONLINE` only. |
| `refunds` | method `ONLINE`; new `gateway_refund_id` (`rfnd_…`) | A refund sent back through Razorpay waits `PENDING` for `refund.processed` or `refund.failed`. |

## `payment_gateway_orders`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `reservation_id` | uuid | The booking (FK with `property_id`) |
| `gateway` | text | `RAZORPAY` |
| `gateway_order_id` | text | Razorpay's `order_…`; unique per gateway |
| `amount`, `currency` | numeric(12,2), char(3) | What the guest is asked for (₹1 or more; paise only at the Razorpay boundary) |
| `status` | text | `CREATED` → `PAID` or `CANCELLED`; `CANCELLED` → `PAID` when the guest paid as it was cancelled |
| `public_token` | text | The guest's page `/pay/<token>`: 32 random bytes, base64url; unique |
| `note` | text | Optional, ≤ 500 (no card numbers: refused by the API) |
| `attempts`, `last_error` | integer, text | Declined tries and the bank's reason (shown to the guest and staff) |
| `callback_at` | timestamptz | When the guest's browser said it paid (the page shows "confirming"; records nothing) |
| `payment_id`, `paid_at` | uuid, timestamptz | Set together when Razorpay's webhook records the payment (`ck_payment_gateway_orders_paid`) |
| `cancelled_at`, `cancelled_by`, `cancel_reason` | | Set together; reason 3–500 characters |
| audit columns | | `created_at`, `updated_at`, `created_by`, `updated_by` |

What a link is for (booking, amount, order, token, note) never changes (`fn_payment_gateway_orders_guard`); a paid link
stays paid. Indexes: by booking (newest first), by organisation.

## `webhook_events`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid | |
| `provider`, `event_id` | text | `RAZORPAY` and Razorpay's `X-Razorpay-Event-Id`; **unique together**: the dedupe |
| `event_type` | text | `payment.captured`, `refund.processed` … |
| `organization_id`, `property_id`, `gateway_order_id`, `payment_id` | uuid | Filled once the event is matched to one of our links (NULL for an unknown order) |
| `status` | text | `PROCESSED`, `IGNORED` (verified, nothing to do: a repeat of a state already recorded, an order that isn't ours) or `FAILED` (our error; Razorpay retries) |
| `outcome` | text | What it did, in words ("P00004 (pay_…, ₹2,000 by UPI) captured; R00003 confirmed"), or why it changed nothing; ≤ 1,000 |
| `payload` | jsonb | The event with what we need of it: ids, amounts, statuses, error. Card details, email, phone and UPI id are stripped first |
| `attempts` | integer | Deliveries of a `FAILED` event |
| `received_at`, `processed_at` | timestamptz | `processed_at` is NULL exactly when `FAILED` (`ck_webhook_events_done`) |

A bad signature stores nothing (401). Only a `FAILED` row can change, when Razorpay's retry processes it
(`fn_webhook_events_guard`). Indexes: by link and time, by time.

## Changed: `payments` and `refunds`

| Column | Notes |
|---|---|
| `payments.method` | adds `ONLINE` ("Online (Razorpay)"); the API refuses it for desk payments |
| `payments.reference` | for `ONLINE`: Razorpay's `pay_…` id; `ux_payments_gateway_payment` makes it unique among online payments, so two webhooks for one payment make one row |
| `payments.gateway_order_id` | the link it came through; set exactly for `ONLINE` (`ck_payments_gateway`) |
| `payments.gateway_method` | how the guest paid in Razorpay: `upi`, `card`, `netbanking`, `wallet` … |
| `refunds.method` | adds `ONLINE` |
| `refunds.gateway_refund_id` | Razorpay's `rfnd_…`; set exactly for `ONLINE` (`ck_refunds_gateway`). Status moves `PENDING` → `PROCESSED` or `FAILED` only |

None of the new or changed columns holds card data.
