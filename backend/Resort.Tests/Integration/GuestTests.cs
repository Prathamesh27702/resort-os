using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

/// <summary>Sprint 3 — guest profiles, duplicates, encrypted ID documents, merge, anonymise, retention. Real API, real PostgreSQL.</summary>
public class GuestTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    // A valid 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // ------------------------------------------------------------------ profiles & search

    [Fact]
    public async Task Reception_finds_a_returning_guest_by_phone_in_one_search()
    {
        var reception = await ReceptionistAsync();
        var phone = UniquePhone();
        var created = await CreateGuestAsync(reception, "Ananya", "Iyer", "0" + phone[3..], email: $"ananya.{Suffix()}@example.in");
        Assert.Equal(phone, created.Phone);                       // typed the local way, stored as +91…

        // any part of the number, typed with spaces, finds her
        var partial = phone[5..10];
        var byPhone = await reception.GetJsonAsync<PagedResult<GuestListItemDto>>($"/api/v1/guests?q={Uri.EscapeDataString(partial[..3] + " " + partial[3..])}");
        Assert.Contains(byPhone.Items, g => g.Id == created.Id);

        // the full number typed the local way finds exactly her
        var exact = await reception.GetJsonAsync<PagedResult<GuestListItemDto>>($"/api/v1/guests?q={Uri.EscapeDataString(phone[3..8] + " " + phone[8..])}");
        Assert.Equal(created.Id, Assert.Single(exact.Items).Id);

        var byName = await reception.GetJsonAsync<PagedResult<GuestListItemDto>>("/api/v1/guests?q=ananya%20iy");
        Assert.Contains(byName.Items, g => g.Id == created.Id && g.FullName == "Ananya Iyer");
    }

    [Fact]
    public async Task Creating_a_likely_duplicate_is_flagged_until_the_user_confirms()
    {
        var owner = await app.SignInOwnerAsync();
        var phone = UniquePhone();
        var first = await CreateGuestAsync(owner, "Rahul", "Menon", phone);

        var dup = await owner.PostAsync("/api/v1/guests", NewGuest("RAHUL", "menon", "+91 " + phone[3..]));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var body = await dup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("POSSIBLE_DUPLICATE", body.GetProperty("code").GetString());
        var match = body.GetProperty("details").EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == first.Id);
        var reasons = match.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("Same phone", reasons);
        Assert.Contains("Same name", reasons);

        var forced = await owner.PostAsync("/api/v1/guests", NewGuest("Rahul", "Menon", phone) with { IgnoreDuplicates = true });
        Assert.Equal(HttpStatusCode.Created, forced.StatusCode);

        var hint = await owner.GetJsonAsync<List<PossibleDuplicateDto>>($"/api/v1/guests/possible-duplicates?phone={Uri.EscapeDataString(phone)}");
        Assert.Equal(2, hint.Count);
    }

    [Fact]
    public async Task Invalid_phone_email_and_gstin_are_field_errors()
    {
        var owner = await app.SignInOwnerAsync();
        var res = await owner.PostAsync("/api/v1/guests", NewGuest("X", null, "12345") with { Email = "not-an-email", Gstin = "123" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var details = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("details");
        Assert.True(details.TryGetProperty("Phone", out _));
        Assert.True(details.TryGetProperty("Email", out _));
        Assert.True(details.TryGetProperty("Gstin", out _));
    }

    [Fact]
    public async Task Edits_need_the_current_version_and_personal_data_is_masked_in_the_audit_trail()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Meera", "Shah", UniquePhone());
        var update = new UpdateGuestRequest(g.FirstName, g.LastName, g.Phone, "meera.new@example.in", null, "IN", null, null, "Prefers a quiet room", true,
            new GuestAddressDto("12 MG Road", null, "Pune", "Maharashtra", "27", "411001", "IN"), [new("DIETARY", "Jain food")], g.Version);
        var ok = await owner.PutAsync($"/api/v1/guests/{g.Id}", update);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = (await ok.Content.ReadFromJsonAsync<GuestDto>())!;
        Assert.Equal("Pune", saved.Address!.City);
        Assert.Equal("Jain food", Assert.Single(saved.Preferences).Value);

        var stale = await owner.PutAsync($"/api/v1/guests/{g.Id}", update);    // old version again
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await stale.ProblemCodeAsync());

        var changes = await AuditChangesAsync(g.Id);
        Assert.DoesNotContain("meera.new@example.in", changes);
        Assert.DoesNotContain("Shah", changes);
        Assert.Contains("\"Email\"", changes);                                // the field is named, its value is not
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Waiter_and_housekeeper_cannot_read_guest_documents()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Kabir", "Das", UniquePhone());
        var doc = await UploadAsync(owner, g.Id, "PASSPORT", "Z1234567", Png);
        var pid = await app.MainPropertyIdAsync();

        var waiter = await SignInAsAsync("WAITER", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync($"/api/v1/guests/{g.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync($"/api/v1/guests/{g.Id}/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync($"/api/v1/guests/{g.Id}/documents/{doc.Id}/file")).StatusCode);
        // a waiter may look a guest up (to charge a meal to the room) but doesn't even learn how many documents exist
        var seen = await waiter.GetJsonAsync<GuestDto>($"/api/v1/guests/{g.Id}");
        Assert.Null(seen.DocumentCount);

        var housekeeper = await SignInAsAsync("HOUSEKEEPER", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync($"/api/v1/guests/{g.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync($"/api/v1/guests/{g.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync("/api/v1/guests?q=kabir")).StatusCode);
    }

    [Fact]
    public async Task Guests_of_another_organisation_are_invisible()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Isha", "Kapoor", UniquePhone());
        var (_, _, otherEmail, otherPw) = await app.CreateSecondTenantAsync();
        var other = await app.SignInAsync(otherEmail, otherPw);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/guests/{g.Id}")).StatusCode);
        var list = await other.GetJsonAsync<PagedResult<GuestListItemDto>>("/api/v1/guests?q=isha");
        Assert.DoesNotContain(list.Items, x => x.Id == g.Id);
    }

    // ------------------------------------------------------------------ documents

    [Fact]
    public async Task ID_number_and_scan_are_stored_encrypted_and_every_view_is_audited()
    {
        var reception = await ReceptionistAsync();
        var g = await CreateGuestAsync(reception, "Lukas", "Weber", "+49 30 1234 5678", nationality: "DE");
        var doc = await UploadAsync(reception, g.Id, "PASSPORT", "C01X 00T47", Png, issuingCountry: "DE", expiresOn: "2031-05-31",
            details: """{"visaNumber":"VN998877"}""");
        Assert.Equal("•••• 0T47", doc.MaskedNumber);
        Assert.True(doc.HasFile);
        Assert.Equal("image/png", doc.ContentType);

        // the raw column holds ciphertext, not the number
        var raw = (byte[])(await ScalarAsync($"SELECT number_encrypted FROM guest_documents WHERE id = '{doc.Id}'"))!;
        Assert.DoesNotContain("C01X00T47", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("C01X00T47", Encoding.Latin1.GetString(raw), StringComparison.Ordinal);
        var rawDetails = (byte[])(await ScalarAsync($"SELECT details_encrypted FROM guest_documents WHERE id = '{doc.Id}'"))!;
        Assert.DoesNotContain("VN998877", Encoding.Latin1.GetString(rawDetails), StringComparison.Ordinal);

        // the scan on disk is not the PNG
        var onDisk = Directory.GetFiles(app.FileRoot, "*", SearchOption.AllDirectories).Single(f => f.EndsWith(doc.Id.ToString("N"), StringComparison.Ordinal));
        Assert.NotEqual(Png, File.ReadAllBytes(onDisk));

        var detail = await reception.GetJsonAsync<GuestDocumentDetailDto>($"/api/v1/guests/{g.Id}/documents/{doc.Id}");
        Assert.Equal("C01X00T47", detail.Number);
        Assert.Equal("VN998877", detail.Details!["visaNumber"]);
        var file = await reception.GetAsync($"/api/v1/guests/{g.Id}/documents/{doc.Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync());

        var reads = await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE action = 'SENSITIVE_READ' AND entity_id = '{doc.Id}'");
        Assert.Equal(2L, reads);
        // the upload's audit row doesn't contain the number, the ciphertext or the file name
        var changes = await AuditChangesAsync(doc.Id);
        Assert.DoesNotContain("0T47", changes);
    }

    [Fact]
    public async Task Aadhaar_keeps_only_the_last_four_digits()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Pooja", "Naik", UniquePhone());
        var doc = await UploadAsync(owner, g.Id, "AADHAAR", "2345 6789 0123", null);
        Assert.Equal("•••• 0123", doc.MaskedNumber);
        Assert.Equal(DBNull.Value, await ScalarAsync($"SELECT number_encrypted FROM guest_documents WHERE id = '{doc.Id}'"));
        var detail = await owner.GetJsonAsync<GuestDocumentDetailDto>($"/api/v1/guests/{g.Id}/documents/{doc.Id}");
        Assert.Null(detail.Number);
    }

    [Fact]
    public async Task Only_jpeg_png_or_pdf_scans_up_to_5_MB_are_accepted()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Arjun", "Rao", UniquePhone());

        var text = await PostDocumentAsync(owner, g.Id, "PASSPORT", "P1234567", Encoding.UTF8.GetBytes("<html>not an image</html>"), "scan.png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
        Assert.Equal("FILE_TYPE", await text.ProblemCodeAsync());

        var big = new byte[5 * 1024 * 1024 + 1];
        "%PDF-"u8.CopyTo(big);
        var tooBig = await PostDocumentAsync(owner, g.Id, "PASSPORT", "P1234567", big, "big.pdf");
        Assert.True(tooBig.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest);

        var empty = await PostDocumentAsync(owner, g.Id, "PASSPORT", null, null, null);
        Assert.Equal("DOCUMENT_EMPTY", await empty.ProblemCodeAsync());
    }

    // ------------------------------------------------------------------ merge & anonymise

    [Fact]
    public async Task Merging_moves_documents_consents_and_preferences_and_hides_the_duplicate()
    {
        var owner = await app.SignInOwnerAsync();
        var survivor = await CreateGuestAsync(owner, "Farah", "Khan", UniquePhone());
        var dupPhone = UniquePhone();
        var dup = await CreateGuestAsync(owner, "Farah", "Khan-Ali", dupPhone, email: $"farah.{Suffix()}@example.in");
        await UploadAsync(owner, dup.Id, "PASSPORT", "M7654321", null);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/guests/{dup.Id}/consents", new RecordConsentRequest("MARKETING_WHATSAPP", true, "FRONT_DESK"))).StatusCode);
        dup = await owner.GetJsonAsync<GuestDto>($"/api/v1/guests/{dup.Id}");

        var res = await owner.PostAsync("/api/v1/guests/merge", new MergeGuestsRequest(survivor.Id, survivor.Version, dup.Id, dup.Version, "Same person, booked twice"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var merged = (await res.Content.ReadFromJsonAsync<GuestDto>())!;
        Assert.Equal(dup.Email, merged.Email);                                    // empty field filled from the duplicate
        Assert.Equal(1, merged.DocumentCount);
        Assert.Equal("MARKETING_WHATSAPP", Assert.Single(merged.Consents).Purpose);

        var gone = await owner.GetJsonAsync<GuestDto>($"/api/v1/guests/{dup.Id}");
        Assert.Equal("MERGED", gone.Status);
        Assert.Equal(survivor.Id, gone.MergedIntoId);
        var search = await owner.GetJsonAsync<PagedResult<GuestListItemDto>>($"/api/v1/guests?q={dupPhone[3..]}");
        Assert.DoesNotContain(search.Items, x => x.Id == dup.Id);
        Assert.Equal("Same person, booked twice", await ScalarAsync(
            $"SELECT reason FROM audit_logs WHERE entity_type = 'Guest' AND entity_id = '{dup.Id}' ORDER BY id DESC LIMIT 1"));

        var again = await owner.PutAsync($"/api/v1/guests/{dup.Id}", new UpdateGuestRequest("X", null, null, null, null, null, null, null, null, false, null, null, gone.Version));
        Assert.Equal("GUEST_MERGED", await again.ProblemCodeAsync());
    }

    [Fact]
    public async Task Anonymising_removes_personal_data_and_documents_keeps_the_profile_and_is_audited_with_the_reason()
    {
        var owner = await app.SignInOwnerAsync();
        var phone = UniquePhone();
        var g = await CreateGuestAsync(owner, "Sanjay", "Pillai", phone, email: $"sanjay.{Suffix()}@example.in");
        var doc = await UploadAsync(owner, g.Id, "PASSPORT", "K9988776", Png);
        var reception = await ReceptionistAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsync($"/api/v1/guests/{g.Id}/anonymise", new AnonymiseGuestRequest("Guest asked"))).StatusCode);

        var res = await owner.PostAsync($"/api/v1/guests/{g.Id}/anonymise", new AnonymiseGuestRequest("Guest asked to be forgotten (email 3 Oct)"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var a = (await res.Content.ReadFromJsonAsync<GuestDto>())!;
        Assert.Equal("ANONYMISED", a.Status);
        Assert.Equal("Anonymised guest", a.FullName);
        Assert.Null(a.Phone);
        Assert.Null(a.Email);

        // still there (stays and invoices will keep pointing at it), but nothing personal is left anywhere
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM guests WHERE id = '{g.Id}'"));
        Assert.Equal("PURGED", await ScalarAsync($"SELECT status FROM guest_documents WHERE id = '{doc.Id}'"));
        Assert.Empty(Directory.GetFiles(app.FileRoot, "*" + doc.Id.ToString("N"), SearchOption.AllDirectories));
        var everything = await ScalarAsync($"SELECT string_agg(coalesce(changes::text, ''), ' ') FROM audit_logs WHERE entity_id IN ('{g.Id}', '{doc.Id}')") as string;
        Assert.DoesNotContain("Pillai", everything);
        Assert.DoesNotContain(phone, everything);
        Assert.Equal("Guest asked to be forgotten (email 3 Oct)", await ScalarAsync(
            $"SELECT reason FROM audit_logs WHERE entity_type = 'Guest' AND entity_id = '{g.Id}' ORDER BY id DESC LIMIT 1"));

        var search = await owner.GetJsonAsync<PagedResult<GuestListItemDto>>($"/api/v1/guests?q={phone[3..]}");
        Assert.Empty(search.Items);
        Assert.Equal("ALREADY_ANONYMISED", await (await owner.PostAsync($"/api/v1/guests/{g.Id}/anonymise", new AnonymiseGuestRequest("again"))).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/guests/{g.Id}/stays")).StatusCode);
    }

    // ------------------------------------------------------------------ retention & database guards

    [Fact]
    public async Task Retention_dry_run_lists_old_documents_and_deletes_nothing()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Old", "Visitor", UniquePhone());
        var old = await UploadAsync(owner, g.Id, "DRIVING_LICENCE", "MH1220190012345", null);
        var recent = await UploadAsync(owner, g.Id, "PAN", "ABCDE1234F", null);
        await SqlAsync($"UPDATE guest_documents SET created_at = now() - interval '400 days' WHERE id = '{old.Id}'");

        var report = await owner.GetJsonAsync<RetentionReportDto>("/api/v1/guests/retention/dry-run");
        Assert.Equal(365, report.RetentionDays);
        Assert.Contains(report.Items, i => i.DocumentId == old.Id);
        Assert.DoesNotContain(report.Items, i => i.DocumentId == recent.Id);
        Assert.Equal("ACTIVE", await ScalarAsync($"SELECT status FROM guest_documents WHERE id = '{old.Id}'"));

        var reception = await ReceptionistAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/guests/retention/dry-run")).StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_deleting_guests_and_rewriting_consents()
    {
        var owner = await app.SignInOwnerAsync();
        var g = await CreateGuestAsync(owner, "Guard", "Test", UniquePhone());
        await owner.PostAsync($"/api/v1/guests/{g.Id}/consents", new RecordConsentRequest("MARKETING_EMAIL", true, "FRONT_DESK"));

        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM guests WHERE id = '{g.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE guest_consents SET granted = false WHERE guest_id = '{g.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM guest_consents WHERE guest_id = '{g.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE guests SET status = 'ANONYMISED', anonymised_at = now() WHERE id = '{g.Id}'"));
    }

    // ------------------------------------------------------------------ helpers

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>A random Indian mobile number in E.164 (+91 then 10 digits starting 9).</summary>
    private static string UniquePhone() => "+919" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static CreateGuestRequest NewGuest(string first, string? last, string? phone, string? email = null, string? nationality = null) =>
        new(first, last, phone, email, null, nationality, null, null, null, false, null, null);

    private static async Task<GuestDto> CreateGuestAsync(TestClient c, string first, string? last, string? phone, string? email = null, string? nationality = null)
    {
        var res = await c.PostAsync("/api/v1/guests", NewGuest(first, last, phone, email, nationality) with { IgnoreDuplicates = true });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<GuestDto>())!;
    }

    private static async Task<HttpResponseMessage> PostDocumentAsync(TestClient c, Guid guestId, string type, string? number, byte[]? file, string? fileName,
        string? issuingCountry = null, string? expiresOn = null, string? details = null)
    {
        using var form = new MultipartFormDataContent { { new StringContent(type), "documentType" } };
        if (number is not null) form.Add(new StringContent(number), "number");
        if (issuingCountry is not null) form.Add(new StringContent(issuingCountry), "issuingCountry");
        if (expiresOn is not null) form.Add(new StringContent(expiresOn), "expiresOn");
        if (details is not null) form.Add(new StringContent(details), "details");
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", fileName ?? "scan.png");
        }
        return await c.PostContentAsync($"/api/v1/guests/{guestId}/documents", form);
    }

    private static async Task<GuestDocumentDto> UploadAsync(TestClient c, Guid guestId, string type, string? number, byte[]? file,
        string? issuingCountry = null, string? expiresOn = null, string? details = null)
    {
        var res = await PostDocumentAsync(c, guestId, type, number, file, file is null ? null : "passport scan.png", issuingCountry, expiresOn, details);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<GuestDocumentDto>())!;
    }

    private async Task<TestClient> SignInAsAsync(string role, Guid? propertyId)
    {
        var (email, pw) = await app.CreateUserAsync(role, propertyId);
        return await app.SignInAsync(email, pw);
    }

    /// <summary>A receptionist whose role is limited to the main property: guests are shared, so that's enough.</summary>
    private async Task<TestClient> ReceptionistAsync() => await SignInAsAsync("RECEPTIONIST", await app.MainPropertyIdAsync());

    private async Task<string> AuditChangesAsync(Guid entityId) =>
        await ScalarAsync($"SELECT coalesce(string_agg(changes::text, ' '), '') FROM audit_logs WHERE entity_id = '{entityId}'") as string ?? "";

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
