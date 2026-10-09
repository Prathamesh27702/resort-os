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
- Restaurant and housekeeping roles have no access to guest documents (tested). Waiters may look a guest up to charge
  a meal to the room; housekeeping sees no guests at all *(built, Sprint 3)*.
- **Aadhaar:** only the last 4 digits are stored (masked Aadhaar), enforced by a database constraint *(built)*.
- **Audit without personal data:** guest audit rows name the fields that changed but store `***` instead of personal
  values (`[PersonalData]` properties and all ciphertext), so anonymising a guest leaves nothing behind in the
  append-only audit log *(built)*. Opening an ID number or scan writes a `SENSITIVE_READ` audit row *(built)*.
- **Anonymise, don't delete:** `POST /guests/{id}/anonymise` (owner/GM) removes personal data, purges documents and
  deletes scans; the row stays so stays and invoices still add up *(built)*.
- **Retention:** `GET /guests/retention/dry-run` lists the ID documents older than `Guests:DocumentRetentionDays`
  (default 365). The real purge job is switched on only after the owner approves the period (issue #54).

## Field encryption and key rotation *(built, Sprint 3)*

Guest ID numbers, document details and ID scans are encrypted by the API before they reach the database or file
storage, so a database dump or a copied file folder reveals nothing.

| Item | Design |
|---|---|
| Algorithm | AES-256-GCM (authenticated): tampering is detected, not just hidden |
| Key | `Encryption__Key`: 32 random bytes, base64. Locally in `.env` (written by `scripts/setup-dev`); in production from the host's secret store. Never in Git, never the same on two environments |
| Derived keys | HKDF-SHA256 derives a separate data key and lookup-hash key, so one purpose never reuses another's key |
| Envelope | `[version][key id][12-byte random nonce][ciphertext][16-byte tag]`; the key id travels with the data |
| Binding | Associated data = `guest_document:{id}:{field}`; a ciphertext copied to another row or field won't decrypt |
| Lookups | `number_hash` = HMAC-SHA256 (type + normalised number) finds the same passport on two profiles without decrypting |
| Files | Scans are encrypted with the same scheme, checked by their bytes (JPEG/PNG/PDF only), ≤ 5 MB, stored under API-generated keys outside any web folder |
| Fail fast | The API refuses to start if the key is missing or not 32 bytes |

**Rotating the key** (yearly, and at once if it may have leaked or someone with access leaves):

1. Generate a new key: `openssl rand -base64 32`.
2. Move the current key to `Encryption__PreviousKeys__k1` (its id) and put the new one in `Encryption__Key` with
   `Encryption__ActiveKeyId=k2`. Restart. New data uses k2; old data still decrypts with k1.
3. Run the re-encryption job (added with the background worker in Sprint 24): it re-encrypts every row and file whose
   `key_id` is k1 and recomputes `number_hash`. Until then, keep k1 configured.
4. When no row has `key_id = k1` (`SELECT count(*) FROM guest_documents WHERE key_id = 'k1'`), remove the old key.

**Losing the key loses the data.** Keep production keys in the secret store with its own backup; development keys
are throwaway (reset the database if you lose one).

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
| A02 | Cryptographic failures | PBKDF2, SHA-256 token hashes, AES-GCM for sensitive fields, TLS everywhere | *built:* refresh cookie flags, masked audit values, `ID_number_and_scan_are_stored_encrypted…`, `FieldEncryptionTests` (tamper, wrong row, rotation) |
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
