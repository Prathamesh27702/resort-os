namespace Resort.Contracts;

// ---------------------------------------------------------------- guests (Sprint 3)

public sealed record GuestAddressDto(
    string? Line1, string? Line2, string? City, string? State, string? StateCode, string? PostalCode, string CountryCode);

public sealed record GuestPreferenceDto(string Category, string Value);

public sealed record GuestConsentDto(string Purpose, bool Granted, string Source, DateTimeOffset RecordedAt, string? RecordedByName);

/// <summary>A row in the guest list or search results.</summary>
public sealed record GuestListItemDto(
    Guid Id, string FullName, string? Phone, string? Email, string? Nationality, string? City, string? CompanyName,
    bool IsVip, string Status, DateTimeOffset CreatedAt);

/// <summary>A guest profile. <c>DocumentCount</c> is null when the caller can't see documents.</summary>
public sealed record GuestDto(
    Guid Id, string FirstName, string? LastName, string FullName, string? Phone, string? Email, DateOnly? DateOfBirth,
    string? Nationality, string? CompanyName, string? Gstin, string? Notes, bool IsVip, string Status,
    Guid? MergedIntoId, DateTimeOffset? AnonymisedAt,
    GuestAddressDto? Address, IReadOnlyList<GuestPreferenceDto> Preferences, IReadOnlyList<GuestConsentDto> Consents,
    int? DocumentCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

public sealed record CreateGuestRequest(
    string FirstName, string? LastName, string? Phone, string? Email, DateOnly? DateOfBirth, string? Nationality,
    string? CompanyName, string? Gstin, string? Notes, bool IsVip,
    GuestAddressDto? Address, IReadOnlyList<GuestPreferenceDto>? Preferences,
    /// <summary>Create even though the API reported possible duplicates (the user checked them).</summary>
    bool IgnoreDuplicates = false);

public sealed record UpdateGuestRequest(
    string FirstName, string? LastName, string? Phone, string? Email, DateOnly? DateOfBirth, string? Nationality,
    string? CompanyName, string? Gstin, string? Notes, bool IsVip,
    GuestAddressDto? Address, IReadOnlyList<GuestPreferenceDto>? Preferences, uint Version);

/// <summary>A profile that may be the same person, and why.</summary>
public sealed record PossibleDuplicateDto(Guid Id, string FullName, string? Phone, string? Email, string? Nationality, IReadOnlyList<string> Reasons);

public sealed record RecordConsentRequest(string Purpose, bool Granted, string Source);

public sealed record AnonymiseGuestRequest(string Reason);

/// <summary>Folds <c>DuplicateId</c> into <c>SurvivorId</c>. Both versions must match what the user saw.</summary>
public sealed record MergeGuestsRequest(Guid SurvivorId, uint SurvivorVersion, Guid DuplicateId, uint DuplicateVersion, string Reason);

/// <summary>A stay of this guest (reservations arrive in Sprint 5; until then the list is empty).</summary>
public sealed record GuestStayDto(Guid ReservationId, Guid PropertyId, string PropertyName, DateOnly CheckIn, DateOnly CheckOut, string Status);

// ---------------------------------------------------------------- ID documents (guest_document.read)

/// <summary>Document as listed: the number is masked.</summary>
public sealed record GuestDocumentDto(
    Guid Id, string DocumentType, string? MaskedNumber, string? IssuingCountry, DateOnly? ExpiresOn,
    bool HasFile, string? FileName, string? ContentType, int? SizeBytes, string Status,
    DateTimeOffset CreatedAt, string? CreatedByName);

/// <summary>One document opened on purpose: includes the decrypted number and details. Every view is audited.</summary>
public sealed record GuestDocumentDetailDto(
    Guid Id, string DocumentType, string? Number, string? MaskedNumber, string? IssuingCountry, DateOnly? ExpiresOn,
    IReadOnlyDictionary<string, string>? Details, bool HasFile, string? FileName, string? ContentType, int? SizeBytes,
    string Status, DateTimeOffset CreatedAt, string? CreatedByName);

/// <summary>What the retention job WOULD purge. Nothing is deleted (dry run).</summary>
public sealed record RetentionReportDto(int RetentionDays, DateTimeOffset GeneratedAt, int DocumentCount, IReadOnlyList<RetentionItemDto> Items);

public sealed record RetentionItemDto(Guid GuestId, string GuestName, Guid DocumentId, string DocumentType, bool HasFile, DateTimeOffset UploadedAt, DateOnly EligibleSince);
