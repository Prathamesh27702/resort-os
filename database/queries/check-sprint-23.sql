-- Database checks for Sprint 23 (reports and dashboards). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 23. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database (dev seed S020) and that the reports were added up (the job does
-- it within a minute of the API starting, or press Refresh now on Reports).

-- 1. Migration V025 applied. Expect V001 … V025.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The reporting schema: 4 tables and 5 views. Expect 9 rows.
SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = 'reporting' ORDER BY table_type, table_name;

-- 3. When the numbers were last added up, which days, how long it took. (demo) a time in the last 15 minutes, from 1 April
--    of last financial year to 90 days ahead, a few hundred milliseconds.
SELECT p.code, s.refreshed_at, s.refreshed_from, s.refreshed_to, s.duration_ms, s.full_refreshed_at
FROM reporting.refresh_state s JOIN properties p ON p.id = s.property_id;

-- 4. The last 12 months: occupancy, ADR and RevPAR. (demo) about 40–70 % a month, busier from October to May.
SELECT month, units_available, units_sold, occupancy_percent, room_revenue, adr, revpar, total_revenue
FROM reporting.v_monthly WHERE month >= date_trunc('month', current_date) - interval '11 months' ORDER BY month;

-- 5. ADR and RevPAR worked out by hand from the raw tables for last month agree with the summary. Expect the two rows equal.
WITH m AS (SELECT date_trunc('month', current_date - interval '1 month')::date AS s,
                  (date_trunc('month', current_date) - interval '1 day')::date AS e),
raw AS (
  SELECT (SELECT sum(fi.amount) FROM folio_items fi, m WHERE fi.category = 'ROOM' AND fi.service_date BETWEEN m.s AND m.e) AS room_revenue,
         (SELECT count(*) FROM m, generate_series(m.s, m.e, interval '1 day') d
            JOIN reservation_units ru ON ru.superseded_at IS NULL AND ru.start_date <= d::date AND d::date < ru.end_date
            JOIN reservations r ON r.id = ru.reservation_id AND r.status IN ('CONFIRMED','CHECKED_IN','CHECKED_OUT')) AS sold)
SELECT 'by hand' AS how, room_revenue, sold, round(room_revenue / nullif(sold, 0), 2) AS adr FROM raw
UNION ALL
SELECT 'summary', room_revenue, units_sold, adr FROM reporting.v_monthly, m WHERE month = m.s;

-- 6. Every day adds up: total revenue is the sum of its kinds, nothing sold above what is available. Expect NO rows.
SELECT business_date FROM reporting.daily_stats
WHERE total_revenue <> room_revenue + fnb_revenue + activity_revenue + transport_revenue + other_revenue
   OR rooms_sold > rooms_available + out_of_order OR beds_sold > beds_available + out_of_order;

-- 7. Booking sources last month with the commission worked out. (demo) Booking.com at 18 %, MakeMyTrip and Agoda at 18 %,
--    the travel agent at 10 %, direct sources at 0.
SELECT * FROM reporting.v_channels_monthly
WHERE month = date_trunc('month', current_date - interval '1 month')::date ORDER BY room_revenue DESC;

-- 8. GST by month (an estimate for the accountant). (demo) output small (the history has no invoices), "charged on the bills"
--    the GST of the room nights.
SELECT * FROM reporting.v_gst_monthly ORDER BY month DESC LIMIT 6;

-- 9. Pay and supplier bills by department: departments map to budget departments. (demo) FRONT → FO, HK → HK,
--    KITCHEN and REST → FB, MAINT → MNT.
SELECT d.code, d.name, c.code AS budget_department FROM departments d LEFT JOIN cost_centres c ON c.id = d.cost_centre_id ORDER BY d.code;
SELECT * FROM reporting.v_costs_monthly WHERE month = date_trunc('month', current_date)::date ORDER BY 1, 2;

-- 10. The Power BI role can read the views and nothing else. Expect: USAGE on reporting, SELECT on the 5 views only.
SELECT grantee, table_schema, table_name, privilege_type FROM information_schema.role_table_grants
WHERE grantee = 'resort_reporting' ORDER BY table_name;
