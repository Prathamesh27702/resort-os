-- Database checks for Sprint 14 (menu, restaurant orders, kitchen tickets, room charges). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 14. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 14).

-- 1. Migration V016 applied. Expect V001 … V016.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 10 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('menu_categories', 'menu_items', 'menu_item_variants', 'modifier_groups', 'modifiers', 'restaurant_tables',
                     'orders', 'order_items', 'kots', 'kot_items')
ORDER BY table_name;

-- 3. The menu by section. (demo) 5 sections, 14 dishes; Paneer Tikka with 2 portions and 2 groups; Masala Chai sold out.
SELECT c.name AS section, i.code, i.name, i.food_type, i.price, i.is_available, i.is_active,
       (SELECT count(*) FROM menu_item_variants v WHERE v.menu_item_id = i.id AND v.is_active) AS portions,
       (SELECT count(*) FROM modifier_groups g WHERE g.menu_item_id = i.id AND g.is_active) AS choice_groups
FROM menu_items i JOIN menu_categories c ON c.id = i.category_id
ORDER BY c.sort_order, i.sort_order, i.name;

-- 4. Today's orders. (demo) O00001 at T2 open with K00001; after the review, the Table T4 order SETTLED as ROOM_CHARGE
--    for ₹1,250 before GST, and a takeaway VOIDED with its reason.
SELECT o.number, o.order_type, o.status, t.code AS table_code, u.code AS room, r.reference AS booking, o.subtotal,
       o.discount_amount, o.tax_amount, o.total, o.settlement, o.payment_method, f.number AS folio, o.void_reason
FROM orders o LEFT JOIN restaurant_tables t ON t.id = o.table_id LEFT JOIN accommodation_units u ON u.id = o.unit_id
LEFT JOIN reservations r ON r.id = o.reservation_id LEFT JOIN folios f ON f.id = o.folio_id
ORDER BY o.created_at DESC LIMIT 30;

-- 5. An order's money adds up: the subtotal is the sum of its live lines. Expect NO rows.
SELECT o.number, o.subtotal, coalesce(sum(l.amount) FILTER (WHERE l.status <> 'VOIDED'), 0) AS lines
FROM orders o LEFT JOIN order_items l ON l.order_id = o.id
GROUP BY o.id, o.number, o.subtotal HAVING o.subtotal <> coalesce(sum(l.amount) FILTER (WHERE l.status <> 'VOIDED'), 0);

-- 6. A room charge is exactly one live FOOD line from the restaurant on that guest's folio, with the order's GST and
--    amount after discount. Expect NO rows.
SELECT o.number, o.subtotal - o.discount_amount AS order_net, fi.amount, o.tax_amount, fi.tax_amount, fi.source, fi.category
FROM orders o JOIN folio_items fi ON fi.id = o.folio_item_id
WHERE o.settlement = 'ROOM_CHARGE'
  AND (fi.amount <> o.subtotal - o.discount_amount OR fi.tax_amount <> o.tax_amount OR fi.source <> 'RESTAURANT'
       OR fi.category <> 'FOOD' OR fi.folio_id <> o.folio_id);

-- 7. Every room charge has its folio line, and every restaurant folio line belongs to one order. Expect NO rows.
SELECT 'order without line' AS problem, o.number FROM orders o WHERE o.settlement = 'ROOM_CHARGE' AND o.folio_item_id IS NULL
UNION ALL
SELECT 'line without order', fi.description FROM folio_items fi
WHERE fi.source = 'RESTAURANT' AND fi.item_type = 'CHARGE' AND NOT EXISTS (SELECT 1 FROM orders o WHERE o.folio_item_id = fi.id);

-- 8. Cash paid at the restaurant is in the drawer once, for the order's total. Expect NO rows.
SELECT o.number, o.total, m.amount FROM orders o LEFT JOIN cash_drawer_movements m ON m.order_id = o.id
WHERE o.settlement = 'PAID' AND o.payment_method = 'CASH' AND o.total > 0 AND (m.id IS NULL OR m.amount <> o.total);

-- 9. At most one open order per table (the database refuses a second). Expect NO rows.
SELECT table_id, count(*) FROM orders WHERE status = 'OPEN' AND table_id IS NOT NULL GROUP BY table_id HAVING count(*) > 1;

-- 10. Every line sent to the kitchen is on its ticket once, and voided lines are struck through there too. Expect NO rows.
SELECT o.number, l.name, l.status, k.number AS kot, ki.id IS NULL AS missing_on_ticket,
       (l.status = 'VOIDED') <> (ki.voided_at IS NOT NULL) AS void_mismatch
FROM order_items l JOIN orders o ON o.id = l.order_id LEFT JOIN kots k ON k.id = l.kot_id
LEFT JOIN kot_items ki ON ki.order_item_id = l.id
WHERE l.status <> 'NEW' AND (ki.id IS NULL OR (l.status = 'VOIDED') <> (ki.voided_at IS NOT NULL));

-- 11. A ticket with every line voided is cancelled; a cancelled one has nothing live on it. Expect NO rows.
SELECT k.number, k.status, count(ki.id) AS lines, count(ki.id) FILTER (WHERE ki.voided_at IS NULL) AS live
FROM kots k LEFT JOIN kot_items ki ON ki.kot_id = k.id GROUP BY k.id, k.number, k.status
HAVING (k.status = 'CANCELLED') <> (count(ki.id) FILTER (WHERE ki.voided_at IS NULL) = 0);

-- 12. A settled order has nothing still in the kitchen or not sent. Expect NO rows.
SELECT o.number, l.status AS line_status, k.status AS kot_status FROM orders o
LEFT JOIN order_items l ON l.order_id = o.id AND l.status = 'NEW'
LEFT JOIN kots k ON k.order_id = o.id AND k.status IN ('NEW', 'PREPARING', 'READY')
WHERE o.status = 'SETTLED' AND (l.id IS NOT NULL OR k.id IS NOT NULL);

-- 13. Order and ticket numbers are gap-free per property (O00001 …, K00001 …). Expect NO rows.
SELECT p.code, 'orders' AS kind, count(*) AS how_many, max(substring(o.number FROM 2)::int) AS highest
FROM orders o JOIN properties p ON p.id = o.property_id GROUP BY p.code HAVING count(*) <> max(substring(o.number FROM 2)::int)
UNION ALL
SELECT p.code, 'kots', count(*), max(substring(k.number FROM 2)::int)
FROM kots k JOIN properties p ON p.id = k.property_id GROUP BY p.code HAVING count(*) <> max(substring(k.number FROM 2)::int);

-- 14. Kitchen timings today: how long tickets waited before Ready.
SELECT k.number, o.number AS order_number, k.status, k.sent_at, k.started_at, k.ready_at, k.served_at,
       round(extract(epoch FROM (coalesce(k.ready_at, now()) - k.sent_at)) / 60) AS minutes_to_ready
FROM kots k JOIN orders o ON o.id = k.order_id ORDER BY k.sent_at DESC LIMIT 20;

-- 15. Who did what: discounts, voids and settlements are in the audit trail with their reason.
SELECT entity_type, action, count(*) FROM audit_logs
WHERE entity_type IN ('Order', 'OrderItem', 'Kot', 'MenuItem', 'MenuCategory', 'RestaurantTable')
GROUP BY entity_type, action ORDER BY entity_type, action;
