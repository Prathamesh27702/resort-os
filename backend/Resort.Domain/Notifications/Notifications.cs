using Resort.Domain.Common;

namespace Resort.Domain.Notifications;

/// <summary>How a message reaches someone: WhatsApp (guests), email (guests and staff) or the bell in the app (staff).</summary>
public static class MessageChannels
{
    public const string WhatsApp = "WHATSAPP";
    public const string Email = "EMAIL";
    public const string InApp = "IN_APP";

    public static readonly string[] All = [WhatsApp, Email, InApp];

    public static string Words(string c) => c switch
    {
        WhatsApp => "WhatsApp",
        Email => "Email",
        InApp => "In the app",
        _ => c,
    };
}

/// <summary>
/// A message is QUEUED by the business change (or a job) that needs it, then SENT when the provider accepted it, and
/// WhatsApp's status webhooks move it on to DELIVERED and READ. FAILED: it can't be sent, with the reason (a staff member
/// can send it again). SKIPPED: it was never going to be sent (no number, the guest opted out, the template is off), kept
/// so staff can see why the guest didn't get it. Delivery only moves forward: a late "sent" after "read" changes nothing.
/// </summary>
public static class MessageStatuses
{
    public const string Queued = "QUEUED";
    public const string Sent = "SENT";
    public const string Delivered = "DELIVERED";
    public const string Read = "READ";
    public const string Failed = "FAILED";
    public const string Skipped = "SKIPPED";

    public static readonly string[] All = [Queued, Sent, Delivered, Read, Failed, Skipped];

    /// <summary>How far a message got. A status webhook only ever moves it to a higher rank; FAILED can follow SENT
    /// (WhatsApp couldn't deliver it) but never DELIVERED or READ.</summary>
    public static int Rank(string? s) => s switch
    {
        Queued => 0,
        Sent => 1,
        Failed => 2,
        Delivered => 3,
        Read => 4,
        _ => -1,
    };

    public static string Words(string s) => s switch
    {
        Queued => "Waiting to be sent",
        Sent => "Sent",
        Delivered => "Delivered",
        Read => "Read",
        Failed => "Failed",
        Skipped => "Not sent",
        _ => s,
    };
}

public static class RecipientTypes
{
    public const string Guest = "GUEST";
    public const string User = "USER";
}

/// <summary>What wrote the message: a business change (a booking confirmed), a scheduled job, or a person.</summary>
public static class MessageSources
{
    public const string Event = "EVENT";
    public const string Job = "JOB";
    public const string Manual = "MANUAL";
}

/// <summary>The messages Resort OS sends. Each has a template per channel (message_templates).</summary>
public static class MessageCodes
{
    public const string BookingConfirmation = "booking_confirmation";
    public const string ReservationReminder = "reservation_reminder";
    public const string PreArrival = "pre_arrival";
    public const string CheckoutReminder = "checkout_reminder";
    public const string PaymentReminder = "payment_reminder";
    public const string InventoryAlert = "inventory_alert";
    public const string LeaveReminder = "leave_reminder";
}

/// <summary>The scheduled jobs (scheduled_jobs.job, job_runs.job).</summary>
public static class JobCodes
{
    public const string ReservationReminder = "reservation_reminder";
    public const string PreArrival = "pre_arrival";
    public const string CheckoutReminder = "checkout_reminder";
    public const string PaymentReminder = "payment_reminder";
    public const string InventoryAlert = "inventory_alert";
    public const string LeaveReminder = "leave_reminder";

    public static readonly string[] All = [ReservationReminder, PreArrival, CheckoutReminder, PaymentReminder, InventoryAlert, LeaveReminder];
}

public static class JobTriggers
{
    public const string Schedule = "SCHEDULE";
    public const string Manual = "MANUAL";
}

/// <summary>A message's wording for one channel at a property. Body uses {{variables}} (guest_name, check_in…); for
/// WhatsApp it is our copy of the template approved in WhatsApp Manager (ProviderTemplate, Language), whose named
/// parameters are filled from the same variables.</summary>
public class MessageTemplate : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ProviderTemplate { get; set; }
    public string Language { get; set; } = "en";
    public string? Subject { get; set; }
    public string Body { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One message to one person on one channel (see <see cref="MessageStatuses"/>). Written in the same transaction
/// as the change that needs it, once per DedupeKey; sent later by the dispatcher.</summary>
public class OutboxMessage : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string DedupeKey { get; set; } = default!;
    public string TemplateCode { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public string RecipientType { get; set; } = default!;
    public Guid? GuestId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ReservationId { get; set; }
    public string RecipientName { get; set; } = default!;
    public string? ToAddress { get; set; }
    public string? ProviderTemplate { get; set; }
    public string? Language { get; set; }
    public string? Subject { get; set; }
    public string Body { get; set; } = default!;
    /// <summary>JSON object of WhatsApp's named body parameters.</summary>
    public string Params { get; set; } = "{}";
    public string? Link { get; set; }
    public string Source { get; set; } = default!;
    public Guid? JobRunId { get; set; }
    public string Status { get; set; } = MessageStatuses.Queued;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A message WhatsApp accepted (its wamid) and what its status webhooks reported.</summary>
public class WhatsAppMessage : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid OutboxMessageId { get; set; }
    public string Wamid { get; set; } = default!;
    public string TemplateName { get; set; } = default!;
    public string Language { get; set; } = default!;
    public string Status { get; set; } = MessageStatuses.Sent;
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorTitle { get; set; }
    public string? PricingCategory { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An email the provider accepted.</summary>
public class EmailMessage : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid OutboxMessageId { get; set; }
    public string Provider { get; set; } = default!;
    public string ProviderMessageId { get; set; } = default!;
    public string Status { get; set; } = MessageStatuses.Sent;
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>A line in a staff member's bell.</summary>
public class Notification : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid? PropertyId { get; set; }
    public Guid UserId { get; set; }
    public Guid? OutboxMessageId { get; set; }
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>When a job runs at a property: every day at RunAt (local time), NextRunAt being the next one. An API instance
/// claims a due job by moving NextRunAt forward (compare-and-set), so one instance runs it.</summary>
public class ScheduledJob : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Job { get; set; } = default!;
    public bool Enabled { get; set; } = true;
    public TimeOnly RunAt { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastFinishedAt { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastError { get; set; }
    public uint Version { get; set; }
}

/// <summary>What a job did for one business key (a booking and its arrival date, a day's stock alert). Unique per
/// property, job and key, so a job that runs again sends nothing twice. Append-only.</summary>
public class JobRun : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Job { get; set; } = default!;
    public string BusinessKey { get; set; } = default!;
    public Guid? ReservationId { get; set; }
    public string TriggeredBy { get; set; } = default!;
    public Guid? RunBy { get; set; }
    public string Outcome { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}
