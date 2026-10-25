using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Folios;
using Resort.Domain.Invoicing;
using Resort.Domain.Organizations;

namespace Resort.Application.Invoicing;

/// <summary>A tax of a line, summed by rule, code and rate (a charge's rows plus its credit lines' rows).</summary>
internal sealed record NetTax(Guid? TaxRuleId, string Code, decimal Rate, decimal TaxableValue, decimal Amount);

/// <summary>A folio charge as it would be billed now: its amount less what credit notes already took off it (Discount),
/// with its tax rows net of those credits. Never recalculated: the rows are the folio's.</summary>
internal sealed record BillableCharge(FolioItem Charge, decimal Discount, decimal TaxableValue, IReadOnlyList<NetTax> Taxes, string? HsnSac)
{
    public decimal TaxAmount => Taxes.Sum(t => t.Amount);
}

/// <summary>
/// What a folio has billed and what it could still bill (Sprint 12), and the next number of a series. Read under the
/// reservation row lock by the services that issue documents, so two can't bill the same charge.
///
/// A charge is billed while it is on an invoice (or on a debit note of an invoice) that has no CANCEL credit note.
/// Queries ignore the tenant filter and filter by folio instead, as <see cref="Folios.FolioPosting"/> does: check-out
/// issues invoices inside its own transaction with ids it has already loaded.
/// </summary>
public sealed class FolioBilling(IAppDbContext db)
{
    /// <summary>The charges of the given folios that are billed now, and the number of the document that billed each.</summary>
    public async Task<Dictionary<Guid, string>> BilledAsync(IReadOnlyCollection<Guid> folioIds, CancellationToken ct)
    {
        var cancelled = db.CreditNotes.IgnoreQueryFilters().Where(c => c.NoteType == CreditNoteTypes.Cancel).Select(c => c.InvoiceId);
        var onInvoices = await (from ii in db.InvoiceItems.IgnoreQueryFilters()
                                join i in db.Invoices.IgnoreQueryFilters() on ii.InvoiceId equals i.Id
                                where folioIds.Contains(i.FolioId) && !cancelled.Contains(i.Id)
                                select new { ii.FolioItemId, i.Number }).ToListAsync(ct);
        var onDebitNotes = await (from di in db.DebitNoteItems.IgnoreQueryFilters()
                                  join d in db.DebitNotes.IgnoreQueryFilters() on di.DebitNoteId equals d.Id
                                  join i in db.Invoices.IgnoreQueryFilters() on d.InvoiceId equals i.Id
                                  where folioIds.Contains(i.FolioId) && !cancelled.Contains(i.Id)
                                  select new { di.FolioItemId, d.Number }).ToListAsync(ct);
        return onInvoices.Select(x => (x.FolioItemId, x.Number)).Concat(onDebitNotes.Select(x => (x.FolioItemId, x.Number)))
            .GroupBy(x => x.FolioItemId).ToDictionary(g => g.Key, g => g.First().Number);
    }

    /// <summary>The folio's invoice that hasn't been cancelled, if any (a folio has at most one at a time).</summary>
    public async Task<Invoice?> ActiveInvoiceAsync(Guid folioId, CancellationToken ct)
    {
        var cancelled = db.CreditNotes.IgnoreQueryFilters().Where(c => c.NoteType == CreditNoteTypes.Cancel).Select(c => c.InvoiceId);
        return await db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.FolioId == folioId && !cancelled.Contains(i.Id)).OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>The folio's charges that still count and aren't billed, net of credits, oldest first. Charges that net to
    /// nothing (a complimentary line, or fully credited) are left out.</summary>
    internal async Task<List<BillableCharge>> UnbilledAsync(Folio folio, CancellationToken ct)
    {
        var items = await db.FolioItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.FolioId == folio.Id).ToListAsync(ct);
        var reversed = items.Where(i => i.ReversesItemId != null).Select(i => i.ReversesItemId!.Value).ToHashSet();
        var billed = await BilledAsync([folio.Id], ct);
        var charges = items.Where(i => i.ItemType == FolioItemTypes.Charge && !reversed.Contains(i.Id) && !billed.ContainsKey(i.Id))
            .OrderBy(i => i.ServiceDate).ThenBy(i => i.PostedAt).ThenBy(i => i.Id).ToList();
        var credits = items.Where(i => i.ItemType == FolioItemTypes.Credit).ToLookup(i => i.AdjustsItemId!.Value);
        var ids = charges.Select(c => c.Id).Concat(charges.SelectMany(c => credits[c.Id]).Select(c => c.Id)).ToList();
        var taxes = (await db.FolioItemTaxes.IgnoreQueryFilters().AsNoTracking().Where(t => ids.Contains(t.FolioItemId)).ToListAsync(ct))
            .ToLookup(t => t.FolioItemId);
        var hsn = await HsnAsync(folio.OrganizationId, ct);

