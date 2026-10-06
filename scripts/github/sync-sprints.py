#!/usr/bin/env python3
"""
Sprint plan → docs and GitHub.

  python3 scripts/github/sync-sprints.py docs                 # regenerate docs/sprints/*.md from sprints.yml
  python3 scripts/github/sync-sprints.py github OWNER/REPO    # create/update labels, milestones, sprint epics
  python3 scripts/github/sync-sprints.py tasks OWNER/REPO N   # create one issue per task of sprint N

GitHub calls go through the `gh` CLI (must be authenticated with repo scope). The script is idempotent:
labels/milestones/issues are matched by name/title and updated rather than duplicated.

Requires: Python 3.9+, PyYAML (pip install pyyaml).
"""
import json
import pathlib
import subprocess
import sys

import yaml

ROOT = pathlib.Path(__file__).resolve().parents[2]
PLAN = ROOT / "docs" / "sprints" / "sprints.yml"
OUT = ROOT / "docs" / "sprints"

PHASES = {
    0: "Phase 0 — Requirements & architecture",
    1: "Phase 1 — Foundation, property, rooms, beds",
    2: "Phase 2 — Guests, reservations, availability, calendar, front desk",
    3: "Phase 3 — Folio, payments, Razorpay, GST, invoices",
    4: "Phase 4 — Housekeeping, maintenance",
    5: "Phase 5 — Restaurant, KOT, menu, inventory, purchasing",
    6: "Phase 6 — Employees, attendance, timesheets, leave, roster",
    7: "Phase 7 — Activities, transport, equipment",
    8: "Phase 8 — Expenses, budgets, reports",
    9: "Phase 9 — Notifications, WhatsApp, Zoho Books, OTA, booking site",
    10: "Phase 10 — Hardening, backup, monitoring, deployment",
}
STATUS = {"planned": "⚪ Planned", "in-progress": "🔵 In progress", "in-review": "🟡 In review", "done": "🟢 Done"}

LABELS = {
    # type
    "type:feature": ("1d76db", "New capability"),
    "type:bug": ("d73a4a", "Something is broken"),
    "type:chore": ("cfd3d7", "Tooling, refactor, dependencies"),
    "type:docs": ("0075ca", "Documentation"),
    "type:test": ("5319e7", "Tests only"),
    "type:security": ("b60205", "Security-relevant change"),
    "epic": ("3e4b9e", "Sprint epic — tracks every task in the sprint"),
    # layer
    "area:db": ("fbca04", "Schema, migrations, seed"),
    "area:api": ("0e8a16", "Backend / API"),
    "area:web": ("c2e0c6", "Angular frontend"),
    "area:infra": ("bfdadc", "CI/CD, Docker, hosting"),
    "area:integration": ("f9d0c4", "External systems (Razorpay, Zoho, WhatsApp, OTA)"),
    # priority / flow
    "priority:high": ("b60205", "Blocks the sprint goal"),
    "priority:normal": ("fef2c0", ""),
    "status:blocked": ("000000", "Waiting on something outside the team"),
    "needs:owner-decision": ("ff7619", "Product owner must decide"),
    "good first issue": ("7057ff", "Small, well-defined starter task"),
}


def load():
    with open(PLAN, encoding="utf-8") as f:
        return yaml.safe_load(f)


def slug(title):
    keep = "".join(c.lower() if c.isalnum() else "-" for c in title.split("—")[0])
    words, out = [p for p in keep.split("-") if p], []
    for w in words:
        if len("-".join(out + [w])) > 40:
            break
        out.append(w)
    return "-".join(out)


def fname(s):
    return f"sprint-{s['id']:02d}-{slug(s['title'])}.md"


def milestone_title(s):
    return f"Sprint {s['id']:02d} — {s['title']}"


def bullet(items, code=False):
    if not items:
        return "_None._\n"
    return "".join(f"- `{i}`\n" if code else f"- {i}\n" for i in items)


