# Sprint 17 — schema reference (employees and roster)

Source of truth: `database/migrations/V019__employees.sql`. Dev seed: `database/seed/dev/S014__employees.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.27](../architecture/system-design.md#427-roster-clash-detection-and-shortages).

Who works at the property, what they are paid, their documents, and the weekly roster. Pay is kept in its own table behind
its own endpoint and permission (`employee_salary.read`): profiles, the roster and the audit trail never show an amount
(audit rows show `***`). Documents are encrypted by the API (AES-256-GCM, key outside the database) and need
`employee_document.read`; opening one is a `SENSITIVE_READ` audit row. Employee numbers come from `property_counters`
(`employee` → `E00001`). Nothing here is ever deleted: people leave (`LEFT`), shifts are cancelled with a reason, and
departments, job titles and templates are switched off.

## What changed

| Object | Change | Why |
|---|---|---|
| `departments`, `designations` | new tables; never deleted (switched off instead) | Front office, Kitchen … and the job titles inside each. |
| `employees` | new table; never deleted | One person: department, job title, employment type, joining date, weekly day off, optional staff account. |
| `employee_documents` | new table; never deleted (removed = number and scan wiped) | Aadhaar (last 4 only), PAN, bank account, passport, driving licence, offer letter. |
| `employee_compensation` | new table; append-only (trigger) | What a person is paid from a date. |
| `shift_templates` | new table; never deleted | The shifts a department works and how many people each needs. |
| `roster_shifts` | new table; never deleted (cancelled instead); exclusion constraint | One person on one shift on one day; no two overlapping shifts. |

## `departments` / `designations`

Departments: `code` (`^[A-Z0-9][A-Z0-9_-]{0,19}$`, unique per property), `name` (2–60, unique per property in any case),
`sort_order`, `is_active`. A department with active employees can't be switched off (the API says how many). Job titles:
`department_id`, `name` (2–60, unique per department in any case), `is_active`.

## `employees`

| Column | Notes |
|---|---|
| `code` | `E00001` … (`^E[0-9]{5,}$`), unique per property |
| `first_name`, `last_name`, `phone`, `email`, `date_of_birth`, `address`, emergency contact | Personal data (masked in the audit trail); at least 18 years old (API) |
| `department_id`, `designation_id` | Same property; the job title must be in the department (composite FK) |
| `employment_type` | `FULL_TIME`, `PART_TIME`, `CONTRACT`, `SEASONAL` |
| `joined_on`, `weekly_off` | Weekly day off as ISO day 1 Monday … 7 Sunday, or none. A shift on it is a warning, not refused |
| `user_id` | The staff account linked to this person (optional); one employee per account (`ux_employees_user`). Used by `/roster/mine` |
| `status`, `left_on`, `leaving_reason` | `ACTIVE`, or `LEFT` with the last working day (on or after joining) and the reason (`ck_employees_left`) |

## `employee_documents`

`document_type` (`AADHAAR`, `PAN`, `BANK_ACCOUNT`, `PASSPORT`, `DRIVING_LICENCE`, `OFFER_LETTER`, `OTHER`),
`number_encrypted` (AES-GCM envelope; always NULL for Aadhaar: `ck_employee_documents_aadhaar`), `number_last4` (for
"•••• 234F"), `expires_on`, `note`, `storage_key` / `file_name` / `content_type` / `size_bytes` (the encrypted scan, at most
5 MB), `key_id`, `status` (`ACTIVE`, `REMOVED` with `removed_at` and `removed_reason`; removing wipes the number and the
scan).

## `employee_compensation`

`effective_from`, `pay_basis` (`MONTHLY`, `DAILY`), `basic` (> 0), `allowances` (≥ 0), `pf_applicable`, `esi_applicable`,
`note`, `created_at`, `created_by`. `trg_employee_compensation_append_only` refuses UPDATE and DELETE: a raise or a
correction is a new row. The pay for a day is the latest row with `effective_from` on or before it (for one date, the
latest recorded).

## `shift_templates`

`department_id`, `code` (unique per property), `name` (2–40), `start_time`, `end_time` (at or before the start: ends the next
day), `break_minutes` (0–180), `min_staff` (0–50: people needed every day; 0 is never short), `is_active`.

## `roster_shifts`

| Column | Notes |
|---|---|
| `employee_id`, `department_id`, `template_id` | Same property; no template = own times |
| `work_date` | The local day the shift starts on |
| `starts_at`, `ends_at` | Exact instants from the property's time zone; at most 16 hours (`ck_roster_shifts_times`) |
| `period` | Generated: `tstzrange(starts_at, ends_at, '[)')` (back to back is not an overlap) |
| `break_minutes` | 0–180; paid time is the length minus the break |
| `status`, `cancel_reason` | `SCHEDULED`, or `CANCELLED` with the reason |
| `overlap_allowed`, `overlap_reason` | A manager allowed this shift to overlap another of the person's, with the reason (set together) |

`ex_roster_shifts_clash EXCLUDE USING gist (employee_id WITH =, period WITH &&) WHERE (status = 'SCHEDULED' AND NOT
overlap_allowed)`: the database refuses one person on two overlapping scheduled shifts, even when two managers save at the
same moment. The API maps it to `409 SHIFT_CLASH`.

## Data classification

`departments`, `designations`, `shift_templates`, `roster_shifts`: internal. `employees`: confidential.
`employee_documents`, `employee_compensation`: sensitive.

## Dev seed S014

Five departments (`FRONT` Front office, `HK` Housekeeping, `KITCHEN`, `REST` Restaurant, `MAINT` Maintenance) with eight job
titles; ten employees `E00001`–`E00010` (Asha Naik, receptionist, off on Tuesday … Joel Rodrigues, part time Friday to
Sunday), each with pay; nine shift templates (Front office Morning 07–15, Evening 15–23, Night 23–07 needing nobody;
Housekeeping Day 08–17 needing 2; Kitchen, Restaurant and Maintenance shifts); this week's roster (56 shifts, 11 gaps). Next
week is empty, for the demo. Seeded directly, so it has no audit rows.
