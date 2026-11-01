# Payroll export

Resort OS does not run payroll. It keeps who worked when (attendance and timesheets, Sprint 18) and who was on leave
(Sprint 19), and once a month hands the payroll provider a file with each person's days and hours. The provider holds the
salaries, statutory deductions (PF, ESI, professional tax, TDS) and payslips.

The provider is not chosen yet ([risks-and-open-questions.md → Q3](architecture/risks-and-open-questions.md)), so the file
is a documented generic CSV that any provider, or the accountant's spreadsheet, can import. A provider's own format can
be added later as another `format` next to `GENERIC_CSV_V1`, from the same rows.

## Making the file

**Payroll export** (`/payroll`, permission `payroll.export`: Owner and HR) → choose a month → the preview shows the rows
the file will hold and what isn't ready yet:

| Warning | What it means | What to do |
|---|---|---|
| *October 2026 hasn't ended* | The month is still running: its remaining days count as employed, with no hours yet | Wait for the 1st, or confirm |
| *…, week of Mon 19 Oct: the timesheet is waiting for approval / still a draft / sent back* | Only **approved** and **locked** timesheets count; this week's hours are left out | Approve it in **Timesheets** |
| *…, week of Mon 26 Oct: no timesheet yet* | Someone clocked in or had a shift that started, but no timesheet exists for that week | Open **Timesheets** for that week, submit and approve |
| *…: Casual leave LV00002 on … isn't decided yet* | Leave still waiting is left out (neither paid nor unpaid) | Approve or refuse it in **Leave → Team** |

With no warnings, **Make the file** makes it straight away. With warnings, tick **Make the file anyway** first; the API
refuses an unconfirmed request with `422 PAYROLL_NOT_READY` listing them. The warnings are saved with the file.

Making the file:

- **locks** every approved timesheet with days in the month (Sprint 18's *Locked for payroll*: it can't be corrected
  after that), so the hours in the file can't change under it;
- keeps the file in `payroll_exports` (append-only) with its SHA-256, who made it and when; **Files made** lists every file
  to download again. A month can be exported again (for example after approving a late timesheet); the newest is the
  one to send.

File name: `payroll-{property code in lower case}-{yyyy-MM}-{yyyyMMdd-HHmm}.csv`, e.g.
`payroll-beach01-2026-10-20261101-0930.csv` (the time is the property's local time).

## Format `GENERIC_CSV_V1`

UTF-8 without a byte order mark, comma separated, CRLF line ends, one header line, then one line per person employed for
at least one day of the month (joined on or before the last day, not left before the first), ordered by department and
employee code. Numbers use a dot for decimals and no thousands separator. A text cell with a comma, quote or line break is
quoted with `"` (quotes doubled); a text cell starting with `=`, `+`, `-` or `@` gets a leading `'` so a spreadsheet
never runs it as a formula.

| # | Column | Type | Meaning |
|---|---|---|---|
| 1 | `employee_code` | text | `E00001` … |
| 2 | `employee_name` | text | First and last name |
| 3 | `department` | text | Department name |
| 4 | `employment_type` | text | `FULL_TIME`, `PART_TIME`, `CONTRACT` or `SEASONAL` |
| 5 | `period_start` | date | The 1st of the month (`yyyy-MM-dd`) |
| 6 | `period_end` | date | The last day of the month |
| 7 | `employed_days` | integer | Days of the month between joining and leaving (all of them for most people) |
| 8 | `weekly_offs` | integer | Of those, the person's weekly day off (not counting holidays) |
| 9 | `holidays` | integer | Of those, the property's holidays |
| 10 | `days_worked` | integer | Days with hours on an approved or locked timesheet |
| 11 | `worked_hours` | decimal (2) | Hours worked on those days, breaks taken off (`7.62` = 7 h 37 min) |
| 12 | `overtime_hours` | decimal (2) | Of those, hours past the shift's paid time (Sprint 18 rules) |
| 13 | `late_minutes` | integer | Minutes late in the month |
| 14 | `paid_leave_days` | decimal (1) | Approved paid leave in the month (CL, SL, EL…); half days count 0.5 |
| 15 | `unpaid_leave_days` | decimal (1) | Approved unpaid leave in the month (LOP) |
| 16 | `absent_days` | integer | Rostered days marked *Not in* on an approved timesheet |
| 17 | `payable_days` | decimal (1) | `employed_days − unpaid_leave_days − absent_days` (never below 0) |
| 18 | `leave_detail` | text | Approved leave by kind, e.g. `CL 1.5; SL 1` |

Leave days are the days counted when the leave was asked for (holidays and the weekly day off are never counted), so a
leave across a month end is split between the two months by its own days.

Example (header and two lines):

```csv
employee_code,employee_name,department,employment_type,period_start,period_end,employed_days,weekly_offs,holidays,days_worked,worked_hours,overtime_hours,late_minutes,paid_leave_days,unpaid_leave_days,absent_days,payable_days,leave_detail
E00001,Asha Naik,Front office,FULL_TIME,2026-10-01,2026-10-31,31,4,1,24,186.50,2.00,12,1.5,0,0,31,CL 1.5
E00007,Ravi Kamat,Kitchen,FULL_TIME,2026-10-01,2026-10-31,31,4,1,23,190.25,6.50,0,0,1,1,29,LOP 1
```

## Checking a file

- `database/queries/check-sprint-19.sql` block 15 compares each stored file with its SHA-256; block 16 lists approved
  timesheets a file should have locked.
- To check a downloaded file: `Get-FileHash payroll-….csv -Algorithm SHA256` (PowerShell) and compare with the first 16
  characters shown under **Files made**.

## Not in this format (yet)

- Pay amounts, arrears, bonuses, advances and deductions: the provider's job.
- Leave encashment and year-end lapse or carry-forward: not handled yet; balances carry on, capped by each policy's
  maximum.
- An API push to a provider: once Q3 is decided, an adapter can send the same rows (same pattern as the other
  integrations in [INTEGRATIONS.md](INTEGRATIONS.md)).
