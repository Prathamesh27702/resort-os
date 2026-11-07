using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 24 — notifications. A confirmed booking queues the guest's confirmation in its own transaction (the
/// outbox); the day-before reminder job finds tomorrow's arrivals; the dispatcher sends through (the fake) WhatsApp and email.
/// Every job is idempotent: run twice, or twice at the same moment, it sends once. WhatsApp's status webhooks are verified
/// by their signature (a bad one stores nothing), processed once each and only move a message forward. A message that can't
/// go says why; one that fails for a reason that may pass is tried again later. Each test uses a property of its own. Real
/// API, real PostgreSQL, the real WhatsApp and email adapters talking HTTP to the fakes.</summary>
public class NotificationsTests(NotificationsFactory app) : IClassFixture<NotificationsFactory>
{
    // ------------------------------------------------------------------ the acceptance: confirmation + day-before reminder

    [Fact]
    public async Task A_guest_gets_the_confirmation_when_booked_and_the_reminder_the_day_before()
    {
        var s = await SetupAsync();
        var booking = await BookAsync(s, Today.AddDays(1), nights: 2);

        // The booking's transaction queued the confirmation on both channels; nothing is sent yet.
        var queued = await MessagesAsync(s, booking.Id);
        Assert.Equal([("booking_confirmation", "EMAIL", "QUEUED"), ("booking_confirmation", "WHATSAPP", "QUEUED")],
            queued.Select(m => (m.TemplateCode, m.Channel, m.Status)).OrderBy(x => x.Channel));
        Assert.Equal("+91 98•••••345", queued.Single(m => m.Channel == "WHATSAPP").To);
        Assert.Equal("as•••@example.com", queued.Single(m => m.Channel == "EMAIL").To);
        Assert.DoesNotContain(app.WhatsApp.Sent, m => m.To == s.Phone);

        await app.DispatchAsync();
        var wa = app.WhatsApp.Sent.Single(m => m.To == s.Phone);
        Assert.Equal(("booking_confirmation", "en"), (wa.Template, wa.Language));
        Assert.Equal("Asha", wa.Parameters["guest_name"]);
        Assert.Equal(booking.Reference, wa.Parameters["reference"]);
        Assert.Equal(Day(booking.CheckIn), wa.Parameters["check_in"]);
        Assert.Equal("2 nights", wa.Parameters["nights"]);
        Assert.Equal("2:00 pm", wa.Parameters["check_in_time"]);
        Assert.StartsWith("1 × Garden room", wa.Parameters["rooms"]);
        var mail = app.Email.Sent.Single(m => m.To == s.Email);
        Assert.Equal($"Your booking {booking.Reference} at {s.Name} is confirmed", mail.Subject);
        Assert.Contains($"Arrival: {Day(booking.CheckIn)} (check-in from 2:00 pm)", mail.Text);
        Assert.Contains("<br>", mail.Html);
        Assert.All(await MessagesAsync(s, booking.Id), m => Assert.Equal(("SENT", "Sent"), (m.Status, m.StatusWords)));

        // The next morning's job sends the day-before reminder on both channels.
        var run = await RunAsync(s, "reservation_reminder");
        Assert.Equal((1, 2, 0, 0), (run.Found, run.Queued, run.Skipped, run.Already));
        await app.DispatchAsync();
        var reminder = app.WhatsApp.Sent.Single(m => m.To == s.Phone && m.Template == "reservation_reminder");
        Assert.Equal(s.Name, reminder.Parameters["property_name"]);
        Assert.Contains(app.Email.Sent, m => m.To == s.Email && m.Subject == $"See you tomorrow at {s.Name}");

        var detail = await s.Owner.GetJsonAsync<MessageDto>($"{Url(s.Pid)}/messages/{(await MessagesAsync(s, booking.Id))
            .Single(m => m.TemplateCode == "reservation_reminder" && m.Channel == "WHATSAPP").Id}");
        Assert.Equal(("Day-before reminder", "reservation_reminder"), (detail.JobName, detail.ProviderTemplate));
        Assert.StartsWith("Hello Asha, we look forward to welcoming you at " + s.Name + " tomorrow", detail.Body);
        Assert.Equal(["QUEUED", "SENT"], detail.Steps.Select(x => x.Status));
        Assert.True(detail.CanFakeStatus);

        // The Messages page finds them by the booking reference (any case) or the guest's name.
        var byRef = await s.Owner.GetJsonAsync<PagedResult<MessageListItemDto>>($"{Url(s.Pid)}/messages?q={booking.Reference.ToLowerInvariant()}");
        Assert.Equal(4, byRef.Total);
        Assert.All(byRef.Items, m => Assert.Equal(booking.Reference, m.ReservationReference));
        Assert.Equal(4, (await s.Owner.GetJsonAsync<PagedResult<MessageListItemDto>>($"{Url(s.Pid)}/messages?q=ASHA")).Total);
        Assert.Equal(0, (await s.Owner.GetJsonAsync<PagedResult<MessageListItemDto>>($"{Url(s.Pid)}/messages?q=nobody")).Total);
    }

