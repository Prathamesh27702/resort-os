-- =====================================================================
-- DEV SEED S005 — something to see on the reservation calendar (Sprint 6)
--   Room 106 is blocked for maintenance from tomorrow for 2 nights ("Plumbing repair"), so the review demo
--   "drag a booking onto a blocked room is refused" works: drag R00002 (Room 105, from tomorrow) onto Room 106.
--   R00001 (Room 101, Deluxe, tonight) can be dragged to Room 102 for the "move a guest" demo.
-- Room 106 only has an enquiry and a cancelled booking (S004), which hold nothing, so the block is allowed.
-- Room 103 and dorm beds B01–B04 are left alone: the Sprint 02 review blocks them.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the block exists).
-- =====================================================================
DO $$
DECLARE
    p_id  uuid;
    o_id  uuid;
    today date;
    u_id  uuid;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S005: bootstrap property not found, skipped';
        RETURN;
    END IF;
    SELECT id INTO u_id FROM accommodation_units WHERE property_id = p_id AND code = '106';
    IF u_id IS NULL THEN
        RAISE NOTICE 'dev seed S005: Room 106 (S001) not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM unit_blocks WHERE unit_id = u_id AND reason = 'Plumbing repair') THEN
        RAISE NOTICE 'dev seed S005: calendar demo block already there, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM reservation_units WHERE unit_id = u_id AND holds_inventory
                AND stay && daterange(today + 1, today + 3, '[)')) THEN
        RAISE NOTICE 'dev seed S005: Room 106 is booked from tomorrow, block skipped';
        RETURN;
    END IF;

    INSERT INTO unit_blocks (organization_id, property_id, unit_id, block_type, start_date, end_date, reason)
    VALUES (o_id, p_id, u_id, 'MAINTENANCE', today + 1, today + 3, 'Plumbing repair');

    RAISE NOTICE 'dev seed S005: Room 106 blocked for maintenance from tomorrow, 2 nights';
END $$;
