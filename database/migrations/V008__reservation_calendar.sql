-- =====================================================================
-- V008  Reservation calendar (Sprint 6)
--
-- The calendar reads every current reservation row that overlaps a window of up to 62 days, whatever it holds
-- (checked-out stays still show), so it needs an index on current rows, not only on held ones (ix_reservation_units_held).
-- Moving a booking to another room needs no new table: the old reservation_units row is superseded and a new one
-- inserted (V007), the audit trail records who and why, and a note on the booking says it in words.
-- =====================================================================

CREATE INDEX ix_reservation_units_calendar ON reservation_units (property_id, start_date, end_date)
    WHERE superseded_at IS NULL;
