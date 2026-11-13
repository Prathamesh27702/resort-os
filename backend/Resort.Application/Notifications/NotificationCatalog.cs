using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Resort.Domain.Notifications;

namespace Resort.Application.Notifications;

/// <summary>A {{variable}} a message can use, what it means and an example (for the template editor's preview).</summary>
public sealed record MessageVariable(string Name, string Description, string Sample);

/// <summary>A template's default wording, created at a property the first time messages are used there.</summary>
public sealed record DefaultTemplate(string Code, string Channel, string Name, string? ProviderTemplate, string? Subject, string Body);

/// <summary>A scheduled job: its name and what it does in words, the local time it runs by default, and the message it sends.</summary>
public sealed record JobDefinition(string Code, string Name, string Description, TimeOnly DefaultRunAt, string MessageCode);

/// <summary>
/// Everything Resort OS can send (Sprint 24): the messages, which variables each can use, their default wording on each
/// channel, and the scheduled jobs. Wording is edited per property on the Messages page; this is only where it starts.
/// The WhatsApp wording must match the template approved in WhatsApp Manager under the same name, with the same named
/// parameters ({{guest_name}} …), because WhatsApp sends its approved copy, filled in with our values.
/// </summary>
public static class NotificationCatalog
{
    public const string PreArrivalDaysSetting = "notifications.pre_arrival_days";
    public const int DefaultPreArrivalDays = 3;

    private static readonly MessageVariable[] Stay =
    [
        new("guest_name", "The guest's first name", "Asha"),
        new("property_name", "The property's name", "Sea Breeze Resort"),
        new("reference", "The booking reference", "R00042"),
        new("check_in", "The arrival day", "Sat 14 Nov 2026"),
        new("check_out", "The departure day", "Mon 16 Nov 2026"),
        new("nights", "How many nights", "2 nights"),
        new("rooms", "The rooms booked", "1 × Garden cottage"),
        new("check_in_time", "Check-in time at the property", "2:00 pm"),
        new("check_out_time", "Check-out time at the property", "11:00 am"),
        new("property_phone", "The property's phone number", "+91 98200 12345"),
    ];

    public static readonly IReadOnlyDictionary<string, string> MessageNames = new Dictionary<string, string>
    {
        [MessageCodes.BookingConfirmation] = "Booking confirmation",
        [MessageCodes.ReservationReminder] = "Day-before reminder",
        [MessageCodes.PreArrival] = "Pre-arrival message",
        [MessageCodes.CheckoutReminder] = "Check-out reminder",
        [MessageCodes.PaymentReminder] = "Payment reminder",
        [MessageCodes.InventoryAlert] = "Low stock alert",
        [MessageCodes.LeaveReminder] = "Leave reminder",
    };

    /// <summary>When each message goes, in words.</summary>
    public static readonly IReadOnlyDictionary<string, string> MessageWhen = new Dictionary<string, string>
    {
        [MessageCodes.BookingConfirmation] = "When a booking is confirmed (booked as confirmed, confirmed by staff, or paid online).",
        [MessageCodes.ReservationReminder] = "The day before arrival, to guests with a confirmed booking.",
        [MessageCodes.PreArrival] = "A few days before arrival (notifications.pre_arrival_days, 3), with check-in details.",
        [MessageCodes.CheckoutReminder] = "On the morning of departure, to guests in house.",
        [MessageCodes.PaymentReminder] = "To bookings still waiting for payment a day after their payment link was made, once.",
        [MessageCodes.InventoryAlert] = "Every morning there are items at or below their reorder level, to staff who manage stock.",
        [MessageCodes.LeaveReminder] = "Every morning leave requests have waited more than a day, to staff who approve leave.",
    };

