using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Purchasing;

namespace Resort.Application.Validation;

// Field rules for suppliers and purchasing (Sprint 16). Rules that need the property's data (is the GSTIN valid and
// free, how much is still to come in or to bill, the balance of a bill) are in the purchasing services, under the same
// field names.

internal static class PurchaseRules
{
    public static bool Rate(decimal r) => PurchaseMath.GstRates.Contains(r);

    public const string RateWords = "Choose the GST rate: 0, 0.25, 3, 5, 12, 18, 28 or 40 %.";

    public static bool Price(decimal p) => p is >= 0 and <= 1_000_000m && decimal.Round(p, 4) == p;

    public static bool Optional(string? s, int min, int max) => s is null || s.Trim().Length == 0 || s.Trim().Length >= min && s.Trim().Length <= max;
}

public sealed class SaveSupplierRequestValidator : AbstractValidator<SaveSupplierRequest>
{
    public SaveSupplierRequestValidator()
    {
        RuleFor(x => x.Code).Must(StockRules.Code)
            .WithMessage("The code is 1–20 letters, digits, - or _, starting with a letter or digit, e.g. FRESH-VEG.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 120)
            .WithMessage("Give the supplier's name as on their bills, 2–120 characters.");
        RuleFor(x => x.ContactName).Must(s => PurchaseRules.Optional(s, 2, 80)).WithMessage("The contact name is 2–80 characters, or empty.");
        RuleFor(x => x.Phone).Must(p => p is null || p.Trim().Length == 0 || Regex.IsMatch(p.Trim(), @"^\+?[0-9][0-9 -]{6,18}[0-9]$"))
            .WithMessage("Enter the phone number with digits only (spaces and - allowed), e.g. +91 98220 12345, or leave it empty.");
        RuleFor(x => x.Email).Must(e => e is null || e.Trim().Length == 0 || Regex.IsMatch(e.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            .WithMessage("Enter an e-mail address like accounts@supplier.in, or leave it empty.");
        RuleFor(x => x.Address).Must(s => PurchaseRules.Optional(s, 3, 300)).WithMessage("The address is 3–300 characters, or empty.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 500)).WithMessage("The note can be at most 500 characters.");
        RuleFor(x => x.PaymentTermsDays).InclusiveBetween(0, 180)
            .WithMessage("Payment terms are 0–180 days after the bill (0: pay on delivery).");
    }
}

public sealed class PurchaseRequestLineValidator : AbstractValidator<PurchaseRequestLine>
{
    public PurchaseRequestLineValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty().WithMessage("Choose the stock item.");
        RuleFor(x => x.Quantity).Must(q => q > 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("Enter how much is needed: more than 0, at most 1,000,000, with at most three decimals.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 200)).WithMessage("The note can be at most 200 characters.");
    }
}

public sealed class CreatePurchaseRequestRequestValidator : AbstractValidator<CreatePurchaseRequestRequest>
{
    public CreatePurchaseRequestRequestValidator()
    {
        RuleFor(x => x.LocationId).NotEmpty().WithMessage("Choose the store the stock is for.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 500)).WithMessage("The note can be at most 500 characters.");
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l is null || l.Count <= 100).WithMessage("A request can have at most 100 items.");
        RuleForEach(x => x.Items).SetValidator(new PurchaseRequestLineValidator());
    }
}

public sealed class PurchaseReasonRequestValidator : AbstractValidator<PurchaseReasonRequest>
{
    public PurchaseReasonRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length is >= 3 and <= 300)
            .WithMessage("Say why, in 3–300 characters (e.g. Supplier can't deliver this week).");
    }
}

public sealed class SavePurchaseOrderLineValidator : AbstractValidator<SavePurchaseOrderLine>
{
    public SavePurchaseOrderLineValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty().WithMessage("Choose the stock item.");
        RuleFor(x => x.Quantity).Must(q => q > 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("Enter the quantity to order: more than 0, at most 1,000,000, with at most three decimals.");
        RuleFor(x => x.UnitPrice).Must(PurchaseRules.Price)
            .WithMessage("The price of one unit before GST must be ₹0–₹10,00,000 with at most four decimals.");
        RuleFor(x => x.GstRate).Must(PurchaseRules.Rate).WithMessage(PurchaseRules.RateWords);
    }
}

public sealed class SavePurchaseOrderRequestValidator : AbstractValidator<SavePurchaseOrderRequest>
{
    public SavePurchaseOrderRequestValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty().WithMessage("Choose the supplier.");
        RuleFor(x => x.LocationId).NotEmpty().WithMessage("Choose the store the goods are delivered to.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 500)).WithMessage("The note can be at most 500 characters.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item to order.")
            .Must(l => l is null || l.Count <= 100).WithMessage("An order can have at most 100 lines.");
        RuleForEach(x => x.Lines).SetValidator(new SavePurchaseOrderLineValidator());
    }
}

public sealed class PurchaseOrderStepRequestValidator : AbstractValidator<PurchaseOrderStepRequest>
{
    public PurchaseOrderStepRequestValidator()
    {
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
    }
}

public sealed class ReceiveGoodsLineValidator : AbstractValidator<ReceiveGoodsLine>
{
    public ReceiveGoodsLineValidator()
    {
        RuleFor(x => x.OrderItemId).NotEmpty().WithMessage("Choose the order line.");
        RuleFor(x => x.Quantity).Must(q => q > 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("Enter how much came in: more than 0, with at most three decimals. Leave out a line that didn't come.");
    }
}

public sealed class ReceiveGoodsRequestValidator : AbstractValidator<ReceiveGoodsRequest>
{
    public ReceiveGoodsRequestValidator()
    {
        RuleFor(x => x.ChallanNumber).Must(s => PurchaseRules.Optional(s, 1, 40)).WithMessage("The challan number is at most 40 characters.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Enter at least one line that came in.");
        RuleForEach(x => x.Lines).SetValidator(new ReceiveGoodsLineValidator());
    }
}

public sealed class SupplierInvoiceLineRequestValidator : AbstractValidator<SupplierInvoiceLineRequest>
{
    public SupplierInvoiceLineRequestValidator()
    {
        RuleFor(x => x.OrderItemId).NotEmpty().WithMessage("Choose the order line.");
        RuleFor(x => x.Quantity).Must(q => q > 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("Enter the billed quantity: more than 0, with at most three decimals. Leave out a line that isn't on the bill.");
        RuleFor(x => x.UnitPrice).Must(PurchaseRules.Price)
            .WithMessage("The billed price of one unit before GST must be ₹0–₹10,00,000 with at most four decimals.");
        RuleFor(x => x.GstRate).Must(PurchaseRules.Rate).WithMessage(PurchaseRules.RateWords);
    }
}

public sealed class RecordSupplierInvoiceRequestValidator : AbstractValidator<RecordSupplierInvoiceRequest>
{
    public RecordSupplierInvoiceRequestValidator()
    {
        RuleFor(x => x.SupplierNumber).Must(n => n is not null && n.Trim().Length is >= 1 and <= 40)
            .WithMessage("Enter the bill number printed on the supplier's invoice (at most 40 characters).");
        RuleFor(x => x.InvoiceDate).NotEmpty().WithMessage("Enter the date printed on the bill.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Enter at least one billed line.");
        RuleForEach(x => x.Lines).SetValidator(new SupplierInvoiceLineRequestValidator());
    }
}

public sealed class PaySupplierRequestValidator : AbstractValidator<PaySupplierRequest>
{
    public PaySupplierRequestValidator()
    {
        RuleFor(x => x.Amount).Must(a => a > 0 && decimal.Round(a, 2) == a)
            .WithMessage("Enter the amount paid: more than ₹0, in rupees and paise.");
        RuleFor(x => x.Method).Must(m => m is not null && SupplierPaymentMethods.All.Contains(m.Trim().ToUpperInvariant()))
            .WithMessage("Choose how it was paid: CASH, BANK_TRANSFER, UPI or CHEQUE.");
        RuleFor(x => x.Reference)
            .Must((x, r) => x.Method?.Trim().ToUpperInvariant() == SupplierPaymentMethods.Cash ? PurchaseRules.Optional(r, 1, 60) : r is not null && r.Trim().Length is >= 3 and <= 60)
            .WithMessage(x => x.Method?.Trim().ToUpperInvariant() == SupplierPaymentMethods.Cash
                ? "The reference is at most 60 characters."
                : "Enter the UTR (bank transfer or UPI) or the cheque number, 3–60 characters, so the payment can be matched to the bank statement.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
    }
}