        var result = new List<BillableCharge>();
        foreach (var c in charges)
        {
            var credited = credits[c.Id].ToList();
            var discount = -credited.Sum(x => x.Amount);
            var value = c.Amount - discount;
            if (value <= 0) continue;
            var rows = taxes[c.Id].Concat(credited.SelectMany(x => taxes[x.Id]))
                .GroupBy(t => (t.TaxRuleId, t.TaxCode, t.Rate))
                .Select(g => new NetTax(g.Key.TaxRuleId, g.Key.TaxCode, g.Key.Rate, g.Sum(t => t.TaxableValue), g.Sum(t => t.Amount)))
                .Where(t => t.TaxableValue != 0 || t.Amount != 0)
                .OrderBy(t => t.Rate).ThenBy(t => Array.IndexOf(TaxOrder, t.Code)).ToList();
            var sac = rows.Select(t => t.TaxRuleId).FirstOrDefault(r => r != null) is { } rule && hsn.ByRule.TryGetValue(rule, out var s)
                ? s : hsn.ByCategory.GetValueOrDefault(c.Category);
            result.Add(new BillableCharge(c, discount, value, rows, sac));
        }
        return result;
    }

    internal static readonly string[] TaxOrder = ["CGST", "SGST", "IGST", "CESS"];

    /// <summary>SAC/HSN codes: by the rule a tax row used (its category's code) and by folio charge category.</summary>
    internal async Task<(Dictionary<Guid, string> ByRule, Dictionary<string, string> ByCategory)> HsnAsync(Guid orgId, CancellationToken ct)
    {
        var categories = await db.TaxCategories.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == orgId).ToListAsync(ct);
        var rules = await db.TaxRules.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == orgId)
            .Select(r => new { r.Id, r.TaxCategoryId }).ToListAsync(ct);
        var byId = categories.ToDictionary(c => c.Id, c => c.HsnSac);
        return (rules.Where(r => byId.ContainsKey(r.TaxCategoryId)).ToDictionary(r => r.Id, r => byId[r.TaxCategoryId]),
            categories.Where(c => c.ChargeCategory != null).ToDictionary(c => c.ChargeCategory!, c => c.HsnSac));
    }

    /// <summary>
    /// The next number of the property's series for <paramref name="documentType"/> in the financial year of
    /// <paramref name="day"/>. The upsert locks the series row until the transaction ends, so concurrent documents queue
    /// and numbers have no gaps (a rolled-back document gives its number back) and no repeats.
    /// </summary>
    public async Task<(Guid SeriesId, string Number, string FinancialYear)> NextNumberAsync(Property property, string documentType, DateOnly day,
        CancellationToken ct)
    {
        var fy = InvoiceMath.FinancialYear(day);
        var prefix = InvoiceMath.Prefix(property.Code, documentType, fy);
        var width = InvoiceMath.Width(documentType);
        var row = (await db.Database.SqlQuery<SeriesRow>(
            $"""
             INSERT INTO invoice_series (id, organization_id, property_id, document_type, financial_year, prefix, width, last_number)
             VALUES ({Guid.CreateVersion7()}, {property.OrganizationId}, {property.Id}, {documentType}, {fy}, {prefix}, {width}, 1)
             ON CONFLICT (property_id, document_type, financial_year)
             DO UPDATE SET last_number = invoice_series.last_number + 1, updated_at = now()
             RETURNING id AS "Id", prefix AS "Prefix", width AS "Width", last_number AS "LastNumber"
             """).ToListAsync(ct)).Single();
        return (row.Id, InvoiceMath.Number(row.Prefix, row.Width, row.LastNumber), fy);
    }

    private sealed class SeriesRow
    {
        public Guid Id { get; set; }
        public string Prefix { get; set; } = "";
        public short Width { get; set; }
        public int LastNumber { get; set; }
    }
}
