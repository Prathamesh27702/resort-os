-- =====================================================================
-- V015  Housekeeping and maintenance (Sprint 13, brief §33, §34, §53)
--
-- Model
--   unit_housekeeping_status (V009)   still the one place a room's (or dorm bed's) cleaning state lives:
--                                     DIRTY → CLEANING → CLEAN → INSPECTION → READY, and OUT_OF_ORDER.
--   housekeeping_tasks                a piece of cleaning work on one room or dorm bed: who does it, when it was started,
--                                     finished and inspected, and the checklist ticked. A check-out (or a room move, or a
--                                     repair finished) records a DIRTY change in unit_housekeeping_history; the same
--                                     transaction turns that change into a task and marks the change consumed. The task
--                                     points back to the change (source_change_id, UNIQUE), so one change can never make
--                                     two tasks, and a room has at most one open task (partial unique index).
--                                     Task status: OPEN (room DIRTY) → IN_PROGRESS (CLEANING) → CLEANED (CLEAN, or
--                                     INSPECTION while a supervisor checks it) → DONE (READY). CANCELLED when the room
--                                     goes out of order or is marked ready some other way.
--   housekeeping_checklist_templates  what to do in a room, per property and task type (and optionally per room type).
--   housekeeping_checklist_items      the lines of a template. A task copies them when it is created (checklist jsonb),
--                                     so editing a template never changes work already done.
--   maintenance_assets                things that break: an AC unit, a geyser, the pool pump. Optionally in a room.
--   maintenance_tickets               a reported problem, numbered M00001 … per property. OPEN → ASSIGNED → IN_PROGRESS
--                                     (or ON_HOLD) → RESOLVED → CLOSED. A ticket that takes a room out of order blocks it
--                                     (unit_blocks, type OUT_OF_ORDER) from the day it is reported until the expected
--                                     fix date, and sets the room OUT_OF_ORDER; resolving it releases the block and
--                                     sends the room to cleaning (a new housekeeping task).
--   maintenance_comments              the ticket's timeline: notes and every status change. Append-only.
--   maintenance_attachments           photos of the problem (compressed on the phone, at most 2 MB each, 6 per ticket),
--                                     stored encrypted through IFileStore; only the key is here. Append-only.
--
-- Settings read (property_settings, JSON booleans; defaults in code)
--   housekeeping.inspection_required  true: a finished room waits for a supervisor's inspection before it is READY.
-- =====================================================================

-- ---------------------------------------------------------------------
-- unit_housekeeping_history : two more sources
--   HOUSEKEEPING  a step on the housekeeping board (start, finish, inspect)
--   MAINTENANCE   a repair ticket took the room out of order or gave it back
-- ---------------------------------------------------------------------
ALTER TABLE unit_housekeeping_history DROP CONSTRAINT ck_unit_housekeeping_history_source;
ALTER TABLE unit_housekeeping_history ADD CONSTRAINT ck_unit_housekeeping_history_source
    CHECK (source IN ('CHECK_OUT','ROOM_MOVE','MANUAL','HOUSEKEEPING','MAINTENANCE'));

-- ---------------------------------------------------------------------
-- housekeeping_checklist_templates
-- ---------------------------------------------------------------------
CREATE TABLE housekeeping_checklist_templates (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid        NOT NULL REFERENCES organizations (id),
    property_id            uuid        NOT NULL REFERENCES properties (id),
    name                   text        NOT NULL,
    task_type              text        NOT NULL,
    accommodation_type_id  uuid        NULL,
    is_active              boolean     NOT NULL DEFAULT true,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid        NULL,
    updated_by             uuid        NULL,
    CONSTRAINT ck_hk_templates_name      CHECK (length(btrim(name)) BETWEEN 2 AND 80),
    CONSTRAINT ck_hk_templates_task_type CHECK (task_type IN ('DEPARTURE','STAYOVER','DEEP_CLEAN','AFTER_REPAIR')),
    CONSTRAINT fk_hk_templates_type      FOREIGN KEY (accommodation_type_id, property_id) REFERENCES accommodation_types (id, property_id),
    CONSTRAINT ux_hk_templates_id_prop   UNIQUE (id, property_id)
);
-- One active template per task type and room type (NULL room type = every other room type).
CREATE UNIQUE INDEX ux_hk_templates_active ON housekeeping_checklist_templates (property_id, task_type, accommodation_type_id)
    NULLS NOT DISTINCT WHERE is_active;

