-- Database checks for Sprint 15 (inventory and recipe costing). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 15. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 15).

-- 1. Migration V017 applied. Expect V001 … V017.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 10 rows (the nine of the plan, and stock_levels, the on-hand kept from the ledger).
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('inventory_units', 'inventory_categories', 'stock_locations', 'inventory_items', 'stock_movements', 'stock_levels',
                     'stock_adjustments', 'stock_counts', 'recipes', 'recipe_ingredients')
ORDER BY table_name;

-- 3. Stock by item and store. (demo) 21 items; Paneer 5 kg in the Kitchen before the review, 4.4 kg after selling 3
--    Paneer Tikka (Full); Fresh cream at or below its reorder level of 2 l.
SELECT c.name AS grp, i.code, i.name, u.code AS unit, i.unit_cost, i.reorder_level, l.code AS store, s.on_hand
FROM inventory_items i JOIN inventory_categories c ON c.id = i.category_id JOIN inventory_units u ON u.id = i.unit_id
LEFT JOIN stock_levels s ON s.item_id = i.id LEFT JOIN stock_locations l ON l.id = s.location_id
ORDER BY c.sort_order, i.name, l.sort_order;

-- 4. On hand is always the sum of the ledger, per item and store. Expect NO rows.
SELECT s.item_id, s.location_id, s.on_hand, coalesce(sum(m.quantity), 0) AS ledger
FROM stock_levels s LEFT JOIN stock_movements m ON m.item_id = s.item_id AND m.location_id = s.location_id
GROUP BY s.item_id, s.location_id, s.on_hand HAVING s.on_hand <> coalesce(sum(m.quantity), 0);

-- 5. Each movement's "on hand after" follows on from the one before it at the same store. Expect NO rows.
SELECT m.item_id, m.location_id, m.created_at, m.quantity, m.on_hand_after, prev AS before
FROM (SELECT m.*, lag(m.on_hand_after, 1, 0::numeric) OVER (PARTITION BY m.item_id, m.location_id ORDER BY m.created_at, m.id) AS prev
      FROM stock_movements m) m
WHERE m.on_hand_after <> m.prev + m.quantity;

-- 6. The ledger, newest first. (demo) after the review: CONSUMPTION rows for the 3 Paneer Tikka with their order and
--    ticket, a WASTAGE of fresh cream and a COUNT of paneer, each with its reason.
SELECT m.created_at, i.name AS item, l.code AS store, m.movement_type, m.quantity, m.unit_cost, m.value, m.on_hand_after,
       coalesce(a.number, c.number) AS doc, m.note
FROM stock_movements m JOIN inventory_items i ON i.id = m.item_id JOIN stock_locations l ON l.id = m.location_id
LEFT JOIN stock_adjustments a ON a.id = m.adjustment_id LEFT JOIN stock_counts c ON c.id = m.count_id
ORDER BY m.created_at DESC, m.id DESC LIMIT 40;

-- 7. A served dish used its recipe once: every consumption points at a served ticket's live line. Expect NO rows.
SELECT m.id, oi.status, k.status AS ticket
FROM stock_movements m JOIN order_items oi ON oi.id = m.order_item_id JOIN kots k ON k.id = m.kot_id
WHERE m.movement_type = 'CONSUMPTION' AND (k.status <> 'SERVED' OR oi.kot_id <> k.id);

-- 8. Every person's stock change has a reason and is in the audit log with it. Expect only SA00001 (written straight
--    into the database by the demo data, not through the app).
SELECT a.number, a.kind, a.reason FROM stock_adjustments a
WHERE length(btrim(a.reason)) < 3
   OR NOT EXISTS (SELECT 1 FROM audit_logs l WHERE l.entity_type = 'StockAdjustment' AND l.entity_id = a.id::text AND l.reason LIKE a.number || '%');

-- 9. Recipe cost and food cost per dish at today's unit costs. (demo) Paneer Tikka (Full) ₹105.00 of ₹280 = 37.50 %.
SELECT mi.name || coalesce(' (' || v.name || ')', '') AS dish, coalesce(v.price, mi.price) AS price,
       round(sum(round(ri.quantity * ru.factor / iu.factor, 3) * it.unit_cost), 2) AS cost,
       round(sum(round(ri.quantity * ru.factor / iu.factor, 3) * it.unit_cost) / nullif(coalesce(v.price, mi.price), 0) * 100, 2) AS food_cost_pct
FROM recipes r JOIN menu_items mi ON mi.id = r.menu_item_id LEFT JOIN menu_item_variants v ON v.id = r.variant_id
JOIN recipe_ingredients ri ON ri.recipe_id = r.id JOIN inventory_items it ON it.id = ri.item_id
JOIN inventory_units ru ON ru.id = ri.unit_id JOIN inventory_units iu ON iu.id = it.unit_id
WHERE r.is_active
GROUP BY mi.name, v.name, v.price, mi.price ORDER BY food_cost_pct DESC;

-- 10. A recipe's units are of its items' kind (no ml of an item kept in kg). Expect NO rows.
SELECT r.id, it.name, ru.code AS recipe_unit, iu.code AS item_unit
FROM recipe_ingredients ri JOIN recipes r ON r.id = ri.recipe_id JOIN inventory_items it ON it.id = ri.item_id
JOIN inventory_units ru ON ru.id = ri.unit_id JOIN inventory_units iu ON iu.id = it.unit_id
WHERE ru.dimension <> iu.dimension;
