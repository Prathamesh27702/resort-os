# Sprint 16 — schema reference (suppliers and purchasing)

Source of truth: `database/migrations/V018__purchasing.sql`. Dev seed: `database/seed/dev/S013__purchasing.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.26](../architecture/system-design.md#426-purchasing-approval-goods-received-and-supplier-bills).

Purchasing builds on inventory (Sprint 15): a request and an order are for one store (`stock_locations`), their lines are
stock items (`inventory_items`, in the item's unit), and each goods received line is one `RECEIVED` row in the stock
ledger (`stock_movements`), which moves the item's average cost. Cash paid to a supplier leaves the cash drawer of Sprint 10
(`cash_drawer_movements`, `PAY_OUT`). Numbers come from `property_counters` (`purchase_request` → `PR00001`,
`purchase_order` → `PO00001`, `goods_received` → `GRN00001`, `supplier_invoice` → `SI00001`, `supplier_payment` →
`SP00001`). Every change is written to `audit_logs`; a goods received note, a payment and a cancel, close or send-back carry
their reason.

Setting read from `property_settings`: `purchasing.approval_limit` (a number or `{"amount": n}`; default ₹10,000). An order
whose total with GST is over it needs `purchase.approve` from someone who did not raise or submit it. The limit in force is
saved on the order when it is submitted (`approval_limit`).

## What changed

| Object | Change | Why |
|---|---|---|
| `suppliers` | new table; never deleted (switched off instead) | Who the property buys from, with their GSTIN and the state they supply from. |
| `purchase_requests`, `purchase_request_items` | new tables; request never deleted, its lines never changed | What a store needs, before an order is raised. |
| `purchase_orders`, `purchase_order_items` | new tables; never deleted | The order to one supplier with its approval and how much has come in and been billed per line. |
| `goods_received_notes`, `grn_items` | new tables; append-only (trigger) | Each delivery against an order. |
| `supplier_invoices`, `supplier_invoice_items` | new tables; a bill never deleted and fixed once saved except what is paid; lines append-only | The supplier's bill with its GST (input tax). |
| `supplier_payments` | new table; append-only | Money paid against a bill. |
| `stock_movements` | `grn_item_id` added; `ck_stock_movements_source` rewritten | A `RECEIVED` movement now comes from either a person's adjustment or a goods received line (exactly one). |
| `cash_drawer_movements` | `supplier_payment_id` added | A cash supplier payment is a `PAY_OUT` linked to the payment. |

## `suppliers`

| Column | Notes |
|---|---|
| `code` | `^[A-Z0-9][A-Z0-9_-]{0,19}$`, unique per property (`ux_suppliers_code`) |
| `name` | 2–120, as on their bills |
| `gstin` | NULL = unregistered (their bills carry no GST). 15 characters; the app checks the PAN part and the check digit; unique per property (`ux_suppliers_gstin`) |
| `state_code` | 2 digits; equals the GSTIN's first two when there is one (`ck_suppliers_gstin`). Decides CGST + SGST (same state as the property's GST registration) or IGST |
| `payment_terms_days` | 0–180; a bill's due date defaults to its date plus these days |
| `contact_name`, `phone`, `email`, `address`, `note`, `is_active` | |

## `purchase_requests` / `purchase_request_items`

`number` (`PR00001` …), `location_id` (the store), `needed_by`, `note`, `status` (`OPEN`, `ORDERED`, `CANCELLED`),
`purchase_order_id` (set exactly when `ORDERED`), `cancel_reason` (3–300, set exactly when `CANCELLED`). Lines: `item_id`
(once per request), `quantity` (> 0, in the item's unit), `note`. Cancelling the order raised from a request opens the
request again.

## `purchase_orders` / `purchase_order_items`

| Column | Notes |
|---|---|
| `number` | `PO00001` … |
| `supplier_id`, `location_id`, `request_id` | Same property (composite FKs). One live order per request (`ux_purchase_orders_request`, cancelled ones excepted) |
| `status` | `DRAFT` → `PENDING_APPROVAL` → `APPROVED` → `PARTLY_RECEIVED` → `RECEIVED`; `CLOSED` (part received, the rest not coming); `CANCELLED` (nothing received). Over the limit it waits in `PENDING_APPROVAL`; within it, submit goes straight to `APPROVED` |
| `intra_state` | The supplier's state equals the property's GST state: CGST + SGST, else IGST |
| `subtotal`, `tax_total`, `total` | `total = subtotal + tax_total` (CHECK); worked out from the lines |
| `approval_limit`, `submitted_at`/`_by`, `approved_at`/`_by`, `approval_note` | Submitted once it left draft; approved before anything can come in (CHECKs) |
| `rejected_reason`, `end_reason` | The last send-back reason; the reason it was cancelled or closed (3–300, set exactly then) |

Lines: `item_id` (once per order), `quantity` (> 0), `unit_price` (one unit before GST, 4 decimals), `gst_rate` (0, 0.25, 3,
5, 12, 18, 28, 40), `taxable_value = round(quantity × unit_price, 2)`, `tax`, `received_quantity`, `billed_quantity`.
`ck_purchase_order_items_flow`: 0 ≤ billed ≤ received ≤ ordered, so the database itself refuses receiving more than was
ordered or billing more than came in. What is still to come is `quantity − received_quantity`.

## `goods_received_notes` / `grn_items`

`number` (`GRN00001` …), `order_id`, `supplier_id`, `location_id` (the store it went into), `received_on`, `challan_number`
(the supplier's delivery challan), `note`, `value` (before GST). Lines: `order_item_id`, `item_id`, `quantity` (> 0),
`unit_cost` (the order's price), `value = round(quantity × unit_cost, 2)`. Both append-only. Each line has exactly one
`stock_movements` row (`grn_item_id`, `ux_stock_movements_grn_item`) with note "PO00001 · Mapusa Fresh Vegetables".

## `supplier_invoices` / `supplier_invoice_items`

| Column | Notes |
|---|---|
| `number`, `supplier_number` | Ours (`SI00001` …) and theirs; the supplier's number is unique per supplier, any case or spaces (`ux_supplier_invoices_supplier_number`) |
| `invoice_date`, `due_date` | Due on or after the bill date |
| `supplier_gstin`, `supplier_state_code`, `buyer_state_code`, `intra_state` | As on the bill date. Unregistered supplier: no tax (`ck_supplier_invoices_unreg`) |
| `itc_eligible` | The property had a regular GST registration and the supplier a GSTIN: the input tax can be claimed |
| `taxable_value`, `cgst`, `sgst`, `igst`, `tax_total`, `total` | `tax_total = cgst + sgst + igst`; inside one state only CGST + SGST, across states only IGST (CHECK) |
| `amount_paid`, `status` | `UNPAID` (nothing paid), `PART_PAID`, `PAID` (= total) — CHECK keeps them in step |

`trg_supplier_invoices_fixed`: once saved, only `amount_paid` (which only grows), `status`, `note` and the update stamps can
change. Lines: `order_item_id` (once per bill), `quantity`, `unit_price` (may differ from the order; the app flags it),
`gst_rate`, `taxable_value`, `cgst`, `sgst`, `igst`, `total`.

## `supplier_payments`

`number` (`SP00001` …), `supplier_id`, `invoice_id`, `paid_on`, `amount` (> 0), `method` (`CASH`, `BANK_TRANSFER`, `UPI`,
`CHEQUE`), `reference` (the UTR or cheque number: 3–60, required except for cash), `note`, `cash_drawer_session_id` (set
exactly for cash). Append-only. A cash payment adds a `cash_drawer_movements` row (`PAY_OUT`, `supplier_payment_id`, one per
payment).

## Data classification

`suppliers`, `purchase_requests`, `purchase_request_items`, `goods_received_notes`, `grn_items`: internal.
`purchase_orders`, `purchase_order_items`, `supplier_invoices`, `supplier_invoice_items`, `supplier_payments`: confidential
(prices and money paid).

## Dev seed S013

Four suppliers: `FRESH-VEG` Mapusa Fresh Vegetables (30AAACF1234A1ZS, Goa, 7 days), `DAIRY` Goa Dairy Co-operative
(30AAATG4321B1ZO, 15 days), `SPICES` Konkan Spice Traders (27AABCK7788C1ZA, Maharashtra: IGST, 30 days), `FISH` Anjuna
Fishermen (unregistered, Goa, pays on delivery). One open request `PR00001` for the main store: 15 kg onion and capsicum,
10 kg mixed vegetables, 100 limes. Seeded directly, so it has no audit row.