    public static readonly IReadOnlyDictionary<string, MessageVariable[]> Variables = new Dictionary<string, MessageVariable[]>
    {
        [MessageCodes.BookingConfirmation] = Stay,
        [MessageCodes.ReservationReminder] = Stay,
        [MessageCodes.PreArrival] = Stay,
        [MessageCodes.CheckoutReminder] = Stay,
        [MessageCodes.PaymentReminder] =
        [
            .. Stay,
            new("amount", "The amount of the payment link", "₹7,000"),
            new("pay_link", "The guest's payment page", "https://book.example.com/pay/k3J9…"),
        ],
        [MessageCodes.InventoryAlert] =
        [
            new("property_name", "The property's name", "Sea Breeze Resort"),
            new("count", "How many items are low", "2"),
            new("items", "The items, what is left and their reorder level", "Basmati rice 4 kg (reorder at 10 kg); Hand towels 6 pcs (reorder at 12 pcs)"),
        ],
        [MessageCodes.LeaveReminder] =
        [
            new("property_name", "The property's name", "Sea Breeze Resort"),
            new("count", "How many requests are waiting", "1"),
            new("requests", "Who asked for what", "Kavya Naik: Casual leave, 2 days from Sat 14 Nov 2026"),
        ],
    };

    public static readonly DefaultTemplate[] Templates =
    [
        new(MessageCodes.BookingConfirmation, MessageChannels.WhatsApp, "Booking confirmation", "booking_confirmation", null,
            "Hello {{guest_name}}, your booking {{reference}} at {{property_name}} is confirmed: {{check_in}} to {{check_out}} ({{nights}}), " +
            "{{rooms}}. Check-in is from {{check_in_time}}. Questions? Call us on {{property_phone}}."),
        new(MessageCodes.BookingConfirmation, MessageChannels.Email, "Booking confirmation", null,
            "Your booking {{reference}} at {{property_name}} is confirmed",
            "Hello {{guest_name}},\n\nThank you for booking with {{property_name}}. Your booking {{reference}} is confirmed.\n\n" +
            "Arrival: {{check_in}} (check-in from {{check_in_time}})\nDeparture: {{check_out}} (check-out by {{check_out_time}})\n" +
            "Stay: {{nights}}, {{rooms}}\n\nIf you have any questions, call us on {{property_phone}}.\n\nSee you soon,\n{{property_name}}"),
        new(MessageCodes.ReservationReminder, MessageChannels.WhatsApp, "Day-before reminder", "reservation_reminder", null,
            "Hello {{guest_name}}, we look forward to welcoming you at {{property_name}} tomorrow, {{check_in}}. Check-in is from " +
            "{{check_in_time}}. Booking {{reference}}. Need directions? Call {{property_phone}}."),
        new(MessageCodes.ReservationReminder, MessageChannels.Email, "Day-before reminder", null,
            "See you tomorrow at {{property_name}}",
            "Hello {{guest_name}},\n\nWe look forward to welcoming you tomorrow, {{check_in}}. Check-in is from {{check_in_time}}.\n\n" +
            "Booking {{reference}}: {{nights}}, {{rooms}}, until {{check_out}}.\n\nNeed directions? Call us on {{property_phone}}.\n\n{{property_name}}"),
        new(MessageCodes.PreArrival, MessageChannels.WhatsApp, "Pre-arrival message", "pre_arrival", null,
            "Hello {{guest_name}}, your stay at {{property_name}} begins on {{check_in}} ({{nights}}). Check-in from {{check_in_time}}, " +
            "check-out by {{check_out_time}}. Call {{property_phone}} with your arrival time or any requests. Booking {{reference}}."),
        new(MessageCodes.PreArrival, MessageChannels.Email, "Pre-arrival message", null,
            "Your stay at {{property_name}} begins on {{check_in}}",
            "Hello {{guest_name}},\n\nYour stay begins on {{check_in}} ({{nights}}, {{rooms}}).\n\nCheck-in is from {{check_in_time}} and " +
            "check-out by {{check_out_time}} on {{check_out}}. Call us on {{property_phone}} with your arrival time or any requests.\n\n" +
            "Booking {{reference}}\n{{property_name}}"),
        new(MessageCodes.CheckoutReminder, MessageChannels.WhatsApp, "Check-out reminder", "checkout_reminder", null,
            "Good morning {{guest_name}}, check-out today is by {{check_out_time}}. Your bill is ready at the front desk. " +
            "Thank you for staying at {{property_name}}!"),
        new(MessageCodes.PaymentReminder, MessageChannels.WhatsApp, "Payment reminder", "payment_reminder", null,
            "Hello {{guest_name}}, your booking {{reference}} at {{property_name}} for {{check_in}} is waiting for a payment of " +
            "{{amount}}. Pay securely here: {{pay_link}}"),
        new(MessageCodes.PaymentReminder, MessageChannels.Email, "Payment reminder", null,
            "Your booking {{reference}} is waiting for payment",
            "Hello {{guest_name}},\n\nYour booking {{reference}} at {{property_name}} ({{check_in}} to {{check_out}}) is waiting for a " +
            "payment of {{amount}}.\n\nPay securely by UPI, card or netbanking here:\n{{pay_link}}\n\nQuestions? Call {{property_phone}}.\n\n{{property_name}}"),
        new(MessageCodes.InventoryAlert, MessageChannels.InApp, "Low stock alert", null,
            "Low stock: {{count}} to reorder", "At or below their reorder level: {{items}}."),
        new(MessageCodes.InventoryAlert, MessageChannels.Email, "Low stock alert", null,
            "Low stock at {{property_name}}: {{count}} to reorder",
            "These items are at or below their reorder level:\n\n{{items}}\n\nRaise a purchase order on the Purchasing page."),
        new(MessageCodes.LeaveReminder, MessageChannels.InApp, "Leave reminder", null,
            "Leave: {{count}} waiting for a decision", "Waiting for a decision for over a day: {{requests}}."),
    ];

