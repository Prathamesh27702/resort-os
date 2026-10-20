-- =====================================================================
-- DEV SEED S008 — GST for the demo property (Sprint 9)
--   * the demo property is registered for GST in Goa from 1 Jul 2017: GSTIN 30AABCT1234C1Z9 (a made-up number with a valid
--     check digit; not a real business), so every charge posted from now on carries GST
--   * the tax categories and rules themselves are not demo data: migration V011 seeds them for every organisation
--   * the demo charges of S007 were posted before the tax engine existed and keep no GST — a posted line never changes
-- Runs ONLY in Development (DevSeed:Enabled). Idempotent: skipped when the property already has a registration.
-- =====================================================================
DO $$
DECLARE
    p_id    uuid;
    o_id    uuid;
BEGIN
    SELECT p.id, p.organization_id INTO p_id, o_id
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S008: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM gst_registrations WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S008: the demo property already has a GST registration, skipped';
        RETURN;
    END IF;

    UPDATE properties SET gst_state_code = '30' WHERE id = p_id AND gst_state_code IS NULL;
    INSERT INTO gst_registrations (organization_id, property_id, gstin, legal_name, trade_name, state_code, registration_type, effective_from)
    SELECT o_id, p_id, '30AABCT1234C1Z9', coalesce(p.legal_name, o.name), p.name, '30', 'REGULAR', DATE '2017-07-01'
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE p.id = p_id AND coalesce(p.gst_state_code, '30') = '30';

    RAISE NOTICE 'dev seed S008: demo property registered for GST (30AABCT1234C1Z9, Goa)';
END $$;