    // ------------------------------------------------------------------ required: job run twice sends once

    [Fact]
    public async Task A_job_run_twice_or_twice_at_the_same_moment_sends_each_guest_one_reminder()
    {
        var s = await SetupAsync();
        var a = await BookAsync(s, Today.AddDays(1));
        var b = await BookAsync(s, Today.AddDays(1), guest: await GuestAsync(s.Owner, "Ravi", "+91 99000 11122", null));
        await app.DispatchAsync();   // the confirmations
        var before = app.WhatsApp.Sent.Count;

        var first = await RunAsync(s, "reservation_reminder");
        Assert.Equal((2, 3, 1, 0), (first.Found, first.Queued, first.Skipped, first.Already));   // Ravi has no email
        Assert.Contains("has no email address", Assert.Single(first.Notes));
        var again = await RunAsync(s, "reservation_reminder");
        Assert.Equal((2, 0, 0, 2), (again.Found, again.Queued, again.Skipped, again.Already));
        Assert.Equal("Day-before reminder: all 2 already done earlier, so nothing was sent twice.", again.Message);

        // Twice at the same moment (a click on Run now while the scheduler runs it): still once.
        var c = await BookAsync(s, Today.AddDays(1), guest: await GuestAsync(s.Owner, "Meera", "+91 99000 33344", "meera@example.com"));
        var together = await Task.WhenAll(RunAsync(s, "reservation_reminder"), RunAsync(s, "reservation_reminder"), RunAsync(s, "reservation_reminder"));
        Assert.Equal(2, together.Sum(r => r.Queued));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM job_runs WHERE reservation_id = '{c.Id}'"));

        // The dispatcher twice at the same moment: each message goes once.
        await Task.WhenAll(app.DispatchAsync(), app.DispatchAsync());
        await app.DispatchAsync();
        var reminders = app.WhatsApp.Sent.Skip(before).Where(m => m.Template == "reservation_reminder").ToList();
        Assert.Equal(3, reminders.Count);
        Assert.Equal(3, reminders.Select(m => m.To).Distinct().Count());
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM job_runs WHERE property_id = '{s.Pid}' AND job = 'reservation_reminder'"));
        Assert.Equal(6L, await ScalarAsync(   // three guests on two channels; Ravi's email skipped
            $"SELECT count(*) FROM outbox_messages WHERE property_id = '{s.Pid}' AND template_code = 'reservation_reminder' AND status IN ('SENT','SKIPPED')"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM outbox_messages WHERE property_id = '{s.Pid}' AND status = 'QUEUED'"));

        // job_runs are append-only, and the key is unique even without the job.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE job_runs SET outcome = 'x' WHERE reservation_id = '{a.Id}'"));
        Assert.Equal("42501", ex.SqlState);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO job_runs (organization_id, property_id, job, business_key, triggered_by, outcome) SELECT organization_id, property_id, job, business_key, " +
            $"'SCHEDULE', 'again' FROM job_runs WHERE reservation_id = '{b.Id}'"));
        Assert.Equal("ux_job_runs", ex.ConstraintName);
    }

