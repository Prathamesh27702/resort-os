// Software documentation: architecture, design and the sprint-by-sprint record (UI, API, database, code, tests).
// Most of it is generated from the repository (sprints.yml, migrations, OpenAPI, code, CI), so it stays accurate.
import { readFileSync } from 'node:fs';
import { Body } from '../lib/doc.mjs';
import { C, TEXT_W, p, small, bullets, steps, table, kv, callout, stats, image, spacer, STATUS } from '../lib/kit.mjs';
import { two, PHASES } from '../lib/repo.mjs';

const clean = (s) => String(s).replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');   // markdown links → text
const sprintRange = (txt) => String(txt).split(/[,;]/).flatMap(part => {
  const m = part.trim().match(/^(\d+)(?:\s*[–-]\s*(\d+))?/);
  if (!m) return [];
  const a = +m[1]; const z = m[2] ? +m[2] : a;
  return Array.from({ length: z - a + 1 }, (_, i) => a + i);
});

function moduleMap(r) {
  const md = readFileSync(r.abs('docs/ARCHITECTURE.md'), 'utf8');
  const start = md.indexOf('## B. Module map');
  const rows = md.slice(start).split('\n').filter(l => /^\| \d+ \|/.test(l)).map(l => l.split('|').slice(1, -1).map(x => x.trim()));
  const byId = Object.fromEntries(r.sprints.map(s => [s.id, s]));
  return rows.map(([n, name, owns, sprint]) => {
    const ids = sprintRange(sprint).filter(id => byId[id]);
    const st = ids.length && ids.every(id => byId[id].status === 'done') ? 'done'
      : ids.some(id => byId[id].status !== 'planned') ? 'in-progress' : 'planned';
    return { n, name, owns, sprint, st };
  });
}

