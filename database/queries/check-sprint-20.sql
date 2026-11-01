-- Database checks for Sprint 20 (activities and equipment). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 20. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 20).

-- 1. Migration V022 applied. Expect V001 … V022.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 6 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('activities', 'activity_schedules', 'activity_bookings', 'equipment_types', 'equipment', 'equipment_assignments')
ORDER BY table_name;

-- 3. The guards are there. Expect 4 rows: seats kept by the bookings, the equipment status moves, the seats CHECK and
--    one open assignment per item.
SELECT tgname AS name, tgrelid::regclass AS on_table FROM pg_trigger
WHERE tgname IN ('trg_activity_bookings_seats', 'trg_equipment_status_transition')
UNION ALL
SELECT conname, conrelid::regclass FROM pg_constraint WHERE conname = 'ck_activity_schedules_capacity'
UNION ALL
SELECT indexname, tablename::regclass FROM pg_indexes WHERE indexname = 'ux_equipment_assignments_open'
ORDER BY 1;

-- 4. Activities. (demo) KAYAK 90 min 6 seats ₹800 with a life jacket each, YOGA free, SNORKEL ₹1,500 with a snorkel set,
--    CYCLE ₹500 with a bicycle.
SELECT a.code, a.name, a.duration_minutes, a.capacity, a.price, t.name AS needs, a.weather_dependent, a.is_active
FROM activities a LEFT JOIN equipment_types t ON t.id = a.equipment_type_id
ORDER BY a.sort_order, a.code;

-- 5. Seats booked on every session equal the seats of its bookings, and never go past the seats. Expect NO rows.
SELECT s.id, s.session_date, s.starts_at, s.capacity, s.seats_booked, coalesce(sum(b.seats) FILTER (WHERE b.status = 'BOOKED'), 0) AS counted
FROM activity_schedules s LEFT JOIN activity_bookings b ON b.schedule_id = s.id
GROUP BY s.id
HAVING s.seats_booked <> coalesce(sum(b.seats) FILTER (WHERE b.status = 'BOOKED'), 0) OR s.seats_booked > s.capacity;

-- 6. Tomorrow's sessions with the seats left. (demo) KAYAK 07:00 4/6 (the agent group), YOGA 06:30 2/20.
SELECT a.code, to_char(s.starts_at AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS starts, s.status, s.seats_booked, s.capacity,
       s.capacity - s.seats_booked AS seats_left, s.cancel_reason
FROM activity_schedules s JOIN activities a ON a.id = s.activity_id
WHERE s.session_date = (now() AT TIME ZONE 'Asia/Kolkata')::date + 1
ORDER BY s.starts_at, a.code;

-- 7. Every booking put on a bill has exactly its charge there, and a refunded one has its reversal. Expect NO rows.
SELECT b.code, b.status, b.refund, b.amount, i.amount AS charged, r.amount AS reversed
FROM activity_bookings b
LEFT JOIN folio_items i ON i.id = b.folio_item_id
LEFT JOIN folio_items r ON r.id = b.reversal_item_id
WHERE b.settlement = 'ROOM_CHARGE'
  AND (i.id IS NULL OR i.amount <> b.amount OR i.source <> 'ACTIVITY'
       OR (b.refund = 'REVERSED' AND (r.id IS NULL OR r.amount <> -b.amount)));

-- 8. A booking moved by the weather points to its copy, and the copy keeps the charge (nothing refunded). Expect NO rows.
SELECT b.code, m.code AS moved_to, b.folio_item_id, m.folio_item_id
FROM activity_bookings b JOIN activity_bookings m ON m.id = b.moved_to_booking_id
WHERE b.refund <> 'MOVED' OR m.moved_from_booking_id <> b.id OR m.folio_item_id IS DISTINCT FROM b.folio_item_id;

-- 9. Bookings with how they were paid and what happened to them. (demo) AB00001 complimentary agent group of 4,
--    AB00002–AB00003 free yoga; after the review, one weather refund and one move.
SELECT b.code, a.code AS activity, b.guest_name, b.seats, b.settlement, b.amount, b.status, b.cancel_kind, b.refund,
       b.cancel_reason
FROM activity_bookings b JOIN activities a ON a.id = b.activity_id
ORDER BY b.code;

-- 10. Equipment by kind and status. (demo) LJ 12 available, KY 5 + 1 in maintenance, SN 7 + 1 damaged, BC 7 + 1 lost;
--     after the review, one life jacket damaged then in maintenance.
SELECT t.code, e.status, count(*) FROM equipment e JOIN equipment_types t ON t.id = e.equipment_type_id
GROUP BY t.code, t.sort_order, e.status ORDER BY t.sort_order, t.code, e.status;

-- 11. An item's status agrees with its open assignment: RESERVED or ISSUED exactly when one is open. Expect NO rows.
SELECT e.tag, e.status, a.status AS assignment
FROM equipment e LEFT JOIN equipment_assignments a ON a.equipment_id = e.id AND a.status IN ('RESERVED', 'ISSUED')
WHERE (e.status IN ('RESERVED', 'ISSUED')) <> (a.id IS NOT NULL) OR e.status <> coalesce(a.status, e.status);

-- 12. Equipment that came back damaged or was lost says why. Expect NO rows (a CHECK refuses it anyway).
SELECT e.tag, a.status, a.return_condition, a.return_note
FROM equipment_assignments a JOIN equipment e ON e.id = a.equipment_id
WHERE a.return_condition IN ('DAMAGED', 'LOST') AND a.return_note IS NULL;

-- 13. Out now, overdue first.
SELECT e.tag, a.issued_to, a.status, a.issued_at, a.due_back_at, a.due_back_at < now() AS overdue
FROM equipment_assignments a JOIN equipment e ON e.id = a.equipment_id
WHERE a.status IN ('RESERVED', 'ISSUED')
ORDER BY (a.due_back_at < now()) DESC, a.due_back_at;
