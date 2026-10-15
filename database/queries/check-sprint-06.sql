-- Database checks for Sprint 06 (reservation calendar and room moves). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 06. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 06).

-- 1. Migration V008 applied. Expect V001 … V008.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The calendar index exists. Expect one row: ix_reservation_units_calendar … WHERE (superseded_at IS NULL).
SELECT indexname, indexdef FROM pg_indexes WHERE indexname = 'ix_reservation_units_calendar';

-- 3. The demo maintenance block (dev seed S005). (demo) Room 106, MAINTENANCE, from tomorrow for 2 nights, "Plumbing repair".
SELECT u.code AS room, k.block_type, k.start_date, k.end_date, k.reason, k.status
FROM unit_blocks k JOIN accommodation_units u ON u.id = k.unit_id
WHERE k.reason = 'Plumbing repair';

-- 4. Every room move: the old row (superseded, no longer holding) and the row that replaced it, with who and why.
--    (demo) after the review: R00001 moved from Room 101 to Room 102 with your reason.
SELECT r.reference, uo.code AS from_room, bo.code AS from_bed, un.code AS to_room, bn.code AS to_bed,
       o.start_date, o.end_date, o.superseded_at, a.reason, us.full_name AS moved_by
FROM reservation_units o
JOIN reservation_units n ON n.reservation_id = o.reservation_id AND n.superseded_at IS NULL
                        AND n.start_date = o.start_date AND n.end_date = o.end_date AND n.created_at >= o.superseded_at
JOIN reservations r ON r.id = o.reservation_id
JOIN accommodation_units uo ON uo.id = o.unit_id
JOIN accommodation_units un ON un.id = n.unit_id
LEFT JOIN beds bo ON bo.id = o.bed_id
LEFT JOIN beds bn ON bn.id = n.bed_id
LEFT JOIN audit_logs a ON a.entity_type = 'ReservationUnit' AND a.entity_id = n.id::text AND a.action = 'INSERT'
LEFT JOIN users us ON us.id = a.user_id
WHERE o.superseded_at IS NOT NULL AND (o.unit_id <> n.unit_id OR o.bed_id IS DISTINCT FROM n.bed_id)
ORDER BY o.superseded_at;

-- 5. A move keeps the price: for every booking, the current rows' nightly prices add up to the booking total.
--    Expect NO rows.
SELECT r.reference, r.total_amount, sum(b.total) AS nights_total
FROM reservations r
JOIN reservation_units ru ON ru.reservation_id = r.id AND ru.superseded_at IS NULL
JOIN reservation_rate_breakdowns b ON b.reservation_unit_id = ru.id
GROUP BY r.id, r.reference, r.total_amount
HAVING sum(b.total) <> r.total_amount;

-- 6. Still no double booking after moves (same check as Sprint 05, block 4). Expect NO rows.
SELECT a.unit_id, a.bed_id, a.stay * b.stay AS overlap, ra.reference AS first, rb.reference AS second
FROM reservation_units a
JOIN reservation_units b ON b.unit_id = a.unit_id AND b.id > a.id AND a.stay && b.stay
                        AND (a.bed_id IS NULL OR b.bed_id IS NULL OR a.bed_id = b.bed_id)
JOIN reservations ra ON ra.id = a.reservation_id
JOIN reservations rb ON rb.id = b.reservation_id
WHERE a.holds_inventory AND b.holds_inventory;

-- 7. What the calendar draws for the next 7 days (the same rows GET /calendar returns).
SELECT u.code AS room, bd.code AS bed, r.reference, r.status, ru.start_date, ru.end_date
FROM reservation_units ru
JOIN reservations r ON r.id = ru.reservation_id
JOIN accommodation_units u ON u.id = ru.unit_id
JOIN properties p ON p.id = ru.property_id
LEFT JOIN beds bd ON bd.id = ru.bed_id
WHERE ru.superseded_at IS NULL AND r.status IN ('PENDING', 'CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
  AND ru.start_date < (now() AT TIME ZONE p.timezone)::date + 7 AND (now() AT TIME ZONE p.timezone)::date < ru.end_date
ORDER BY u.sort_order, u.code, bd.code, ru.start_date;
