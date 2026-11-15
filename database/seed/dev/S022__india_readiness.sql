-- =====================================================================
-- DEV SEED S022 — India readiness (Sprint 30)
--   The demo property gets a food licence number and a UPI ID, so restaurant bills print "FSSAI Lic. No." and a UPI QR
--   from the first run. Both are made up: the licence number belongs to nobody and the UPI ID can't receive money.
--   Replace them on Settings → Properties before a real guest sees a bill.
--   Form C sheets are not seeded: the app opens one for every foreign guest in house the first time the Form C page is
--   opened, and at each check-in after that.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property already has either value).
-- =====================================================================
DO $$
DECLARE
    p_id uuid;
BEGIN
    SELECT p.id INTO p_id
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S022: bootstrap property not found, skipped';
        RETURN;
    END IF;
    UPDATE properties
       SET fssai_licence_no = '10020099000123', upi_id = 'resort.demo@exampleupi'
     WHERE id = p_id AND fssai_licence_no IS NULL AND upi_id IS NULL;
    RAISE NOTICE 'dev seed S022: demo FSSAI licence number and UPI ID set';
END $$;
