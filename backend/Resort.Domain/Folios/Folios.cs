using Resort.Domain.Common;

namespace Resort.Domain.Folios;

/// <summary>What a bill line is for (brief §20). The first four match the package revenue categories.</summary>
public static class ChargeCategories
{
    public const string Room = "ROOM";
    public const string Food = "FOOD";
    public const string Beverage = "BEVERAGE";
    public const string Activity = "ACTIVITY";
    public const string Transport = "TRANSPORT";
    public const string Equipment = "EQUIPMENT";
    public const string Laundry = "LAUNDRY";
    public const string Other = "OTHER";

    public static readonly string[] All = [Room, Food, Beverage, Activity, Transport, Equipment, Laundry, Other];

    /// <summary>"Room", "Food", "Laundry" — for descriptions and messages.</summary>
    public static string Words(string category) => category[..1] + category[1..].ToLowerInvariant();
}

public static class FolioItemTypes
{
    public const string Charge = "CHARGE";
    /// <summary>Cancels a charge: negative quantity and amount, the charge's id in ReversesItemId, a reason.</summary>
    public const string Reversal = "REVERSAL";
    /// <summary>Takes part (or all) of a charge off the bill because a credit note reduced its invoice (Sprint 12):
    /// quantity −1, the credited value as the unit price, the charge's id in AdjustsItemId and the note's in CreditNoteId.</summary>
    public const string Credit = "CREDIT";
}

/// <summary>How a line was posted.</summary>
public static class FolioItemSources
{
    /// <summary>Posted by staff: restaurant, transport, laundry …</summary>
    public const string Manual = "MANUAL";
    /// <summary>One night of the stay, from the prices the booking was made at.</summary>
    public const string RoomNight = "ROOM_NIGHT";
    /// <summary>One component of a package sale (lines of one sale share GroupId).</summary>
    public const string Package = "PACKAGE";
}

/// <summary>When a stay's room nights are put on the bill (property setting <c>folio.room_charge_posting</c>).</summary>
public static class RoomChargePosting
{
    public const string SettingKey = "folio.room_charge_posting";
    /// <summary>The whole stay is charged when the guest checks in (default).</summary>
    public const string AtCheckIn = "AT_CHECK_IN";
    /// <summary>Each night is charged on that day (at check-in for the first night, then by the nightly job).</summary>
    public const string Nightly = "NIGHTLY";
    public static readonly string[] All = [AtCheckIn, Nightly];
}

/// <summary>The bill of a reservation. Every confirmed booking has one main folio; more can be opened to split the bill.
/// Never deleted. Totals and balance are always summed from its lines.</summary>
public class Folio : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    /// <summary>F00001, F00002 … per property.</summary>
    public string Number { get; set; } = default!;
    public string Name { get; set; } = "Main";
    public bool IsPrimary { get; set; }
    public Guid? BillToGuestId { get; set; }
    public string Currency { get; set; } = "INR";
}

/// <summary>One line of a bill. Immutable (a database trigger rejects UPDATE and DELETE): a mistake is corrected by a
/// <see cref="FolioItemTypes.Reversal"/> line, never by editing or deleting (brief §85).</summary>
public class FolioItem : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid FolioId { get; set; }
    public Guid ReservationId { get; set; }
    public string ItemType { get; set; } = FolioItemTypes.Charge;
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    /// <summary>The property-local day the service was given (the night, for a room charge).</summary>
    public DateOnly ServiceDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>round(Quantity × UnitPrice, 2), before tax.</summary>
    public decimal Amount { get; set; }
    /// <summary>Tax on this line (Sprint 9); 0 until the tax engine arrives.</summary>
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Source { get; set; } = FolioItemSources.Manual;
    public Guid? PackageId { get; set; }
    /// <summary>Lines posted together (the components of one package sale).</summary>
    public Guid? GroupId { get; set; }
    public Guid? ReversesItemId { get; set; }
    /// <summary>The line on another folio this one was moved from (split).</summary>
    public Guid? TransferredFromItemId { get; set; }
    /// <summary>The charge a CREDIT line reduces (Sprint 12).</summary>
    public Guid? AdjustsItemId { get; set; }
    /// <summary>The credit note that put a CREDIT line here (Sprint 12).</summary>
    public Guid? CreditNoteId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset PostedAt { get; set; }
    public Guid? PostedBy { get; set; }
}

/// <summary>The tax on a bill line as calculated when it was posted (rule and rate copied; brief §86). Sprint 9.</summary>
public class FolioItemTax : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid FolioItemId { get; set; }
    public Guid? TaxRuleId { get; set; }
    public string TaxCode { get; set; } = default!;
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Bill arithmetic that needs no database. Pure, unit-tested; the database CHECKs the same rules.</summary>
public static class FolioMath
{
    /// <summary>A line's amount: quantity × unit price rounded to the paisa, halves away from zero (as PostgreSQL's
    /// round(numeric) does), so a reversal is always the exact negative of its charge.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) =>
        decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    /// <summary>The amounts of a package sold <paramref name="quantity"/> times: each component's amount × quantity.
    /// Component amounts already add up to the package price (Sprint 4 split), so these add up to price × quantity.</summary>
    public static IReadOnlyList<decimal> PackageLines(IReadOnlyList<decimal> componentAmounts, int quantity) =>
        componentAmounts.Select(a => a * quantity).ToList();

    /// <summary>A reversal line for <paramref name="charge"/>: the same line with quantity, amount, tax and total negated.</summary>
    public static (decimal Quantity, decimal Amount, decimal Tax, decimal Total) Negate(FolioItem charge) =>
        (-charge.Quantity, -charge.Amount, -charge.TaxAmount, -charge.Total);
}
