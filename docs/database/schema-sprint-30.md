# Sprint 30 — schema reference (India readiness: FSSAI, UPI, Form C)

Source of truth: `database/migrations/V027__india_readiness.sql`. Dev seed: `database/seed/dev/S022__india_readiness.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.36](../architecture/system-design.md#436-india-readiness-upi-qr-printed-bills-and-form-c).
The owner's guide: [INDIA.md](../INDIA.md).

## What changed

| Object | Change | Why |
|---|---|---|
| `properties.fssai_licence_no` | new, text, CHECK 14 digits | The food licence number printed on bills and invoices. |
| `properties.upi_id` | new, text, CHECK name@bank, at most 100 characters | The UPI ID the QR on bills pays to. |
| `properties.upi_payee_name` | new, text, CHECK 2 to 60 characters | The name the payer's UPI app shows; the property's name when empty. |
| `form_c_records` | new table, unique (reservation, guest), no deletes | One Form C per foreign guest and stay. |
| `scheduled_jobs.job`, `job_runs.job` | CHECK adds `form_c_reminder` | The morning reminder of the forms still to file. |

## Columns worth knowing

**form_c_records**

| Column | Meaning |
|---|---|
| `status` | PENDING (to file), SUBMITTED (filed on the FRRO portal) or NOT_REQUIRED (entered as foreign by mistake). |
| `arrived_at`, `due_at` | The check-in moment and 24 hours later. Overdue is `status = PENDING AND due_at <= now()`, worked out when read. |
| `details_encrypted`, `key_id` | What staff typed on the sheet, as AES-256-GCM JSON (field key → value), like `guest_documents.details_encrypted`. Values the app knows from the profile, documents and booking are not copied here. Both null until something is typed, and again after the guest is anonymised. |
| `frro_reference` | The acknowledgement number the portal gave (optional, 3 to 40 characters). |
| `submitted_at`, `submitted_by` | Set exactly when the status is SUBMITTED (CHECK). |
| `not_required_reason` | Set exactly when the status is NOT_REQUIRED (CHECK), 3 to 300 characters. |

Indexes: open sheets by property and deadline (partial, PENDING only), all sheets by property and arrival, by guest.
A `BEFORE DELETE` trigger refuses deletes: what was reported, or should have been, stays on the register.

Data classes: the table is *sensitive*; `frro_reference` is *confidential*; the three property columns are *public*
(they are printed on bills).

## Checks

`database/queries/check-sprint-30.sql`: the migration, the property's values and their CHECKs, sheets by status, the
24-hour rule, one sheet per guest and stay, foreign guests in house without a sheet, and the reminder done once a day.
