-- Database checks for Sprint 12 (GST invoices, credit and debit notes, receipts). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 12. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 12).

-- 1. Migration V014 applied. Expect V001 … V014.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 12 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('invoice_series', 'invoices', 'invoice_items', 'invoice_item_taxes', 'debit_notes', 'debit_note_items',
                     'debit_note_item_taxes', 'credit_notes', 'credit_note_items', 'credit_note_item_taxes', 'receipts', 'einvoice_records')
ORDER BY table_name;

-- 3. The number series: one per property, document type and financial year (April–March). (demo) INVOICE BEACH/2627/
--    at 2 (R00007's invoice, then the B2B one), CREDIT_NOTE at 2, DEBIT_NOTE at 1, RECEIPT at the receipts issued.
SELECT p.code, s.document_type, s.financial_year, s.prefix, s.width, s.last_number
FROM invoice_series s JOIN properties p ON p.id = s.property_id ORDER BY p.code, s.financial_year, s.document_type;

-- 4. Numbers are gap-free: every series' documents are numbered 1 … last_number with no hole and no repeat. Expect NO rows.
WITH docs AS (
    SELECT series_id, number FROM invoices UNION ALL SELECT series_id, number FROM credit_notes
    UNION ALL SELECT series_id, number FROM debit_notes UNION ALL SELECT series_id, number FROM receipts)
SELECT s.prefix, s.last_number, count(d.number) AS issued, count(DISTINCT d.number) AS distinct_numbers
FROM invoice_series s LEFT JOIN docs d ON d.series_id = s.id
GROUP BY s.id, s.prefix, s.last_number
HAVING count(d.number) <> s.last_number OR count(DISTINCT d.number) <> s.last_number
    OR max(right(d.number, s.width)::int) <> s.last_number;

-- 5. Every invoice with its customer, totals and what credit notes took off. (demo) R00007's B2C invoice; the e2e or review
--    B2B invoice to a company with a GSTIN, place of supply 30 (Goa) and CGST+SGST, never IGST.
SELECT i.number, i.document_type, i.invoice_date, r.reference, f.number AS folio, i.customer_name, i.customer_gstin,
       i.customer_state_code, i.place_of_supply, i.taxable_value, i.cgst, i.sgst, i.igst, i.total,
       coalesce((SELECT sum(total) FROM credit_notes c WHERE c.invoice_id = i.id), 0) AS credited,
       (SELECT string_agg(note_type, ',') FROM credit_notes c WHERE c.invoice_id = i.id) AS credit_notes
FROM invoices i JOIN reservations r ON r.id = i.reservation_id JOIN folios f ON f.id = i.folio_id
ORDER BY i.issued_at;

-- 6. Documents add up: header = sum of lines, line = sum of its taxes (the database refuses anything else). Expect NO rows.
SELECT 'invoice' AS doc, i.number FROM invoices i
WHERE i.taxable_value <> (SELECT coalesce(sum(taxable_value), 0) FROM invoice_items WHERE invoice_id = i.id)
   OR i.tax_total <> (SELECT coalesce(sum(t.amount), 0) FROM invoice_items x JOIN invoice_item_taxes t ON t.invoice_item_id = x.id WHERE x.invoice_id = i.id)
UNION ALL
SELECT 'credit note', c.number FROM credit_notes c
WHERE c.taxable_value <> (SELECT coalesce(sum(taxable_value), 0) FROM credit_note_items WHERE credit_note_id = c.id)
UNION ALL
SELECT 'debit note', d.number FROM debit_notes d
WHERE d.taxable_value <> (SELECT coalesce(sum(taxable_value), 0) FROM debit_note_items WHERE debit_note_id = d.id);

-- 7. A hotel stay is taxed where the hotel is: place of supply = the supplier's state, so no IGST on any invoice. Expect NO rows.
SELECT number, supplier_state_code, place_of_supply, igst FROM invoices WHERE place_of_supply <> supplier_state_code OR igst <> 0;

-- 8. A folio charge is on at most one invoice or debit note that stands (not cancelled by a CANCEL credit note). Expect NO rows.
WITH billed AS (
    SELECT x.folio_item_id, i.id AS invoice_id FROM invoice_items x JOIN invoices i ON i.id = x.invoice_id
    UNION ALL
    SELECT x.folio_item_id, d.invoice_id FROM debit_note_items x JOIN debit_notes d ON d.id = x.debit_note_id)
SELECT folio_item_id, count(*) FROM billed b
WHERE NOT EXISTS (SELECT 1 FROM credit_notes c WHERE c.invoice_id = b.invoice_id AND c.note_type = 'CANCEL')
GROUP BY folio_item_id HAVING count(*) > 1;

-- 9. Credit notes and the bill: each REDUCE line put a CREDIT line on the folio for the same amount; CANCEL notes don't
--    touch the bill. (demo) "Credit note …: Dinner …" lines with the reason. Expect the "differs" column to be all false.
SELECT c.number, c.note_type, x.description, x.taxable_value + x.tax_amount AS credited, fi.total AS on_bill, fi.reason,
       (fi.id IS NULL) <> (c.note_type = 'CANCEL') OR coalesce(-fi.total, 0) <> CASE WHEN c.note_type = 'CANCEL' THEN 0 ELSE x.taxable_value + x.tax_amount END AS differs
FROM credit_notes c JOIN credit_note_items x ON x.credit_note_id = c.id LEFT JOIN folio_items fi ON fi.id = x.folio_credit_item_id
ORDER BY c.issued_at, x.line_no;

-- 10. Receipts: one per payment that counts, for the payment's amount. (demo) R00007's P00001 receipt from check-out.
SELECT rc.number, rc.receipt_date, p.number AS payment, p.status, rc.amount, p.amount AS paid, rc.method, rc.received_from
FROM receipts rc JOIN payments p ON p.id = rc.payment_id ORDER BY rc.issued_at;

-- 11. Issued documents are never edited or deleted. Each statement below must FAIL with "Rows in … cannot be changed" (run them
--     one at a time, inside BEGIN; … ROLLBACK; if you want to be safe):
--   UPDATE invoices SET customer_name = 'X';
--   DELETE FROM receipts;
--   UPDATE invoice_series SET last_number = last_number + 5;   -- fails: ck_invoice_series_gap_free
SELECT tgrelid::regclass AS table_name, tgname FROM pg_trigger
WHERE NOT tgisinternal AND tgrelid::regclass::text IN ('invoices', 'invoice_items', 'credit_notes', 'debit_notes', 'receipts', 'invoice_series')
ORDER BY 1, 2;
