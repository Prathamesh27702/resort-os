-- Database checks for Sprint 08 (folios: the bill of a stay). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 08. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 08).

-- 1. Migration V010 applied. Expect V001 … V010.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The three new tables exist. Expect folios, folio_items, folio_item_taxes.
SELECT table_name FROM information_schema.tables WHERE table_name IN ('folios', 'folio_items', 'folio_item_taxes') ORDER BY table_name;

-- 3. Every confirmed, in-house or checked-out booking has exactly one main folio. Expect NO rows.
SELECT r.reference, r.status, count(f.id) FILTER (WHERE f.is_primary) AS main_folios
FROM reservations r LEFT JOIN folios f ON f.reservation_id = r.id
WHERE r.status IN ('CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
GROUP BY r.id, r.reference, r.status
HAVING count(f.id) FILTER (WHERE f.is_primary) <> 1;

-- 4. The bills: each folio's charges, reversals and balance (payments arrive in Sprint 10, GST in Sprint 9).
--    (demo) R00008's main folio has its room nights, the airport pick-up (₹1,200) and dinner (₹1,450).
SELECT r.reference, f.number, f.name, f.is_primary,
       coalesce(sum(i.total) FILTER (WHERE i.item_type = 'CHARGE'), 0)   AS charges,
       coalesce(sum(i.total) FILTER (WHERE i.item_type = 'REVERSAL'), 0) AS reversals,
       coalesce(sum(i.total), 0)                                          AS balance
FROM folios f JOIN reservations r ON r.id = f.reservation_id
LEFT JOIN folio_items i ON i.folio_id = f.id
GROUP BY r.reference, f.id, f.number, f.name, f.is_primary
ORDER BY f.number;

-- 5. Every line of one bill in order, with who posted it. Change R00008 to any reference.
SELECT f.number, i.service_date, i.item_type, i.category, i.description, i.quantity, i.unit_price, i.total, i.source,
       i.reason, u.full_name AS posted_by, i.posted_at
FROM folio_items i JOIN folios f ON f.id = i.folio_id JOIN reservations r ON r.id = i.reservation_id
LEFT JOIN users u ON u.id = i.posted_by
WHERE r.reference = 'R00008'
ORDER BY i.posted_at, i.id;

-- 6. Reversals cancel their charge exactly and carry a reason. Expect NO rows.
SELECT c.description, c.total AS charge, x.total AS reversal, x.reason
FROM folio_items x JOIN folio_items c ON c.id = x.reverses_item_id
WHERE x.total <> -c.total OR x.quantity <> -c.quantity OR x.folio_id <> c.folio_id OR coalesce(trim(x.reason), '') = '';

-- 7. A room night is charged at most once per stay and night (moved lines don't count twice). Expect NO rows.
SELECT r.reference, i.service_date, count(*) AS live_room_nights
FROM folio_items i JOIN reservations r ON r.id = i.reservation_id
WHERE i.source = 'ROOM_NIGHT' AND i.item_type = 'CHARGE'
  AND NOT EXISTS (SELECT 1 FROM folio_items x WHERE x.reverses_item_id = i.id)
GROUP BY r.reference, i.service_date
HAVING count(*) > 1;

-- 8. Lines moved between folios of the same stay: a reversal on the old folio and a copy on the new one.
--    (demo) after the review: Kayaking moved to "Paid by Arjun (friend)" (the browser test's own guest).
SELECT r.reference, src.number AS from_folio, dst.number AS to_folio, c.description, c.total
FROM folio_items c
JOIN folio_items o ON o.id = c.transferred_from_item_id
JOIN folios src ON src.id = o.folio_id
JOIN folios dst ON dst.id = c.folio_id
JOIN reservations r ON r.id = c.reservation_id
ORDER BY c.posted_at DESC;

-- 9. Bill lines can't be changed or deleted. Expect both statements to FAIL with "Rows in folio_items cannot be changed" /
--    "cannot be deleted" (run them on their own, inside BEGIN … ROLLBACK; they are commented out so this file stays read-only).
-- UPDATE folio_items SET description = description WHERE id = (SELECT id FROM folio_items LIMIT 1);
-- DELETE FROM folio_items WHERE id = (SELECT id FROM folio_items LIMIT 1);

-- 10. The setting that decides when room nights are posted (no row = AT_CHECK_IN, the default), and the demo-only
--     setting that lets a guest with a balance check out until payments arrive (Sprint 10).
SELECT p.code, s.key, s.value
FROM property_settings s JOIN properties p ON p.id = s.property_id
WHERE s.key IN ('folio.room_charge_posting', 'frontdesk.allow_checkout_with_balance');
