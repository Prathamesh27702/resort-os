namespace Resort.Contracts;

// ---------------------------------------------------------------- UPI (Sprint 30)

/// <summary>A UPI QR for the property's UPI ID. Link: the upi://pay link a UPI app opens. QrPng: the same link as a QR, a
/// PNG data URI ready for an &lt;img&gt;. Amount null: the payer types it.</summary>
public sealed record UpiQrDto(string UpiId, string PayeeName, decimal? Amount, string? Note, string Link, string QrPng);

// ---------------------------------------------------------------- the printed restaurant bill (Sprint 30)

/// <summary>Everything a printed restaurant bill carries, already in the order it prints. Property: who sold it (name,
/// address, GSTIN, FSSAI licence number). Upi: the QR to pay the total, only while the bill is still open and the property
/// has a UPI ID. PaidBy: how a settled bill was paid.</summary>
public sealed record OrderBillDto(
    BillPropertyDto Property, Guid OrderId, string Number, string Where, string OrderType, DateOnly BusinessDate, DateTimeOffset PrintedAt,
    short? Covers, string? WaiterName, string? GuestName, IReadOnlyList<BillLineDto> Lines, decimal Subtotal, decimal DiscountAmount,
    string? DiscountLabel, decimal Net, IReadOnlyList<DocumentTaxDto> Taxes, decimal TaxAmount, decimal Total, string? TaxNote, string? SacCode,
    string Status, string? PaidBy, UpiQrDto? Upi);

public sealed record BillPropertyDto(
    string Name, string? LegalName, IReadOnlyList<string> AddressLines, string? Phone, string? Gstin, string? FssaiLicenceNo);

public sealed record BillLineDto(string Name, string? Detail, short Quantity, decimal UnitPrice, decimal Amount, string FoodType);

// ---------------------------------------------------------------- Form C (Sprint 30)

/// <summary>A Form C in the list. HoursLeft: until the 24-hour deadline (negative when it has passed). Missing: how many
/// required fields are still empty.</summary>
public sealed record FormCListItemDto(
    Guid Id, string Status, Guid GuestId, string GuestName, string? Nationality, Guid ReservationId, string Reference, string? Room,
    DateOnly CheckOut, DateTimeOffset ArrivedAt, DateTimeOffset DueAt, double HoursLeft, bool Overdue, int Missing, string? FrroReference,
    DateTimeOffset? SubmittedAt, string? SubmittedByName, string? NotRequiredReason);

public sealed record FormCListDto(IReadOnlyList<FormCListItemDto> Items, int Pending, int Overdue, int HoursToFile, string PortalUrl);

/// <summary>One line of the sheet. Kind: text, date (yyyy-MM-dd), time (HH:mm), number, sex (M, F or X) or yesno (Yes or
/// No). FromProfile: the value comes from the guest's profile, documents or booking and nobody has typed over it.</summary>
public sealed record FormCFieldDto(
    string Key, string Section, string Label, bool Required, string? Hint, string Kind, int MaxLength, string? Value, bool FromProfile);

public sealed record FormCSheetDto(
    Guid Id, string Status, Guid GuestId, string GuestName, string? Nationality, Guid ReservationId, string Reference, string? Room,
    DateOnly CheckIn, DateOnly CheckOut, DateTimeOffset ArrivedAt, DateTimeOffset DueAt, double HoursLeft, bool Overdue,
    IReadOnlyList<FormCFieldDto> Fields, IReadOnlyList<string> Missing, string? FrroReference, DateTimeOffset? SubmittedAt,
    string? SubmittedByName, string? NotRequiredReason, string PortalUrl, uint Version);

/// <summary>The sheet as typed: field key → value (empty clears what was typed, so the profile's value shows again).</summary>
public sealed record SaveFormCRequest(IReadOnlyDictionary<string, string?> Values, uint Version);

/// <summary>Filed on the FRRO portal. FrroReference: the acknowledgement number the portal gave (optional).</summary>
public sealed record SubmitFormCRequest(string? FrroReference, uint Version);

public sealed record FormCNotRequiredRequest(string Reason, uint Version);
public sealed record ReopenFormCRequest(uint Version);