-- ---------------------------------------------------------------------
-- housekeeping_checklist_items
-- ---------------------------------------------------------------------
CREATE TABLE housekeeping_checklist_items (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    template_id      uuid        NOT NULL REFERENCES housekeeping_checklist_templates (id) ON DELETE CASCADE,
    label            text        NOT NULL,
    is_required      boolean     NOT NULL DEFAULT false,
    sort_order       integer     NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_hk_items_label CHECK (length(btrim(label)) BETWEEN 2 AND 120)
);
CREATE INDEX ix_hk_items_template ON housekeeping_checklist_items (template_id, sort_order);

-- ---------------------------------------------------------------------
-- housekeeping_tasks
-- ---------------------------------------------------------------------
CREATE TABLE housekeeping_tasks (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid        NOT NULL REFERENCES organizations (id),
    property_id            uuid        NOT NULL REFERENCES properties (id),
    unit_id                uuid        NOT NULL,
    bed_id                 uuid        NULL,
    task_type              text        NOT NULL,
    status                 text        NOT NULL DEFAULT 'OPEN',
    source                 text        NOT NULL,
    source_change_id       uuid        NULL REFERENCES unit_housekeeping_history (id),
    reservation_id         uuid        NULL,
    maintenance_ticket_id  uuid        NULL,
    template_id            uuid        NULL REFERENCES housekeeping_checklist_templates (id),
    checklist              jsonb       NOT NULL DEFAULT '[]',
    business_date          date        NOT NULL,
    note                   text        NULL,
    assigned_to            uuid        NULL REFERENCES users (id),
    assigned_at            timestamptz NULL,
    started_at             timestamptz NULL,
    started_by             uuid        NULL REFERENCES users (id),
    finished_at            timestamptz NULL,
    finished_by            uuid        NULL REFERENCES users (id),
    inspected_at           timestamptz NULL,
    inspected_by           uuid        NULL REFERENCES users (id),
    inspection_note        text        NULL,
    failed_inspections     integer     NOT NULL DEFAULT 0,
    closed_at              timestamptz NULL,
    close_reason           text        NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid        NULL,
    updated_by             uuid        NULL,
    CONSTRAINT ck_hk_tasks_type     CHECK (task_type IN ('DEPARTURE','STAYOVER','DEEP_CLEAN','AFTER_REPAIR')),
    CONSTRAINT ck_hk_tasks_status   CHECK (status IN ('OPEN','IN_PROGRESS','CLEANED','DONE','CANCELLED')),
    CONSTRAINT ck_hk_tasks_source   CHECK (source IN ('CHECK_OUT','ROOM_MOVE','MANUAL','MAINTENANCE')),
    CONSTRAINT ck_hk_tasks_checklist CHECK (jsonb_typeof(checklist) = 'array'),
    CONSTRAINT ck_hk_tasks_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    CONSTRAINT ck_hk_tasks_inspection_note CHECK (inspection_note IS NULL OR length(btrim(inspection_note)) BETWEEN 1 AND 500),
    CONSTRAINT ck_hk_tasks_closed   CHECK ((status IN ('DONE','CANCELLED')) = (closed_at IS NOT NULL)),
    CONSTRAINT ck_hk_tasks_cancel_reason CHECK (status <> 'CANCELLED' OR close_reason IS NOT NULL),
    CONSTRAINT fk_hk_tasks_unit         FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_hk_tasks_bed          FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    CONSTRAINT fk_hk_tasks_reservation  FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    -- One change of state makes at most one task, however often the request that caused it is retried.
    CONSTRAINT ux_hk_tasks_source_change UNIQUE (source_change_id)
);
-- A room (or dorm bed) has at most one open task.
CREATE UNIQUE INDEX ux_hk_tasks_open ON housekeeping_tasks (unit_id, bed_id) NULLS NOT DISTINCT
    WHERE status IN ('OPEN','IN_PROGRESS','CLEANED');
