using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 17 — employees and roster: pay readable only with employee_salary.read (a MANAGER can't), overlapping
/// shifts refused (by the service and by the EXCLUDE constraint, also when two managers save at the same moment), the
/// week's shortage view and warnings, copying a week, staff account links and the employee's own shifts, leaving, and
/// encrypted documents. Each test uses a property of its own. Real API, real PostgreSQL.</summary>
public class EmployeesTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    // ------------------------------------------------------------------ required: MANAGER cannot read salaries

    [Fact]
    public async Task A_manager_sees_the_profile_and_the_roster_but_never_the_pay_and_the_audit_trail_hides_the_amounts()
    {
        var (owner, pid) = await PropertyAsync();
        var hr = await StaffAsync("HR", pid);
        var manager = await StaffAsync("MANAGER", pid);
        var gm = await StaffAsync("GENERAL_MANAGER", pid);
        var kitchen = await DepartmentAsync(owner, pid, "KITCHEN", "Kitchen");
        var asha = await EmployeeAsync(hr, pid, kitchen.Id, "Asha", "Naik");

        var pay = await OkAsync<EmployeeCompensationDto>(await hr.PostAsync($"{EmpUrl(pid, asha.Id)}/compensation",
            new RecordCompensationRequest(asha.JoinedOn, "MONTHLY", 18000m, 3000m, true, true, "Offer letter")), HttpStatusCode.Created);
        Assert.Equal((18000m, 3000m, 21000m, true), (pay.Current!.Basic, pay.Current.Allowances, pay.Current.Gross, pay.Current.IsCurrent));

        // The manager reads the profile (no pay in it) and the roster, but the pay endpoint is closed to them, and to the GM.
        var seen = await OkAsync<EmployeeDto>(await manager.GetAsync(EmpUrl(pid, asha.Id)));
        Assert.Equal("Asha Naik", seen.FullName);
        Assert.DoesNotContain("18000", await (await manager.GetAsync(EmpUrl(pid, asha.Id))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Url(pid)}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{EmpUrl(pid, asha.Id)}/compensation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gm.GetAsync($"{EmpUrl(pid, asha.Id)}/compensation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"{EmpUrl(pid, asha.Id)}/compensation",
            new RecordCompensationRequest(asha.JoinedOn, "MONTHLY", 99000m, 0m, false, false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{EmpUrl(pid, asha.Id)}/compensation")).StatusCode);

        // A raise next month and a correction of it: both kept, the newest for a date wins, and today's pay is unchanged.
        var next = Today.AddDays(30);
        await OkAsync<EmployeeCompensationDto>(await hr.PostAsync($"{EmpUrl(pid, asha.Id)}/compensation",
            new RecordCompensationRequest(next, "MONTHLY", 20000m, 3000m, true, true, null)), HttpStatusCode.Created);
        pay = await OkAsync<EmployeeCompensationDto>(await hr.PostAsync($"{EmpUrl(pid, asha.Id)}/compensation",
            new RecordCompensationRequest(next, "MONTHLY", 19500m, 3000m, true, true, "Typo")), HttpStatusCode.Created);
        Assert.Equal(18000m, pay.Current!.Basic);
        Assert.Equal([(19500m, false), (20000m, true), (18000m, false)], pay.History.Select(h => (h.Basic, h.Replaced)));
        Assert.Equal($"Asha Naik joined on {Day(asha.JoinedOn)}; pay can start on that day or later.",
            await FieldErrorAsync(await hr.PostAsync($"{EmpUrl(pid, asha.Id)}/compensation",
                new RecordCompensationRequest(asha.JoinedOn.AddDays(-1), "MONTHLY", 18000m, 0m, false, false, null)), "EffectiveFrom"));

        // The audit trail records each entry with the amounts masked; pay rows can't be changed in the database.
        var changes = (string)(await ScalarAsync($"SELECT string_agg(changes::text, ' ') FROM audit_logs WHERE entity_type = 'EmployeeCompensation' AND changes::text LIKE '%{asha.Id}%'"))!;
        Assert.Contains("\"Basic\": {\"new\": \"***\"}", changes);
        Assert.DoesNotContain("18000", changes);
        Assert.DoesNotContain("19500", changes);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE employee_compensation SET basic = 1 WHERE employee_id = '{asha.Id}'"));
        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ required: overlapping shift rejected

    [Fact]
    public async Task An_overlapping_shift_is_refused_naming_the_other_one_unless_a_manager_allows_it_with_a_reason()
    {
        var (owner, pid) = await PropertyAsync();
        var manager = await StaffAsync("MANAGER", pid);
        var kitchen = await DepartmentAsync(owner, pid, "KITCHEN", "Kitchen");
        var morning = await TemplateAsync(manager, pid, kitchen.Id, "KIT-AM", "Breakfast", new TimeOnly(6, 0), new TimeOnly(14, 0), 1);
        var dinner = await TemplateAsync(manager, pid, kitchen.Id, "KIT-PM", "Dinner", new TimeOnly(13, 0), new TimeOnly(22, 0), 1);
        var ravi = await EmployeeAsync(owner, pid, kitchen.Id, "Ravi", "Kamat");
        var day = Today.AddDays(3);

        var first = await ShiftAsync(manager, pid, new SaveRosterShiftRequest(ravi.Id, day, morning.Id, null, null, null, null));
        Assert.Equal((new TimeOnly(6, 0), new TimeOnly(14, 0), 450, "Breakfast"), (first.StartTime, first.EndTime, first.PaidMinutes, first.TemplateName));

        // Dinner from 13:00 overlaps breakfast until 14:00: refused, naming the shift it clashes with.
        var res = await manager.PostAsync($"{Url(pid)}/roster/shifts", new SaveRosterShiftRequest(ravi.Id, day, dinner.Id, null, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var (code, message) = await FrontDeskTests.ProblemAsync(res);
        Assert.Equal("SHIFT_CLASH", code);
        Assert.Equal($"Ravi Kamat is already on Kitchen · Breakfast 06:00–14:00 on {Day(day)}. One person can't work two shifts at once: choose another " +
                     "time or another person, or allow the overlap with a reason (e.g. a handover).", message);

        // Allowing it needs a reason; with one it is saved and shown as a warning on the week.
        Assert.Equal("Say why Ravi works two shifts at once, e.g. a handover (3–200 characters).",
            await FieldErrorAsync(await manager.PostAsync($"{Url(pid)}/roster/shifts",
                new SaveRosterShiftRequest(ravi.Id, day, dinner.Id, null, null, null, null, AllowOverlap: true)), "OverlapReason"));
        var allowed = await ShiftAsync(manager, pid, new SaveRosterShiftRequest(ravi.Id, day, dinner.Id, null, null, null, null, true, "Handover to the dinner chef"));
        Assert.True(allowed.OverlapAllowed);
        var week = await manager.GetJsonAsync<RosterWeekDto>($"{Url(pid)}/roster?week={day:yyyy-MM-dd}");
        var warning = Assert.Single(week.Warnings, w => w.Kind == "OVERLAP_ALLOWED");
        Assert.EndsWith("(13:00–22:00): Handover to the dinner chef", warning.Message);

        // A shift that doesn't overlap (moving dinner to 14:00) clears the allowance.
        var moved = await OkAsync<RosterShiftDto>(await manager.PutAsync($"{Url(pid)}/roster/shifts/{allowed.Id}",
            new SaveRosterShiftRequest(ravi.Id, day, dinner.Id, null, new TimeOnly(14, 0), null, null, Version: allowed.Version)));
        Assert.Equal((false, (string?)null), (moved.OverlapAllowed, moved.OverlapReason));

        // The database refuses an overlapping row on its own (the final guard).
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO roster_shifts (organization_id, property_id, employee_id, department_id, work_date, starts_at, ends_at) " +
            $"SELECT organization_id, property_id, employee_id, department_id, work_date, starts_at + interval '1 hour', ends_at FROM roster_shifts WHERE id = '{first.Id}'"));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
        Assert.Equal("ex_roster_shifts_clash", ex.ConstraintName);
    }

    [Fact]
    public async Task Two_managers_putting_one_person_on_overlapping_shifts_at_the_same_moment_exactly_one_succeeds_10_times()
    {
        var (owner, pid) = await PropertyAsync();
        var a = await StaffAsync("MANAGER", pid);
        var b = await StaffAsync("HR", pid);
        var dept = await DepartmentAsync(owner, pid, "HK", "Housekeeping");
        var maria = await EmployeeAsync(owner, pid, dept.Id, "Maria", "Fernandes");

        for (var run = 0; run < 10; run++)
        {
            var day = Today.AddDays(1 + run);
            using var barrier = new Barrier(2);
            var x = Task.Run(() => { barrier.SignalAndWait(); return a.PostAsync($"{Url(pid)}/roster/shifts", Custom(maria.Id, day, dept.Id, 8, 17)); });
            var y = Task.Run(() => { barrier.SignalAndWait(); return b.PostAsync($"{Url(pid)}/roster/shifts", Custom(maria.Id, day, dept.Id, 12, 20)); });
            var results = await Task.WhenAll(x, y);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("SHIFT_CLASH", (await FrontDeskTests.ProblemAsync(loser)).Code);
        }
        Assert.Equal(10L, await ScalarAsync($"SELECT count(*) FROM roster_shifts WHERE employee_id = '{maria.Id}' AND status = 'SCHEDULED'"));
    }

    // ------------------------------------------------------------------ acceptance: next week's roster with shortages

    [Fact]
    public async Task A_manager_builds_next_weeks_roster_and_the_shortages_and_warnings_show_what_is_missing()
    {
        var (owner, pid) = await PropertyAsync();
        var manager = await StaffAsync("MANAGER", pid);
        var front = await DepartmentAsync(owner, pid, "FRONT", "Front office");
        var hk = await DepartmentAsync(owner, pid, "HK", "Housekeeping");
        var am = await TemplateAsync(manager, pid, front.Id, "FO-AM", "Morning", new TimeOnly(7, 0), new TimeOnly(15, 0), 1);
        var night = await TemplateAsync(manager, pid, front.Id, "FO-NIGHT", "Night", new TimeOnly(23, 0), new TimeOnly(7, 0), 0);
        var hkDay = await TemplateAsync(manager, pid, hk.Id, "HK-DAY", "Day", new TimeOnly(8, 0), new TimeOnly(17, 0), 2, breakMinutes: 60);
        Assert.Equal((true, 450), (night.CrossesMidnight, night.PaidMinutes));
        var asha = await EmployeeAsync(owner, pid, front.Id, "Asha", "Naik", weeklyOff: 2);
        var sunil = await EmployeeAsync(owner, pid, hk.Id, "Sunil", "Gawas");
        var pooja = await EmployeeAsync(owner, pid, hk.Id, "Pooja", "Shetye");

        var monday = NextMonday();
        // Asha mornings Monday to Saturday (Tuesday is her day off: a warning, not a refusal), Sunil and Pooja every day.
        for (var i = 0; i < 6; i++) await ShiftAsync(manager, pid, new SaveRosterShiftRequest(asha.Id, monday.AddDays(i), am.Id, null, null, null, null));
        for (var i = 0; i < 7; i++) await ShiftAsync(manager, pid, new SaveRosterShiftRequest(sunil.Id, monday.AddDays(i), hkDay.Id, null, null, null, null));
        for (var i = 0; i < 5; i++) await ShiftAsync(manager, pid, new SaveRosterShiftRequest(pooja.Id, monday.AddDays(i), hkDay.Id, null, null, null, null));
        // A night shift ends the next morning: 23:00 IST is 17:30 UTC, 07:00 IST the next day 01:30 UTC.
        var n = await ShiftAsync(manager, pid, new SaveRosterShiftRequest(sunil.Id, monday.AddDays(6), night.Id, front.Id, null, null, null));
        Assert.Equal((monday.AddDays(6), new DateTimeOffset(monday.AddDays(6).ToDateTime(new TimeOnly(17, 30)), TimeSpan.Zero),
            new DateTimeOffset(monday.AddDays(7).ToDateTime(new TimeOnly(1, 30)), TimeSpan.Zero)), (n.WorkDate, n.StartsAt.ToUniversalTime(), n.EndsAt.ToUniversalTime()));

        var week = await manager.GetJsonAsync<RosterWeekDto>($"{Url(pid)}/roster?week={monday.AddDays(3):yyyy-MM-dd}");
        Assert.Equal((monday, monday.AddDays(6), 19), (week.WeekStart, week.WeekEnd, week.Shifts.Count));
        // Shortages: Front office Morning on Sunday (nobody), Housekeeping Day on Saturday and Sunday (1 of 2). The night shift needs nobody.
        Assert.Equal([(monday.AddDays(5), "Day", 2, 1, 1), (monday.AddDays(6), "Morning", 1, 0, 1), (monday.AddDays(6), "Day", 2, 1, 1)],
            week.Shortages.OrderBy(s => s.Date).ThenBy(s => s.DepartmentName).Select(s => (s.Date, s.TemplateName, s.Needed, s.Rostered, s.Missing)));
        var dayOff = Assert.Single(week.Warnings, w => w.Kind == "DAY_OFF");
        Assert.Equal($"Asha Naik is on the roster on Tuesday {monday.AddDays(1):ddd d MMM}, their weekly day off.", dayOff.Message);
        // Sunil: 7 × 8 h + 7.5 h night = 63.5 h, over the 48-hour week.
        var longWeek = Assert.Single(week.Warnings, w => w.Kind == "LONG_WEEK");
        Assert.Equal("Sunil Gawas has 63 h 30 min this week, over the 48-hour limit for a week.", longWeek.Message);
        Assert.Equal(3810, week.Employees.Single(e => e.Id == sunil.Id).PaidMinutes);

        // Filling the gap clears the shortage; removing a shift brings it back and keeps the row with the reason.
        var sun = await ShiftAsync(manager, pid, new SaveRosterShiftRequest(asha.Id, monday.AddDays(6), am.Id, null, null, null, null));
        week = await manager.GetJsonAsync<RosterWeekDto>($"{Url(pid)}/roster?week={monday:yyyy-MM-dd}");
        Assert.DoesNotContain(week.Shortages, s => s.TemplateId == am.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"{Url(pid)}/roster/shifts/{sun.Id}/cancel", new CancelRosterShiftRequest("Swapped", sun.Version))).StatusCode);
        week = await manager.GetJsonAsync<RosterWeekDto>($"{Url(pid)}/roster?week={monday:yyyy-MM-dd}");
        Assert.Contains(week.Shortages, s => s.TemplateId == am.Id && s.Date == monday.AddDays(6));
        Assert.Equal("CANCELLED Swapped", await ScalarAsync($"SELECT status || ' ' || cancel_reason FROM roster_shifts WHERE id = '{sun.Id}'"));

        // Plain-words refusals under the field.
        Assert.Equal("06:00–23:00 is 17 h long. A shift is at most 16 hours; split it into two.", await FieldErrorAsync(await manager.PostAsync(
            $"{Url(pid)}/roster/shifts", Custom(pooja.Id, monday.AddDays(6), hk.Id, 6, 23)), "EndTime"));
        Assert.Equal("Choose a shift, or give the start time.", await FieldErrorAsync(await manager.PostAsync($"{Url(pid)}/roster/shifts",
            new SaveRosterShiftRequest(pooja.Id, monday.AddDays(6), null, null, null, null, null)), "StartTime"));
        var receptionist = await StaffAsync("RECEPTIONIST", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.PostAsync($"{Url(pid)}/roster/shifts", Custom(pooja.Id, monday, hk.Id, 6, 8))).StatusCode);
    }

    [Fact]
    public async Task Copying_last_week_copies_day_for_day_and_lists_what_it_skipped()
    {
        var (owner, pid) = await PropertyAsync();
        var manager = await StaffAsync("MANAGER", pid);
        var rest = await DepartmentAsync(owner, pid, "REST", "Restaurant");
        var lunch = await TemplateAsync(manager, pid, rest.Id, "RST-LUNCH", "Lunch", new TimeOnly(11, 0), new TimeOnly(17, 0), 1);
        var neha = await EmployeeAsync(owner, pid, rest.Id, "Neha", "Parab");
        var joel = await EmployeeAsync(owner, pid, rest.Id, "Joel", "Rodrigues");
        var monday = NextMonday();
        for (var i = 0; i < 3; i++)
        {
            await ShiftAsync(manager, pid, new SaveRosterShiftRequest(neha.Id, monday.AddDays(i), lunch.Id, null, null, null, null));
            await ShiftAsync(manager, pid, new SaveRosterShiftRequest(joel.Id, monday.AddDays(i), lunch.Id, null, null, null, null));
        }
        var next = monday.AddDays(7);
        // Joel already works Tuesday evening next week (overlaps lunch), and leaves on Tuesday.
        await ShiftAsync(manager, pid, Custom(joel.Id, next.AddDays(1), rest.Id, 16, 22));
        await OkAsync<EmployeeDto>(await owner.PostAsync($"{EmpUrl(pid, joel.Id)}/leave", new EmployeeLeftRequest(next.AddDays(1), "End of season", null)));

        var result = await OkAsync<CopyRosterWeekResultDto>(await manager.PostAsync($"{Url(pid)}/roster/copy-week", new CopyRosterWeekRequest(monday, next.AddDays(2))));
        Assert.Equal(4, result.Copied);   // Neha × 3, Joel Monday
        Assert.Equal([$"Joel Rodrigues, {next.AddDays(1):ddd d MMM} 11:00–17:00: already on 16:00–22:00 that day.",
                      $"Joel Rodrigues, {next.AddDays(2):ddd d MMM} 11:00–17:00: left on {next.AddDays(1):ddd d MMM}."], result.Skipped);
        Assert.Equal(next, result.Week.WeekStart);
        Assert.Equal(5, result.Week.Shifts.Count);

        Assert.Equal("Choose a different week to copy into: this is the week being copied.",
            await FieldErrorAsync(await manager.PostAsync($"{Url(pid)}/roster/copy-week", new CopyRosterWeekRequest(monday, monday.AddDays(4))), "ToWeek"));
        Assert.Equal($"The week of {Day(RosterWeekStart(Today).AddDays(-7))} has passed. Copy into this week or a later one.",
            await FieldErrorAsync(await manager.PostAsync($"{Url(pid)}/roster/copy-week", new CopyRosterWeekRequest(monday, Today.AddDays(-7))), "ToWeek"));
    }

    // ------------------------------------------------------------------ employees, accounts, leaving

    [Fact]
    public async Task Employees_are_numbered_linked_to_one_account_see_their_own_shifts_and_leave_once_off_the_roster()
    {
        var (owner, pid) = await PropertyAsync();
        var hr = await StaffAsync("HR", pid);
        var maint = await DepartmentAsync(owner, pid, "MAINT", "Maintenance");
        var dept = await OkAsync<DepartmentDto>(await hr.PostAsync($"{Url(pid)}/designations", new SaveDesignationRequest(maint.Id, "Technician")), HttpStatusCode.Created);
        var tech = Assert.Single(dept.Designations);
        var (email, pw) = await app.CreateUserAsync("MAINTENANCE", pid);
        var userId = (Guid)(await ScalarAsync($"SELECT id FROM users WHERE normalized_email = lower('{email}')"))!;

        var santosh = await OkAsync<EmployeeDto>(await hr.PostAsync($"{Url(pid)}/employees", Employee(maint.Id, "Santosh", "Velip") with
        {
            DesignationId = tech.Id,
            UserId = userId,
            WeeklyOff = 7,
            Phone = "+91 98230 10010",
        }), HttpStatusCode.Created);
        Assert.Equal(("E00001", "Technician", email, (short?)7), (santosh.Code, santosh.DesignationName, santosh.UserEmail, santosh.WeeklyOff));
        var second = await EmployeeAsync(hr, pid, maint.Id, "Lata", "Velip");
        Assert.Equal("E00002", second.Code);

        // One account, one employee.
        Assert.Equal($"The account {email} is already linked to Santosh Velip (E00001). An account belongs to one employee.",
            await FieldErrorAsync(await hr.PutAsync(EmpUrl(pid, second.Id), Employee(maint.Id, "Lata", "Velip") with { UserId = userId, Version = second.Version }), "UserId"));
        var accounts = await hr.GetJsonAsync<List<LinkableAccountDto>>($"{Url(pid)}/employees/accounts");
        Assert.Equal("Santosh Velip (E00001)", accounts.Single(a => a.Id == userId).LinkedTo);
        Assert.Equal("Choose the department they work in.", await FieldErrorAsync(await hr.PostAsync($"{Url(pid)}/employees",
            Employee(Guid.Empty, "X", null)), "DepartmentId"));

        // The linked account sees its own shifts (roster.self) and no one else's roster.
        var shift = await ShiftAsync(owner, pid, Custom(santosh.Id, Today.AddDays(2), maint.Id, 9, 18));
        var self = await app.SignInAsync(email, pw);
        var mine = await self.GetJsonAsync<MyRosterDto>($"{Url(pid)}/roster/mine");
        Assert.Equal(("Santosh Velip", "Maintenance", shift.Id), (mine.EmployeeName, mine.DepartmentName, Assert.Single(mine.Shifts).Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await self.GetAsync($"{Url(pid)}/roster")).StatusCode);
        var unlinked = await StaffAsync("HOUSEKEEPER", pid);
        Assert.Equal("Your account isn't linked to an employee record at this property. Ask HR to link it on your employee profile.",
            (await FrontDeskTests.ProblemAsync(await unlinked.GetAsync($"{Url(pid)}/roster/mine"))).Message);

        // Leaving while on the roster after the last day is refused, naming the shifts; once they are off it, it works.
        Assert.Equal($"Santosh Velip is on the roster after {Day(Today)}: {Day(Today.AddDays(2))}. Remove those shifts from the roster first.",
            await FieldErrorAsync(await hr.PostAsync($"{EmpUrl(pid, santosh.Id)}/leave", new EmployeeLeftRequest(Today, "Resigned", null)), "LeftOn"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{Url(pid)}/roster/shifts/{shift.Id}/cancel", new CancelRosterShiftRequest("Leaving", null))).StatusCode);
        var left = await OkAsync<EmployeeDto>(await hr.PostAsync($"{EmpUrl(pid, santosh.Id)}/leave", new EmployeeLeftRequest(Today, "Resigned", null)));
        Assert.Equal(("LEFT", Today, "Resigned"), (left.Status, left.LeftOn, left.LeavingReason));
        Assert.DoesNotContain(await hr.GetJsonAsync<List<EmployeeDto>>($"{Url(pid)}/employees"), e => e.Id == santosh.Id);
        Assert.Contains(await hr.GetJsonAsync<List<EmployeeDto>>($"{Url(pid)}/employees?status=LEFT"), e => e.Id == santosh.Id);
        Assert.Equal($"Santosh Velip left on {Day(Today)}, before {Day(Today.AddDays(3))}. Choose someone else.",
            await FieldErrorAsync(await owner.PostAsync($"{Url(pid)}/roster/shifts", Custom(santosh.Id, Today.AddDays(3), maint.Id, 9, 18)), "EmployeeId"));

        // A department with active people can't be switched off.
        Assert.Equal("1 active employee works in Maintenance. Move them to another department first.",
            await FieldErrorAsync(await hr.PutAsync($"{Url(pid)}/departments/{maint.Id}", new SaveDepartmentRequest("MAINT", "Maintenance", 0, false, null)), "IsActive"));
        Assert.True((long)(await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_type = 'Employee' AND entity_id = '{santosh.Id}'"))! >= 2);
    }

    // ------------------------------------------------------------------ documents

    [Fact]
    public async Task Documents_are_encrypted_aadhaar_keeps_only_the_last_four_digits_and_opening_one_is_audited()
    {
        var (owner, pid) = await PropertyAsync();
        var hr = await StaffAsync("HR", pid);
        var manager = await StaffAsync("MANAGER", pid);
        var front = await DepartmentAsync(owner, pid, "FRONT", "Front office");
        var asha = await EmployeeAsync(hr, pid, front.Id, "Asha", "Naik");
        var docs = $"{EmpUrl(pid, asha.Id)}/documents";

        var pan = await UploadAsync(hr, docs, "PAN", "abcpk1234f");
        Assert.Equal(("PAN", "•••• 234F", false), (pan.DocumentType, pan.MaskedNumber, pan.HasFile));
        var aadhaar = await UploadAsync(hr, docs, "AADHAAR", "1234 5678 9012", file: [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        Assert.Equal(("•••• 9012", true, "image/jpeg"), (aadhaar.MaskedNumber, aadhaar.HasFile, aadhaar.ContentType));

        // Only ciphertext in the database; Aadhaar has none at all.
        Assert.Equal(false, await ScalarAsync($"SELECT position('ABCPK1234F'::bytea in number_encrypted) > 0 FROM employee_documents WHERE id = '{pan.Id}'"));
        Assert.Equal(true, await ScalarAsync($"SELECT number_encrypted IS NULL FROM employee_documents WHERE id = '{aadhaar.Id}'"));

        // The manager can't see documents; HR opens the PAN (audited as a sensitive read).
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(docs)).StatusCode);
        var opened = await OkAsync<EmployeeDocumentDetailDto>(await hr.GetAsync($"{docs}/{pan.Id}"));
        Assert.Equal("ABCPK1234F", opened.Number);
        Assert.Equal((byte[])[0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], await (await hr.GetAsync($"{docs}/{aadhaar.Id}/file")).Content.ReadAsByteArrayAsync());
        Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE action = 'SENSITIVE_READ' AND entity_type = 'EmployeeDocument' AND changes::text LIKE '%{asha.Id}%'"));

        // Refusals say why, under the field.
        Assert.Equal("A PAN is 10 characters: 5 letters, 4 digits, 1 letter, e.g. ABCPK1234F.", await FieldErrorAsync(await PostDocAsync(hr, docs, "PAN", "1234"), "Number"));
        Assert.Equal("Enter the document number, attach a scan, or both.", await FieldErrorAsync(await PostDocAsync(hr, docs, "PASSPORT", null), "Number"));
        Assert.Equal("Scans must be a JPEG or PNG photo, or a PDF.", await FieldErrorAsync(await PostDocAsync(hr, docs, "OTHER", null, [1, 2, 3, 4]), "File"));

        // Removing wipes the number and the scan and keeps the entry.
        var removed = await OkAsync<EmployeeDocumentDto>(await hr.PostAsync($"{docs}/{aadhaar.Id}/remove", new RemoveEmployeeDocumentRequest("Wrong file")));
        Assert.Equal(("REMOVED", false, "Wrong file"), (removed.Status, removed.HasFile, removed.RemovedReason));
        Assert.Equal(HttpStatusCode.NotFound, (await hr.GetAsync($"{docs}/{aadhaar.Id}/file")).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static SaveEmployeeRequest Employee(Guid dept, string first, string? last, short? weeklyOff = null) =>
        new(first, last, null, null, null, dept, null, "FULL_TIME", new DateOnly(2024, 1, 1), weeklyOff, null, null, null, null, null);

    private static SaveRosterShiftRequest Custom(Guid emp, DateOnly day, Guid dept, int from, int to) =>
        new(emp, day, null, dept, new TimeOnly(from, 0), new TimeOnly(to % 24, 0), 0);

    private static async Task<EmployeeDto> EmployeeAsync(TestClient c, Guid pid, Guid dept, string first, string? last, short? weeklyOff = null) =>
        await OkAsync<EmployeeDto>(await c.PostAsync($"{Url(pid)}/employees", Employee(dept, first, last, weeklyOff)), HttpStatusCode.Created);

    private static async Task<DepartmentDto> DepartmentAsync(TestClient c, Guid pid, string code, string name) =>
        await OkAsync<DepartmentDto>(await c.PostAsync($"{Url(pid)}/departments", new SaveDepartmentRequest(code, name)), HttpStatusCode.Created);

    private static async Task<ShiftTemplateDto> TemplateAsync(TestClient c, Guid pid, Guid dept, string code, string name, TimeOnly from, TimeOnly to, int minStaff,
        int breakMinutes = 30) =>
        await OkAsync<ShiftTemplateDto>(await c.PostAsync($"{Url(pid)}/roster/templates", new SaveShiftTemplateRequest(dept, code, name, from, to, breakMinutes, minStaff)),
            HttpStatusCode.Created);

    private static async Task<RosterShiftDto> ShiftAsync(TestClient c, Guid pid, SaveRosterShiftRequest r) =>
        await OkAsync<RosterShiftDto>(await c.PostAsync($"{Url(pid)}/roster/shifts", r), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> PostDocAsync(TestClient c, string url, string type, string? number, byte[]? file = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "documentType" } };
        if (number is not null) form.Add(new StringContent(number), "number");
        if (file is not null) form.Add(new ByteArrayContent(file), "file", "scan.jpg");
        return c.PostContentAsync(url, form);
    }

    private static async Task<EmployeeDocumentDto> UploadAsync(TestClient c, string url, string type, string? number, byte[]? file = null) =>
        await OkAsync<EmployeeDocumentDto>(await PostDocAsync(c, url, type, number, file), HttpStatusCode.Created);

    private async Task<(TestClient Owner, Guid PropertyId)> PropertyAsync()
    {
        var owner = await app.SignInOwnerAsync();
        var code = "P" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (owner, (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id);
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static DateOnly NextMonday() => RosterWeekStart(Today).AddDays(7);
    private static DateOnly RosterWeekStart(DateOnly d) => Resort.Domain.Employees.RosterMath.WeekStart(d);
    private static string Day(DateOnly d) => Resort.Domain.Rates.QuoteEngine.Day(d);
    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";
    private static string EmpUrl(Guid pid, Guid id) => $"{Url(pid)}/employees/{id}";

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