export function technicalBody(r) {
  const b = new Body();
  const last = r.lastDone;
  const totalTables = r.migrations.reduce((n, m) => n + m.tables.length, 0);
  const tw = TEXT_W;

  // 1 ─────────────────────────────────────────────────────────────
  b.h1('Introduction');
  b.h2('Purpose and audience');
  b.add(p('This document describes how Resort OS is built: its technology, architecture, security and data design, the engineering process, and a sprint-by-sprint record of what was delivered, how each screen, API and table is designed, which code changed and how it was tested. It is written for developers joining the project, technical reviewers, auditors and the owner.'));
  b.add(p('A companion document, **Resort OS — Product Overview**, explains the system in plain language for clients.'));
  b.h2('How this document is produced');
  b.add(p(`It is generated from the repository by \`tools/handbooks\` (\`npm run build\`), reading the sprint plan (\`docs/sprints/sprints.yml\`), the SQL migrations, the OpenAPI snapshot, the source tree and the CI workflow. This edition was built from commit \`${r.git.sha}\` (${r.git.date}). When a sprint is accepted the plan is updated and the document rebuilt, so it always matches the code.`));
  b.h2('Related documents in the repository');
  b.add(table(['Document', 'What it covers'], [
    ['README.md', 'Project summary, quick start, current sprint'],
    ['docs/GETTING_STARTED.md', 'Tools to install, clone, run and test, step by step (Windows first)'],
    ['docs/ONBOARDING.md', 'How a new developer joins and how two developers collaborate'],
    ['docs/ARCHITECTURE.md, docs/architecture/*', 'Architecture, module map, lifecycles, frontend, decisions, risks'],
    ['docs/DATABASE.md, docs/database/*', 'Conventions, migrations, schema per sprint, tables by sprint'],
    ['docs/API.md, docs/api/*', 'API conventions, error format, endpoint specs, OpenAPI snapshot'],
    ['docs/SECURITY.md, docs/security/*', 'Security architecture, OWASP mapping, permission matrix'],
    ['docs/TESTING.md, docs/testing/VERIFYING.md', 'Test strategy; UI → API → database checks per feature'],
    ['docs/sprints/*', 'The plan, process, review scripts and one guide per sprint'],
    ['docs/COSTS.md, DEPLOYMENT.md, OPERATIONS.md', 'Costs, environments, operations and backups'],
    ['CHANGELOG.md', 'What changed in each accepted sprint'],
  ], [3600, tw - 3600], { zebra: true }));

  // 2 ─────────────────────────────────────────────────────────────
  b.h1('Project summary');
  b.add(p('Resort OS is an India-first resort management system delivered as a **modular monolith**: one ASP.NET Core API, one PostgreSQL database and one Angular staff app (an installable PWA), with a separate public booking site later. External specialists (payments, accounting, payroll, OTAs, messaging) are reached only through adapters. There are no microservices, message brokers, Redis or Kubernetes: the system is sized for a resort, cheap to run and simple to operate.'));
  b.add(spacer(4), stats([
    { value: `${r.done.length}/${r.sprints.length - 1}`, label: 'build sprints accepted', color: C.palm },
    { value: String(totalTables), label: 'database tables built' },
    { value: String(r.operations.length), label: 'API operations' },
    { value: String(r.testMethods.backend + r.testMethods.frontend + r.testMethods.e2e), label: 'automated test methods' },
    { value: (r.code.backend.lines + r.code.frontend.lines).toLocaleString('en-IN'), label: 'lines of app code' },
  ]), spacer(8));
  b.add(kv([
    ['Repository', 'github.com/Prathamesh27702/resort-os (private). Branches: main (released, tagged sprint-NN), develop (integration), feature/* and fix/*'],
    ['Current sprint', `Sprint ${two(r.current.id)} — ${r.current.title} (${STATUS[r.current.status].text.toLowerCase()})`],
    ['Last accepted', last ? `Sprint ${two(last.id)} — ${last.title}. ${clean(last.progress?.outcome ?? '')}` : '—'],
    ['Target property', 'A beach resort in India with 6 rooms (2 deluxe, 4 standard) and a 10-bed dormitory; multi-property and multi-organisation by design'],
    ['Cost so far', '₹0: free tools and free tiers only (docs/COSTS.md)'],
  ]));

  // 3 ─────────────────────────────────────────────────────────────
  b.h1('Technology stack');
  const v = (k) => r.pkgs[k] ?? '';
  b.add(table(['Layer', 'Technology', 'Version', 'Why this choice'], [
    ['Runtime', '.NET SDK (LTS)', r.sdk, 'Long-term support, high performance, one language across API and tests'],
    ['API', 'ASP.NET Core minimal APIs', '10', 'Thin endpoints, built-in auth, rate limiting, OpenAPI'],
    ['Data access', 'EF Core + Npgsql', v('Microsoft.EntityFrameworkCore'), 'Typed queries, global tenant filters, xmin concurrency; schema owned by SQL migrations'],
    ['Validation', 'FluentValidation', v('FluentValidation'), 'One validator per request; field-level messages'],
    ['Auth', 'JWT bearer + rotating refresh cookie', v('Microsoft.AspNetCore.Authentication.JwtBearer'), 'Short-lived tokens in memory; HttpOnly refresh with reuse detection'],
    ['Logging / tracing', 'Serilog, OpenTelemetry', `${v('Serilog.AspNetCore')} / ${v('OpenTelemetry.Extensions.Hosting')}`, 'Structured logs with correlation ids; vendor-neutral tracing'],
    ['API docs', 'Microsoft.AspNetCore.OpenApi + Swagger UI', v('Microsoft.AspNetCore.OpenApi'), 'Live spec in Development; snapshot committed'],
    ['Database', 'PostgreSQL', '16', 'Exclusion constraints, ranges, triggers, JSONB; free and managed everywhere'],
    ['Frontend', 'Angular (standalone, signals)', r.feVer('@angular/core'), 'Typed, structured, long-lived; strong forms and routing'],
    ['UI kit', 'Angular Material + Tailwind CSS', `${r.feVer('@angular/material')} / ${r.feVer('tailwindcss')}`, 'Accessible components; utilities for layout'],
    ['Language', 'TypeScript', r.feVer('typescript'), 'Strict typing in the browser code'],
    ['Backend tests', 'xUnit + WebApplicationFactory', v('xunit'), 'Integration tests against a real PostgreSQL 16'],
    ['Frontend tests', 'Jasmine + Karma', r.feVer('jasmine-core'), 'Component and service unit tests'],
    ['Browser tests', 'Playwright', r.feVer('@playwright/test'), 'The sprint review scripts, automated, in Chromium'],
    ['Lint / format', 'dotnet format, ESLint (angular-eslint)', r.feVer('eslint'), 'Consistent code; enforced in CI'],
    ['CI', 'GitHub Actions', '—', `${r.ciJobs.length} jobs on every pull request (section 9.5)`],
    ['Local environment', 'Docker Desktop (PostgreSQL), VS Code', '—', 'Same database version as production; free for small teams'],
    ['Production (Sprint 29)', 'Linux VM in an Indian region, managed PostgreSQL with PITR, blob storage, Cloudflare', '—', 'Low cost, data residency, point-in-time recovery'],
  ], [1500, 2600, 1100, tw - 5200], { zebra: true, boldFirst: true, size: 18 }));

  // 4 ─────────────────────────────────────────────────────────────
  b.h1('Architecture');
  b.h2('System context');
  b.add(image(r.abs('tools/handbooks/assets/architecture.png'), { caption: 'System architecture: built parts in solid boxes, planned parts dashed' }));
  b.add(bullets([
    '**One deployable API** (`Resort.Api`) serves the staff app, later the public booking site, and partner webhooks.',
    '**One PostgreSQL database** holds every module\'s tables. Every business row carries `organization_id` (and `property_id` where it belongs to a property).',
    '**A background worker** (Sprint 24) delivers outbound work from an outbox table: messages, accounting and OTA sync, scheduled jobs, with retries and idempotency.',
    '**Adapters per capability** (`IPaymentGateway`, `IAccountingProvider`, `IChannelManager`, `IMessageSender`, `IFileStore`) keep vendors replaceable.',
  ]));
  b.h2('Backend structure');
  b.add(image(r.abs('tools/handbooks/assets/layers.png'), { caption: 'Projects, dependency direction and module folders' }));
  b.add(p(`Projects: ${r.projects.map(x => `\`${x}\``).join(', ')}. Dependencies point inwards: Api → Infrastructure → Application → Domain/Contracts. Business rules live in Application and Domain, never in endpoints or Angular components. Each module is a folder in every layer, and modules talk to each other through application services, never by writing another module's tables.`));
  b.h2('Request pipeline');
  b.add(image(r.abs('tools/handbooks/assets/pipeline.png'), { caption: 'Middleware and checks applied to every request' }));
  b.h2('Cross-cutting rules');
  b.add(table(['Concern', 'Rule'], [
    ['Tenancy', 'Global EF Core query filter on organization_id; anonymous queries see nothing; another tenant\'s id returns 404, never 403'],
    ['Time', 'Instants stored as UTC timestamptz. Business dates (stays, blocks, shifts) are date values in the property\'s time zone (Asia/Kolkata), never the server clock'],
    ['Money', 'decimal in C#, numeric(12,2) in SQL, never float. Tax rule id and rate stored on every taxed line (Sprints 8–12)'],
    ['Concurrency', 'Optimistic: PostgreSQL xmin exposed as version, 409 on stale edits. Inventory: row locks plus EXCLUDE constraints'],
    ['Idempotency', 'Idempotency-Key header on booking and money POSTs; webhook event-id de-duplication (Sprints 5, 11)'],
    ['Audit', 'EF interceptor writes old/new values, user, IP, device, reason, correlation and request id in the same transaction; append-only by trigger'],
    ['Errors', '{code, message, details, correlationId}; no stack traces, SQL or infrastructure details ever returned'],
    ['Secrets', 'Only in environment variables / secret stores; .env is gitignored; gitleaks scans every pull request'],
  ], [1700, tw - 1700], { zebra: true, boldFirst: true }));
  b.h2('Module map');
  const mods = moduleMap(r);
  b.add(p(`The ${mods.length} modules, the tables each owns, and the sprint that builds it. Status is computed from the sprint plan.`));
  b.add(table(['#', 'Module', 'Owns (main tables)', 'Sprint', 'Status'],
    mods.map(m => [m.n, m.name, m.owns, m.sprint, STATUS[m.st].text]),
    [500, 2000, tw - 500 - 2000 - 900 - 1300, 900, 1300],
    { size: 17, fills: (ri, ci) => (ci === 4 ? STATUS[mods[ri].st].fill : undefined) }));

  // 5 ─────────────────────────────────────────────────────────────
  b.h1('Security architecture');
  b.h2('Authentication (built)');
  b.add(bullets([
    'Passwords hashed with PBKDF2-HMAC-SHA512 (100,000 iterations, per-hash salt); minimum 10 characters with letters and digits.',
    'Lock-out after 5 failures for 15 minutes; the same error for an unknown email and a wrong password, with equalised timing.',
    'Access token: JWT HS256, 15 minutes, kept in memory by the app (never localStorage).',
    'Refresh token: 32 random bytes stored only as a SHA-256 hash, in an HttpOnly, Secure, SameSite=Strict cookie scoped to /api/v1/auth, rotated on every use. Reuse of a rotated token revokes the whole session family and is audited.',
    'Cookie endpoints require the X-Resort-Client header (CSRF defence on top of SameSite).',
    'A security stamp in every token is checked on every request: deactivation, password change and admin reset sign the user out at once.',
    'Temporary passwords: every endpoint except change-password returns 403 PASSWORD_CHANGE_REQUIRED. MFA (TOTP) for owner, GM and accountant arrives in Sprint 28.',
  ]));
  b.h2('Authorisation (built)');
  b.add(bullets([
    `Permission codes, never role names, in code: \`.RequirePermission("unit_block.manage")\`. ${r.permissions.length} permissions (${r.permissions.filter(x => x.sensitive).length} flagged sensitive) and ${r.roles.length} system roles are seeded by migration V002.`,
    'A grant is organisation-wide or limited to one property; property endpoints check the {propertyId} route value.',
    'Escalation guard: a user can only grant or remove roles whose permissions they hold in that scope; no self role changes or self-deactivation; at least one active owner always remains.',
    'Resource-level checks in services (CallerAccess) cover anything the route cannot express.',
  ]));
  b.h2('Data classification');
  b.add(table(['Class', 'Examples', 'Controls'], [
    ['Public', 'Property name, room types, menu', 'None'],
    ['Internal', 'Reservations, room status, rosters', 'Authenticated + permission'],
    ['Confidential', 'Guest contact details, invoices, payments, staff records', 'Permission + audit of bulk export reads (Sprint 23)'],
    ['Sensitive', 'Guest ID numbers and scans, salaries, bank details', 'Dedicated permission, AES-GCM field encryption, separate endpoints, never in lists or message bodies'],
  ], [1500, 3500, tw - 5000], { zebra: true, boldFirst: true }));
  b.h2('OWASP Top 10 (2021) mapping');
  b.add(table(['Risk', 'Controls', 'Tested by'], [
    ['A01 Broken access control', 'Permission policies, property scoping, tenant filters, escalation guard', 'AccessControlTests (built)'],
    ['A02 Cryptographic failures', 'PBKDF2, SHA-256 token hashes, AES-GCM for sensitive fields, TLS', 'Cookie flags, masked audit values (built); encrypted column (Sprint 3)'],
    ['A03 Injection', 'Parameterised EF Core queries only; Angular escaping', 'Injection payload suite (Sprint 28)'],
    ['A04 Insecure design', 'Exclusion constraints and idempotency for bookings and money', 'Concurrency tests (built for blocks; Sprint 5 for bookings)'],
    ['A05 Security misconfiguration', 'Secure headers, Swagger only in Development, least-privilege DB role', 'Security_headers_are_present (built)'],
    ['A06 Vulnerable components', 'Dependabot; CI fails on known-vulnerable packages', 'CI security job (built)'],
    ['A07 Authentication failures', 'Lock-out, rate limit, rotation + reuse detection, security stamp, MFA', 'AuthTests (built)'],
    ['A08 Integrity failures', 'Migration checksums, signed webhooks, CI-built artefacts', 'Migrator tamper test (built); webhook signatures (Sprint 11)'],
    ['A09 Logging failures', 'Structured logs, append-only audit, login events, alerts', 'Audit append-only test (built)'],
    ['A10 SSRF', 'No user-supplied URLs fetched; allow-listed outbound hosts', 'Sprint 28'],
  ], [2400, 3900, tw - 6300], { zebra: true, boldFirst: true, size: 18 }));

  // 6 ─────────────────────────────────────────────────────────────
  b.h1('Data architecture');
  b.h2('Conventions');
  b.add(table(['Rule', 'Detail'], [
    ['Names', 'snake_case, plural tables; ix_ / ux_ / ck_ prefixes'],
    ['Keys', 'uuid primary keys generated by the API as UUIDv7 (time-ordered); append-only logs use bigint identity'],
    ['Tenancy', 'organization_id NOT NULL on every business table; property_id where the row belongs to one property; composite FKs keep children inside one property'],
    ['Time', 'timestamptz in UTC; business dates as date in the property\'s time zone; ranges are [start, end) like check-in/check-out'],
    ['Money', 'numeric(12,2); quantities numeric(12,3); never float or money'],
    ['Audit columns', 'created_at, updated_at, created_by, updated_by on mutable tables'],
    ['Deletion', 'Master data is deactivated; financial rows and history are never updated in place or deleted (enforced by triggers)'],
    ['Enums', 'text + CHECK constraint, easy to extend in a migration'],
    ['Classification', 'Personal-data columns carry COMMENT ON COLUMN … \'class: sensitive\''],
  ], [1800, tw - 1800], { zebra: true, boldFirst: true }));
  b.h2('Migrations');
  b.add(p('The schema is owned by plain SQL migrations in `database/migrations`, applied in order at API start-up (in production, as a separate pipeline step). Each file runs in one transaction under an advisory lock. The API stores a SHA-256 checksum of every applied file and refuses to start if a merged migration is edited, so fixes always go forward in a new file.'));
  b.add(table(['Migration', 'Lines', 'Tables created'],
    r.migrations.map(m => [m.file, String(m.lines), m.tables.length ? m.tables.join(', ') : 'none (seed data / columns)']),
    [3300, 800, tw - 4100], { zebra: true, size: 18 }));
  b.add(small(`Total: ${totalTables} tables in ${r.migrations.length} migrations. Development demo data lives in database/seed/dev and runs only in Development.`));
  b.add(image(r.abs('tools/handbooks/assets/er-foundation.png'), { caption: 'Sprint 01 tables: identity, organisation, properties and audit' }));

  // 7 ─────────────────────────────────────────────────────────────
  b.h1('API design');
  b.h2('Conventions');
  b.add(table(['Topic', 'Rule'], [
    ['Base path', '/api/v1/<group>; property-specific resources under /api/v1/properties/{propertyId}/…'],
    ['Auth', 'Authorization: Bearer <access token> (15 min); refresh via HttpOnly cookie on /api/v1/auth/refresh with X-Resort-Client: web'],
    ['Permissions', 'Each endpoint requires one permission code; under /properties/{propertyId} the grant must be org-wide or for that property'],
    ['Time', 'ISO-8601 UTC instants; business dates as YYYY-MM-DD in property local time'],
    ['Concurrency', 'Mutable resources return version; send it back on PUT; stale → 409 CONCURRENCY_CONFLICT'],
    ['Idempotency', 'Booking and money POSTs require Idempotency-Key; retries return the original response (Sprint 5+)'],
    ['Reason', 'X-Change-Reason header (or a reason field in the body) on sensitive changes; stored in the audit log'],
    ['Tracing', 'Optional X-Correlation-Id; every response returns X-Correlation-Id and X-Request-Id'],
    ['Paging', '?page=1&pageSize=50 (max 200) → { items, page, pageSize, total }'],
    ['Rate limits', 'Sign-in and password change 10/min/IP; everything else 600/min per user'],
  ], [1600, tw - 1600], { zebra: true, boldFirst: true }));
  b.h2('Error format');
  b.add(p('Every non-2xx response has the body `{ code, message, details, correlationId }`. Stack traces, SQL and exception types are never returned; the correlation id finds the server log line.'));
  b.add(table(['HTTP', 'Codes', 'When'], [
    ['400', 'VALIDATION_FAILED, BAD_REQUEST, MISSING_CLIENT_HEADER', 'Invalid input (details = field → messages)'],
    ['401', 'UNAUTHORIZED, INVALID_CREDENTIALS, ACCOUNT_LOCKED, INVALID_REFRESH_TOKEN', 'Not signed in or bad credentials'],
    ['403', 'FORBIDDEN, PASSWORD_CHANGE_REQUIRED', 'Missing permission or temporary password'],
    ['404', 'NOT_FOUND', 'Missing or in another tenant (never says which)'],
    ['409', 'CONCURRENCY_CONFLICT, DUPLICATE, OVERLAP, EMAIL_TAKEN …', 'State conflict'],
    ['422', 'LAST_OWNER, SELF_ROLE_CHANGE, CONSTRAINT_VIOLATION …', 'Valid request that a business rule refuses'],
    ['429', 'RATE_LIMITED', 'Too many requests'],
    ['500', 'INTERNAL_ERROR', 'A bug; details only in the server log'],
  ], [800, 4500, tw - 5300], { zebra: true, size: 18 }));
  b.h2('Endpoint catalogue (built)');
  b.add(p(`${r.operations.length} operations on ${new Set(r.operations.map(o => o.path)).size} paths, from the committed OpenAPI snapshot (docs/api/openapi-v1.json). Full request and response specifications are in docs/API.md and in Swagger UI at /swagger when the API runs in Development.`));
  b.add(table(['Method', 'Path', 'Group'],
    r.operations.map(o => [o.method, o.path.replace('/api/v1', ''), o.tag]),
    [1000, tw - 1000 - 1800, 1800], { zebra: true, size: 17 }));
  b.add(small('Paths are shown without the /api/v1 prefix.'));

  // 8 ─────────────────────────────────────────────────────────────
  b.h1('Frontend design');
  b.add(p('Angular 20 with standalone components, signals, reactive forms and lazy-loaded routes; Angular Material components with Tailwind utilities. Components render and collect input; business logic lives in the API. Each feature talks to the API through its own typed service.'));
  b.add(table(['Folder', 'Contents'], [
    ['core/', 'Auth service (token in memory, silent refresh), HTTP interceptor and error mapping, guards, property context, models'],
    ['shared/', 'Reusable pieces such as the *appCan permission directive'],
    ['layout/', 'Shell: side navigation filtered by permission, top bar with property clock (IST), user menu'],
    ['features/', 'One folder per module: auth, dashboard, settings, users, audit, rooms … (added by their sprints)'],
  ], [1700, tw - 1700], { zebra: true, boldFirst: true }));
  b.h2('UI conventions');
  b.add(table(['Element', 'Convention'], [
    ['Layout', 'Page head (title, one-line purpose, one primary action) above panels'],
    ['Forms', 'Reactive forms, outline fields, two columns collapsing to one at 640 px; server field errors mapped onto controls'],
    ['Lists', 'Loading, empty (with the next action) and error states on every list; secondary columns hidden on phones'],
    ['Feedback', 'Snack bar for 2.5 s in the past tense ("Changes saved"); errors show the API message'],
    ['Time', 'Always shown in the property\'s time zone'],
    ['Access', 'Guards and *appCan hide what a user cannot use; the API checks every request again'],
    ['Phones', 'Menu behind a button below 900 px; touch targets ≥ 44 px on staff-mobile screens'],
    ['Palette and type', 'Tide #0E3B43, kokum #8A2449, palm #2E6B3C on lime-wash #F5F7F6; IBM Plex Sans (self-hosted)'],
  ], [1700, tw - 1700], { zebra: true, boldFirst: true }));

  // 9 ─────────────────────────────────────────────────────────────
  b.h1('Engineering process');
  b.h2('Sprint lifecycle');
  b.add(image(r.abs('tools/handbooks/assets/sprint-process.png'), { caption: 'From plan to published' }));
  b.add(table(['When', 'Event', 'Output'], [
    ['Day 1', 'Sprint planning (60 min)', 'Goal agreed; issues in the sprint milestone; current: set in sprints.yml'],
    ['Daily', 'Stand-up (15 min)', 'Yesterday / today / blockers'],
    ['Day 9', 'Code freeze', 'Only fixes merged; acceptance checks run'],
    ['Day 10', 'Sprint review (45 min)', 'Owner runs the REVIEWING.md script; accepts or files issues'],
    ['Day 10', 'Retro (30 min)', 'One or two process changes written into PROCESS.md'],
    ['On acceptance', 'Release', 'develop → main (merge commit), tag sprint-NN, CHANGELOG dated, handbooks rebuilt'],
  ], [1500, 2600, tw - 4100], { zebra: true }));
  b.h2('Branching');
  b.add(bullets([
    '`main`: production-ready, one tagged release per accepted sprint.',
    '`develop`: integration branch; every feature pull request targets it.',
    '`feature/S03-12-guest-search`, `fix/S03-20-…`: one branch per issue, squash-merged with a conventional title such as `feat(guests): search by phone (#12)`.',
    '`hotfix/…`: from main for production incidents only; merged back to develop.',
  ]));
  b.h2('Definition of Done');
  b.add(bullets([
    'Merged to develop through a pull request with green CI (and one approving review once the team has two developers).',
    'Tests written for the behaviour, including the sprint guide\'s required tests.',
    'State-changing endpoints have a permission check, a validator, an audit entry, and idempotency where money or bookings are involved.',
    'No secrets, keys or personal data committed.',
    'docs/API.md and docs/DATABASE.md updated when endpoints or tables change.',
    'Screens have loading, empty and error states and work at 360 px width.',
  ]));
  b.h2('Secrets and configuration');
  b.add(p('Configuration comes from appsettings files (no secrets), then environment variables. Locally, `scripts/setup-dev` writes a gitignored `.env` at the repository root with throwaway values (database password, JWT signing key, encryption key, first owner password). Integration test keys (Razorpay, WhatsApp, Zoho) are handed out privately by the owner; `.env.example` lists the variable names with blank values. CI runs gitleaks on every pull request.'));
  b.h2('Continuous integration');
  b.add(table(['Job', 'What it checks'], r.ciJobs.map(name => [name, ({
    'Backend — build, lint, test': 'Restore, build with warnings as errors, dotnet format check, all backend tests against PostgreSQL 16 on Linux',
    'Frontend — lint, test, build': 'npm ci, ESLint, unit tests in headless Chrome, production build',
    'Backend — tests on Windows': 'All backend tests on a Windows runner with PostgreSQL (catches platform differences such as time-zone lookup)',
    'Browser tests — Sprint 01–02 screens': 'Starts its own API, web app and throwaway database; runs the sprint review scripts in Chromium; uploads the report',
    'Migrations — immutable and ordered': 'Merged migration files unchanged; numbering continuous',
    'Security — secrets and vulnerable packages': 'gitleaks over the history; known-vulnerable NuGet and npm packages fail the build',
  })[name] ?? '']), [3400, tw - 3400], { zebra: true, boldFirst: true }));
  b.add(small('An Onboarding workflow (Linux and Windows) also runs the setup scripts from scratch whenever they change.'));

  // 10 ────────────────────────────────────────────────────────────
  b.h1('Sprint record');
  b.add(p('One section per sprint that has been accepted, then the sprint in progress. Each accepted sprint lists its outcome, the UI, API and database design, the code that changed, the tests and their results, and the behaviours a developer should know about.'));
  b.add(table(['#', 'Sprint', 'Size', 'Status', 'Outcome'],
    r.sprints.filter(s => s.status !== 'planned').map(s => [two(s.id), s.title, s.size, STATUS[s.status].text, clean(s.progress?.outcome ?? s.progress?.now ?? '')]),
    [500, 2600, 600, 1200, tw - 4900],
    { size: 17, fills: (ri, ci, row) => (ci === 3 ? STATUS[r.sprints.find(s => two(s.id) === row[0]).status].fill : undefined) }));
  for (const s of r.done) sprintSection(b, r, s);
  currentSection(b, r, r.current);

  // 11 ────────────────────────────────────────────────────────────
  b.h1('Quality report');
  b.add(p(`Results as recorded at the acceptance of Sprint ${last ? two(last.id) : '—'}, and the test inventory in the repository at commit ${r.git.sha}.`));
  b.add(table(['Kind', 'Where', 'Tool', 'Count', 'Runs'], [
    ['Backend unit + integration', 'backend/Resort.Tests', 'xUnit, real PostgreSQL 16', `${r.testMethods.backend} methods`, 'CI on Linux and Windows; scripts/test'],
    ['Frontend unit', 'frontend/src/**/*.spec.ts', 'Jasmine + Karma', `${r.testMethods.frontend} specs`, 'CI; npm test'],
    ['Browser (end to end)', 'frontend/e2e', 'Playwright, Chromium', `${r.testMethods.e2e} steps`, 'CI; npm run e2e:headed to watch'],
    ['API checks', 'scripts/check-api.mjs', 'Node', '34 checks', 'By hand against a running API'],
    ['Database checks', 'database/queries/check-sprint-01-02.sql', 'psql / any SQL client', '16 queries', 'By hand, read-only'],
  ], [2000, 2500, 1700, 1200, tw - 7400], { zebra: true, boldFirst: true, size: 18 }));
  if (last?.progress?.results) {
    b.h2(`Latest recorded results (Sprint ${two(last.id)})`);
    b.add(bullets(last.progress.results.map(clean)));
  }
  b.h2('Code size');
  const cs = r.code;
  b.add(table(['Area', 'Files', 'Lines'], [
    ['Backend application code (C#)', cs.backend.files, cs.backend.lines],
    ['Backend tests (C#)', cs.backendTests.files, cs.backendTests.lines],
    ['Frontend application code (TS, HTML, SCSS)', cs.frontend.files, cs.frontend.lines],
    ['Frontend unit tests', cs.frontendSpecs.files, cs.frontendSpecs.lines],
    ['Browser tests and helpers', cs.e2e.files, cs.e2e.lines],
    ['SQL (migrations, seeds, checks)', cs.sql.files, cs.sql.lines],
    ['Documentation (Markdown)', cs.docs.files, cs.docs.lines],
  ].map(([a, f, l]) => [a, String(f), l.toLocaleString('en-IN')]), [5300, 1600, tw - 6900], { zebra: true }));
  b.add(small('Line counts include blank lines and comments.'));

  // 12 ────────────────────────────────────────────────────────────
  b.h1('Roadmap');
  b.add(p('All sprints in the plan with their goal, size (S ≈ 1, M ≈ 2, L ≈ 3–4 developer-weeks), dependencies and status. Each has a guide in docs/sprints with tasks, required tests, acceptance criteria and debugging notes.'));
  b.add(table(['#', 'Sprint and goal', 'Phase', 'Size', 'Needs', 'Status'],
    r.sprints.map(s => [two(s.id), [`**${s.title}**`, clean(s.goal)], String(s.phase), s.size, (s.depends ?? []).map(two).join(', ') || '—', STATUS[s.status].text]),
    [500, tw - 500 - 700 - 600 - 1100 - 1200, 700, 600, 1100, 1200],
    { size: 17, fills: (ri, ci) => (ci === 5 ? STATUS[r.sprints[ri].status].fill : undefined) }));
  b.add(small(`Phases: ${Object.entries(PHASES).map(([k, x]) => `${k} ${x}`).join(' · ')}.`));

  // 13 ────────────────────────────────────────────────────────────
  b.h1('Appendices');
  b.h2('A. Roles');
  b.add(table(['Code', 'Name', 'Purpose'], r.roles.map(x => [`\`${x.code}\``, x.name, x.description]), [2900, 2200, tw - 5100], { zebra: true, size: 18 }));
  b.h2('B. Permissions by module');
  const byMod = {};
  for (const x of r.permissions) (byMod[x.module] ??= []).push(x);
  b.add(table(['Module', 'Count', 'Permission codes (sensitive in bold)'],
    Object.entries(byMod).map(([m, xs]) => [m, String(xs.length), xs.map(x => (x.sensitive ? `**${x.code}**` : x.code)).join(', ')]),
    [1700, 800, tw - 2500], { zebra: true, size: 16 }));
  b.add(small('Which role holds which permission: docs/security/permission-matrix.md, or the Roles & permissions screen in the app.'));
  b.h2('C. Glossary');
  b.add(table(['Term', 'Meaning'], [
    ['Accommodation type', 'What is sold: a room type (UNIT, sold whole) or a dormitory type (BED, sold per bed)'],
    ['Block', 'A room or bed out of sale for [start, end) nights; released, never deleted'],
    ['Correlation id', 'An id that follows a request through logs and the audit trail'],
    ['EXCLUDE constraint', 'A PostgreSQL constraint that forbids overlapping ranges for the same key'],
    ['Folio', 'The running bill of a stay'],
    ['Idempotency key', 'A client-chosen id that makes a retried request safe'],
    ['Security stamp', 'A per-user value in every token; changing it signs the user out everywhere'],
    ['Tenant', 'An organisation; its rows are invisible to every other organisation'],
    ['UUIDv7', 'A time-ordered UUID, good for index locality'],
    ['xmin', 'PostgreSQL\'s row version, exposed by the API as version for optimistic concurrency'],
  ], [2400, tw - 2400], { zebra: true, boldFirst: true }));
  b.h2('D. Rebuilding this document');
  b.add(steps([
    'When a sprint is accepted, update its entry in `docs/sprints/sprints.yml`: status `done`, `progress` (done, files, results, outcome) and the `handbook` block (highlights, design notes for UI, API and database, screenshots).',
    'Add a line to `tools/handbooks/history.yml` with the new version (1.<sprint number>) and date.',
    'If a diagram changed (new module status, new tables), edit `tools/handbooks/diagrams/diagrams.html` and run `npm run diagrams`.',
    'Run `npm ci` once and then `npm run build` in `tools/handbooks`. Both Word files are written to `docs/handbooks/`.',
    'Open both files, check them, and commit them with the sprint\'s closing pull request.',
  ]));
  return b;
}

