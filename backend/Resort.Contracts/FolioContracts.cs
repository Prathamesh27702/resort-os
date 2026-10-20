namespace Resort.Contracts;

// ---------------------------------------------------------------- folios (Sprint 8)

/// <summary>A bill with every line, its totals by category and its balance. Lines are never edited or deleted: a
/// correction is a REVERSAL line. Total = sum of the lines, GST included (Sprint 9); Balance = Total − Payments
/// (payments arrive in Sprint 10, 0 until then). CanPost: the booking is in a state that takes charges (confirmed,
/// checked in or checked out); CantPostReason says why not otherwise.</summary>
public sealed record FolioDto(
    Guid Id, string Number, string Name, bool IsPrimary, Guid ReservationId, string Reference, string ReservationStatus,
    Guid GuestId, string GuestName, Guid? BillToGuestId, string? BillToName, DateOnly CheckIn, DateOnly CheckOut, string Rooms,
    string Currency, IReadOnlyList<FolioItemDto> Items, IReadOnlyList<FolioCategoryTotalDto> Categories,
    decimal Charges, decimal Reversals, decimal Tax, decimal Total, decimal Payments, decimal Balance,
    IReadOnlyList<FolioSummaryDto> Folios, bool CanPost, string? CantPostReason);

/// <summary>One line. ItemType CHARGE or REVERSAL; Source MANUAL, ROOM_NIGHT or PACKAGE. ReversedBy / ReversalReason:
/// the reversal that cancelled this charge. MovedFrom / MovedTo: the folio number a line was split from or to.
/// Live: a charge that still counts (not reversed). Taxes: the GST on the line as calculated when it was posted (Sprint 9),
/// with the rule and rate used; a reversal or a moved line carries the same taxes as its charge.</summary>
public sealed record FolioItemDto(
    Guid Id, string ItemType, string Category, string Description, DateOnly ServiceDate, decimal Quantity, decimal UnitPrice,
    decimal Amount, decimal TaxAmount, decimal Total, string Source, Guid? PackageId, string? PackageName, Guid? GroupId,
    Guid? ReversesItemId, Guid? ReversedByItemId, string? Reason, string? ReversalReason, string? MovedFrom, string? MovedTo,
    DateTimeOffset PostedAt, string? PostedByName, bool Live, IReadOnlyList<FolioItemTaxDto> Taxes);

/// <summary>One tax on a line: CGST 2.5% on ₹1,450.00 = ₹36.25, under rule TaxRuleId.</summary>
public sealed record FolioItemTaxDto(string Code, decimal Rate, decimal TaxableValue, decimal Amount, Guid? TaxRuleId);

/// <summary>What a folio adds up to in one category (charges minus reversals).</summary>
public sealed record FolioCategoryTotalDto(string Category, decimal Amount);

/// <summary>A folio of the same booking, for the folio switcher and the split target list.</summary>
public sealed record FolioSummaryDto(Guid Id, string Number, string Name, bool IsPrimary, decimal Total, decimal Balance);

/// <summary>A row on the Folios screen.</summary>
public sealed record FolioListItemDto(
    Guid Id, string Number, string Name, bool IsPrimary, Guid ReservationId, string Reference, string ReservationStatus,
    string GuestName, DateOnly CheckIn, DateOnly CheckOut, string Rooms, int Lines, decimal Total, decimal Balance, string Currency);

/// <summary>
/// Posts a charge. Either a free charge (Category, Description, UnitPrice, Quantity) or a package (PackageId, Quantity):
/// a package becomes one line per component (room, food, transport, activity) adding up to the package price.
/// ServiceDate: the day of the service (default today at the property). Needs an Idempotency-Key header.
/// </summary>
public sealed record PostChargeRequest(
    string? Category, string? Description, decimal Quantity = 1, decimal UnitPrice = 0, DateOnly? ServiceDate = null, Guid? PackageId = null);

/// <summary>Cancels a charge with a reversal line. Needs folio.adjust and a reason (3–500 characters).</summary>
public sealed record ReverseChargeRequest(string? Reason);

/// <summary>
/// Moves lines to another folio of the same booking: an existing one (TargetFolioId) or a new one (NewFolioName, and
/// optionally who it is billed to). Each line is reversed here and posted again there, so both bills keep their history.
/// </summary>
public sealed record SplitFolioRequest(IReadOnlyList<Guid> ItemIds, Guid? TargetFolioId, string? NewFolioName, Guid? BillToGuestId = null);

/// <summary>Both folios after a split.</summary>
public sealed record SplitFolioResultDto(FolioDto From, FolioDto To);
