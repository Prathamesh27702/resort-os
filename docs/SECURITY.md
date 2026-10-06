# Security architecture

Designed for defence in depth. We don't claim the system can't be hacked (brief §56); we make each layer hard to
get past, keep attacks visible, and test the controls.

## Secrets

| Rule | How |
|---|---|
| No secrets in Git | `.env` and `*.local.json` are gitignored; `appsettings*.json` contain no keys or passwords. CI runs **gitleaks** on every PR |
| Local development | `scripts/setup-dev` generates throwaway local values. Integration **test-mode** keys are handed out privately by the owner |
| Staging / production | Environment variables injected from the host's secret store (Azure Key Vault / AWS Secrets Manager / GitHub Environments secrets). Different keys per environment; production keys known only to the owner and the deploy pipeline |
| Rotation | JWT signing key, DB password and integration keys rotated at least yearly and immediately after anyone with access leaves |
| If a secret is committed | Treat it as leaked: rotate it first, then remove it from history. Deleting the file isn't enough |

## Authentication *(built)*

- Passwords: PBKDF2-HMAC-SHA512, 100k iterations, per-hash salt (ASP.NET Core Identity v3 format), rehash on sign-in when parameters change. Minimum 10 characters with letters and digits.
- Lockout after 5 failures for 15 minutes; identical error for unknown email vs wrong password; timing equalised.
- Access token: JWT HS256, 15 minutes, kept **in memory** by the SPA (never localStorage).
- Refresh token: 32 random bytes, stored only as SHA-256, HttpOnly + Secure + SameSite=Strict cookie scoped to `/api/v1/auth`, rotated on every use. **Reuse of a rotated token revokes the whole session family** and is audited.
- Cookie endpoints require the `X-Resort-Client` header (CSRF defence on top of SameSite).
- **Security stamp** in every token, checked on every request: deactivation, password change and admin reset sign the user out immediately.
- New users and reset passwords are temporary: everything except change-password returns `403 PASSWORD_CHANGE_REQUIRED`.
- MFA (TOTP) for OWNER, GENERAL_MANAGER and ACCOUNTANT: Sprint 28 (`users.mfa_enabled` already exists).

## Authorisation *(built)*

- **Permission codes, never role names**, in code (`.RequirePermission("reservation.cancel")`). Roles are data.
- Grants are org-wide or limited to one property; property endpoints check the `{propertyId}` route value.
- **Escalation guard:** you can only grant or remove a role whose permissions you hold yourself in that scope. No self role changes, no self deactivation, at least one active owner.
- **Tenant isolation:** EF global filters on `organization_id`; anonymous queries see nothing; cross-tenant ids return 404.
- Resource-level checks in services (`CallerAccess`) for anything the route can't express.
- Sensitive permissions (refunds, salaries, ID documents, audit, tax rules) are flagged in the [permission matrix](security/permission-matrix.md).

## Data protection and privacy (brief §59)

| Class | Examples | Controls |
|---|---|---|
| Public | property name, room types, menu | none |
| Internal | reservations, room status, rosters | authenticated + permission |
| Confidential | guest contact details, invoices, payments, staff records | permission + audit of reads on bulk export (Sprint 23) |
| Sensitive | guest ID numbers/scans, salaries, bank details | dedicated permission (`guest_document.read`, `employee_salary.read`), field-level AES-GCM encryption, separate endpoints, never in list responses, never in WhatsApp/email bodies |

- Collect only what the law and operations need (ID details only where Form-C / local police rules require).
- Retention: guest ID scans deleted N days after checkout (owner to confirm N, default 365); anonymisation instead of deletion where accounting needs the stay. The retention job runs as a dry-run report first (Sprint 3).
- Restaurant and housekeeping roles have no access to guest documents (tested).

## Transport, headers and infrastructure

- HTTPS only (Cloudflare edge + HSTS 1 year in non-dev). TLS to the database.
- Headers *(built)*: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, COOP/CORP, CSP `default-src 'none'` on the API, `Cache-Control: no-store` on `/api`.
- CORS: explicit origins only. In production the UI and API share one site behind a reverse proxy.
- Rate limiting *(built)*: sign-in 10/min/IP; global 600/min per user. Cloudflare WAF + bot protection in front (Sprint 29).
- Database on a private network; the app's DB role has no DDL/TRUNCATE in production; a separate migration role runs migrations.
- Uploaded files: type and size checked, stored outside the web root, served via short-lived signed URLs (Sprint 3).

## Audit (brief §58) *(built)*

Every insert/update/delete of an audited entity writes an `audit_logs` row **in the same transaction**: user, time,
IP, device (User-Agent), entity, entity id, action, old and new values (secrets masked), reason (`X-Change-Reason`),
correlation id and request id. UPDATE/DELETE on `audit_logs` and `login_events` are rejected by database triggers.
Later sprints require a reason for: refund, invoice cancellation, booking cancellation, room reassignment, price
change, manual attendance, leave approval, inventory adjustment, expense approval.

## OWASP Top 10 (2021) — controls and tests

| # | Risk | Controls | Test |
|---|---|---|---|
| A01 | Broken access control | Permission policies, property scoping, tenant filters, escalation guard | *built:* `AccessControlTests` (403/404 matrix, tenant isolation, escalation) |
| A02 | Cryptographic failures | PBKDF2, SHA-256 token hashes, AES-GCM for sensitive fields, TLS everywhere | *built:* refresh cookie flags, masked audit values · Sprint 3: encrypted column test |
| A03 | Injection | EF Core parameterised queries; `FromSql` with interpolation only (parameterised); no string-built SQL; Angular escaping | Sprint 28: injection payload suite on every string input |
| A04 | Insecure design | Threat-model notes per sprint; exclusion constraints + idempotency for bookings/money | Sprint 5 concurrency tests, Sprint 11 webhook tests |
| A05 | Security misconfiguration | Secure headers, Swagger only in Development, no secrets in config, least-privilege DB role | *built:* `Security_headers_are_present` |
| A06 | Vulnerable components | Dependabot; CI fails on known-vulnerable NuGet/npm packages | CI `security` job |
| A07 | Identification & authentication failures | Lockout, rate limit, rotation + reuse detection, security stamp, MFA (Sprint 28) | *built:* `AuthTests` |
| A08 | Software & data integrity failures | Migration checksums; signed webhooks (HMAC); CI-built artefacts only; branch protection | *built:* migrator tamper test · Sprint 11 signature tests |
| A09 | Logging & monitoring failures | Structured logs with correlation ids, append-only audit, login events, alerts (Sprint 29) | *built:* audit append-only test |
| A10 | SSRF | No user-supplied URLs fetched server-side; outbound HTTP only to allow-listed integration hosts via named HttpClients | Sprint 28 test |

## Reporting a vulnerability

Don't open a public issue. Email the owner directly (see the root [SECURITY.md](../SECURITY.md)).
