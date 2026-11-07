namespace Resort.Contracts;

// ---------------------------------------------------------------- notifications (Sprint 24)

/// <summary>What the Messages page needs to start: whether WhatsApp and email are connected (LIVE), the built-in fakes
/// (FAKE: development and tests; the page shows test buttons) or off (OFF: production without keys), and what the caller
/// may do.</summary>
public sealed record MessagingSetupDto(string WhatsAppMode, string EmailMode, bool CanSend, bool CanManage, int PreArrivalDays,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>One message in the list. To is masked (+91 98•••••345). Reason: why it failed or wasn't sent, or what the
/// last try said.</summary>
public sealed record MessageListItemDto(
    Guid Id, DateTimeOffset CreatedAt, string TemplateCode, string TemplateName, string Channel, string RecipientType, string RecipientName,
    string To, Guid? ReservationId, string? ReservationReference, string Status, string StatusWords, string? Reason, int Attempts,
    DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt, DateTimeOffset? FailedAt, string Source, bool CanRetry);

/// <summary>A message with what it said and its delivery. Steps: the delivery so far, oldest first.</summary>
public sealed record MessageDto(
    MessageListItemDto Summary, string? Subject, string Body, string? ProviderTemplate, string? Language, IReadOnlyDictionary<string, string> Parameters,
    string? ProviderMessageId, DateTimeOffset NextAttemptAt, string? PricingCategory, string? JobName, IReadOnlyList<MessageStepDto> Steps,
    bool CanFakeStatus);

public sealed record MessageStepDto(DateTimeOffset At, string Status, string Words);

/// <summary>A message's wording on one channel. Variables: what {{…}} it can use, with an example of each.</summary>
public sealed record MessageTemplateDto(
    Guid Id, string Code, string Channel, string Name, string When, string? ProviderTemplate, string Language, string? Subject, string Body,
    bool IsActive, IReadOnlyList<MessageVariableDto> Variables, DateTimeOffset UpdatedAt, uint Version);

public sealed record MessageVariableDto(string Name, string Description, string Sample);

/// <summary>Changes a template. ProviderTemplate (WhatsApp only): the template's name in WhatsApp Manager. Subject: email and
/// in-app only.</summary>
public sealed record SaveMessageTemplateRequest(string? ProviderTemplate, string? Language, string? Subject, string Body, bool IsActive, uint? Version);

/// <summary>A scheduled job at a property: on or off, its local time, when it runs next and what its last run did.</summary>
public sealed record ScheduledJobDto(
    string Job, string Name, string Description, bool Enabled, TimeOnly RunAt, DateTimeOffset NextRunAt, DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastFinishedAt, string? LastOutcome, string? LastError, uint Version);

public sealed record SaveScheduledJobRequest(bool Enabled, TimeOnly RunAt, uint? Version);

/// <summary>What a run of a job did: how much it found, queued, couldn't send (Notes say why) and had already done.</summary>
public sealed record JobRunResultDto(string Job, string Name, int Found, int Queued, int Skipped, int Already, string Message, IReadOnlyList<string> Notes);

/// <summary>One business key a job handled (a booking's reminder, a day's stock alert).</summary>
public sealed record JobRunDto(Guid Id, string Job, string JobName, string BusinessKey, Guid? ReservationId, string? ReservationReference, string TriggeredBy,
    string? RunByName, string Outcome, DateTimeOffset CreatedAt);

/// <summary>The jobs and the latest things they did.</summary>
public sealed record JobsDto(IReadOnlyList<ScheduledJobDto> Jobs, IReadOnlyList<JobRunDto> Recent);

/// <summary>A line in the bell.</summary>
public sealed record NotificationDto(Guid Id, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationListDto(IReadOnlyList<NotificationDto> Items, int Unread);

/// <summary>With the fake WhatsApp only: what WhatsApp's webhook should say about a message (DELIVERED, READ or FAILED).</summary>
public sealed record FakeWhatsAppStatusRequest(string Status);
