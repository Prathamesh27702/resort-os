using Resort.Domain.Common;
using Resort.Domain.Guests;

namespace Resort.Domain.Compliance;

public static class FormCStatuses
{
    public const string Pending = "PENDING";
    public const string Submitted = "SUBMITTED";
    public const string NotRequired = "NOT_REQUIRED";

    public static string Words(string status) => status switch
    {
        Pending => "to file",
        Submitted => "filed",
        NotRequired => "not needed",
        _ => status,
    };
}

/// <summary>
/// Form C for one foreign guest and one stay (Sprint 30). The law gives the property 24 hours from the guest's arrival to
/// report them to the FRRO; the sheet (passport, visa, arrival in India…) is kept encrypted like an ID document.
/// </summary>
public class FormCRecord : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid GuestId { get; set; }
    public string Status { get; set; } = FormCStatuses.Pending;
    public DateTimeOffset ArrivedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    [PersonalData] public byte[]? DetailsEncrypted { get; set; }
    public string? KeyId { get; set; }
    public string? FrroReference { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public string? NotRequiredReason { get; set; }
    public uint Version { get; set; }
}

/// <summary>One line of the Form C sheet: where it sits, what the FRRO portal calls it, and whether the portal insists on it.</summary>
public sealed record FormCField(string Key, string Section, string Label, bool Required, string? Hint = null, int MaxLength = 120, string Kind = "text");

/// <summary>The fields of Form C as the FRRO portal (indianfrro.gov.in, "Form C") asks for them, in its order.</summary>
public static class FormCFields
{
    public const int HoursToFile = 24;

    public const string Personal = "Personal details";
    public const string Passport = "Passport";
    public const string Visa = "Visa";
    public const string Arrival = "Arrival";
    public const string Other = "Other details";

    public static readonly FormCField[] All =
    [
        new("surname", Personal, "Surname", false),
        new("givenName", Personal, "Given name", true),
        new("sex", Personal, "Sex", true, "Male, Female or Other, as on the passport", 10, "sex"),
        new("dateOfBirth", Personal, "Date of birth", true, null, 10, "date"),
        new("nationality", Personal, "Nationality", true, null, 60),
        new("addressAbroad", Personal, "Address in the country of residence", true, "House, street, city and country", 200),
        new("addressInIndia", Personal, "Address in India (this property)", true, null, 200),
        new("passportNumber", Passport, "Passport number", true, null, 20),
        new("passportPlaceOfIssue", Passport, "Place of issue", true, null, 60),
        new("passportDateOfIssue", Passport, "Date of issue", true, null, 10, "date"),
        new("passportExpiry", Passport, "Valid until", true, null, 10, "date"),
        new("visaNumber", Visa, "Visa number", true, "On the visa sticker or the e-Visa (ETA) print-out", 20),
        new("visaType", Visa, "Type of visa", true, "Tourist, e-Tourist, Business, Employment, OCI…", 40),
        new("visaPlaceOfIssue", Visa, "Place of issue", true, null, 60),
        new("visaDateOfIssue", Visa, "Date of issue", true, null, 10, "date"),
        new("visaExpiry", Visa, "Valid until", true, null, 10, "date"),
        new("arrivedFrom", Arrival, "Arrived from", true, "The country, city and place the guest came to India from", 120),
        new("dateOfArrivalInIndia", Arrival, "Date of arrival in India", true, null, 10, "date"),
        new("dateOfArrivalHere", Arrival, "Date of arrival at this property", true, null, 10, "date"),
        new("timeOfArrivalHere", Arrival, "Time of arrival at this property", true, null, 5, "time"),
        new("intendedDurationDays", Arrival, "Intended stay here (days)", true, null, 4, "number"),
        new("purposeOfVisit", Other, "Purpose of visit", true, "Tourism, business, medical, visiting family…", 80),
        new("nextDestination", Other, "Next destination", true, "The place and its address, in India or abroad", 200),
        new("employedInIndia", Other, "Employed in India", true, "Yes or No", 3, "yesno"),
        new("phoneInIndia", Other, "Phone number in India", false, null, 20),
        new("phoneAbroad", Other, "Phone number in the country of residence", false, null, 20),
    ];

    public static readonly IReadOnlySet<string> Keys = All.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The labels of the required fields still empty in <paramref name="values"/>.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, string> values) =>
        All.Where(f => f.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(f.Key))).Select(f => $"{f.Section}: {f.Label}").ToList();
}
