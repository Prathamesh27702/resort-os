-- Database checks for Sprint 16 (suppliers and purchasing). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 16. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 16).

-- 1. Migration V018 applied. Expect V001 … V018.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 10 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('suppliers', 'purchase_requests', 'purchase_request_items', 'purchase_orders', 'purchase_order_items',
                     'goods_received_notes', 'grn_items', 'supplier_invoices', 'supplier_invoice_items', 'supplier_payments')
ORDER BY table_name;

-- 3. Suppliers. (demo) FRESH-VEG and DAIRY in Goa (30), SPICES in Maharashtra (27), FISH unregistered (no GSTIN).
--    A registered supplier's state is the first two characters of its GSTIN.
SELECT code, name, gstin, state_code, payment_terms_days, is_active,
       CASE WHEN gstin IS NOT NULL AND left(gstin, 2) <> state_code THEN 'STATE DIFFERS FROM GSTIN' END AS problem
FROM suppliers ORDER BY code;

-- 4. Orders with what has come in and what is still to come. (demo) after the review: PO00001 Mapusa Fresh Vegetables
--    RECEIVED, ₹11,000, approved by the owner (not the clerk who raised it), 160 kg mixed vegetables in over two deliveries.
SELECT o.number, s.name AS supplier, o.status, o.total, o.approval_limit, o.total > o.approval_limit AS needed_approval,
       o.created_by = o.approved_by AS self_approved, i.name AS item, oi.quantity, oi.received_quantity,
       oi.quantity - oi.received_quantity AS to_come, oi.billed_quantity
FROM purchase_orders o JOIN suppliers s ON s.id = o.supplier_id
JOIN purchase_order_items oi ON oi.order_id = o.id JOIN inventory_items i ON i.id = oi.item_id
ORDER BY o.created_at DESC, oi.sort_order LIMIT 60;

-- 5. Received is the sum of the order line's goods received notes. Expect NO rows.
SELECT oi.id, oi.received_quantity, coalesce(sum(g.quantity), 0) AS from_grns
FROM purchase_order_items oi LEFT JOIN grn_items g ON g.order_item_id = oi.id
GROUP BY oi.id, oi.received_quantity HAVING oi.received_quantity <> coalesce(sum(g.quantity), 0);

-- 6. Every goods received line put its stock into the store: one RECEIVED movement, same item, quantity and cost.
--    Expect NO rows.
SELECT gi.id, gi.quantity, m.quantity AS moved, gi.unit_cost, m.unit_cost AS moved_cost
FROM grn_items gi JOIN goods_received_notes g ON g.id = gi.grn_id
LEFT JOIN stock_movements m ON m.grn_item_id = gi.id
WHERE m.id IS NULL OR m.movement_type <> 'RECEIVED' OR m.item_id <> gi.item_id OR m.location_id <> g.location_id
   OR m.quantity <> gi.quantity OR m.unit_cost <> gi.unit_cost;

-- 7. An order's status matches what came in. Expect NO rows.
SELECT o.number, o.status, sum(oi.received_quantity) AS received, sum(oi.quantity) AS ordered
FROM purchase_orders o JOIN purchase_order_items oi ON oi.order_id = o.id
GROUP BY o.id, o.number, o.status
HAVING (o.status = 'RECEIVED' AND bool_or(oi.received_quantity < oi.quantity))
    OR (o.status = 'PARTLY_RECEIVED' AND (sum(oi.received_quantity) = 0 OR bool_and(oi.received_quantity = oi.quantity)))
    OR (o.status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'CANCELLED') AND sum(oi.received_quantity) > 0);

-- 8. Supplier bills: GST split, input tax, what is paid. (demo) one bill on PO00001, ₹11,000 (vegetables at 0 %), PAID.
--    A bill's amount_paid is the sum of its payments (the "paid" column) and an unregistered supplier's bill has no GST.
SELECT b.number, b.supplier_number, s.name AS supplier, b.invoice_date, b.due_date, b.intra_state, b.taxable_value,
       b.cgst, b.sgst, b.igst, b.total, b.itc_eligible, b.amount_paid, coalesce(sum(p.amount), 0) AS paid, b.status
FROM supplier_invoices b JOIN suppliers s ON s.id = b.supplier_id LEFT JOIN supplier_payments p ON p.invoice_id = b.id
GROUP BY b.id, s.name ORDER BY b.created_at DESC LIMIT 20;

-- 9. Bills add up: amount paid = payments, total = lines, never billed more than came in. Expect NO rows.
SELECT b.number, 'paid differs from payments' AS problem FROM supplier_invoices b
WHERE b.amount_paid <> (SELECT coalesce(sum(amount), 0) FROM supplier_payments p WHERE p.invoice_id = b.id)
UNION ALL
SELECT b.number, 'total differs from lines' FROM supplier_invoices b
WHERE b.total <> (SELECT coalesce(sum(total), 0) FROM supplier_invoice_items l WHERE l.invoice_id = b.id)
UNION ALL
SELECT o.number, 'billed more than received' FROM purchase_order_items oi JOIN purchase_orders o ON o.id = oi.order_id
WHERE oi.billed_quantity > oi.received_quantity;

-- 10. A cash supplier payment left the open drawer as a PAY_OUT of the same amount. Expect NO rows.
SELECT p.number, p.amount, d.amount AS drawer
FROM supplier_payments p LEFT JOIN cash_drawer_movements d ON d.supplier_payment_id = p.id
WHERE p.method = 'CASH' AND (d.id IS NULL OR d.movement_type <> 'PAY_OUT' OR abs(d.amount) <> p.amount);

-- 11. Every goods received note and payment made in the app is in the audit log. Expect NO rows.
SELECT g.number FROM goods_received_notes g
WHERE NOT EXISTS (SELECT 1 FROM audit_logs l WHERE l.entity_type = 'GoodsReceivedNote' AND l.entity_id = g.id::text)
UNION ALL
SELECT p.number FROM supplier_payments p
WHERE NOT EXISTS (SELECT 1 FROM audit_logs l WHERE l.entity_type = 'SupplierPayment' AND l.entity_id = p.id::text);