def render(s, plan):
    sc = s.get("scope", {}) or {}
    deps = ", ".join(f"[Sprint {d:02d}]({fname(next(x for x in plan['sprints'] if x['id'] == d))})" for d in s.get("depends", [])) or "—"
    current = " — **current sprint**" if s["id"] == plan["current"] else ""
    md = [f"# Sprint {s['id']:02d} — {s['title']}\n\n",
          f"| Status | Phase | Size | Depends on |\n|---|---|---|---|\n"
          f"| {STATUS[s['status']]}{current} | {PHASES[s['phase']]} | {s['size']} | {deps} |\n\n",
          f"**Goal:** {s['goal']}\n\n",
          f"**GitHub:** milestone *{milestone_title(s)}* · branch prefix `feature/S{s['id']:02d}-`\n\n",
          "## Scope\n\n"]
    for key, label, code in [("tables", "Tables", True), ("migrations", "Migrations", True), ("api", "API endpoints", True),
                             ("screens", "Screens / routes", True), ("external", "External APIs used", False),
                             ("docs", "Documents", True), ("other", "Other", False)]:
        if sc.get(key):
            md.append(f"**{label}**\n\n{bullet(sc[key], code)}\n")
    md.append("Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.\n")

    md.append("\n## Tasks\n\nEach task becomes a GitHub issue under this sprint's milestone.\n\n")
    for t in s.get("tasks", []):
        done = t.startswith("[done]")
        md.append(f"- [{'x' if done else ' '}] {t.replace('[done] ', '')}\n")

    md.append(f"""
## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S{s['id']:02d}-<issue-number>-<short-name>`
3. **Database up:** `docker compose --env-file .env -f infra/docker/docker-compose.yml up -d db`
4. **Schema changes:** add the next `database/migrations/V###__<name>.sql` (never edit an applied one). The API applies it on start.
5. **API:** `cd backend/Resort.Api && dotnet watch run` → http://localhost:5180/swagger
6. **Web:** `cd frontend && npm start` → http://localhost:4300
7. **Fresh data:** `scripts/db-reset.sh`, then restart the API (migrations + bootstrap owner run again; dev seed data from Sprint 2 onward)

## How to debug

- **API:** run the `Resort.Api` launch profile in VS Code (C# Dev Kit), Rider or Visual Studio and set breakpoints; Swagger's *Authorize* button takes the `accessToken` from `POST /api/v1/auth/login`.
- **Every error response has a `correlationId`.** Search the API console/log for it to find the full exception.
- **SQL:** set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information` in `.env` to log every SQL statement.
- **Database:** `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U $POSTGRES_USER $POSTGRES_DB`
- **Web:** Angular DevTools + the browser Network tab; API errors are `{{code, message, details, correlationId}}`.
""")
    if s.get("debug"):
        md.append("\nSprint-specific:\n\n")
    for d in s.get("debug", []):
        md.append(f"- {d}\n")

    md.append("\n## How to test\n\nRequired tests for this sprint (the sprint is not done without them):\n\n")
    md.append(bullet(s.get("tests", [])))
    md.append("""
Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

""")
    md.append(bullet(s.get("acceptance", [])))
    md.append("\n## Sprint review demo\n\n")
    md.append(bullet(s.get("demo", [])))
    md.append("""
## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-NN` created on `main`
""".replace("sprint-NN", f"sprint-{s['id']:02d}"))
    return "".join(md)


def render_index(plan):
    cur = next(s for s in plan["sprints"] if s["id"] == plan["current"])
    rows = "".join(
        f"| {s['id']:02d} | [{s['title']}]({fname(s)}) | {PHASES[s['phase']].split(' — ')[0]} | {s['size']} | {STATUS[s['status']]} |\n"
        for s in plan["sprints"])
    return f"""# Sprints

> **Current sprint: [Sprint {cur['id']:02d} — {cur['title']}]({fname(cur)})** · status: {STATUS[cur['status']]}
>
> Change the current sprint by editing `current:` in [sprints.yml](sprints.yml) and running
> `python3 scripts/github/sync-sprints.py docs`.

How we work: [PROCESS.md](PROCESS.md) (cadence, ceremonies, branching, Definition of Ready/Done).

| # | Sprint | Phase | Size | Status |
|---|---|---|---|---|
{rows}
Sizes assume a 2-week sprint with two developers: S ≈ 1 dev-week, M ≈ 2, L ≈ 3–4. Sprints that don't depend on each other
(for example 13 Housekeeping and 17 Employees) can run in parallel with a bigger team.

Phases follow brief §81. Every sprint guide lists its scope, tasks, how to run/debug/test it, acceptance criteria and
the review demo.
"""


def render_tables(plan):
    rows = "".join(f"| `{t}` | [Sprint {s['id']:02d}](../sprints/{fname(s)}) | {STATUS[s['status']]} |\n"
                   for s in plan["sprints"] for t in (s.get("scope") or {}).get("tables", []))
    n = sum(len((s.get("scope") or {}).get("tables", [])) for s in plan["sprints"])
    return ("# Tables by sprint\n\n_Generated from docs/sprints/sprints.yml — do not edit by hand._\n\n"
            f"{n} tables planned. Column-level definitions are written into [../DATABASE.md](../DATABASE.md) by the sprint that creates them.\n\n"
            "| Table | Sprint | Status |\n|---|---|---|\n" + rows)


def render_endpoints(plan):
    rows = "".join(f"| `{e}` | [Sprint {s['id']:02d}](../sprints/{fname(s)}) | {STATUS[s['status']]} |\n"
                   for s in plan["sprints"] for e in (s.get("scope") or {}).get("api", []))
    return ("# Endpoints by sprint\n\n_Generated from docs/sprints/sprints.yml — do not edit by hand._\n\n"
            "Full specifications (permission, request/response, errors, idempotency, audit) are in [../API.md](../API.md) once built.\n\n"
            "| Endpoint | Sprint | Status |\n|---|---|---|\n" + rows)


