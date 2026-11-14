using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 30 — India readiness. A property's FSSAI licence number and UPI ID are checked and explained under their
/// fields; the UPI QR carries the ID, the name and the exact amount; the restaurant bill carries the FSSAI number and a QR
/// for its total while it is open. A foreign guest's check-in opens one Form C with 24 hours to file, filled in from the
/// profile and the passport; filing with gaps is refused naming the gaps; it is filed, reopened, marked not needed; the
/// register never shows a full passport number; the morning reminder run twice sends once; anonymising the guest empties
/// the sheet. Each test uses a property of its own. Real API, real PostgreSQL.</summary>
public class IndiaReadinessTests(NotificationsFactory app) : IClassFixture<NotificationsFactory>
{
    // ------------------------------------------------------------------ FSSAI and UPI on the property

    [Fact]
    public async Task The_fssai_number_and_upi_id_are_checked_under_their_fields_and_the_qr_carries_the_exact_amount()
    {
        var s = await SetupAsync();
        var p = await s.Owner.GetJsonAsync<PropertyDto>(Url(s.Pid));
        Assert.Equal((null, null), (p.FssaiLicenceNo, p.UpiId));

        // No UPI ID yet: the reason says where to add it.
        var none = await ProblemAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/upi-qr?amount=100"));
        Assert.Equal("UPI_NOT_SET", none.Code);
        Assert.Equal($"{s.Name} has no UPI ID yet, so there is no QR to show. Add it under Settings → Properties → {s.Name} → Food licence and UPI.", none.Message);

        var bad = await s.Owner.PutAsync(Url(s.Pid), Update(p, "1002009900012", "palmgrove", "P"));
        var errors = await ErrorsAsync(bad);
        Assert.Contains("14 digits", errors["FssaiLicenceNo"]);
        Assert.Contains("name@bank", errors["UpiId"]);
        Assert.Contains("2 to 60", errors["UpiPayeeName"]);

        // Spaces in the licence number are how the certificate prints it; they are dropped.
        p = await OkAsync<PropertyDto>(await s.Owner.PutAsync(Url(s.Pid), Update(p, "1002 0099 000 456", " palmgrove@okhdfcbank ", null)));
        Assert.Equal(("10020099000456", "palmgrove@okhdfcbank", null), (p.FssaiLicenceNo, p.UpiId, p.UpiPayeeName));

        var qr = await s.Owner.GetJsonAsync<UpiQrDto>($"{Url(s.Pid)}/upi-qr?amount=1234.5&note=O00012");
        Assert.Equal($"upi://pay?pa=palmgrove%40okhdfcbank&pn={Uri.EscapeDataString(s.Name)}&am=1234.50&cu=INR&tn=O00012", qr.Link);
        Assert.Equal((s.Name, 1234.5m, "O00012"), (qr.PayeeName, qr.Amount, qr.Note));
        Assert.StartsWith("data:image/png;base64,iVBOR", qr.QrPng);
        // Without an amount the guest types it.
        Assert.DoesNotContain("am=", (await s.Owner.GetJsonAsync<UpiQrDto>($"{Url(s.Pid)}/upi-qr")).Link);
        Assert.Equal("UPI_AMOUNT", (await ProblemAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/upi-qr?amount=2000000"))).Code);
        // The payee name the owner chose is the one the guest's app shows.
        p = await OkAsync<PropertyDto>(await s.Owner.PutAsync(Url(s.Pid), Update(p, p.FssaiLicenceNo, p.UpiId, "Sea Breeze Hospitality")));
        Assert.Contains("pn=Sea%20Breeze%20Hospitality", (await s.Owner.GetJsonAsync<UpiQrDto>($"{Url(s.Pid)}/upi-qr")).Link);
        // A housekeeper has no business with the property's settings or its QR.
        Assert.Equal(HttpStatusCode.OK, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/upi-qr")).StatusCode);
    }

    // ------------------------------------------------------------------ the printed restaurant bill

    [Fact]
    public async Task A_restaurant_bill_carries_the_fssai_number_and_a_upi_qr_for_its_total_until_it_is_paid()
    {
        var s = await SetupAsync();
        var p = await s.Owner.GetJsonAsync<PropertyDto>(Url(s.Pid));
        await OkAsync<PropertyDto>(await s.Owner.PutAsync(Url(s.Pid), Update(p, "10020099000456", "seabreeze@okaxis", null)));
        var cat = await OkAsync<MenuCategoryDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/menu/categories", new SaveMenuCategoryRequest("Mains", 1)), HttpStatusCode.Created);
        var naan = await OkAsync<MenuItemDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/menu/items",
            new SaveMenuItemRequest(cat.Id, "BN", "Butter Naan", null, "VEG", 60m, true, 0, null, null, null)), HttpStatusCode.Created);

        // Two lines of the same dish print as one line of three.
        var order = await OkAsync<OrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/orders", new CreateOrderRequest("TAKEAWAY", null, null, null, null,
            [new OrderLineRequest(naan.Id, null, null, 2, null), new OrderLineRequest(naan.Id, null, null, 1, null)])), HttpStatusCode.Created);
        var bill = await s.Owner.GetJsonAsync<OrderBillDto>($"{Url(s.Pid)}/orders/{order.Id}/bill");
        Assert.Equal((s.Name, "10020099000456"), (bill.Property.Name, bill.Property.FssaiLicenceNo));
        Assert.Equal((order.Number, "OPEN", (string?)null), (bill.Number, bill.Status, bill.PaidBy));
        var line = Assert.Single(bill.Lines);
        Assert.Equal(("Butter Naan", (short)3, 60m, 180m), (line.Name, line.Quantity, line.UnitPrice, line.Amount));
        Assert.Equal((180m, order.Total), (bill.Subtotal, bill.Total));
        Assert.NotNull(bill.Upi);
        Assert.Equal((bill.Total, $"Bill {order.Number}", "seabreeze@okaxis"), (bill.Upi.Amount, bill.Upi.Note, bill.Upi.UpiId));
        Assert.Contains($"am={bill.Total:0.00}", bill.Upi.Link);

        // Paid: the bill says how, and asks for nothing more.
        order = await OkAsync<OrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/orders/{order.Id}/send", new SendToKitchenRequest(null, order.Version)));
        foreach (var k in order.Kots)
        {
            await OkAsync<KotDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/kitchen/kots/{k.Id}/ready", new KotStepRequest(null)));
            order = await OkAsync<OrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/orders/{order.Id}/kots/{k.Id}/serve"));
        }
        await OkAsync<OrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/orders/{order.Id}/pay", new PayOrderRequest("UPI", "UPI 4123 5678", order.Version)));
        bill = await s.Owner.GetJsonAsync<OrderBillDto>($"{Url(s.Pid)}/orders/{order.Id}/bill");
        Assert.Equal("SETTLED", bill.Status);
        Assert.Null(bill.Upi);
        Assert.StartsWith("Paid by UPI", bill.PaidBy);

        // An order with nothing on it has no bill to print, and the reason says so.
        var empty = await OkAsync<OrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/orders", new CreateOrderRequest("TAKEAWAY", null, null, null, null, null)), HttpStatusCode.Created);
        Assert.Equal("BILL_EMPTY", (await ProblemAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/orders/{empty.Id}/bill"))).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/orders/{order.Id}/bill")).StatusCode);
    }

    // ------------------------------------------------------------------ the acceptance: Form C

    [Fact]
    public async Task A_foreign_guests_check_in_opens_one_form_c_that_is_filled_filed_reopened_and_kept_on_the_register()
    {
        var s = await SetupAsync();
        var lena = await GuestAsync(s.Owner, "Lena", "DE", new DateOnly(1990, 4, 12));
        await PassportAsync(s.Owner, lena.Id, "C4FZ2X9P1", "2031-01-14");
        var asha = await GuestAsync(s.Owner, "Asha", "IN", null);
        var body = NewBooking(s.Stay, lena.Id, s.Source, Today, Today.AddDays(3), adults: 2) with { GuestIds = [lena.Id, asha.Id] };
        var booked = await CreatedAsync(await BookAsync(s.Owner, s.Pid, body));

        // Nothing before she arrives.
        Assert.Empty((await ListAsync(s.Reception, s.Pid, "ALL")).Items);
        await FrontDeskTests.OkAsync(await s.Reception.PostAsync(FrontDeskTests.CheckInUrl(s.Pid, booked), new CheckInRequest(null, true, "Room checked by hand")));

        // One sheet, for the German guest only; 24 hours from the check-in.
        var list = await ListAsync(s.Reception, s.Pid, null);
        var item = Assert.Single(list.Items);
        Assert.Equal((1, 0, 24, "https://indianfrro.gov.in/frro/FormC"), (list.Pending, list.Overdue, list.HoursToFile, list.PortalUrl));
        Assert.Equal((lena.Id, "DE", booked.Reference, "PENDING", false), (item.GuestId, item.Nationality, item.Reference, item.Status, item.Overdue));
        Assert.Equal(TimeSpan.FromHours(24), item.DueAt - item.ArrivedAt);
        Assert.InRange(item.HoursLeft, 23.5, 24);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM form_c_records WHERE reservation_id = '{booked.Id}'"));
        var dash = await s.Reception.GetJsonAsync<FrontDeskDashboardDto>($"{Url(s.Pid)}/front-desk/dashboard");
        Assert.Equal((1, 0), (dash.FormCPending, dash.FormCOverdue));
        // Reception's bell says who and by when.
        await app.DispatchAsync();
        var bell = await s.Reception.GetJsonAsync<NotificationListDto>("/api/v1/notifications");
        var note = Assert.Single(bell.Items, n => n.Link == "/form-c");
        Assert.Contains($"{lena.FullName} ({booked.Reference}), to file by ", note.Body);

        // The sheet knows what the profile, the passport and the booking know.
        var sheet = await s.Reception.GetJsonAsync<FormCSheetDto>($"{Url(s.Pid)}/form-c/{item.Id}");
        string? V(string key) => sheet.Fields.Single(f => f.Key == key).Value;
        Assert.Equal(("Lena", "Germany", "1990-04-12", "C4FZ2X9P1", "2031-01-14", "3"),
            (V("givenName"), V("nationality"), V("dateOfBirth"), V("passportNumber"), V("passportExpiry"), V("intendedDurationDays")));
        Assert.Equal("Torstrasse 12, Berlin, 10119, Germany", V("addressAbroad"));
        Assert.StartsWith(s.Name, V("addressInIndia"));
        Assert.True(sheet.Fields.Single(f => f.Key == "passportNumber").FromProfile);
        Assert.Contains("Visa: Visa number", sheet.Missing);
        Assert.Equal(item.Missing, sheet.Missing.Count);
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM audit_logs WHERE action = 'SENSITIVE_READ' AND entity_id = '{item.Id}'"));

        // A value that can't be right is refused under its field; nothing of that save is kept.
        var bad = await s.Reception.PutAsync($"{Url(s.Pid)}/form-c/{item.Id}", new SaveFormCRequest(
            new Dictionary<string, string?> { ["visaNumber"] = "vj1234567", ["intendedDurationDays"] = "0", ["visaExpiry"] = "14/01/2027", ["shoeSize"] = "9" }, sheet.Version));
        var errors = await ErrorsAsync(bad);
        Assert.Equal("Intended stay here (days) must be a whole number of days from 1 to 3650.", errors["Values.intendedDurationDays"]);
        Assert.Equal("Valid until must be a date, e.g. 14 Nov 2026.", errors["Values.visaExpiry"]);
        Assert.Equal("Form C has no field called shoeSize.", errors["Values.shoeSize"]);
        Assert.Null((await s.Reception.GetJsonAsync<FormCSheetDto>($"{Url(s.Pid)}/form-c/{item.Id}")).Fields.Single(f => f.Key == "visaNumber").Value);

        // Filing with gaps is refused, naming them.
        var gaps = await ProblemAsync(await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/submit", new SubmitFormCRequest("FRRO-1", sheet.Version)));
        Assert.Equal("FORM_C_INCOMPLETE", gaps.Code);
        Assert.StartsWith($"{sheet.Missing.Count} required fields are still empty: ", gaps.Message);
        Assert.Contains("Visa: Visa number", gaps.Message);

        // Reception types the rest. What is stored is encrypted: the visa number isn't readable in the table.
        var rest = sheet.Fields.Where(f => f.Required && f.Value is null).ToDictionary(f => f.Key, f => (string?)(f.Kind switch
        {
            "date" => "2026-11-01",
            "time" => "14:30",
            "number" => "3",
            "sex" => "f",
            "yesno" => "no",
            _ => "Berlin",
        }));
        rest["visaNumber"] = "vj1234567";
        sheet = await OkAsync<FormCSheetDto>(await s.Reception.PutAsync($"{Url(s.Pid)}/form-c/{item.Id}", new SaveFormCRequest(rest, sheet.Version)));
        Assert.Empty(sheet.Missing);
        Assert.Equal(("VJ1234567", "Female", "No"), (sheet.Fields.Single(f => f.Key == "visaNumber").Value,
            sheet.Fields.Single(f => f.Key == "sex").Value, sheet.Fields.Single(f => f.Key == "employedInIndia").Value));
        Assert.False(sheet.Fields.Single(f => f.Key == "visaNumber").FromProfile);
        var stored = (byte[])(await ScalarAsync($"SELECT details_encrypted FROM form_c_records WHERE id = '{item.Id}'"))!;
        Assert.DoesNotContain("VJ1234567", Encoding.UTF8.GetString(stored));
        // A stale copy of the sheet can't overwrite it.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Reception.PutAsync($"{Url(s.Pid)}/form-c/{item.Id}",
            new SaveFormCRequest(new Dictionary<string, string?> { ["visaType"] = "Business" }, sheet.Version - 1))).StatusCode);

        // Filed with the portal's number: who and when are kept, and the sheet is locked.
        Assert.Contains("3 to 40 characters", await FieldErrorAsync(
            await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/submit", new SubmitFormCRequest("AB", sheet.Version)), "FrroReference"));
        sheet = await OkAsync<FormCSheetDto>(await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/submit", new SubmitFormCRequest(" FRRO-2026-0042 ", sheet.Version)));
        Assert.Equal(("SUBMITTED", "FRRO-2026-0042", false), (sheet.Status, sheet.FrroReference, sheet.Overdue));
        Assert.NotNull(sheet.SubmittedAt);
        Assert.NotNull(sheet.SubmittedByName);
        Assert.Equal("FORM_C_CLOSED", (await ProblemAsync(await s.Reception.PutAsync($"{Url(s.Pid)}/form-c/{item.Id}",
            new SaveFormCRequest(new Dictionary<string, string?> { ["visaType"] = "Business" }, sheet.Version)))).Code);
        Assert.Equal("FORM_C_ALREADY_FILED", (await ProblemAsync(await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/submit",
            new SubmitFormCRequest(null, sheet.Version)))).Code);
        Assert.Empty((await ListAsync(s.Reception, s.Pid, "PENDING")).Items);
        Assert.Equal("FRRO-2026-0042", Assert.Single((await ListAsync(s.Reception, s.Pid, "SUBMITTED")).Items).FrroReference);
        Assert.Equal(0, (await s.Reception.GetJsonAsync<FrontDeskDashboardDto>($"{Url(s.Pid)}/front-desk/dashboard")).FormCPending);

        // The register: one row, the passport as its last four characters only.
        var csv = await s.Reception.GetAsync($"{Url(s.Pid)}/form-c/register.csv?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains($"{lena.FullName},Germany,•••• X9P1,{booked.Reference},", text);
        Assert.Contains(",Filed,", text);
        Assert.Contains("FRRO-2026-0042", text);
        Assert.DoesNotContain("C4FZ2X9P1", text);
        Assert.Equal("The register covers at most 366 days at a time. Choose a shorter period.", await FieldErrorAsync(
            await s.Reception.GetAsync($"{Url(s.Pid)}/form-c/register.csv?from={Today.AddDays(-400):yyyy-MM-dd}&to={Today:yyyy-MM-dd}"), "to"));

        // Reopened to correct, then marked as not needed with a reason; a row is never deleted.
        sheet = await OkAsync<FormCSheetDto>(await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/reopen", new ReopenFormCRequest(sheet.Version)));
        Assert.Equal(("PENDING", null, null), (sheet.Status, sheet.FrroReference, sheet.SubmittedAt));
        Assert.Contains("Say why no Form C is needed", await FieldErrorAsync(
            await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/not-required", new FormCNotRequiredRequest(" ", sheet.Version)), "Reason"));
        sheet = await OkAsync<FormCSheetDto>(await s.Reception.PostAsync($"{Url(s.Pid)}/form-c/{item.Id}/not-required",
            new FormCNotRequiredRequest("OCI card holder, entered as German by mistake", sheet.Version)));
        Assert.Equal(("NOT_REQUIRED", "OCI card holder, entered as German by mistake"), (sheet.Status, sheet.NotRequiredReason));
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync($"DELETE FROM form_c_records WHERE id = '{item.Id}'"));

        // Who may: a housekeeper sees no sheet; a manager of another property can't reach this one.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/form-c")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/form-c/{item.Id}")).StatusCode);
        var other = await SetupAsync();
        Assert.Contains((await other.Reception.GetAsync($"{Url(s.Pid)}/form-c/{item.Id}")).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"{Url(other.Pid)}/form-c/{item.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ required: the reminder run twice sends once

    [Fact]
    public async Task The_morning_reminder_lists_the_forms_still_to_file_and_run_twice_sends_once()
    {
        var s = await SetupAsync();
        // Nothing to file: the job finds nothing.
        var run = await RunAsync(s, "form_c_reminder");
        Assert.Equal((0, 0), (run.Found, run.Queued));

        var jack = await GuestAsync(s.Owner, "Jack", "AU", null);
        var booked = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(s.Stay, jack.Id, s.Source, Today, Today.AddDays(1))));
        await FrontDeskTests.OkAsync(await s.Owner.PostAsync(FrontDeskTests.CheckInUrl(s.Pid, booked), new CheckInRequest(null, true, "Room checked by hand")));
        // The sheet is 30 hours old: past its 24.
        await ScalarAsync($"UPDATE form_c_records SET arrived_at = now() - interval '30 hours', due_at = now() - interval '6 hours' WHERE reservation_id = '{booked.Id}'");
        var list = await ListAsync(s.Reception, s.Pid, null);
        Assert.Equal((1, 1), (list.Pending, list.Overdue));
        Assert.True(list.Items[0].Overdue);
        Assert.InRange(list.Items[0].HoursLeft, -6.1, -5.9);

        run = await RunAsync(s, "form_c_reminder");
        Assert.Equal(1, run.Found);
        Assert.True(run.Queued >= 1, run.Message);
        var again = await RunAsync(s, "form_c_reminder");
        Assert.Equal((1, 0, 1), (again.Found, again.Queued, again.Already));
        await app.DispatchAsync();
        var bell = await s.Reception.GetJsonAsync<NotificationListDto>("/api/v1/notifications");
        Assert.Contains(bell.Items, n => n.Link == "/form-c" && n.Body.Contains($"{jack.FullName} ({booked.Reference}), overdue since ", StringComparison.Ordinal));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM job_runs WHERE property_id = '{s.Pid}' AND job = 'form_c_reminder'"));

        // The job is one of the owner's daily jobs, at 09:00.
        var jobs = await s.Owner.GetJsonAsync<JobsDto>($"{Url(s.Pid)}/jobs");
        Assert.Equal(new TimeOnly(9, 0), jobs.Jobs.Single(j => j.Job == "form_c_reminder").RunAt);
    }

    // ------------------------------------------------------------------ guests in house before, and erasure

    [Fact]
    public async Task A_guest_made_foreign_after_check_in_gets_a_sheet_on_the_next_look_and_anonymising_empties_it()
    {
        var s = await SetupAsync();
        var guest = await GuestAsync(s.Owner, "Sophie", null, null);
        var booked = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(s.Stay, guest.Id, s.Source, Today, Today.AddDays(1))));
        await FrontDeskTests.OkAsync(await s.Owner.PostAsync(FrontDeskTests.CheckInUrl(s.Pid, booked), new CheckInRequest(null, true, "Room checked by hand")));
        Assert.Empty((await ListAsync(s.Reception, s.Pid, "ALL")).Items);

        // Reception learns she is French and corrects the profile: the sheet appears, once, however often the list is opened.
        await ScalarAsync($"UPDATE guests SET nationality = 'FR' WHERE id = '{guest.Id}'");
        var item = Assert.Single((await ListAsync(s.Reception, s.Pid, "ALL")).Items);
        Assert.Single((await ListAsync(s.Reception, s.Pid, "ALL")).Items);
        Assert.Equal("FR", item.Nationality);
        var sheet = await s.Reception.GetJsonAsync<FormCSheetDto>($"{Url(s.Pid)}/form-c/{item.Id}");
        await OkAsync<FormCSheetDto>(await s.Reception.PutAsync($"{Url(s.Pid)}/form-c/{item.Id}",
            new SaveFormCRequest(new Dictionary<string, string?> { ["passportNumber"] = "19AB12345" }, sheet.Version)));
        Assert.NotNull(await ScalarAsync($"SELECT details_encrypted FROM form_c_records WHERE id = '{item.Id}'"));

        // She leaves and asks to be forgotten: the typed sheet goes, the row (that a form was due) stays.
        await ScalarAsync($"UPDATE reservations SET status = 'CHECKED_OUT' WHERE id = '{booked.Id}'");
        var gone = await s.Owner.PostAsync($"/api/v1/guests/{guest.Id}/anonymise", new AnonymiseGuestRequest("Guest asked to be forgotten"));
        Assert.True(gone.IsSuccessStatusCode, $"{(int)gone.StatusCode}: {await gone.Content.ReadAsStringAsync()}");
        Assert.Equal(DBNull.Value, await ScalarAsync($"SELECT details_encrypted FROM form_c_records WHERE id = '{item.Id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM form_c_records WHERE id = '{item.Id}'"));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Setup(Guid Pid, string Name, TestClient Owner, TestClient Reception, TestClient Housekeeper, Stay Stay, Guid Source);

    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "I" + Suffix();
        var name = $"Sea Breeze {code}";
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, name, null, "RESORT", "Asia/Kolkata", "INR",
            "Beach Road", null, "Malvan", null, "Maharashtra", "27", "416606", "IN", "+91 98200 00000", null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        var reception = await StaffAsync("RECEPTIONIST", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var stay = await BookingKit.SetupAsync(owner, pid, rooms: 3);
        return new Setup(pid, name, owner, reception, housekeeper, stay, await WalkInAsync(owner, pid));
    }

    private static UpdatePropertyRequest Update(PropertyDto p, string? fssai, string? upi, string? payee) => new(p.Name, p.LegalName, p.PropertyType, p.Timezone,
        p.Currency, p.AddressLine1, p.AddressLine2, p.City, p.District, p.StateName, p.GstStateCode, p.PostalCode, p.CountryCode, p.Phone, p.Email,
        p.CheckInTime, p.CheckOutTime, p.Latitude, p.Longitude, p.IsActive, p.Version, fssai, upi, payee);

    private static async Task<GuestDto> GuestAsync(TestClient c, string first, string? nationality, DateOnly? born)
    {
        var res = await c.PostAsync("/api/v1/guests", new CreateGuestRequest(first, "G" + Suffix().ToLowerInvariant(), null, null, born, nationality,
            null, null, null, false,
            nationality == "DE" ? new GuestAddressDto("Torstrasse 12", null, "Berlin", null, null, "10119", "DE") : null, null, IgnoreDuplicates: true));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<GuestDto>())!;
    }

    private static async Task PassportAsync(TestClient c, Guid guestId, string number, string expiresOn)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("PASSPORT"), "documentType" }, { new StringContent(number), "number" },
            { new StringContent("DE"), "issuingCountry" }, { new StringContent(expiresOn), "expiresOn" },
        };
        var res = await c.PostContentAsync($"/api/v1/guests/{guestId}/documents", form);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    private static Task<FormCListDto> ListAsync(TestClient c, Guid pid, string? status) =>
        c.GetJsonAsync<FormCListDto>($"{Url(pid)}/form-c{(status is null ? "" : "?status=" + status)}");

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static async Task<JobRunResultDto> RunAsync(Setup s, string job) =>
        await OkAsync<JobRunResultDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/jobs/{job}/run"));

    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>The first message per field of a 400.</summary>
    private static async Task<Dictionary<string, string>> ErrorsAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("details").EnumerateObject().ToDictionary(p => p.Name, p => p.Value[0].GetString()!);
    }

    private static async Task<ApiError> ProblemAsync(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ApiError>())!;

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
