-- Database checks for Sprint 13 (housekeeping tasks, inspection, maintenance tickets). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 13. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 13).

-- 1. Migration V015 applied. Expect V001 … V015.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 7 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('housekeeping_checklist_templates', 'housekeeping_checklist_items', 'housekeeping_tasks', 'maintenance_assets',
                     'maintenance_tickets', 'maintenance_comments', 'maintenance_attachments')
ORDER BY table_name;

-- 3. Checklists: (demo) every property has a "Departure clean" with 7 lines (none required until a supervisor marks some).
SELECT p.code, t.name, t.task_type, coalesce(a.name, 'every room type') AS room_type, t.is_active,
       count(i.id) AS lines, count(i.id) FILTER (WHERE i.is_required) AS required
FROM housekeeping_checklist_templates t JOIN properties p ON p.id = t.property_id
LEFT JOIN accommodation_types a ON a.id = t.accommodation_type_id
LEFT JOIN housekeeping_checklist_items i ON i.template_id = t.id
GROUP BY p.code, t.id, t.name, t.task_type, a.name, t.is_active ORDER BY p.code, t.task_type, t.name;

-- 4. Today's tasks with where they came from. A check-out, room move or repair makes one by itself (source CHECK_OUT,
--    ROOM_MOVE, MAINTENANCE); MANUAL is a supervisor's. (demo) Room 101 DEPARTURE from the seed; after the review, the
--    room you checked out DONE with failed_inspections 1, then an AFTER_REPAIR task once the AC ticket is resolved.
SELECT u.code AS room, b.code AS bed, t.task_type, t.status, t.source, r.reference AS after_booking, t.failed_inspections,
       t.inspection_note, t.started_at, t.finished_at, t.inspected_at, t.closed_at, t.close_reason
FROM housekeeping_tasks t JOIN accommodation_units u ON u.id = t.unit_id LEFT JOIN beds b ON b.id = t.bed_id
LEFT JOIN reservations r ON r.id = t.reservation_id
ORDER BY t.created_at DESC LIMIT 30;

-- 5. One open task per room or bed at most (the database refuses a second). Expect NO rows.
SELECT unit_id, bed_id, count(*) FROM housekeeping_tasks WHERE status IN ('OPEN', 'IN_PROGRESS', 'CLEANED')
GROUP BY unit_id, bed_id HAVING count(*) > 1;

-- 6. One state change makes at most one task, even when three people check the same guest out at once. Expect NO rows.
SELECT source_change_id, count(*) FROM housekeeping_tasks WHERE source_change_id IS NOT NULL GROUP BY source_change_id HAVING count(*) > 1;

-- 7. Every room that isn't ready (and isn't out of order) has an open task, so nothing is forgotten. Expect NO rows.
SELECT u.code, s.bed_id, s.status FROM unit_housekeeping_status s JOIN accommodation_units u ON u.id = s.unit_id
WHERE s.status IN ('DIRTY', 'CLEANING', 'CLEAN', 'INSPECTION')
  AND NOT EXISTS (SELECT 1 FROM housekeeping_tasks t WHERE t.unit_id = s.unit_id AND t.bed_id IS NOT DISTINCT FROM s.bed_id
                  AND t.status IN ('OPEN', 'IN_PROGRESS', 'CLEANED'));

-- 8. Every DIRTY change has been turned into a task (consumed_at set). Expect NO rows.
SELECT u.code, h.bed_id, h.source, h.changed_at FROM unit_housekeeping_history h JOIN accommodation_units u ON u.id = h.unit_id
WHERE h.to_status = 'DIRTY' AND h.consumed_at IS NULL;

-- 9. Tickets: number, status, where, and the out-of-order block they hold. (demo) M00001 pool pump, M00002 tap in Room 104,
--    then the review's AC ticket, CLOSED, with its block released.
SELECT t.number, t.title, t.category, t.priority, t.status, u.code AS room, a.code AS asset, t.location, t.out_of_order,
       t.expected_ready_on, b.block_type, b.released_at, t.resolution, t.closed_at
FROM maintenance_tickets t LEFT JOIN accommodation_units u ON u.id = t.unit_id LEFT JOIN maintenance_assets a ON a.id = t.asset_id
LEFT JOIN unit_blocks b ON b.id = t.block_id ORDER BY t.number;

-- 10. An out-of-order room is blocked exactly while its ticket is unresolved. Expect NO rows.
SELECT t.number, t.status, b.released_at FROM maintenance_tickets t JOIN unit_blocks b ON b.id = t.block_id
WHERE (t.status IN ('OPEN', 'ASSIGNED', 'IN_PROGRESS', 'ON_HOLD') AND b.released_at IS NOT NULL)
   OR (t.status IN ('RESOLVED', 'CLOSED') AND b.released_at IS NULL);

-- 11. Ticket numbers are gap-free per property (M00001 …). Expect NO rows.
SELECT p.code, count(*) AS tickets, max(substring(t.number FROM 2)::int) AS highest
FROM maintenance_tickets t JOIN properties p ON p.id = t.property_id GROUP BY p.code
HAVING count(*) <> max(substring(t.number FROM 2)::int);

-- 12. Photos are stored encrypted, never in the database: a storage key and the key id that encrypted them, at most 2 MB.
SELECT t.number, f.file_name, f.content_type, f.size_bytes, f.key_id, f.storage_key LIKE 'maintenance/%' AS in_file_store
FROM maintenance_attachments f JOIN maintenance_tickets t ON t.id = f.ticket_id ORDER BY f.created_at;

-- 13. The timeline is append-only: every status change of a ticket is a STATUS line. (demo) the AC ticket's lines.
SELECT t.number, c.kind, c.from_status, c.to_status, left(c.body, 60) AS body, c.created_at
FROM maintenance_comments c JOIN maintenance_tickets t ON t.id = c.ticket_id ORDER BY t.number, c.created_at;

-- 14. Who did what: every task and ticket change is in the audit trail.
SELECT entity_type, action, count(*) FROM audit_logs
WHERE entity_type IN ('HousekeepingTask', 'MaintenanceTicket', 'MaintenanceAsset', 'HousekeepingChecklistTemplate')
GROUP BY entity_type, action ORDER BY entity_type, action;
