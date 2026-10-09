# Handbook builder

Builds the two Word documents in [`docs/handbooks/`](../../docs/handbooks/README.md) from the repository:

- **Resort OS — Product Overview** (for clients) → `Resort-OS-Product-Overview.docx`
- **Resort OS — Software Documentation** (technical) → `Resort-OS-Software-Documentation.docx`

## Run it

Needs Node.js 20+ (already installed for the frontend).

```powershell
cd tools/handbooks
npm ci            # once
npm run build     # writes both .docx files to docs/handbooks/
```

Optional: `$env:DOC_DATE="2026-10-24"` before `npm run build` prints that date on the cover instead of the date in
`history.yml`.

## Where the content comes from

| Source | Used for |
|---|---|
| `docs/sprints/sprints.yml` | Sprint status, goals, scope (tables, endpoints, screens), done lists, files, tests, results, acceptance, debugging notes; `client:` (one plain sentence per sprint); `handbook:` (highlights, UI/API/database design notes, screenshots, diagrams) |
| `database/migrations/*.sql` | Tables per migration; roles and permissions (V002) |
| `docs/api/openapi-v1.json` | Endpoint catalogue |
| `backend/`, `frontend/`, `database/`, `docs/` | Code size and test counts |
| `Directory.Packages.props`, `frontend/package.json`, `global.json` | Technology versions |
| `.github/workflows/ci.yml` | CI jobs |
| `docs/ARCHITECTURE.md` | Module map (status recomputed from the plan) |
| `history.yml` | Edition number and the document-control table |
| `content/client.mjs`, `content/technical.mjs` | The written text and section order |
| `diagrams/diagrams.html` → `assets/*.png` | Diagrams (rendered with `npm run diagrams`, which uses the frontend's Playwright) |
| `lib/kit.mjs` | House style: fonts, colours, tables, cover, header and footer |

## At the end of each sprint

Follow *Closing a sprint* in [docs/sprints/PROCESS.md](../../docs/sprints/PROCESS.md). For the handbooks that means:

1. Fill the sprint's `handbook:` block in `sprints.yml` (see Sprints 01–02 for examples) and set its status to `done`.
2. Add a line to `history.yml`: version `1.<sprint number>`, the date, one sentence on what changed.
3. If the capability map or architecture changed (a module started or finished, a new part went live), edit
   `diagrams/diagrams.html` and run `npm run diagrams`.
4. `npm run build`, open both files, check them, commit them with the sprint-closing pull request.

## Checking the output

Open the files in Word (or LibreOffice). On a machine with LibreOffice you can also render them to PDF:
`soffice --headless --convert-to pdf ../../docs/handbooks/*.docx`.
