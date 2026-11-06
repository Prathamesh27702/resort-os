-- A read-only login for Power BI or Excel (Sprint 23). Run it once, as the database admin (the postgres user), on the
-- Resort OS database:
--
--   psql -U postgres -d resort_dev -v pbi_password=choose-a-long-password -f scripts/powerbi-user.sql
--
-- The password is given on the command line, never written in this file. The login can read the reporting views
-- (reporting.v_daily, v_monthly, v_channels_monthly, v_costs_monthly, v_gst_monthly) and nothing else: not the bookings,
-- the guests, the bills or anyone's pay. How to connect Power BI: docs/REPORTING.md.
\set ON_ERROR_STOP on

-- The group role the views are granted to (migration V025 makes it when the migrating login may create roles).
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'resort_reporting') THEN
        CREATE ROLE resort_reporting NOLOGIN;
    END IF;
END $$;
GRANT USAGE ON SCHEMA reporting TO resort_reporting;
GRANT SELECT ON reporting.v_daily, reporting.v_monthly, reporting.v_channels_monthly, reporting.v_costs_monthly,
                reporting.v_gst_monthly TO resort_reporting;

-- The login Power BI signs in with: a member of resort_reporting, read-only, a few connections at most.
SELECT format('CREATE ROLE resort_powerbi LOGIN PASSWORD %L CONNECTION LIMIT 5 IN ROLE resort_reporting', :'pbi_password')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'resort_powerbi') \gexec
SELECT format('ALTER ROLE resort_powerbi PASSWORD %L', :'pbi_password') \gexec
ALTER ROLE resort_powerbi SET default_transaction_read_only = on;
ALTER ROLE resort_powerbi SET search_path = reporting;
ALTER ROLE resort_powerbi SET statement_timeout = '60s';
GRANT CONNECT ON DATABASE :"DBNAME" TO resort_powerbi;