function sprintSection(b, r, s) {
  const tw = TEXT_W;
  const hb = s.handbook ?? {};
  b.h2(`Sprint ${two(s.id)} — ${s.title}`);
  b.add(kv([
    ['Goal', clean(s.goal)],
    ['In plain words', s.client ?? ''],
    ['Phase / size', `${s.phase} — ${PHASES[s.phase]} · ${s.size}`],
    ['Depends on', (s.depends ?? []).map(d => `Sprint ${two(d)}`).join(', ') || '—'],
    ['Outcome', clean(s.progress?.outcome ?? '')],
  ], 2100));
  if (s.progress?.done) { b.h3('What was delivered'); b.add(bullets(s.progress.done.map(clean))); }

  b.h3('UI design');
  if (hb.design?.ui) b.add(p(hb.design.ui));
  if (s.scope?.screens) b.add(table(['Screens (routes)'], s.scope.screens.map(x => [clean(x)]), [tw], { zebra: true, size: 18 }));
  for (const sh of hb.screenshots ?? []) b.add(image(r.abs(sh.file), { widthIn: sh.width ?? 5.4, caption: sh.caption, border: true }));

  b.h3('API design');
  if (hb.design?.api) b.add(p(hb.design.api));
  if (s.scope?.api) b.add(table(['Endpoints'], s.scope.api.map(x => [`\`${x.replace('/api/v1', '')}\``]), [tw], { zebra: true, size: 18 }));

  b.h3('Database design');
  if (hb.design?.database) b.add(p(hb.design.database));
  if (s.scope?.tables) b.add(kv([['Tables', s.scope.tables.join(', ')], ['Migrations', (s.scope.migrations ?? []).join(', ') || '—']], 2100));
  for (const d of hb.diagrams ?? []) b.add(image(r.abs(d.file), { caption: d.caption }));

  if (s.progress?.files) {
    b.h3('Code changed');
    b.add(table(['Files and folders'], s.progress.files.map(f => [clean(f)]), [tw], { zebra: true, size: 17 }));
  }
  b.h3('Tests and results');
  if (s.tests) b.add(table(['Required test'], s.tests.map(t => [clean(t)]), [tw], { zebra: true, size: 18 }));
  if (s.progress?.results) b.add(spacer(4), callout('Results', s.progress.results.map(clean), { fill: C.greenBg, accent: C.palm }));
  if (s.acceptance) { b.h3('Acceptance criteria'); b.add(bullets(s.acceptance.map(clean))); }
  if (s.debug?.length) { b.h3('Behaviours to know when debugging'); b.add(bullets(s.debug.map(clean))); }
}

function currentSection(b, r, s) {
  const tw = TEXT_W;
  b.h2(`Sprint ${two(s.id)} — ${s.title} (${STATUS[s.status].text.toLowerCase()})`);
  b.add(kv([
    ['Goal', clean(s.goal)],
    ['In plain words', s.client ?? ''],
    ['Phase / size', `${s.phase} — ${PHASES[s.phase]} · ${s.size}`],
    ['Depends on', (s.depends ?? []).map(d => `Sprint ${two(d)}`).join(', ') || '—'],
  ], 2100));
  b.h3('Planned scope');
  b.add(kv([
    ['Tables', (s.scope?.tables ?? []).join(', ')],
    ['Endpoints', (s.scope?.api ?? []).map(clean).join(' · ')],
    ['Screens', (s.scope?.screens ?? []).map(clean).join(', ')],
  ], 2100));
  if (s.tasks) { b.h3('Tasks'); b.add(bullets(s.tasks.map(t => clean(t).replace('[done] ', '✓ ')))); }
  if (s.tests) { b.h3('Required tests'); b.add(bullets(s.tests.map(clean))); }
  if (s.acceptance) { b.h3('Acceptance criteria'); b.add(bullets(s.acceptance.map(clean))); }
}
