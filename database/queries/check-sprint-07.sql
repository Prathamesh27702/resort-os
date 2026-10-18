-- Database checks for Sprint 07 (front desk: check-in, check-out, walk-ins, room readiness). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 07. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 07).

-- 1. Migration V009 applied. Expect V001 … V009.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new columns and tables exist. Expect checked_in_at and checked_out_at, then the two housekeeping tables.
SELECT table_name, column_name FROM information_schema.columns
WHERE table_name = 'reservations' AND column_name IN ('checked_in_at', 'checked_out_at')
UNION ALL
SELECT table_name, NULL FROM information_schema.tables WHERE table_name IN ('unit_housekeeping_status', 'unit_housekeeping_history');

-- 3. Who is in house now, since when, and who checked them in.
--    (demo) R00008 (Room 104) from the seed until you check it out; after the review also R00001 (Room 102) and your walk-in.
SELECT r.reference, r.status, r.check_in, r.check_out, r.checked_in_at, us.full_name AS checked_in_by
FROM reservations r
LEFT JOIN reservation_status_history h ON h.reservation_id = r.id AND h.to_status = 'CHECKED_IN'
LEFT JOIN users us ON us.id = h.changed_by
WHERE r.status = 'CHECKED_IN'
ORDER BY r.checked_in_at;

-- 4. Every check-in and check-out has its time, and nobody left before they arrived. Expect NO rows.
SELECT reference, status, checked_in_at, checked_out_at FROM reservations
WHERE (status IN ('CHECKED_IN', 'CHECKED_OUT') AND checked_in_at IS NULL)
   OR (status = 'CHECKED_OUT' AND (checked_out_at IS NULL OR checked_out_at < checked_in_at));

-- 5. Rooms that aren't ready (no row = ready). (demo) after the review: Room 101 (dirty since its morning departure is
--    the seed's story; you checked R00001 into Room 102 instead) and Room 104 (R00008 left early) unless you marked them ready.
SELECT u.code AS room, b.code AS bed, s.status, s.reason, s.changed_at, us.full_name AS changed_by
FROM unit_housekeeping_status s
JOIN accommodation_units u ON u.id = s.unit_id
LEFT JOIN beds b ON b.id = s.bed_id
LEFT JOIN users us ON us.id = s.changed_by
WHERE s.status <> 'READY'
ORDER BY u.sort_order, u.code, b.code;

-- 6. The housekeeping events, newest first: CHECK_OUT and ROOM_MOVE come from the front desk, MANUAL from someone
--    marking a room. consumed_at stays empty until the housekeeping module (Sprint 13) picks them up.
--    (demo) R00007 → Room 105 DIRTY (CHECK_OUT), then Room 105 READY (MANUAL) when you marked it ready.
SELECT h.changed_at, u.code AS room, b.code AS bed, h.from_status, h.to_status, h.source, r.reference, h.reason, h.consumed_at
FROM unit_housekeeping_history h
JOIN accommodation_units u ON u.id = h.unit_id
LEFT JOIN beds b ON b.id = h.bed_id
LEFT JOIN reservations r ON r.id = h.reservation_id
ORDER BY h.changed_at DESC
LIMIT 20;

-- 7. Check-ins into a room that wasn't ready, with the reason (kept in the status history and the audit trail).
SELECT r.reference, h.changed_at, h.reason
FROM reservation_status_history h JOIN reservations r ON r.id = h.reservation_id
WHERE h.to_status = 'CHECKED_IN' AND h.reason IS NOT NULL
ORDER BY h.changed_at DESC;

-- 8. Early departures: the booked nights after the departure day were released and the total re-priced.
--    (demo) R00008: "Left early on … instead of …; … released (₹… less). Reason: …".
SELECT r.reference, r.check_out, r.total_amount, n.note
FROM reservation_notes n JOIN reservations r ON r.id = n.reservation_id
WHERE n.note LIKE 'Left early on %'
ORDER BY n.created_at DESC;

-- 9. Prices still add up after early departures and in-house moves (same as Sprint 06, block 5). Expect NO rows.
SELECT r.reference, r.total_amount, sum(b.total) AS nights_total
FROM reservations r
JOIN reservation_units ru ON ru.reservation_id = r.id AND ru.superseded_at IS NULL
JOIN reservation_rate_breakdowns b ON b.reservation_unit_id = ru.id
GROUP BY r.id, r.reference, r.total_amount
HAVING sum(b.total) <> r.total_amount;

-- 10. Walk-ins: the source is Walk-in and the booking was checked in within a minute of being made.
SELECT r.reference, r.check_in, r.check_out, r.created_at, r.checked_in_at
FROM reservations r JOIN reservation_sources s ON s.id = r.source_id
WHERE s.code = 'WALK_IN' AND r.checked_in_at BETWEEN r.created_at AND r.created_at + interval '1 minute'
ORDER BY r.created_at DESC;