    [Fact]
    public async Task The_scheduler_runs_a_due_job_once_moves_it_to_tomorrow_and_skips_jobs_turned_off()
    {
        var s = await SetupAsync();
        await BookAsync(s, Today.AddDays(1));
        var jobs = await s.Owner.GetJsonAsync<JobsDto>($"{Url(s.Pid)}/jobs");
        Assert.Equal(["reservation_reminder", "pre_arrival", "checkout_reminder", "payment_reminder", "inventory_alert", "leave_reminder"],
            jobs.Jobs.Select(j => j.Job));
        var reminder = jobs.Jobs[0];
        Assert.Equal((true, new TimeOnly(10, 0)), (reminder.Enabled, reminder.RunAt));

        // Make it due now (as at 10:00) and turn the check-out reminder off.
        var off = await OkAsync<ScheduledJobDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/jobs/checkout_reminder",
            new SaveScheduledJobRequest(false, new TimeOnly(7, 30), jobs.Jobs[2].Version)));
        Assert.Equal((false, new TimeOnly(7, 30)), (off.Enabled, off.RunAt));
        await ExecuteAsync($"UPDATE scheduled_jobs SET next_run_at = now() - interval '1 minute' WHERE property_id = '{s.Pid}' AND job IN ('reservation_reminder','checkout_reminder')");

        // Two schedulers at once (two API instances): one runs it.
        var ran = await Task.WhenAll(app.SchedulerAsync(s.Pid), app.SchedulerAsync(s.Pid));
        Assert.Equal(1, ran.Sum());
        Assert.Equal(0, await app.SchedulerAsync(s.Pid));
        jobs = await s.Owner.GetJsonAsync<JobsDto>($"{Url(s.Pid)}/jobs");
        reminder = jobs.Jobs[0];
        Assert.Equal("Day-before reminder: 1 to do, 2 messages queued.", reminder.LastOutcome);
        Assert.True(reminder.NextRunAt > DateTimeOffset.UtcNow);
        Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(reminder.NextRunAt, Ist).DateTime));
        Assert.Null(jobs.Jobs[2].LastStartedAt);
        Assert.Equal("SCHEDULE", Assert.Single(jobs.Recent).TriggeredBy);

        // Who may do what: the manager sees and runs jobs, only the owner changes them; a housekeeper sees nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PutAsync($"{Url(s.Pid)}/jobs/pre_arrival", new SaveScheduledJobRequest(false, new TimeOnly(9, 0), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.PostAsync($"{Url(s.Pid)}/jobs/pre_arrival/run")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/messages")).StatusCode);
        Assert.Equal("There is no job called nightly. The jobs are: reservation_reminder, pre_arrival, checkout_reminder, payment_reminder, inventory_alert, leave_reminder.",
            (await ProblemAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/jobs/nightly/run"))).Message);
    }

    // ------------------------------------------------------------------ required: WhatsApp status webhook signature check

    [Fact]
    public async Task WhatsApp_status_webhooks_are_checked_by_signature_processed_once_and_only_move_a_message_forward()
    {
        var s = await SetupAsync();
        var booking = await BookAsync(s, Today.AddDays(5));
        await app.DispatchAsync();
        var message = (await MessagesAsync(s, booking.Id)).Single(m => m.Channel == "WHATSAPP");
        var wamid = (await s.Owner.GetJsonAsync<MessageDto>($"{Url(s.Pid)}/messages/{message.Id}")).ProviderMessageId!;
        Assert.StartsWith("wamid.", wamid);
        var events = await ScalarAsync("SELECT count(*) FROM webhook_events");

        // A wrong signature, a missing one, or a body changed after signing: 401 and nothing stored.
        var delivered = app.WhatsApp.StatusWebhook(wamid, "delivered");
        var bad = await app.DeliverAsync(delivered, signature: "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains("X-Hub-Signature-256 header doesn't match", (await bad.Content.ReadFromJsonAsync<ApiError>())!.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.DeliverAsync(delivered, signature: "")).StatusCode);
        var tampered = Encoding.UTF8.GetBytes(delivered.Text.Replace("delivered", "read"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.DeliverAsync(delivered, body: tampered)).StatusCode);
        Assert.Equal(events, await ScalarAsync("SELECT count(*) FROM webhook_events"));
        Assert.Equal("SENT", (await GetAsync(s, message.Id)).Summary.Status);

        // Signed: delivered, then read. The same delivery again changes nothing.
        var ok = await app.DeliverAsync(delivered);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("processed", (await ok.Content.ReadFromJsonAsync<WebhookAckDto>())!.Status);
        Assert.Equal("duplicate", (await (await app.DeliverAsync(delivered)).Content.ReadFromJsonAsync<WebhookAckDto>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await app.DeliverAsync(app.WhatsApp.StatusWebhook(wamid, "read"))).StatusCode);
        // A late "sent" after "read" can't move it back.
        var late = await (await app.DeliverAsync(app.WhatsApp.StatusWebhook(wamid, "sent"))).Content.ReadFromJsonAsync<WebhookAckDto>();
        Assert.Equal("ignored", late!.Status);
        var read = await GetAsync(s, message.Id);
        Assert.Equal(("READ", "utility"), (read.Summary.Status, read.PricingCategory));
        Assert.NotNull(read.Summary.DeliveredAt);
        Assert.Equal(["QUEUED", "SENT", "DELIVERED", "READ"], read.Steps.Select(x => x.Status));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM webhook_events WHERE provider = 'WHATSAPP' AND event_id = '{wamid}:delivered'"));
        Assert.Equal("IGNORED", await ScalarAsync($"SELECT status FROM webhook_events WHERE event_id = '{wamid}:sent'"));
        // No phone number in what is kept of a webhook.
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM webhook_events WHERE provider = 'WHATSAPP' AND payload::text LIKE '%919800000000%'"));

        // A failure: the message fails with WhatsApp's reason (via the test button, which goes through the same webhook).
        var other = await BookAsync(s, Today.AddDays(6), guest: await GuestAsync(s.Owner, "Ravi", "+91 99000 11122", null));
        await app.DispatchAsync();
        var otherWa = (await MessagesAsync(s, other.Id)).Single(m => m.Channel == "WHATSAPP");
        var failed = await OkAsync<MessageDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/messages/{otherWa.Id}/fake-status", new FakeWhatsAppStatusRequest("FAILED")));
        Assert.Equal(("FAILED", "WhatsApp couldn't deliver it: Message undeliverable: Test failure from the fake WhatsApp. (error 131026).", true),
            (failed.Summary.Status, failed.Summary.Reason, failed.Summary.CanRetry));
        Assert.Equal("The test status is DELIVERED, READ or FAILED.",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/messages/{otherWa.Id}/fake-status", new FakeWhatsAppStatusRequest("SEEN")), "Status"));

        // Not ours, and a guest's reply: acknowledged, nothing changes, the reply isn't stored.
        Assert.Equal("ignored", (await (await app.DeliverAsync(app.WhatsApp.StatusWebhook("wamid.SOMEONE-ELSE", "delivered"))).Content
            .ReadFromJsonAsync<WebhookAckDto>())!.Status);
        var reply = await (await app.DeliverAsync(app.WhatsApp.ReplyWebhook("919820012345", "What time is breakfast?"))).Content.ReadFromJsonAsync<WebhookAckDto>();
        Assert.Contains("1 guest reply not read", reply!.Message);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM webhook_events WHERE payload::text LIKE '%breakfast%'"));

        // Meta's check of the webhook URL: the challenge with the right token, 403 with a wrong one.
        var anon = app.NewClient();
        var verify = await anon.GetAsync("/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=fake-verify-token&hub.challenge=1158201444");
        Assert.Equal((HttpStatusCode.OK, "1158201444"), (verify.StatusCode, await verify.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await anon.GetAsync("/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=guess&hub.challenge=1")).StatusCode);
    }

    // ------------------------------------------------------------------ why a message didn't go; retries

    [Fact]
    public async Task A_message_that_cant_go_says_why_and_one_that_fails_for_a_passing_reason_is_tried_again_later()
    {
        var s = await SetupAsync();

        // No email; and a guest who asked for no WhatsApp booking messages.
        var noEmail = await GuestAsync(s.Owner, "Ravi", "+91 99000 11122", null);
        var optOut = await GuestAsync(s.Owner, "Neha", "+91 99000 55566", "neha@example.com");
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsync($"/api/v1/guests/{optOut.Id}/consents",
            new RecordConsentRequest("SERVICE_WHATSAPP", false, "FRONT_DESK"))).StatusCode);
        var r1 = await BookAsync(s, Today.AddDays(9), guest: noEmail);
        var r2 = await BookAsync(s, Today.AddDays(9), guest: optOut);
        var e1 = (await MessagesAsync(s, r1.Id)).Single(m => m.Channel == "EMAIL");
        Assert.Equal(("SKIPPED", "Not sent", $"Ravi {noEmail.LastName} has no email address. Add it on the guest's profile.", false),
            (e1.Status, e1.StatusWords, e1.Reason, e1.CanRetry));
        var w2 = (await MessagesAsync(s, r2.Id)).Single(m => m.Channel == "WHATSAPP");
        Assert.Equal("SKIPPED", w2.Status);
        Assert.StartsWith($"Neha {optOut.LastName} asked not to get booking messages on WhatsApp (recorded ", w2.Reason);
        var problems = await s.Owner.GetJsonAsync<PagedResult<MessageListItemDto>>($"{Url(s.Pid)}/messages?status=PROBLEM");
        Assert.Equal(2, problems.Total);
        Assert.Equal("MESSAGE_NOT_FAILED", (await ProblemAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/messages/{e1.Id}/retry"))).Code);
        // A skipped message is never sent later, even by SQL.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE outbox_messages SET status = 'QUEUED' WHERE id = '{e1.Id}'"));
        Assert.Equal("ck_outbox_messages_frozen", ex.ConstraintName);
        await app.DispatchAsync();

        // WhatsApp is down: the message waits and is tried again later (not at once), then goes.
        app.Clock.At = DateTimeOffset.UtcNow;
        app.WhatsApp.FailNext((HttpStatusCode.ServiceUnavailable, 131000, "Something went wrong"));
        var r3 = await BookAsync(s, Today.AddDays(9));
        await app.DispatchAsync();
        var w3 = (await MessagesAsync(s, r3.Id)).Single(m => m.Channel == "WHATSAPP");
        Assert.Equal(("QUEUED", 1), (w3.Status, w3.Attempts));
        Assert.Equal("Try 1 of 5 didn't work (WhatsApp refused it: Something went wrong (error 131000)); it is tried again automatically.", w3.Reason);
        await app.DispatchAsync();
        Assert.Equal(1, (await GetAsync(s, w3.Id)).Summary.Attempts);
        app.Clock.At = app.Clock.At.Value.AddMinutes(2);
        await app.DispatchAsync();
        Assert.Equal(("SENT", 2, (string?)null), ((await GetAsync(s, w3.Id)).Summary.Status, (await GetAsync(s, w3.Id)).Summary.Attempts, (await GetAsync(s, w3.Id)).Summary.Reason));

        // Not on WhatsApp: fails at once with the reason; Send again sends it (the tries start again).
        app.WhatsApp.FailNext((HttpStatusCode.BadRequest, 131026, "Message undeliverable"));
        app.Email.FailNext((HttpStatusCode.UnprocessableEntity, "The domain resort-os.local is not verified."));
        var r4 = await BookAsync(s, Today.AddDays(9));
        await app.DispatchAsync();
        var w4 = (await MessagesAsync(s, r4.Id)).Single(m => m.Channel == "WHATSAPP");
        var m4 = (await MessagesAsync(s, r4.Id)).Single(m => m.Channel == "EMAIL");
        Assert.Equal(("FAILED", "WhatsApp refused it: Message undeliverable (error 131026)", true), (w4.Status, w4.Reason, w4.CanRetry));
        Assert.Equal(("FAILED", "the email provider refused it: The domain resort-os.local is not verified. (422)"), (m4.Status, m4.Reason));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.PostAsync($"{Url(s.Pid)}/messages/{w4.Id}/retry")).StatusCode);
        var again = await OkAsync<MessageDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/messages/{w4.Id}/retry"));
        Assert.Equal(("SENT", 1, false), (again.Summary.Status, again.Summary.Attempts, again.Summary.CanRetry));
        Assert.Equal("SENT", (await OkAsync<MessageDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/messages/{m4.Id}/retry"))).Summary.Status);
        app.Clock.At = null;

        // A template turned off: the next message is not sent, and says so.
        var templates = await s.Owner.GetJsonAsync<List<MessageTemplateDto>>($"/api/v1/properties/{s.Pid}/message-templates");
        var email = templates.Single(t => t.Code == "booking_confirmation" && t.Channel == "EMAIL");
        await OkAsync<MessageTemplateDto>(await s.Owner.PutAsync($"/api/v1/properties/{s.Pid}/message-templates/{email.Id}",
            new SaveMessageTemplateRequest(null, "en", email.Subject, email.Body, false, email.Version)));
        var r5 = await BookAsync(s, Today.AddDays(9));
        Assert.Equal("The Email template \"Booking confirmation\" is turned off on the Messages page.",
            (await MessagesAsync(s, r5.Id)).Single(m => m.Channel == "EMAIL").Reason);
    }

    // ------------------------------------------------------------------ templates

    [Fact]
    public async Task Templates_are_edited_without_a_release_and_a_mistake_is_explained_under_its_field()
    {
        var s = await SetupAsync();
        var templates = await s.Manager.GetJsonAsync<List<MessageTemplateDto>>($"/api/v1/properties/{s.Pid}/message-templates");
        Assert.Equal(12, templates.Count);
        var wa = templates.Single(t => t.Code == "booking_confirmation" && t.Channel == "WHATSAPP");
        Assert.Equal(("booking_confirmation", "en", (string?)null), (wa.ProviderTemplate, wa.Language, wa.Subject));
        Assert.Contains(wa.Variables, v => v.Name == "guest_name" && v.Sample == "Asha");
        Assert.Contains("pay_link", templates.First(t => t.Code == "payment_reminder").Variables.Select(v => v.Name));
        var url = $"/api/v1/properties/{s.Pid}/message-templates/{wa.Id}";

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PutAsync(url, new SaveMessageTemplateRequest("x", "en", null, "Hi {{guest_name}}", true, null))).StatusCode);
        Assert.Equal("{{gest_name}} isn't something this message can fill in. It can use: {{guest_name}}, {{property_name}}, {{reference}}, {{check_in}}, " +
                     "{{check_out}}, {{nights}}, {{rooms}}, {{check_in_time}}, {{check_out_time}}, {{property_phone}}.",
            await FieldErrorAsync(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("booking_confirmation", "en", null, "Hello {{gest_name}}", true, null)), "Body"));
        Assert.Equal("A WhatsApp template's name is lower-case letters, digits and _ only, exactly as in WhatsApp Manager, e.g. booking_confirmation.",
            await FieldErrorAsync(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("Booking Confirmation", "en", null, "Hi {{guest_name}}", true, null)), "ProviderTemplate"));
        Assert.Equal("Give the name of the template approved in WhatsApp Manager, e.g. booking_confirmation.",
            await FieldErrorAsync(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest(" ", "en", null, "Hi {{guest_name}}", true, null)), "ProviderTemplate"));
        Assert.Equal("The language is WhatsApp's code for it, e.g. en, en_US, hi or mr.",
            await FieldErrorAsync(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("booking_confirmation", "English", null, "Hi {{guest_name}}", true, null)), "Language"));
        Assert.Equal($"A WhatsApp template's text is at most 1,024 characters; this is 1,036.",
            await FieldErrorAsync(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("booking_confirmation", "en", null, "Hi {{guest_name}}" + new string('x', 1019), true, null)), "Body"));
        var mail = templates.Single(t => t.Code == "booking_confirmation" && t.Channel == "EMAIL");
        Assert.Equal("Give the email a subject, 2–150 characters, e.g. Your booking {{reference}} is confirmed.",
            await FieldErrorAsync(await s.Owner.PutAsync($"/api/v1/properties/{s.Pid}/message-templates/{mail.Id}",
                new SaveMessageTemplateRequest(null, "en", "", "Hi {{guest_name}}", true, null)), "Subject"));

        // Saved: the next booking's message uses the new wording and the new approved template name.
        var saved = await OkAsync<MessageTemplateDto>(await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("booking_confirmed_v2", "en_US", null,
            "Namaste {{guest_name}}! {{reference}} is confirmed for {{check_in}}.", true, wa.Version)));
        Assert.Equal(("booking_confirmed_v2", "en_US"), (saved.ProviderTemplate, saved.Language));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PutAsync(url, new SaveMessageTemplateRequest("x", "en", null, "Hi {{guest_name}}", true, wa.Version))).StatusCode);
        var booking = await BookAsync(s, Today.AddDays(10));
        await app.DispatchAsync();
        var sent = app.WhatsApp.Sent.Last(m => m.To == s.Phone);
        Assert.Equal(("booking_confirmed_v2", "en_US", 3), (sent.Template, sent.Language, sent.Parameters.Count));
        Assert.Equal($"Namaste Asha! {booking.Reference} is confirmed for {Day(booking.CheckIn)}.",
            (await GetAsync(s, (await MessagesAsync(s, booking.Id)).Single(m => m.Channel == "WHATSAPP").Id)).Body);
        Assert.Equal("booking_confirmed_v2", await ScalarAsync($"SELECT changes->'ProviderTemplate'->>'new' FROM audit_logs WHERE entity_id = '{wa.Id}' ORDER BY id DESC LIMIT 1"));
    }

    // ------------------------------------------------------------------ the other jobs and the bell

    [Fact]
    public async Task The_other_jobs_remind_about_payment_departures_and_low_stock_and_staff_see_it_in_the_bell()
    {
        var s = await SetupAsync();

        // Payment: a pending booking with a link made more than a day ago gets the link again, once.
        var pending = await BookAsync(s, Today.AddDays(4), status: "PENDING");
        Assert.Empty(await MessagesAsync(s, pending.Id));   // not confirmed: no confirmation
        var link = await OkAsync<PaymentLinkDto>(await s.Owner.PostAsync($"/api/v1/properties/{s.Pid}/payments/razorpay/orders",
            new CreatePaymentLinkRequest(pending.Id, 7000m), new Dictionary<string, string> { ["Idempotency-Key"] = "link-" + Guid.NewGuid() }), HttpStatusCode.Created);
        Assert.Equal(0, (await RunAsync(s, "payment_reminder")).Found);
        await ExecuteAsync($"UPDATE payment_gateway_orders SET created_at = now() - interval '25 hours' WHERE id = '{link.Id}'");
        var pay = await RunAsync(s, "payment_reminder");
        Assert.Equal((1, 2), (pay.Found, pay.Queued));
        Assert.Equal(1, (await RunAsync(s, "payment_reminder")).Already);
        await app.DispatchAsync();
        var payWa = app.WhatsApp.Sent.Last(m => m.Template == "payment_reminder");
        Assert.Equal(("₹7,000", "https://book.test.local" + link.PayPath), (payWa.Parameters["amount"], payWa.Parameters["pay_link"]));
        Assert.Contains("https://book.test.local" + link.PayPath + "\">", app.Email.Sent.Last(m => m.Subject.Contains("waiting for payment")).Html);

        // Departures: a guest in house leaving today.
        // (a stay that began yesterday: booked for today, then moved back a day and checked in, in the database)
        var stay = await BookAsync(s, Today, nights: 1);
        await ExecuteAsync($"UPDATE reservations SET status = 'CHECKED_IN', checked_in_at = now(), check_in = check_in - 1, check_out = check_out - 1 WHERE id = '{stay.Id}'");
        var checkout = await RunAsync(s, "checkout_reminder");
        Assert.Equal((1, 1), (checkout.Found, checkout.Queued));   // WhatsApp only

        // Low stock: the people who manage stock get it in the bell and by email, once a day.
        var inv = $"/api/v1/properties/{s.Pid}/inventory";
        var store = await OkAsync<StockLocationDto>(await s.Owner.PostAsync($"{inv}/locations", new SaveStockLocationRequest("STORE", "Main store", false, 1)), HttpStatusCode.Created);
        var group = await OkAsync<InventoryCategoryDto>(await s.Owner.PostAsync($"{inv}/categories", new SaveInventoryCategoryRequest("Kitchen", 1)), HttpStatusCode.Created);
        var kg = (await s.Owner.GetJsonAsync<InventoryDto>(inv)).Units.Single(u => u.Code == "kg").Id;
        await OkAsync<InventoryItemDto>(await s.Owner.PostAsync($"{inv}/items", new SaveInventoryItemRequest(group.Id, "RICE", "Basmati rice", kg, 90m, 10m, true, 4m, store.Id, null)),
            HttpStatusCode.Created);
        await OkAsync<InventoryItemDto>(await s.Owner.PostAsync($"{inv}/items", new SaveInventoryItemRequest(group.Id, "DAL", "Toor dal", kg, 120m, 5m, true, 20m, store.Id, null)),
            HttpStatusCode.Created);
        var (mgrEmail, mgrPw) = await app.CreateUserAsync("INVENTORY_MANAGER", s.Pid);
        var stockMgr = await app.SignInAsync(mgrEmail, mgrPw);
        var low = await RunAsync(s, "inventory_alert");
        Assert.Equal(1, low.Found);
        Assert.True(low.Queued >= 4);   // the owner and the stock manager, in the bell and by email
        Assert.Equal(0, (await RunAsync(s, "inventory_alert")).Queued);
        await app.DispatchAsync();
        var bell = await stockMgr.GetJsonAsync<NotificationListDto>("/api/v1/notifications");
        var n = Assert.Single(bell.Items);
        Assert.Equal((1, "Low stock: 1 to reorder", "At or below their reorder level: Basmati rice 4 kg (reorder at 10 kg).", "/inventory"),
            (bell.Unread, n.Title, n.Body, n.Link));
        Assert.Contains(app.Email.Sent, m => m.To == mgrEmail && m.Subject == $"Low stock at {s.Name}: 1 to reorder");
        var after = await OkAsync<NotificationListDto>(await stockMgr.PostAsync($"/api/v1/notifications/{n.Id}/read"));
        Assert.Equal(0, after.Unread);
        Assert.Equal("READ", await ScalarAsync($"SELECT status FROM outbox_messages WHERE id = (SELECT outbox_message_id FROM notifications WHERE id = '{n.Id}')"));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Manager.PostAsync($"/api/v1/notifications/{n.Id}/read")).StatusCode);   // not theirs

        // Nothing to do: says so.
        Assert.Equal("Leave reminder: nothing to send today.", (await RunAsync(s, "leave_reminder")).Message);
        var runs = await s.Owner.GetJsonAsync<List<JobRunDto>>($"{Url(s.Pid)}/jobs/runs");
        Assert.Contains(runs, r => r.Job == "payment_reminder" && r.ReservationReference == pending.Reference && r.TriggeredBy == "MANUAL" && r.RunByName == "Test Owner");
    }

    [Fact]
    public async Task Anonymising_a_guest_removes_their_details_from_messages_and_another_tenant_sees_nothing()
    {
        var s = await SetupAsync();
        var booking = await BookAsync(s, Today.AddDays(12));
        await app.DispatchAsync();
        var guestId = booking.Guests[0].GuestId;
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsync($"/api/v1/guests/{guestId}/anonymise", new AnonymiseGuestRequest("Guest asked us to delete their data"))).StatusCode);
        var msgs = await MessagesAsync(s, booking.Id);
        Assert.All(msgs, m => Assert.Equal(("Anonymised guest", "—"), (m.RecipientName, m.To)));
        Assert.Equal("(removed: the guest was anonymised)", (await GetAsync(s, msgs[0].Id)).Body);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM outbox_messages WHERE guest_id = '{guestId}' AND (to_address IS NOT NULL OR body LIKE '%Asha%')"));

        var (_, _, otherEmail, otherPw) = await app.CreateSecondTenantAsync();
        var other = await app.SignInAsync(otherEmail, otherPw);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Url(s.Pid)}/messages/{msgs[0].Id}")).StatusCode);
        Assert.Empty((await other.GetJsonAsync<NotificationListDto>("/api/v1/notifications")).Items);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private sealed record Setup(Guid Pid, string Name, TestClient Owner, TestClient Manager, TestClient Housekeeper, Stay Stay, GuestDto Asha, string Phone,
        string Email, Guid Source);

    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "N" + Suffix();
        var name = $"Sea Breeze {code}";
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, name, null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", "+91 98200 00000", null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        var manager = await StaffAsync("MANAGER", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var stay = await BookingKit.SetupAsync(owner, pid, rooms: 6);
        var phone = "+91982" + Random.Shared.Next(1000, 9999) + "345";
        var email = "as" + Suffix().ToLowerInvariant() + "@example.com";
        var asha = await GuestAsync(owner, "Asha", phone, email);
        return new Setup(pid, name, owner, manager, housekeeper, stay, asha, phone, email, await WalkInAsync(owner, pid));
    }

    private static async Task<GuestDto> GuestAsync(TestClient c, string first, string? phone, string? email)
    {
        var res = await c.PostAsync("/api/v1/guests", new CreateGuestRequest(first, "G" + Suffix().ToLowerInvariant(), phone, email, null, "IN",
            null, null, null, false, null, null, IgnoreDuplicates: true));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<GuestDto>())!;
    }

    private static async Task<ReservationDto> BookAsync(Setup s, DateOnly from, int nights = 2, GuestDto? guest = null, string? status = null)
    {
        var body = NewBooking(s.Stay, (guest ?? s.Asha).Id, s.Source, from, from.AddDays(nights));
        if (status is not null) body = body with { Status = status };
        return await CreatedAsync(await BookingKit.BookAsync(s.Owner, s.Pid, body));
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static async Task<JobRunResultDto> RunAsync(Setup s, string job) =>
        await OkAsync<JobRunResultDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/jobs/{job}/run"));

    private static async Task<List<MessageListItemDto>> MessagesAsync(Setup s, Guid reservationId) =>
        (await s.Owner.GetJsonAsync<PagedResult<MessageListItemDto>>($"{Url(s.Pid)}/messages?reservationId={reservationId}&pageSize=100")).Items.ToList();

    private static Task<MessageDto> GetAsync(Setup s, Guid id) => s.Owner.GetJsonAsync<MessageDto>($"{Url(s.Pid)}/messages/{id}");

    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ApiError> ProblemAsync(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ApiError>())!;

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
