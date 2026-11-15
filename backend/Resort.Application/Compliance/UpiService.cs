using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Compliance;
using Resort.Domain.Organizations;

namespace Resort.Application.Compliance;

/// <summary>Draws a text as a QR code (Infrastructure: QRCoder, MIT-licensed).</summary>
public interface IQrCodes
{
    /// <summary>A PNG of the QR for <paramref name="text"/>, black on white with a quiet zone.</summary>
    byte[] Png(string text);
}

/// <summary>A property's UPI QR for an amount, with the PNG as bytes (for PDFs) next to the DTO's data URI (for screens).</summary>
public sealed record UpiQr(string UpiId, string PayeeName, decimal? Amount, string? Note, string Link, byte[] Png)
{
    public UpiQrDto ToDto() => new(UpiId, PayeeName, Amount, Note, Link, "data:image/png;base64," + Convert.ToBase64String(Png));
}

/// <summary>
/// The UPI QR on bills (Sprint 30). The property's UPI ID is set once under Settings → Properties; a bill, an invoice with
/// money still to pay and the "take a UPI payment" dialogs show a QR for exactly the amount, with the bill number as the
/// note the guest's app shows. The app never sees the money: staff record the payment and its UPI reference as before.
/// </summary>
public sealed class UpiService(IAppDbContext db, IQrCodes qr)
{
    /// <summary>The QR for <paramref name="amount"/> at the property, or null when it has no UPI ID.</summary>
    public UpiQr? For(Property p, decimal? amount, string? note)
    {
        if (string.IsNullOrWhiteSpace(p.UpiId)) return null;
        var payee = string.IsNullOrWhiteSpace(p.UpiPayeeName) ? p.Name : p.UpiPayeeName;
        var link = Upi.PayLink(p.UpiId, payee, amount, note);
        return new UpiQr(p.UpiId, payee, amount is > 0 ? amount : null, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), link, qr.Png(link));
    }

    public async Task<UpiQrDto> GetAsync(Guid propertyId, decimal? amount, string? note, CancellationToken ct)
    {
        var p = await db.Properties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
        if (amount is < 0 or > 10_00_000)
            throw new BusinessRuleAppException("UPI_AMOUNT", "A UPI QR can ask for ₹0.01 to ₹10,00,000. Take a larger amount by bank transfer.");
        return For(p, amount, note)?.ToDto()
            ?? throw new BusinessRuleAppException("UPI_NOT_SET",
                $"{p.Name} has no UPI ID yet, so there is no QR to show. Add it under Settings → Properties → {p.Name} → Food licence and UPI.");
    }
}
