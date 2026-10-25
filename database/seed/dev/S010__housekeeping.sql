-- =====================================================================
-- DEV SEED S010 — housekeeping and maintenance (Sprint 13)
--   The "Departure clean" checklist for the bootstrap property (V015 gives one to properties that existed before it;
--   the bootstrap property is created after the migrations run on a fresh database).
--   A cleaning task for every room that isn't ready and has none (Room 101, made dirty by S006 before tasks existed).
--   Equipment: the AC in Room 101, the geyser in Room 106, the pool pump.
--   Two open tickets: M00001 the pool pump (HIGH), M00002 a dripping tap in Room 104 (in house, so not out of order).
-- Room 103 and dorm bed B01 are left alone (earlier sprints' review scripts block them).
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when M00001 exists).
-- =====================================================================
DO $$
DECLARE
    p_id    uuid;
    o_id    uuid;
    today   date;
    tpl_id  uuid;
    pump_id uuid;
    t_id    uuid;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S010: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM maintenance_tickets WHERE property_id = p_id AND number = 'M00001') THEN
        RAISE NOTICE 'dev seed S010: housekeeping demo already there, skipped';
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM accommodation_units WHERE property_id = p_id AND code = '101') THEN
        RAISE NOTICE 'dev seed S010: demo rooms (S001) not found, skipped';
        RETURN;
    END IF;

    -- The departure checklist.
    SELECT id INTO tpl_id FROM housekeeping_checklist_templates
     WHERE property_id = p_id AND task_type = 'DEPARTURE' AND accommodation_type_id IS NULL AND is_active;
    IF tpl_id IS NULL THEN
        INSERT INTO housekeeping_checklist_templates (organization_id, property_id, name, task_type)
        VALUES (o_id, p_id, 'Departure clean', 'DEPARTURE') RETURNING id INTO tpl_id;
        INSERT INTO housekeeping_checklist_items (organization_id, template_id, label, is_required, sort_order)
        SELECT o_id, tpl_id, x.label, x.req, x.ord FROM (VALUES
            ('Strip beds and put on fresh linen', false, 1),
            ('Clean and disinfect the bathroom', false, 2),
            ('Fresh towels and toiletries', false, 3),
            ('Dust surfaces, sweep and mop the floor', false, 4),
            ('Empty bins', false, 5),
            ('Check AC, lights, TV and taps work', false, 6),
            ('Restock water bottles', false, 7)) AS x(label, req, ord);
    END IF;

    -- A task for each room that needs cleaning and has none.
    INSERT INTO housekeeping_tasks (organization_id, property_id, unit_id, bed_id, task_type, status, source, template_id,
                                    checklist, business_date, note)
    SELECT s.organization_id, s.property_id, s.unit_id, s.bed_id, 'DEPARTURE', 'OPEN', 'MANUAL', tpl_id,
           (SELECT jsonb_agg(jsonb_build_object('id', i.id, 'label', i.label, 'required', i.is_required, 'done', false) ORDER BY i.sort_order)
              FROM housekeeping_checklist_items i WHERE i.template_id = tpl_id),
           today, s.reason
      FROM unit_housekeeping_status s
     WHERE s.property_id = p_id AND s.status = 'DIRTY'
       AND NOT EXISTS (SELECT 1 FROM housekeeping_tasks t WHERE t.unit_id = s.unit_id AND t.bed_id IS NOT DISTINCT FROM s.bed_id
                                                           AND t.status IN ('OPEN','IN_PROGRESS','CLEANED'));
    UPDATE unit_housekeeping_history SET consumed_at = now() WHERE property_id = p_id AND consumed_at IS NULL AND to_status = 'DIRTY';

    -- Equipment.
    INSERT INTO maintenance_assets (organization_id, property_id, code, name, category, unit_id, location) VALUES
        (o_id, p_id, 'AC-101', 'Split AC, Room 101', 'AC', (SELECT id FROM accommodation_units WHERE property_id = p_id AND code = '101'), NULL),
        (o_id, p_id, 'GEYSER-106', 'Geyser, Room 106', 'APPLIANCE', (SELECT id FROM accommodation_units WHERE property_id = p_id AND code = '106'), NULL),
        (o_id, p_id, 'POOL-PUMP', 'Pool pump', 'POOL', NULL, 'Pool deck')
    ON CONFLICT (property_id, code) DO NOTHING;
    SELECT id INTO pump_id FROM maintenance_assets WHERE property_id = p_id AND code = 'POOL-PUMP';

    -- Two open tickets.
    INSERT INTO maintenance_tickets (organization_id, property_id, number, title, description, category, priority, asset_id, reported_by)
    VALUES (o_id, p_id, 'M00001', 'Pool pump making a grinding noise', 'Started this morning; the water is still clear.', 'POOL', 'HIGH',
            pump_id, (SELECT id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1))
    RETURNING id INTO t_id;
    INSERT INTO maintenance_comments (organization_id, ticket_id, kind, body, to_status, created_by)
    VALUES (o_id, t_id, 'STATUS', 'Reported', 'OPEN', (SELECT id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1));

    INSERT INTO maintenance_tickets (organization_id, property_id, number, title, category, priority, unit_id, reported_by)
    VALUES (o_id, p_id, 'M00002', 'Bathroom tap dripping', 'PLUMBING', 'NORMAL',
            (SELECT id FROM accommodation_units WHERE property_id = p_id AND code = '104'),
            (SELECT id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1))
    RETURNING id INTO t_id;
    INSERT INTO maintenance_comments (organization_id, ticket_id, kind, body, to_status, created_by)
    VALUES (o_id, t_id, 'STATUS', 'Reported', 'OPEN', (SELECT id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1));

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'maintenance_ticket', 2)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = greatest(property_counters.last_value, 2);

    RAISE NOTICE 'dev seed S010: departure checklist, cleaning tasks for dirty rooms, 3 pieces of equipment, tickets M00001 and M00002';
END $$;