    public static readonly JobDefinition[] Jobs =
    [
        new(JobCodes.ReservationReminder, "Day-before reminder", "Guests with a confirmed booking arriving tomorrow get a reminder on WhatsApp and by email.",
            new TimeOnly(10, 0), MessageCodes.ReservationReminder),
        new(JobCodes.PreArrival, "Pre-arrival message", "Guests arriving in 3 days (notifications.pre_arrival_days) get the check-in details.",
            new TimeOnly(10, 0), MessageCodes.PreArrival),
        new(JobCodes.CheckoutReminder, "Check-out reminder", "Guests in house who leave today get the check-out time.",
            new TimeOnly(8, 0), MessageCodes.CheckoutReminder),
        new(JobCodes.PaymentReminder, "Payment reminder",
            "Bookings still waiting for payment, whose payment link was made more than a day ago, get the link again, once.",
            new TimeOnly(11, 0), MessageCodes.PaymentReminder),
        new(JobCodes.InventoryAlert, "Low stock alert", "Staff who manage stock get the items at or below their reorder level, once a day.",
            new TimeOnly(8, 0), MessageCodes.InventoryAlert),
        new(JobCodes.LeaveReminder, "Leave reminder", "Staff who approve leave get the requests that have waited more than a day, once a day.",
            new TimeOnly(9, 0), MessageCodes.LeaveReminder),
    ];

    public static JobDefinition Job(string code) => Jobs.SingleOrDefault(j => j.Code == code)
        ?? throw new Common.NotFoundAppException($"There is no job called {code}. The jobs are: {string.Join(", ", Jobs.Select(j => j.Code))}.");
}

