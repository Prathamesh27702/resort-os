# Sprint 13 — schema reference (housekeeping and maintenance)

Source of truth: `database/migrations/V015__housekeeping.sql`. Dev seed: `database/seed/dev/S010__housekeeping.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.23](../architecture/system-design.md#423-housekeeping-one-task-per-check-out-and-out-of-order-rooms).

The cleaning state itself is not new: it stays in Sprint 7's `unit_housekeeping_status` (one row per room, `bed_id` NULL, or
dorm bed) with every change in `unit_housekeeping_history`. Sprint 13 adds the work around it: tasks, checklists, tickets.
Out of order uses Sprint 2's `unit_blocks` (type `OUT_OF_ORDER`), so availability and check-in need no change.

## What changed

| Object | Change | Why |
|---|---|---|
| `unit_housekeeping_history` | `source` adds `HOUSEKEEPING` (a step on the board) and `MAINTENANCE` (a repair took the room out of order or gave it back) | So the history says where every change came from. |
| `housekeeping_checklist_templates`, `housekeeping_checklist_items` | new tables | What to do in a room, per property and kind of cleaning, optionally per room type. Every existing property gets a 7-line "Departure clean". |
| `housekeeping_tasks` | new table; never deleted (trigger) | A piece of cleaning work on one room or bed: who, when started, finished and inspected, and the checklist ticked. |
| `maintenance_assets` | new table; never deleted | Things that break: an AC unit, a geyser, the pool pump; optionally in a room. |
| `maintenance_tickets` | new table; never deleted | A reported problem, `M00001` … per property (counter `maintenance_ticket` in `property_counters`). |
| `maintenance_comments`, `maintenance_attachments` | new tables; append-only (UPDATE and DELETE refused) | A ticket's timeline and its photos. |

## `housekeeping_checklist_templates`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `name` | text | 2–80 characters |
| `task_type` | text | `DEPARTURE`, `STAYOVER`, `DEEP_CLEAN`, `AFTER_REPAIR` |
| `accommodation_type_id` | uuid NULL | NULL: every room type without a template of its own |
| `is_active` | boolean | At most one active template per property, kind and room type (`ux_hk_templates_active`, NULLS NOT DISTINCT) |

`housekeeping_checklist_items`: `template_id` (cascade), `label` (2–120), `is_required`, `sort_order`. A task **copies** the
lines when it is created (`housekeeping_tasks.checklist`), so editing a template never changes work already started.
Which template a new task uses: the room type's own for that kind → the general one for that kind → the departure one →
the built-in departure list (a property with no templates at all).

## `housekeeping_tasks`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `unit_id`, `bed_id` | uuid | The room, and the bed for a dormitory |
| `task_type` | text | as above |
| `status` | text | `OPEN` (room `DIRTY`) → `IN_PROGRESS` (`CLEANING`) → `CLEANED` (`CLEAN` or `INSPECTION`) → `DONE` (`READY`); `CANCELLED` |
| `source` | text | `CHECK_OUT`, `ROOM_MOVE`, `MANUAL`, `MAINTENANCE` |
| `source_change_id` | uuid NULL, **UNIQUE** | The `unit_housekeeping_history` row (the DIRTY change) this task was made from: one change can never make two tasks |
| `reservation_id` | uuid NULL | The stay that left the room dirty |
| `maintenance_ticket_id` | uuid NULL | The repair after which the room is cleaned (`AFTER_REPAIR`) |
| `template_id`, `checklist` | uuid NULL, jsonb | The template copied, and its lines `[{ id, label, required, done }]` |
| `business_date` | date | The property's date the task was made |
| `assigned_to`, `assigned_at` | uuid, timestamptz | Who does it (the first to start an unassigned task) |
| `started_*`, `finished_*`, `inspected_*` | timestamptz, uuid | Who did each step and when |
| `inspection_note`, `failed_inspections` | text, integer | The supervisor's last "redo" note and how many times it was sent back |
| `note` | text | 1–500 |
| `closed_at`, `close_reason` | timestamptz, text | Set exactly when `DONE` or `CANCELLED` (`CANCELLED` needs a reason) |

Indexes: **`ux_hk_tasks_open`** — at most one open task (`OPEN`, `IN_PROGRESS`, `CLEANED`) per room or bed (unique on
`unit_id, bed_id` NULLS NOT DISTINCT, partial); `ix_hk_tasks_board`; `ix_hk_tasks_assignee`.

The migration also makes a task for every room that is already not ready, and marks pending DIRTY history consumed.

## `maintenance_assets`

`code` (`^[A-Z0-9][A-Z0-9_-]{0,19}$`, unique per property), `name` (2–80), `category`, `unit_id` NULL, `location` NULL,
`is_active`.

## `maintenance_tickets`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `number` | text | `^M[0-9]{5,}$`, unique per property, gap-free |
| `title`, `description` | text | 3–120; ≤ 2000 |
| `category` | text | `AC`, `PLUMBING`, `ELECTRICAL`, `FURNITURE`, `APPLIANCE`, `POOL`, `CIVIL`, `OTHER` |
| `priority` | text | `LOW`, `NORMAL`, `HIGH`, `URGENT` |
| `status` | text | `OPEN` → `ASSIGNED` → `IN_PROGRESS` / `ON_HOLD` → `RESOLVED` → `CLOSED` |
| `unit_id`, `bed_id`, `asset_id`, `location` | | Where: at least one of room, asset or location (`ck_maint_tickets_where`) |
| `out_of_order`, `expected_ready_on` | boolean, date | Out of order needs a room and a date (`ck_maint_tickets_ooo`) |
| `block_id` | uuid NULL | The `unit_blocks` row (type `OUT_OF_ORDER`) it holds; released when the ticket is resolved |
| `reported_by`, `assigned_to`, `assigned_at`, `started_at` | | |
| `resolved_at`, `resolved_by`, `resolution` | | Required once `RESOLVED` or `CLOSED` |
| `closed_at`, `closed_by` | | Set exactly when `CLOSED` |

## `maintenance_comments` and `maintenance_attachments`

Comments: `ticket_id`, `kind` (`NOTE` written by someone, `STATUS` a change with `from_status` and `to_status`), `body`
(1–2000). Attachments: `ticket_id`, `storage_key` (`maintenance/{organization}/{id}`, unique), `key_id` (the encryption key
that sealed it), `file_name`, `content_type` (`image/jpeg`, `image/png`, `image/webp`), `size_bytes` (≤ 2 MB). The photo
itself is encrypted in the file store (Sprint 3's `IFileStore` and `IFieldEncryptor`), never in the database. Both tables
refuse UPDATE and DELETE.

## Dev seed (S010)

The departure checklist for the bootstrap property, a task for Room 101 (dirty since S006), equipment `AC-101`,
`GEYSER-106` and `POOL-PUMP`, and two open tickets: `M00001` pool pump (HIGH) and `M00002` tap in Room 104. Room 103 and
dorm bed B01 are left alone (earlier review scripts block them).

Checks: [database/queries/check-sprint-13.sql](../../database/queries/check-sprint-13.sql).
