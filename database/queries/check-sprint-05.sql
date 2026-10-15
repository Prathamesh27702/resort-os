-- Database checks for Sprint 05 (availability and reservations). Read-only: every statement is a SELECT.
-- How to run: docs/testing/VERIFYING.md → "Database checks" (VS Code PostgreSQL extension, or one docker command).
-- Each block says what you should see. Numbers marked (demo) assume a freshly reset database plus the review script.

-- 1. Migration V007 applied. Expect V001 … V007.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. Reservations, newest arrival last. (demo) R00001–R00006 from the dev seed: R00001 Room 101 tonight (CONFIRMED),
--    R00004 two dormitory beds, R00005 an INQUIRY, R00006 CANCELLED; plus any you booked in the review.
SELECT p.code AS property, r.reference, r.status, r.check_in, r.check_out, r.check_out - r.check_in AS nights,
       r.adults, r.children, r.total_amount, s.name AS source, g.first_name || ' ' || coalesce(g.last_name, '') AS guest
FROM reservations r
JOIN properties p ON p.id = r.property_id
JOIN reservation_sources s ON s.id = r.source_id
JOIN guests g ON g.id = r.primary_guest_id
ORDER BY p.code, r.check_in, r.reference;

-- 3. What each booking holds now (current rows). (demo) R00005 and R00006 hold nothing (holds_inventory = false).
SELECT r.reference, r.status, u.code AS room, b.code AS bed, ru.start_date, ru.end_date, ru.adults, ru.children, ru.holds_inventory
FROM reservation_units ru
JOIN reservations r ON r.id = ru.reservation_id
JOIN accommodation_units u ON u.id = ru.unit_id
LEFT JOIN beds b ON b.id = ru.bed_id
WHERE ru.superseded_at IS NULL
ORDER BY r.reference, u.code, b.code;

-- 4. THE double-booking check: any room or bed held twice for the same night. Expect NO rows (the EXCLUDE
--    constraints make this impossible; this query proves it independently).
SELECT a.unit_id, a.bed_id, a.stay * b.stay AS overlap, ra.reference AS first, rb.reference AS second
FROM reservation_units a
JOIN reservation_units b ON b.unit_id = a.unit_id AND b.id > a.id AND a.stay && b.stay
                        AND (a.bed_id IS NULL OR b.bed_id IS NULL OR a.bed_id = b.bed_id)
JOIN reservations ra ON ra.id = a.reservation_id
JOIN reservations rb ON rb.id = b.reservation_id
WHERE a.holds_inventory AND b.holds_inventory;

-- 5. Held rows on blocked rooms. Expect NO rows.
SELECT r.reference, u.code, k.reason, k.start_date, k.end_date
FROM reservation_units ru
JOIN reservations r ON r.id = ru.reservation_id
JOIN accommodation_units u ON u.id = ru.unit_id
JOIN unit_blocks k ON k.unit_id = ru.unit_id AND k.status = 'ACTIVE' AND k.period && ru.stay
                  AND (k.bed_id IS NULL OR k.bed_id = ru.bed_id)
WHERE ru.holds_inventory;

-- 6. Status matches what is held: holding statuses hold their current rows, others hold nothing. Expect NO rows.
SELECT r.reference, r.status, ru.holds_inventory
FROM reservations r JOIN reservation_units ru ON ru.reservation_id = r.id AND ru.superseded_at IS NULL
WHERE ru.holds_inventory <> (r.status IN ('PENDING', 'CONFIRMED', 'CHECKED_IN'));

-- 7. Totals add up: each booking's total = the sum of its current nightly breakdown. Expect NO rows.
SELECT r.reference, r.total_amount, sum(x.total) AS nights
FROM reservations r
JOIN reservation_units ru ON ru.reservation_id = r.id AND ru.superseded_at IS NULL
JOIN reservation_rate_breakdowns x ON x.reservation_unit_id = ru.id
GROUP BY r.id, r.reference, r.total_amount
HAVING sum(x.total) <> r.total_amount;

-- 8. Every booking's status history, oldest first. (demo) R00006: NULL → CONFIRMED, then CONFIRMED → CANCELLED
--    "Guest changed plans".
SELECT r.reference, h.from_status, h.to_status, h.reason, h.changed_at
FROM reservation_status_history h JOIN reservations r ON r.id = h.reservation_id
ORDER BY r.reference, h.changed_at;

-- 9. The allowed status graph (NULL = a status a booking can start in). Expect 14 rows.
SELECT coalesce(from_status, '(new)') AS from_status, to_status FROM reservation_status_transitions ORDER BY 1, 2;

-- 10. Free rooms per type tonight. (demo) Deluxe room 1 of 2 (Room 101 is R00001), Dormitory bed 8 of 10.
SELECT t.name, count(*) FILTER (WHERE held.unit_id IS NULL) AS free, count(*) AS total
FROM accommodation_types t
JOIN properties p ON p.id = t.property_id
JOIN LATERAL (
    SELECT u.id AS unit_id, NULL::uuid AS bed_id FROM accommodation_units u WHERE u.accommodation_type_id = t.id AND u.is_active AND t.inventory_mode = 'UNIT'
    UNION ALL
    SELECT b.unit_id, b.id FROM beds b JOIN accommodation_units u ON u.id = b.unit_id
     WHERE u.accommodation_type_id = t.id AND u.is_active AND b.is_active AND t.inventory_mode = 'BED'
) inv ON true
LEFT JOIN LATERAL (
    SELECT ru.unit_id FROM reservation_units ru
     WHERE ru.unit_id = inv.unit_id AND ru.holds_inventory AND (inv.bed_id IS NULL OR ru.bed_id = inv.bed_id)
       AND ru.stay @> (now() AT TIME ZONE p.timezone)::date
     LIMIT 1
) held ON true
WHERE t.is_active
GROUP BY t.name, t.sort_order ORDER BY t.sort_order, t.name;

-- 11. Idempotency keys: one per create request; completed ones carry the stored response's reservation.
SELECT k.key, k.status_code, r.reference, k.created_at, k.completed_at
FROM idempotency_keys k LEFT JOIN reservations r ON r.id = k.resource_id
ORDER BY k.created_at DESC LIMIT 20;

-- 12. Reservation changes in the audit trail.
SELECT a.occurred_at, a.action, a.entity_type, a.entity_id, a.reason
FROM audit_logs a WHERE a.entity_type LIKE 'Reservation%'
ORDER BY a.occurred_at DESC LIMIT 30;
