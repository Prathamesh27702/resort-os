# Peer code review of a sprint

A second developer reads and runs a finished sprint **before** the owner accepts it. The owner checks that the sprint
does what the business needs (that's [REVIEWING.md](REVIEWING.md)); the reviewer checks that the code is correct,
safe and maintainable. A sprint is done only when both have signed off.

```
build → peer code review (this guide) → fixes → owner review → accept → release to main → next sprint
```

Time needed: about **half a day** for a medium sprint. The first part of this guide applies to every sprint;
the second part is the Sprint 03 checklist.

---

## Part 1 — The process (every sprint)

### Step 1. Get ready (15 minutes)

1. Make sure your PC is set up (`docs/ONBOARDING.md` Part 2) and you can run the tests.
2. Get the code:
   ```powershell
   cd D:\resort-os            # wherever you cloned it
   git switch develop
   git pull
   cd frontend; npm ci; cd ..
   ```
3. Open the sprint's **review pull request** on GitHub (`develop → main`, titled *Sprint NN review*). Its
   **Files changed** tab is exactly what the sprint adds. Sprint 03: **#102**.
4. Open the sprint's **peer review issue** (*SNN: Peer code review*) in the sprint's milestone. It has the checklist
   you'll tick. Assign it to yourself.
5. Read the sprint guide `docs/sprints/sprint-NN-*.md`: goal, the task list, required tests, acceptance.

### Step 2. Run everything yourself (30 minutes)

Never approve code you haven't run.

```powershell
pwsh -ExecutionPolicy Bypass -File scripts/db-reset.ps1     # clean database (answer y)
pwsh -ExecutionPolicy Bypass -File scripts/test.ps1         # backend + frontend tests: all must pass
cd backend/Resort.Api; dotnet watch run                     # terminal 1
cd frontend; npm start                                      # terminal 2
cd frontend; npm run e2e                                    # browser tests (own API and database): all must pass
node scripts/check-api.mjs                                  # API checks against your running API
```

Then do the owner's script in [REVIEWING.md](REVIEWING.md) for this sprint yourself, quickly. You're looking for
anything that feels wrong, not only for errors.

### Step 3. Read the code, task by task (2–3 hours)

Go through the sprint's tasks one at a time (Part 2 lists files and questions per task). For every task:

| Look at | Ask |
|---|---|
| **Database migration** | Is it a **new** `V###` file? Do the constraints match the rules in the sprint guide? Indexes for the queries the code makes? Every personal-data column classified? |
| **Domain + service** | Are business rules in the Application/Domain layer, not in endpoints or Angular? Every query scoped to the tenant (and property where relevant)? Concurrency (`version`) on edits? Nothing deleted that must be kept? |
| **Endpoint** | One permission per endpoint, the right one? Validator attached? Correct status codes? No internals in errors? |
| **Security** | Could a user of another organisation, or a role without the permission, read or change this? Any secret, real name or phone number committed? Anything sensitive logged or written to the audit trail? |
| **Frontend** | Loading, empty and error states? Works at 360–390 px? Buttons hidden when the user lacks the permission (the API still checks)? No business logic in components? |
| **Tests** | Does each required test in the sprint guide exist and actually check the rule (would it fail if the rule were removed)? Edge cases? |
| **Docs** | API.md, DATABASE.md / schema doc, SECURITY.md, GETTING_STARTED.md "What each sprint needs", CHANGELOG updated and correct? |

**How to comment.** On the review PR → **Files changed**, click the line → *Add a single comment* (or *Start a
review* to batch them). Start every comment with its weight:

| Prefix | Meaning | Must be fixed before acceptance? |
|---|---|---|
| **Blocker:** | Wrong behaviour, security or data-loss risk, failing test, broken build | Yes |
| **Should fix:** | Works but fragile, unclear, missing test or doc | Yes, or agreed to move to a later sprint (then it becomes an issue) |
| **Nit:** | Naming, wording, style | No — the author decides |
| **Question:** | You don't understand why something is done | Answer needed; may turn into one of the above |

### Step 4. Turn findings into issues (15 minutes)

For each **Blocker** and **Should fix**, open an issue so nothing is lost:

- Title: `S03: <what is wrong>` (use the sprint number)
- Labels: `type:bug` (or `type:chore`/`type:test`/`type:docs`), the `area:*` label, `priority:high` for blockers
- Milestone: the sprint being reviewed
- Body: file and line (paste the link from the PR), what happens, what should happen, how to reproduce
- Link it in your PR comment: `→ #123`

### Step 5. Give your verdict on the review PR

On the review PR → **Review changes**:

- **Request changes** if there is any open Blocker or Should fix. The summary lists the issue numbers.
- **Approve** when everything is fixed or agreed. Paste the filled-in checklist from the peer review issue as the
  summary, and close the peer review issue.

> Branch protection isn't on yet (needs GitHub Pro), so your approval isn't enforced by GitHub. The team rule is:
> the owner doesn't accept a sprint until the peer review issue is closed and the review PR is approved.

### Step 6. The fix loop

1. The author (or you, if agreed) fixes each issue on its own branch from `develop`:
   `fix/S03-123-short-name`, PR into `develop` with `Closes #123`, green CI, squash-merge.
2. Because the review PR is `develop → main`, it picks up the fixes automatically. Re-check the changed files
   (GitHub shows *"Changes since your last review"*).
3. When all are fixed: **Approve** (Step 5).

### Step 7. Hand over to the owner

Comment on the review PR: *"Peer review done — approved. Ready for the owner's review."* The owner then follows
[REVIEWING.md](REVIEWING.md) Part B/C and accepts or sends back.

### When is the sprint "done"?

All of these must be true. Whoever closes the sprint ticks them in the review PR description.

| # | Check | Where to see it |
|---|---|---|
| 1 | Every task issue of the sprint is closed | Milestone *Sprint NN* → 0 open task issues |
| 2 | All CI checks green on `develop` | Review PR → Checks |
| 3 | Peer review issue closed; review PR **approved** by the second developer | Review PR → Reviewers |
| 4 | No open `Blocker`/`Should fix` issues in the milestone (or moved to a later sprint by agreement) | Milestone |
| 5 | Owner's review script run and the sprint **accepted** (sign-off issue closed with "Accepted") | Sign-off issue *SNN: Review and sign-off by owner* |
| 6 | Release: review PR merged into `main` with a merge commit; tag `sprint-NN` exists | `main` history, Tags |
| 7 | Plan, changelog and handbooks updated (*Closing a sprint* in [PROCESS.md](PROCESS.md)) | `sprints.yml` status `done`; `docs/handbooks/` |

---

## Part 2 — Sprint 03 (Guests) checklist

Review PR: **#102**. Sprint guide: [sprint-03-guests.md](sprint-03-guests.md). Owner script:
[REVIEWING.md → Sprint 03](REVIEWING.md#sprint-03--guests-id-documents-privacy). Schema:
[schema-sprint-03.md](../database/schema-sprint-03.md). API: [API.md → Sprint 3](../API.md#built-endpoints-sprint-3--guests).

### Task 1 — DB: guest tables with a classification on every column (#85)

Files: `database/migrations/V005__guests.sql`

- [ ] New file `V005`; earlier migrations untouched (CI *Migrations* job green)
- [ ] Constraints match the schema doc: phone E.164, anonymised rows empty (`ck_guests_anon_empty`), merged ⇔ `merged_into_id`, Aadhaar never encrypted in full, purged documents empty
- [ ] Triggers: guests can't be deleted; `guest_consents` append-only except `guest_id` on merge
- [ ] Every column has `COMMENT ON COLUMN … 'class: …'` — run query 13 of `database/queries/check-sprint-03.sql` (expect 0 rows)
- [ ] Indexes fit the searches: trigram on `search_text` and `phone_digits`; `(organization_id, phone/email)`

### Task 2 — Field-level encryption and key rotation (#86)

Files: `backend/Resort.Infrastructure/Protection/FieldEncryption.cs`, `backend/Resort.Application/Common/Protection.cs`,
`docs/SECURITY.md` → *Field encryption and key rotation*, `backend/Resort.Api/Program.cs` (key checked at start-up)

- [ ] AES-256-GCM with a fresh random 12-byte nonce per encryption; tag verified on decrypt
- [ ] Separate derived keys for encryption and lookup hashes (HKDF); no key material logged or returned
- [ ] Associated data binds each ciphertext to its row and field (`guest_document:{id}:{field}`)
- [ ] Key id stored in the envelope and in `guest_documents.key_id`; previous keys still decrypt after rotation
- [ ] API refuses to start without a 32-byte key; the rotation steps in SECURITY.md are complete and correct
- [ ] Tests: `FieldEncryptionTests` (round trip, wrong row, tamper, rotation, missing key)

### Task 3 — File storage for ID scans (#87)

Files: `backend/Resort.Infrastructure/Protection/LocalDiskFileStore.cs`, `GuestDocumentService.cs` (upload),
`GuestEndpoints.cs` (multipart)

- [ ] Storage keys are generated by the API; path traversal impossible (`..`, absolute paths refused)
- [ ] Files saved encrypted; folder `App_Data` gitignored and outside any served folder
- [ ] File type decided from the bytes (JPEG/PNG/PDF), not the name or the client's content type; ≤ 5 MB enforced before reading fully
- [ ] If saving the row fails, the stored file is deleted (no orphans)
- [ ] Tests: `Only_jpeg_png_or_pdf_scans_up_to_5_MB_are_accepted`, `ID_number_and_scan_are_stored_encrypted…`

### Task 4 — Search, duplicate hint, merge with audit (#88)

Files: `backend/Resort.Application/Guests/GuestService.cs` (`ListAsync`, `PhoneDigitsOf`, `PossibleDuplicatesAsync`,
`CreateAsync`, `MergeAsync`), `backend/Resort.Domain/Guests/Guests.cs` (`GuestRules.NormalizePhone`)

- [ ] Phone normalisation: Indian local formats → `+91…`; international needs `+`/`00`; bad input is a 400 field error (try your own odd formats in `PhoneNormalisationTests`)
- [ ] Search uses `LIKE` with escaped `%`/`_` (no SQL injection, index usable); paging capped at 200
- [ ] Duplicate rules: same phone, same email, same full name, same ID document; `409 POSSIBLE_DUPLICATE` until `ignoreDuplicates`
- [ ] Merge: both versions checked; documents, consents, preferences, missing address move; duplicate hidden; earlier merges re-pointed; reason in the audit trail
- [ ] Guests are organisation-wide: `PermissionScope.Any` is right for these endpoints (a receptionist limited to one property still sees all guests) — agree or raise it
- [ ] Tests: `Reception_finds_a_returning_guest…`, `Creating_a_likely_duplicate…`, `Merging_moves…`

### Task 5 — Anonymise (#89)

Files: `GuestService.AnonymiseAsync`, `backend/Resort.Infrastructure/Persistence/AuditInterceptor.cs` (masking of
`[PersonalData]` and `byte[]`), `Guests.cs` (`[PersonalData]` attributes, `GuestDocument.Purge`)

- [ ] Needs `guest.delete` **organisation-wide**; reason required and stored
- [ ] Every personal field cleared, on the profile **and** on profiles merged into it; addresses and preferences deleted; documents purged; scan files deleted **after** the commit
- [ ] Audit rows never contain personal values (run query 11 of the SQL checks: expect 0 rows) — check no personal field is missing its `[PersonalData]` attribute
- [ ] What is kept and why (nationality, consents, the row itself) is acceptable — agree or raise it
- [ ] Tests: `Anonymising_removes_personal_data…`, `Edits_need_the_current_version_and_personal_data_is_masked…`

### Task 6 — Retention dry run (#90)

Files: `GuestDocumentService.RetentionDryRunAsync`, `GuestOptions` (`Guests:DocumentRetentionDays`, default 365)

- [ ] Read-only: deletes nothing; needs `guest.delete` org-wide
- [ ] The rule (age since upload, until reservations exist in Sprint 05) matches the sprint guide; purge waits for owner decision #54
- [ ] Test: `Retention_dry_run_lists_old_documents_and_deletes_nothing`

### Task 7 — Dev seed: 20 guests (#91)

Files: `database/seed/dev/S002__guests.sql`, `backend/Resort.Tests/Integration/DevSeedTests.cs`

- [ ] Runs only in Development; idempotent (skips if the organisation has guests)
- [ ] All data invented: phone numbers fictional, emails on `example.com`/`example.in`, no real person
- [ ] Values pass every constraint (the seed runs on each API start in CI's browser tests)

### Task 8 — UI: list/search, profile, documents only with permission (#92)

Files: `frontend/src/app/features/guests/*`, `app.routes.ts`, `layout/shell.ts`, `features/audit/audit.page.ts`

- [ ] Routes guarded with the right permissions; *Guests* menu item only with `guest.read`
- [ ] Documents section shown only when the API returns `documentCount` (i.e. the user has `guest_document.read`); *Anonymise* only for org-wide `guest.delete`
- [ ] Duplicate warning flow; concurrency message on a stale edit; loading/empty/error states
- [ ] Scans opened from a blob (no shareable URL); object URLs released
- [ ] Phone width: list and profile usable at 390 px (`docs/sprints/sprint-03-screens/12-phone-profile.png`)
- [ ] Tests: `guests.forms.spec.ts`; browser tests `e2e/sprint-03.spec.ts` (S03-01…S03-13)

### Across the sprint

- [ ] `scripts/test.ps1`: backend **102 passed**, frontend **20 of 20**
- [ ] `npm run e2e`: **39 passed**; `node scripts/check-api.mjs`: all PASS
- [ ] `database/queries/check-sprint-03.sql`: queries 4, 6, 7, 11 and 13 return 0 rows
- [ ] No secrets, keys, real names or phone numbers in the diff (CI *Security* job green, and your own read)
- [ ] Docs correct: API.md Sprint 3, schema-sprint-03.md, SECURITY.md, GETTING_STARTED.md *Sprint 03 in detail*, CHANGELOG
