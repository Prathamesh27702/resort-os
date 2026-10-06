-- =====================================================================
-- V003  Audit context required by brief §58: device, reason, request id.
-- Sprint 1 (alignment). correlation_id already exists from V001.
-- =====================================================================
ALTER TABLE audit_logs
    ADD COLUMN request_id  text NULL,
    ADD COLUMN user_agent  text NULL,   -- "device" in §58
    ADD COLUMN reason      text NULL,   -- from X-Change-Reason; mandatory for some actions in later sprints
    ADD CONSTRAINT ck_audit_logs_reason_len CHECK (reason IS NULL OR length(reason) <= 500);

CREATE INDEX ix_audit_logs_correlation ON audit_logs (correlation_id) WHERE correlation_id IS NOT NULL;
