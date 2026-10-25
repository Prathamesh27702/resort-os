# Sprint 12 — schema reference (GST invoices, credit and debit notes, receipts)

Source of truth: `database/migrations/V014__invoicing.sql`. No new dev seed: invoices take real numbers from the series, so
they are issued in the demo instead; the seeded stay R00007 (departing today, with P00001 on its bill) is the one to check
out. Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.22](../architecture/system-design.md#422-gst-invoices-gap-free-numbers-and-corrections-only-by-note)
and [GST.md → Invoices](../GST.md#invoices-credit-and-debit-notes-sprint-12).

## What changed

| Object | Change | Why |
|---|---|---|
| `invoice_series` | new table; never deleted; `last_number` only ever goes up by one (trigger) | One numbering sequence per property, document type and financial year: the gap-free counter. |
| `invoices`, `invoice_items`, `invoice_item_taxes` | new tables; never updated or deleted | A GST tax invoice or bill of supply, its lines (one per folio charge) and each line's taxes, copied from the folio. |
| `credit_notes`, `credit_note_items`, `credit_note_item_taxes` | new tables; never updated or deleted | The only way to cancel (`CANCEL`) or reduce (`REDUCE`) an invoice. |
| `debit_notes`, `debit_note_items`, `debit_note_item_taxes` | new tables; never updated or deleted | Charges posted to a folio after its invoice. |
| `receipts` | new table; never updated or deleted; one per payment | A numbered receipt for money received. |
| `einvoice_records` | new table (empty until Sprint 25) | The invoice registration portal's answer for a document: IRN, acknowledgement, signed QR. |
| `folio_items` | `item_type` adds `CREDIT`; new `adjusts_item_id`, `credit_note_id`; `fn_folio_items_check` extended | A REDUCE credit note takes the amount off the guest's bill with a negative line pointing at the charge it reduces. |

## `invoice_series`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `document_type` | text | `INVOICE`, `BILL_OF_SUPPLY`, `CREDIT_NOTE`, `DEBIT_NOTE`, `RECEIPT` |
| `financial_year` | text | `2026-27` (1 April to 31 March) |
| `prefix`, `width` | text, smallint | `BEACH/2627/` and 5 (invoices, receipts) or `BEACH/C2627/` and 4 (notes; `B` bill of supply, `D` debit note, `R` receipt): numbers stay within 16 characters of `A-Z 0-9 / -` (GST rule 46(b)) |
| `last_number` | integer | The last number given out; starts at 1 with the first document |

Unique on `(property_id, document_type, financial_year)`. A number is taken with one statement,
`INSERT … ON CONFLICT DO UPDATE SET last_number = last_number + 1 RETURNING`, inside the transaction that inserts the
document: the row stays locked until that transaction ends, so two invoices at the same moment get consecutive numbers,
and a failure rolls the number back with the document (no gaps). `fn_invoice_series_guard` refuses any other change
(`ck_invoice_series_gap_free`); deleting is refused.

## `invoices`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | |
| `series_id`, `document_type`, `number`, `financial_year`, `invoice_date` | | `INVOICE` or `BILL_OF_SUPPLY`; number unique per property |
| `folio_id`, `reservation_id` | uuid | The bill it is for (FK with `property_id`) |
| `gst_registration_id`, `supplier_gstin`, `supplier_legal_name`, `supplier_trade_name`, `supplier_address`, `supplier_state_code` | | The supplier **as on the day**: copied, so a later change of name or address doesn't change an issued invoice |
| `bill_to_guest_id`, `customer_name`, `customer_gstin`, `customer_address`, `customer_state_code` | | The customer as billed. A GSTIN makes it B2B; its first two digits must be `customer_state_code` (`ck_invoices_cust_state`) |
| `place_of_supply` | char(2) | The property's state for a hotel stay (IGST Act s.12(3)) |
| `taxable_value`, `cgst`, `sgst`, `igst`, `cess`, `tax_total`, `total`, `currency` | | `tax_total` = the four taxes, `total` = taxable + tax (`ck_invoices_totals`); a bill of supply has no tax (`ck_invoices_supply`) |
| `notes` | text | ≤ 500, printed on the invoice |
| `issued_at`, `issued_by` | | |

Status is not stored: it is worked out from the credit notes (`CANCELLED` with a `CANCEL` note; `CREDITED` when they
take off everything; `PARTLY_CREDITED`; else `ISSUED`).

## Lines and taxes (`invoice_items`, `debit_note_items`, `credit_note_items` and their `…_taxes`)

| Column | Notes |
|---|---|
| `line_no`, `folio_item_id`, `category`, `description`, `hsn_sac`, `service_date` | One folio charge per line; SAC from the tax category (996311 rooms, 996331 food …) |
| `quantity`, `unit_price`, `gross_amount`, `discount`, `taxable_value`, `tax_amount`, `total` | `discount` = what credit notes had already taken off the charge before it was billed (when an invoice is issued again after a cancel) |
| `…_taxes`: `tax_rule_id`, `tax_code`, `rate`, `taxable_value`, `amount` | Copied from `folio_item_taxes` as posted: never recalculated |
| `credit_note_items.invoice_item_id` / `debit_note_item_id` | Exactly one: the line it credits; `folio_credit_item_id` = the `CREDIT` line it put on the folio (REDUCE only) |

Checked when the transaction commits (deferred constraint triggers `fn_document_totals_add_up`): each header equals the
sum of its lines and each line's tax the sum of its tax rows (`ck_invoices_add_up`, `ck_credit_notes_add_up`,
`ck_debit_notes_add_up`). `fn_credit_note_items_limit` refuses crediting a line for more than is left of it, value or tax
(`ck_credit_note_items_limit`).

## `credit_notes` and `debit_notes`

| Column | Notes |
|---|---|
| `series_id`, `number`, `financial_year`, `note_date`, `invoice_id` | The invoice it adjusts |
| `note_type` (credit notes) | `CANCEL` (everything left on the invoice and its debit notes; at most one per invoice: `ux_credit_notes_cancel`) or `REDUCE` |
| `reason` | 3–500 characters, printed on the note |
| `taxable_value`, `cgst`, `sgst`, `igst`, `cess`, `tax_total`, `total`, `currency`, `issued_at`, `issued_by` | As on invoices; a note is never for ₹0 |

## `receipts`

| Column | Notes |
|---|---|
| `series_id`, `number`, `financial_year`, `receipt_date` | `BEACH/R2627/00001` |
| `payment_id` | Unique: one receipt per payment (`ux_receipts_payment`) |
| `reservation_id`, `received_from`, `amount`, `method`, `currency` | Copied from the payment when issued. If the payment is voided later, the receipt stays and its PDF says VOID |

## `einvoice_records`

`document_type` (`INVOICE`, `CREDIT_NOTE`, `DEBIT_NOTE`) and `document_id` (unique together), `status` (`PENDING`,
`GENERATED`, `FAILED`, `CANCELLED`), `irn` (64 hex characters), `ack_no`, `ack_date`, `signed_qr`, `signed_invoice`,
`provider`, `attempts`, `last_error`, `cancelled_at`. A `GENERATED` row has IRN, acknowledgement and QR
(`ck_einvoice_records_done`). Nothing writes it yet: Sprint 25 registers B2B invoices with the IRP.

## Changed: `folio_items`

| Column | Notes |
|---|---|
| `item_type` | adds `CREDIT`: quantity −1, `amount` and `tax_amount` ≤ 0, a reason, `adjusts_item_id` and `credit_note_id` set (`ck_folio_items_amounts`) |
| `adjusts_item_id` | The charge a credit line reduces (same folio and category). All its credit lines together never take off more than the charge, value or tax (`ck_folio_items_credit`) |
| `credit_note_id` | The credit note that made it |

A charge that a credit note reduced can't be reversed any more (`ck_folio_items_reversal_credited`): it is corrected with
another credit note. The API also refuses reversing or moving a charge that is on an invoice that stands.