CREATE INDEX ix_hk_tasks_board ON housekeeping_tasks (property_id, status, business_date);
CREATE INDEX ix_hk_tasks_assignee ON housekeeping_tasks (assigned_to) WHERE status IN ('OPEN','IN_PROGRESS','CLEANED');

CREATE TRIGGER trg_hk_tasks_no_delete BEFORE DELETE ON housekeeping_tasks
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- maintenance_assets
-- ---------------------------------------------------------------------
CREATE TABLE maintenance_assets (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    category         text        NOT NULL,
    unit_id          uuid        NULL,
    location         text        NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_maint_assets_code     CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_maint_assets_name     CHECK (length(btrim(name)) BETWEEN 2 AND 80),
    CONSTRAINT ck_maint_assets_category CHECK (category IN ('AC','PLUMBING','ELECTRICAL','FURNITURE','APPLIANCE','POOL','CIVIL','OTHER')),
    CONSTRAINT ck_maint_assets_location CHECK (location IS NULL OR length(btrim(location)) BETWEEN 1 AND 120),
    CONSTRAINT fk_maint_assets_unit     FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT ux_maint_assets_code     UNIQUE (property_id, code),
    CONSTRAINT ux_maint_assets_id_prop  UNIQUE (id, property_id)
);
CREATE TRIGGER trg_maint_assets_no_delete BEFORE DELETE ON maintenance_assets
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- maintenance_tickets
-- ---------------------------------------------------------------------
CREATE TABLE maintenance_tickets (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    number             text        NOT NULL,
    title              text        NOT NULL,
    description        text        NULL,
    category           text        NOT NULL,
    priority           text        NOT NULL DEFAULT 'NORMAL',
    status             text        NOT NULL DEFAULT 'OPEN',
    unit_id            uuid        NULL,
    bed_id             uuid        NULL,
    asset_id           uuid        NULL,
    location           text        NULL,
    out_of_order       boolean     NOT NULL DEFAULT false,
    expected_ready_on  date        NULL,
    block_id           uuid        NULL REFERENCES unit_blocks (id),
    reported_by        uuid        NULL REFERENCES users (id),
    assigned_to        uuid        NULL REFERENCES users (id),
    assigned_at        timestamptz NULL,
    started_at         timestamptz NULL,
    resolved_at        timestamptz NULL,
    resolved_by        uuid        NULL REFERENCES users (id),
    resolution         text        NULL,
    closed_at          timestamptz NULL,
    closed_by          uuid        NULL REFERENCES users (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_maint_tickets_number   CHECK (number ~ '^M[0-9]{5,}$'),
    CONSTRAINT ck_maint_tickets_title    CHECK (length(btrim(title)) BETWEEN 3 AND 120),
    CONSTRAINT ck_maint_tickets_desc     CHECK (description IS NULL OR length(btrim(description)) BETWEEN 1 AND 2000),
    CONSTRAINT ck_maint_tickets_category CHECK (category IN ('AC','PLUMBING','ELECTRICAL','FURNITURE','APPLIANCE','POOL','CIVIL','OTHER')),
    CONSTRAINT ck_maint_tickets_priority CHECK (priority IN ('LOW','NORMAL','HIGH','URGENT')),
    CONSTRAINT ck_maint_tickets_status   CHECK (status IN ('OPEN','ASSIGNED','IN_PROGRESS','ON_HOLD','RESOLVED','CLOSED')),
    CONSTRAINT ck_maint_tickets_location CHECK (location IS NULL OR length(btrim(location)) BETWEEN 1 AND 120),
    CONSTRAINT ck_maint_tickets_where    CHECK (unit_id IS NOT NULL OR asset_id IS NOT NULL OR location IS NOT NULL),
    CONSTRAINT ck_maint_tickets_ooo      CHECK (NOT out_of_order OR (unit_id IS NOT NULL AND expected_ready_on IS NOT NULL)),
    CONSTRAINT ck_maint_tickets_bed      CHECK (bed_id IS NULL OR unit_id IS NOT NULL),
    CONSTRAINT ck_maint_tickets_resolved CHECK (status NOT IN ('RESOLVED','CLOSED') OR (resolved_at IS NOT NULL AND resolution IS NOT NULL)),
    CONSTRAINT ck_maint_tickets_closed   CHECK ((status = 'CLOSED') = (closed_at IS NOT NULL)),
    CONSTRAINT fk_maint_tickets_unit     FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_maint_tickets_bed      FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    CONSTRAINT fk_maint_tickets_asset    FOREIGN KEY (asset_id, property_id) REFERENCES maintenance_assets (id, property_id),
    CONSTRAINT ux_maint_tickets_number   UNIQUE (property_id, number),
    CONSTRAINT ux_maint_tickets_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_maint_tickets_open ON maintenance_tickets (property_id, status) WHERE status <> 'CLOSED';
CREATE INDEX ix_maint_tickets_unit ON maintenance_tickets (unit_id) WHERE unit_id IS NOT NULL;
CREATE TRIGGER trg_maint_tickets_no_delete BEFORE DELETE ON maintenance_tickets
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

ALTER TABLE housekeeping_tasks ADD CONSTRAINT fk_hk_tasks_ticket
    FOREIGN KEY (maintenance_ticket_id, property_id) REFERENCES maintenance_tickets (id, property_id);

-- ---------------------------------------------------------------------
-- maintenance_comments : append-only timeline
-- ---------------------------------------------------------------------
CREATE TABLE maintenance_comments (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    ticket_id        uuid        NOT NULL REFERENCES maintenance_tickets (id),
    kind             text        NOT NULL DEFAULT 'NOTE',
    body             text        NOT NULL,
    from_status      text        NULL,
    to_status        text        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_maint_comments_kind CHECK (kind IN ('NOTE','STATUS')),
    CONSTRAINT ck_maint_comments_body CHECK (length(btrim(body)) BETWEEN 1 AND 2000)
);
CREATE INDEX ix_maint_comments_ticket ON maintenance_comments (ticket_id, created_at);
CREATE TRIGGER trg_maint_comments_append_only BEFORE UPDATE OR DELETE ON maintenance_comments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- maintenance_attachments : photos (encrypted in the file store; only the key here)
-- ---------------------------------------------------------------------
CREATE TABLE maintenance_attachments (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    ticket_id        uuid        NOT NULL REFERENCES maintenance_tickets (id),
    storage_key      text        NOT NULL,
    key_id           text        NOT NULL,
    file_name        text        NOT NULL,
    content_type     text        NOT NULL,
    size_bytes       integer     NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_maint_attachments_type CHECK (content_type IN ('image/jpeg','image/png','image/webp')),
    CONSTRAINT ck_maint_attachments_size CHECK (size_bytes BETWEEN 1 AND 2097152),
    CONSTRAINT ck_maint_attachments_name CHECK (length(file_name) BETWEEN 1 AND 120),
    CONSTRAINT ux_maint_attachments_key  UNIQUE (storage_key)
);
CREATE INDEX ix_maint_attachments_ticket ON maintenance_attachments (ticket_id, created_at);
CREATE TRIGGER trg_maint_attachments_append_only BEFORE UPDATE OR DELETE ON maintenance_attachments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- Existing data
-- ---------------------------------------------------------------------
-- A default departure checklist for every property.
WITH t AS (
    INSERT INTO housekeeping_checklist_templates (organization_id, property_id, name, task_type)
    SELECT organization_id, id, 'Departure clean', 'DEPARTURE' FROM properties
    RETURNING id, organization_id
)
INSERT INTO housekeeping_checklist_items (organization_id, template_id, label, is_required, sort_order)
SELECT t.organization_id, t.id, x.label, false, x.ord
FROM t CROSS JOIN (VALUES
    ('Strip beds and put on fresh linen', 1),
    ('Clean and disinfect the bathroom', 2),
    ('Fresh towels and toiletries', 3),
    ('Dust surfaces, sweep and mop the floor', 4),
    ('Empty bins', 5),
    ('Check AC, lights, TV and taps work', 6),
    ('Restock water bottles', 7)) AS x(label, ord);

-- Rooms that need cleaning now get their task (the check-outs since Sprint 7 that nothing consumed yet).
INSERT INTO housekeeping_tasks (organization_id, property_id, unit_id, bed_id, task_type, status, source, source_change_id,
                                reservation_id, template_id, checklist, business_date, started_at, finished_at)
SELECT s.organization_id, s.property_id, s.unit_id, s.bed_id, 'DEPARTURE',
       CASE s.status WHEN 'DIRTY' THEN 'OPEN' WHEN 'CLEANING' THEN 'IN_PROGRESS' ELSE 'CLEANED' END,
       COALESCE(h.source, 'MANUAL'), h.id, h.reservation_id, tpl.id,
       COALESCE((SELECT jsonb_agg(jsonb_build_object('id', i.id, 'label', i.label, 'required', i.is_required, 'done', false)
                                  ORDER BY i.sort_order)
                 FROM housekeeping_checklist_items i WHERE i.template_id = tpl.id), '[]'::jsonb),
       (s.changed_at AT TIME ZONE p.timezone)::date,
       CASE WHEN s.status <> 'DIRTY' THEN s.changed_at END,
       CASE WHEN s.status IN ('CLEAN','INSPECTION') THEN s.changed_at END
FROM unit_housekeeping_status s
JOIN properties p ON p.id = s.property_id
LEFT JOIN housekeeping_checklist_templates tpl ON tpl.property_id = s.property_id AND tpl.task_type = 'DEPARTURE' AND tpl.is_active
LEFT JOIN LATERAL (
    SELECT hh.id, hh.source, hh.reservation_id FROM unit_housekeeping_history hh
    WHERE hh.unit_id = s.unit_id AND hh.bed_id IS NOT DISTINCT FROM s.bed_id AND hh.to_status = 'DIRTY'
      AND hh.source IN ('CHECK_OUT','ROOM_MOVE','MANUAL') AND hh.consumed_at IS NULL
    ORDER BY hh.changed_at DESC LIMIT 1) h ON true
WHERE s.status IN ('DIRTY','CLEANING','CLEAN','INSPECTION');

UPDATE unit_housekeeping_history SET consumed_at = now() WHERE consumed_at IS NULL AND to_status = 'DIRTY';

-- Data classification (brief §59).
COMMENT ON TABLE housekeeping_checklist_templates IS 'class: internal. What to do in a room, per task type.';
COMMENT ON TABLE housekeeping_checklist_items     IS 'class: internal. Lines of a checklist template.';
COMMENT ON TABLE housekeeping_tasks               IS 'class: internal. Cleaning work per room or dorm bed; made from check-out events.';
COMMENT ON TABLE maintenance_assets               IS 'class: internal. Equipment that can break.';
COMMENT ON TABLE maintenance_tickets              IS 'class: internal. Reported problems; out-of-order ones block the room.';
COMMENT ON TABLE maintenance_comments             IS 'class: internal. Ticket timeline. Append-only.';
COMMENT ON TABLE maintenance_attachments          IS 'class: internal. Ticket photos, encrypted in the file store.';