/// <summary>Filling in templates and showing addresses safely. Pure functions.</summary>
public static partial class MessageText
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The variables a text uses, in the order they first appear.</summary>
    public static IReadOnlyList<string> Placeholders(string? text) =>
        text is null ? [] : Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>The text with each {{variable}} replaced by its value (a variable without a value becomes empty).</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.GetValueOrDefault(m.Groups[1].Value, ""));

    /// <summary>A WhatsApp parameter can't have new lines, tabs or more than four spaces in a row; it is at most 1,024 characters.</summary>
    public static string WhatsAppParameter(string value)
    {
        var s = Regex.Replace(value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '), " {4,}", "   ").Trim();
        return s.Length <= 1024 ? s : s[..1021] + "...";
    }

    /// <summary>The variables a text uses that the message doesn't have, e.g. a typo like {{gest_name}}.</summary>
    public static IReadOnlyList<string> Unknown(string? text, IEnumerable<string> known)
    {
        var k = known.ToHashSet(StringComparer.Ordinal);
        return Placeholders(text).Where(p => !k.Contains(p)).ToList();
    }

    /// <summary>A phone number or email shown to staff without all of it: +91 98•••••345, as•••@example.com.</summary>
    public static string Mask(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "—";
        var at = address.IndexOf('@');
        if (at > 0)
            return address[..Math.Min(2, at)] + "•••" + address[at..];
        if (address.Length <= 6) return new string('•', address.Length);
        var cc = address.StartsWith("+91", StringComparison.Ordinal) ? 3 : Math.Min(3, address.Length - 5);
        return address[..cc] + " " + address[cc..(cc + 2)] + new string('•', address.Length - cc - 5) + address[^3..];
    }

    public static string Nights(int n) => n == 1 ? "1 night" : $"{n} nights";

    public static string Time(TimeOnly t) => t.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>A plain-text email as simple HTML: escaped, with line breaks and links that can be tapped.</summary>
    public static string Html(string text)
    {
        var sb = new StringBuilder("<div style=\"font-family:Arial,sans-serif;font-size:15px;line-height:1.5;color:#1f2933\">");
        foreach (var line in text.Split('\n'))
        {
            var escaped = System.Net.WebUtility.HtmlEncode(line.TrimEnd('\r'));
            escaped = Regex.Replace(escaped, @"https?://[^\s<]+", m => $"<a href=\"{m.Value}\">{m.Value}</a>");
            sb.Append(escaped).Append("<br>");
        }
        return sb.Append("</div>").ToString();
    }

    /// <summary>
    /// The email as the guest sees it: the property's name in a header band, the text on a white card, and a footer with
    /// the property's phone and email when they are set. Tables and inline styles only, so Gmail, Outlook and phone mail
    /// apps all show it the same way; the plain text goes alongside for mail apps that don't show HTML.
    /// </summary>
    public static string BrandedHtml(string text, string propertyName, string? subject, string? phone, string? email)
    {
        string E(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
        var contact = string.Join(" &nbsp;|&nbsp; ", new[] { phone, email }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(E));
        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#eef5f3;padding:24px 0\"><tr><td align=\"center\">");
        sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #d9e6e3\">");
        sb.Append("<tr><td style=\"background:#0a3a40;padding:22px 28px;font-family:Arial,sans-serif\">");
        sb.Append("<div style=\"color:#ffffff;font-size:20px;font-weight:bold;letter-spacing:-0.2px\">").Append(E(propertyName)).Append("</div>");
        if (!string.IsNullOrWhiteSpace(subject))
            sb.Append("<div style=\"color:#b6d8d4;font-size:13px;margin-top:4px\">").Append(E(subject)).Append("</div>");
        sb.Append("</td></tr><tr><td style=\"padding:26px 28px\">").Append(Html(text)).Append("</td></tr>");
        sb.Append("<tr><td style=\"padding:16px 28px;background:#f6faf9;border-top:1px solid #e3eeeb;font-family:Arial,sans-serif;font-size:12px;color:#5b6b6e\">");
        sb.Append(E(propertyName));
        if (contact.Length > 0) sb.Append("<br>").Append(contact);
        sb.Append("</td></tr></table></td></tr></table>");
        return sb.ToString();
    }
}