def gh(*args, input=None):
    r = subprocess.run(["gh", *args], input=input, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"gh {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout


def sync_github(repo, plan):
    existing = {l["name"] for l in json.loads(gh("api", f"repos/{repo}/labels?per_page=100"))}
    for name, (color, desc) in LABELS.items():
        if name in existing:
            gh("api", "-X", "PATCH", f"repos/{repo}/labels/{name.replace(' ', '%20').replace(':', '%3A')}", "-f", f"color={color}", "-f", f"description={desc}")
        else:
            gh("api", "-X", "POST", f"repos/{repo}/labels", "-f", f"name={name}", "-f", f"color={color}", "-f", f"description={desc}")
    print(f"labels: {len(LABELS)}")

    milestones = {m["title"]: m for m in json.loads(gh("api", f"repos/{repo}/milestones?state=all&per_page=100"))}
    issues = {i["title"]: i for i in json.loads(gh("api", f"repos/{repo}/issues?state=all&per_page=100&labels=epic"))}
    for s in plan["sprints"]:
        title = milestone_title(s)
        state = "closed" if s["status"] == "done" else "open"
        desc = f"{s['goal']} Guide: docs/sprints/{fname(s)}"
        if title in milestones:
            num = milestones[title]["number"]
            gh("api", "-X", "PATCH", f"repos/{repo}/milestones/{num}", "-f", f"state={state}", "-f", f"description={desc}")
        else:
            num = json.loads(gh("api", "-X", "POST", f"repos/{repo}/milestones", "-f", f"title={title}", "-f", f"state={state}", "-f", f"description={desc}"))["number"]
        epic_title = f"[Epic] {title}"
        body = (f"**Goal:** {s['goal']}\n\n**Guide:** [docs/sprints/{fname(s)}](../blob/develop/docs/sprints/{fname(s)})\n\n"
                "### Tasks\n" + "".join(f"- [{'x' if t.startswith('[done]') else ' '}] {t.replace('[done] ', '')}\n" for t in s.get("tasks", []))
                + "\n### Acceptance\n" + "".join(f"- {a}\n" for a in s.get("acceptance", [])))
        payload = json.dumps({"title": epic_title, "body": body, "milestone": num, "labels": ["epic"]})
        if epic_title in issues:
            gh("api", "-X", "PATCH", f"repos/{repo}/issues/{issues[epic_title]['number']}", "--input", "-", input=payload)
        else:
            gh("api", "-X", "POST", f"repos/{repo}/issues", "--input", "-", input=payload)
        print(f"{title}: milestone #{num} ({state})")


def sync_tasks(repo, plan, sprint_id):
    s = next(x for x in plan["sprints"] if x["id"] == sprint_id)
    ms = {m["title"]: m["number"] for m in json.loads(gh("api", f"repos/{repo}/milestones?state=all&per_page=100"))}
    num = ms[milestone_title(s)]
    existing = {i["title"] for i in json.loads(gh("api", f"repos/{repo}/issues?state=all&per_page=100&milestone={num}"))}
    for t in s.get("tasks", []):
        if t.startswith("[done]"):
            continue
        area = ("area:db" if t.startswith("DB") else "area:web" if t.startswith("UI") else
                "area:integration" if any(k in t for k in ("Razorpay", "Zoho", "WhatsApp", "OTA", "IChannel")) else
                "type:docs" if t.startswith("Docs") else "area:api")
        title = f"S{sprint_id:02d}: {t.split(' — ')[0].split(';')[0][:90]}"
        if title in existing:
            continue
        body = (f"Part of **{milestone_title(s)}**.\n\n### What\n{t}\n\n### Guide\n"
                f"[docs/sprints/{fname(s)}](../blob/develop/docs/sprints/{fname(s)})\n\n"
                "### Done when\n- [ ] Code + tests merged via PR\n- [ ] Docs updated (API.md / DATABASE.md)\n- [ ] Audit + permission checks in place\n")
        payload = json.dumps({"title": title, "body": body, "milestone": num, "labels": ["type:feature", area]})
        gh("api", "-X", "POST", f"repos/{repo}/issues", "--input", "-", input=payload)
        print("created:", title)


def main():
    plan = load()
    if len(sys.argv) < 2 or sys.argv[1] not in ("docs", "github", "tasks"):
        print(__doc__)
        sys.exit(1)
    if sys.argv[1] == "docs":
        for old in OUT.glob("sprint-*.md"):
            old.unlink()
        for s in plan["sprints"]:
            (OUT / fname(s)).write_text(render(s, plan), encoding="utf-8")
        (OUT / "README.md").write_text(render_index(plan), encoding="utf-8")
        (ROOT / "docs" / "database" / "tables-by-sprint.md").write_text(render_tables(plan), encoding="utf-8")
        (ROOT / "docs" / "api" / "endpoints-by-sprint.md").write_text(render_endpoints(plan), encoding="utf-8")
        print(f"wrote {len(plan['sprints'])} sprint guides + README.md + table/endpoint catalogues")
    elif sys.argv[1] == "github":
        sync_github(sys.argv[2], plan)
    else:
        sync_tasks(sys.argv[2], plan, int(sys.argv[3]))


if __name__ == "__main__":
    main()
