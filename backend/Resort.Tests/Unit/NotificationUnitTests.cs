using Resort.Application.Notifications;
using Resort.Domain.Notifications;
using Resort.Domain.Organizations;
using Resort.Infrastructure.Notifications;

namespace Resort.Tests.Unit;

/// <summary>Sprint 24 — the pure parts of notifications: filling templates, WhatsApp's parameter rules, masking addresses,
/// when a daily job runs next at the property, the forward-only delivery rank, the webhook signature, and that the default
/// templates only use variables their message has.</summary>
public class NotificationUnitTests
{
    [Fact]
    public void Templates_are_filled_from_named_variables_and_unknown_ones_are_found()
    {
        var text = "Hello {{guest_name}}, booking {{ reference }} on {{check_in}}. {{guest_name}}!";
        Assert.Equal(["guest_name", "reference", "check_in"], MessageText.Placeholders(text));
        Assert.Equal("Hello Asha, booking R00042 on . Asha!",
            MessageText.Fill(text, new Dictionary<string, string> { ["guest_name"] = "Asha", ["reference"] = "R00042" }));
        Assert.Equal(["gest_name", ""], MessageText.Unknown("{{gest_name}} {{}} {{guest_name}}", ["guest_name"]));
        Assert.Empty(MessageText.Placeholders(null));
    }

    [Fact]
    public void A_WhatsApp_parameter_has_no_new_lines_tabs_or_long_runs_of_spaces_and_is_at_most_1024_characters()
    {
        Assert.Equal("Rice 4 kg; Dal   2 kg", MessageText.WhatsAppParameter(" Rice 4 kg;\nDal\t      2 kg "));
        Assert.Equal(1024, MessageText.WhatsAppParameter(new string('x', 2000)).Length);
        Assert.EndsWith("...", MessageText.WhatsAppParameter(new string('x', 2000)));
    }

    [Theory]
    [InlineData("+919820012345", "+91 98•••••345")]
    [InlineData("+447946095800", "+44 79•••••800")]
    [InlineData("asha.rao@example.com", "as•••@example.com")]
    [InlineData("a@b.in", "a•••@b.in")]
    [InlineData(null, "—")]
    public void Addresses_are_shown_masked(string? address, string shown) => Assert.Equal(shown, MessageText.Mask(address));

    [Fact]
    public void Words_used_in_messages()
    {
        Assert.Equal(("1 night", "3 nights"), (MessageText.Nights(1), MessageText.Nights(3)));
        Assert.Equal(("2:00 pm", "11:00 am", "9:30 am"), (MessageText.Time(new TimeOnly(14, 0)), MessageText.Time(new TimeOnly(11, 0)), MessageText.Time(new TimeOnly(9, 30))));
        var html = MessageText.Html("Hi <b>Asha</b>\nPay: https://pay.example.com/pay/abc");
        Assert.Contains("Hi &lt;b&gt;Asha&lt;/b&gt;<br>", html);
        Assert.Contains("<a href=\"https://pay.example.com/pay/abc\">https://pay.example.com/pay/abc</a>", html);
    }

    [Fact]
    public void The_guest_email_carries_the_property_name_and_contact_and_escapes_everything()
    {
        var html = MessageText.BrandedHtml("Hello <Asha>\nSee you soon", "Sea & Sand", "Your booking R00042 is confirmed", "+91 98200 12345", null);
        Assert.Contains("Sea &amp; Sand", html);
        Assert.Contains("Your booking R00042 is confirmed", html);
        Assert.Contains("Hello &lt;Asha&gt;<br>", html);
        Assert.Contains("+91 98200 12345", html);
        Assert.DoesNotContain("&nbsp;|&nbsp;", html);   // no separator when only the phone is set
    }

    [Fact]
    public void A_daily_job_runs_next_today_at_its_local_time_if_still_ahead_otherwise_tomorrow()
    {
        var p = new Property { Timezone = "Asia/Kolkata" };
        // 03:00 UTC = 08:30 IST: 10:00 IST today is 04:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 4, 30, 0, TimeSpan.Zero), JobClock.Next(p, new TimeOnly(10, 0), new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero)));
        // exactly at 10:00 IST: the next one is tomorrow
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 4, 30, 0, TimeSpan.Zero), JobClock.Next(p, new TimeOnly(10, 0), new DateTimeOffset(2026, 11, 7, 4, 30, 0, TimeSpan.Zero)));
        // 20:00 UTC on the 7th is 01:30 IST on the 8th: 10:00 IST on the 8th
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 4, 30, 0, TimeSpan.Zero), JobClock.Next(p, new TimeOnly(10, 0), new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Delivery_only_moves_forward_and_failed_can_follow_sent_but_not_delivered()
    {
        Assert.True(MessageStatuses.Rank(MessageStatuses.Sent) > MessageStatuses.Rank(MessageStatuses.Queued));
        Assert.True(MessageStatuses.Rank(MessageStatuses.Failed) > MessageStatuses.Rank(MessageStatuses.Sent));
        Assert.True(MessageStatuses.Rank(MessageStatuses.Delivered) > MessageStatuses.Rank(MessageStatuses.Failed));
        Assert.True(MessageStatuses.Rank(MessageStatuses.Read) > MessageStatuses.Rank(MessageStatuses.Delivered));
    }

    [Fact]
    public void The_webhook_signature_is_hex_hmac_sha256_of_the_raw_body_with_the_app_secret()
    {
        // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog"), the RFC 4231-style test vector.
        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            WhatsAppOptions.Sign("The quick brown fox jumps over the lazy dog"u8.ToArray(), "key"));
    }

    [Fact]
    public void Without_keys_WhatsApp_and_email_use_the_fakes_outside_production_and_are_off_in_production()
    {
        Assert.Equal((ProviderModes.Fake, ProviderModes.Off), (new WhatsAppOptions().Resolve(false), new WhatsAppOptions().Resolve(true)));
        Assert.Equal(ProviderModes.Live, new WhatsAppOptions { PhoneNumberId = "1", AccessToken = "t" }.Resolve(true));
        Assert.Throws<InvalidOperationException>(() => new WhatsAppOptions { Mode = "Fake" }.Resolve(true));
        Assert.Equal((ProviderModes.Fake, ProviderModes.Off), (new EmailOptions().Resolve(false), new EmailOptions().Resolve(true)));
        Assert.Equal(ProviderModes.Off, new EmailOptions { ApiKey = "re_x" }.Resolve(false));   // a key without a From address
        Assert.Equal(ProviderModes.Live, new EmailOptions { ApiKey = "re_x", FromAddress = "Resort <a@b.in>" }.Resolve(true));
    }

    [Fact]
    public void Every_default_template_uses_only_its_messages_variables_and_fits_WhatsApp()
    {
        foreach (var t in NotificationCatalog.Templates)
        {
            var known = NotificationCatalog.Variables[t.Code].Select(v => v.Name);
            Assert.Empty(MessageText.Unknown(t.Body, known));
            Assert.Empty(MessageText.Unknown(t.Subject, known));
            Assert.Equal(t.Channel == MessageChannels.WhatsApp, t.ProviderTemplate is not null);
            Assert.Equal(t.Channel == MessageChannels.WhatsApp, t.Subject is null);
            if (t.Channel == MessageChannels.WhatsApp) Assert.True(t.Body.Length <= 1024);
        }
        Assert.Equal(NotificationCatalog.MessageNames.Keys.Order(), NotificationCatalog.Templates.Select(t => t.Code).Distinct().Order());
        Assert.Equal(JobCodes.All, NotificationCatalog.Jobs.Select(j => j.Code));
    }
}
