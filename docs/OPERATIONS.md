# Operations

> Status: **design**, implemented in Sprint 29. Logging, correlation ids and health checks exist today.

## Health

| Endpoint | Meaning | Use |
|---|---|---|
| `GET /health/live` | process is running | container/VM liveness |
| `GET /health/ready` | database reachable | load balancer readiness, uptime monitor |

## Logging and tracing (brief §61) *(built)*

- Structured JSON logs (Serilog, compact format) to stdout; the host ships them to the log store.
- Every request log carries `CorrelationId` and `RequestId`; the same ids are in error responses and audit rows.
- To investigate a user report: ask for the `correlationId` shown in the error, then search logs for it.
- OpenTelemetry tracing for HTTP, outgoing calls and Npgsql when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Metrics and alerts (Sprint 29)

| Signal | Alert when | Severity |
|---|---|---|
| API error rate (5xx) | > 2 % for 5 min | high |
| API p95 latency | > 1.5 s for 10 min | medium |
| Database unreachable (`/health/ready`) | 2 consecutive failures | critical |
| Payment failures / webhook signature failures | any burst > 5 in 10 min | high |
| Webhook processing failures (Razorpay, channel manager, WhatsApp) | any unprocessed event older than 15 min | high |
| OTA sync failures | 3 consecutive failures | high |
| Backup job | missing or failed daily backup | critical |
| Authentication attacks | > 50 failed sign-ins/10 min, or any `REFRESH_TOKEN_REUSE` | high |

Tooling stays inexpensive: the cloud provider's built-in monitor + an uptime checker + email/WhatsApp alerts to the
owner. No enterprise APM unless needed.

## Backup and restore (brief §60)

| Item | Target |
|---|---|
| Database | Managed daily backups + point-in-time recovery (WAL), 30-day retention |
| Off-site copy | Weekly `pg_dump` to separate storage account/region, 12 weeks |
| Blob storage | Soft delete + versioning, 30 days |
| Encryption key | Stored in the secret store and an offline sealed copy held by the owner |
| **RPO** (max data loss) | ≤ 15 minutes (PITR) — to be confirmed by owner |
| **RTO** (max downtime) | ≤ 4 hours — to be confirmed by owner |
| Restore drill | Monthly, into a scratch database; time taken and checks recorded in `docs/operations/restore-log.md` |
| Monitoring | Backup failure alert (above) |

### Restore procedure (outline)

1. Declare the incident; stop the API (maintenance page at Cloudflare).
2. Restore the database to a new server at the chosen point in time.
3. Verify: row counts for reservations, folios, payments, invoices against the last report; latest audit entry time.
4. Point `DATABASE_URL` at the restored server; start the API; smoke test.
5. Reconcile external systems for the lost window: Razorpay payments (dashboard export), channel manager bookings, Zoho invoices.
6. Write a post-incident note.

## Runbooks (Sprint 29)

- Payment captured at Razorpay but booking not confirmed → replay the stored webhook event.
- OTA booking failed with no availability → reconciliation queue; call the guest/OTA.
- Locked-out staff member → manager resets the password from Staff accounts.
- Suspected credential leak → rotate the secret, revoke all sessions (rotate `Jwt__SigningKey`), review the audit trail.
