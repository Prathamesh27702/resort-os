# Sprint 24 — schema reference (notifications, WhatsApp, email, background jobs)

Source of truth: `database/migrations/V026__notifications.sql`. Dev seed: `database/seed/dev/S021__messages.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.34](../architecture/system-design.md#434-messages-the-outbox-and-the-daily-jobs).
Setup and defaults: [WHATSAPP.md](../WHATSAPP.md).

Every message to a guest or a member of staff is first a row in `outbox_messages`, written in the same transaction as
what caused it (a confirmed booking) or by a daily job. A background sender sends the rows and records what WhatsApp or
the email provider said. Daily jobs are rows in `scheduled_jobs`; what each did is in `job_runs`, whose unique key is
what makes a job run twice send once. Templates and jobs are made by the app with their defaults the first time a
property needs them (no seed rows), so a new property gets them too.

## What changed

| Object | Change | Why |
|---|---|---|
| `message_templates` | new table, unique (property, code, channel) | The words of each message per channel, editable without a release. WhatsApp rows name the template approved in WhatsApp Manager and its language; email and in-app rows have a subject. Never deleted (turn one off). |
| `outbox_messages` | new table, unique (property, `dedupe_key`) | Every message: to whom (guest or user), on which channel, the words as sent, the values, and what became of it. |
| `whatsapp_messages` | new table, unique `wamid`, unique outbox row | WhatsApp's message id and the statuses its webhook reported, with the error code and the pricing category. |
| `email_messages` | new table, append-only, unique outbox row | The provider's id for each email sent (RESEND, or FAKE in development). |
| `notifications` | new table | The bell: one line per person, read or not, with a link inside the app. |
| `scheduled_jobs` | new table, unique (property, job) | Each daily job: on or off, its time (property time), when it next runs, how the last run went. |
| `job_runs` | new table, append-only, unique (property, job, business key) | One row per item a job did (a booking and its arrival day, a payment link, a day's digest), manual or scheduled. |
| `webhook_events.provider` | CHECK now RAZORPAY or WHATSAPP | WhatsApp statuses are recorded once each, like Razorpay's events (event id `wamid:status`). |
| `guest_consents.purpose` | adds `SERVICE_WHATSAPP`, `SERVICE_EMAIL` | A guest can say No to booking messages per channel; no row means they are sent. |

## Columns worth knowing

**outbox_messages**

| Column | Meaning |
|---|---|
| `dedupe_key` | What makes the message unique, e.g. `booking_confirmation:{reservation}:WHATSAPP`, `reservation_reminder:{reservation}:{yyyymmdd}:EMAIL`, `inventory_alert:{day}:{user}:IN_APP`. Inserted with `ON CONFLICT DO NOTHING`. |
| `template_code`, `channel` | Which message (`booking_confirmation`, `reservation_reminder`, `pre_arrival`, `checkout_reminder`, `payment_reminder`, `inventory_alert`, `leave_reminder`) and WHATSAPP, EMAIL or IN_APP. |
| `recipient_type`, `guest_id`, `user_id` | GUEST (with a guest) or USER (with a user); IN_APP is only for users (`ck_outbox_messages_recipient`, `ck_outbox_messages_in_app`). |
| `recipient_name`, `to_address` | As they were when written; `to_address` is the phone in E.164 or the email. Blanked when the guest is anonymised. |
| `provider_template`, `language`, `subject`, `body`, `params` | What was sent: WhatsApp's template name and language, the email subject, the text with the values filled in, and the values (jsonb). |
| `source`, `job_run_id` | EVENT (when it happened), JOB (a daily job; which run) or MANUAL. |
| `status` | QUEUED, SENT, DELIVERED, READ, FAILED or SKIPPED. FAILED and SKIPPED must have `last_error` (`ck_outbox_messages_why`); SENT and later must have `sent_at`. |
| `attempts`, `next_attempt_at` | Tries so far and when the next one is due (1, 5, 15, 60 minutes; at most 5). |
| `provider_message_id` | WhatsApp's `wamid` or the email provider's id. |

`trg_outbox_messages_guard` refuses changing the key, recipient or channel of a message, and any change of status of a
SKIPPED one (`ck_outbox_messages_frozen`). Rows are never deleted.

**scheduled_jobs**: `job` (one of the six codes), `enabled`, `run_at` (time of day, property time), `next_run_at`
(claimed by compare-and-set: `UPDATE … WHERE next_run_at = {seen}`), `last_started_at`, `last_finished_at`,
`last_outcome`, `last_error`; audited and versioned (changing a time with a stale version is a 409).

**job_runs**: `job`, `business_key`, `reservation_id` (when the item is a booking), `triggered_by` SCHEDULE or MANUAL,
`run_by` (only for MANUAL, `ck_job_runs_manual`), `outcome` in words. Append-only.

## Indexes

`ix_outbox_messages_due (next_attempt_at) WHERE status = 'QUEUED'` (the sender), `ix_outbox_messages_list (property_id,
created_at DESC)` (the Messages page), `ix_outbox_messages_reservation`, `ix_outbox_messages_guest` (anonymising);
`ix_notifications_user`, `ix_notifications_unread`; `ix_scheduled_jobs_due (next_run_at) WHERE enabled`.

## Data classification

`outbox_messages.to_address`, `recipient_name`, `body`, `params` and `subject` are **personal data** (a guest's phone,
email and stay) and are blanked by anonymisation. `whatsapp_messages` holds no phone number. `webhook_events.payload` for
WHATSAPP keeps the statuses only: phone numbers and reply texts are taken out before it is stored.

## Dev seed S021

The WhatsApp booking confirmation of the five most recent demo bookings whose guest has a mobile number, as WhatsApp
reported them: two read, one delivered, one sent and one failed ("the number isn't on WhatsApp", error 131026), so
**Send again** can be tried; and "Messages are on" in the owner's bell. Skipped when the property already has messages.
