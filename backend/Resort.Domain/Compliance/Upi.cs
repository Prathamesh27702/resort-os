using System.Globalization;
using System.Text.RegularExpressions;

namespace Resort.Domain.Compliance;

/// <summary>
/// UPI collection links (Sprint 30). A property has a UPI ID (a VPA such as palmgrove@okhdfcbank); a bill shows a QR of a
/// <c>upi://pay</c> link (NPCI's UPI linking specification) with the payee, the amount and the bill number, which any UPI
/// app opens with the amount filled in. No money passes through Resort OS: the guest's bank pays the property's bank and
/// staff record the payment with its UPI reference.
/// </summary>
public static partial class Upi
{
    [GeneratedRegex("^[A-Za-z0-9._-]{2,255}@[A-Za-z][A-Za-z0-9.-]{1,63}$")]
    private static partial Regex Vpa();

    /// <summary>Whether <paramref name="id"/> looks like a UPI ID: name@bank handle.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 100 && Vpa().IsMatch(id);

    /// <summary>FSSAI licence and registration numbers have 14 digits.</summary>
    public static bool IsValidFssai(string? number) => number is { Length: 14 } && number.All(char.IsAsciiDigit);

    /// <summary>
    /// The link a UPI app opens: <c>upi://pay?pa=…&amp;pn=…&amp;am=…&amp;cu=INR&amp;tn=…</c>. <paramref name="amount"/> null or
    /// zero leaves the amount for the guest to type. The note is cut to 50 characters and to letters, digits and spaces,
    /// which every UPI app accepts.
    /// </summary>
    public static string PayLink(string upiId, string payeeName, decimal? amount, string? note)
    {
        var parts = new List<string> { "pa=" + Uri.EscapeDataString(upiId.Trim()), "pn=" + Uri.EscapeDataString(Clean(payeeName, 50)) };
        if (amount is > 0) parts.Add("am=" + decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add("cu=INR");
        if (!string.IsNullOrWhiteSpace(note)) parts.Add("tn=" + Uri.EscapeDataString(Clean(note, 50)));
        return "upi://pay?" + string.Join("&", parts);
    }

    private static string Clean(string s, int max)
    {
        var kept = new string(s.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '.' or '/').ToArray()).Trim();
        return kept.Length <= max ? kept : kept[..max].TrimEnd();
    }
}
