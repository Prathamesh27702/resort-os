using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Folios;
using GuestAddress = Resort.Domain.Guests.GuestAddress;
using Resort.Domain.Invoicing;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;
using Resort.Domain.Tax;

namespace Resort.Application.Invoicing;

/// <summary>
/// GST invoices (Sprint 12, brief §24, §25). An invoice bills a folio's charges as they were posted: amounts, the tax
/// rule ids and rates are copied from the folio, never recalculated (brief §86). Numbers are gap-free per series and
/// financial year (<see cref="FolioBilling.NextNumberAsync"/>). An issued invoice is never changed or deleted: it is
/// corrected by a credit note (CANCEL: the whole invoice, the bill stays and can be invoiced again; REDUCE: chosen lines,
/// the folio gets CREDIT lines), and charges posted after it are billed by a debit note.
///
/// Everything that issues a document takes the booking's row lock first (the lock every folio change takes), so a charge
/// can't be billed twice and can't be reversed while it is being invoiced.
/// </summary>
public sealed class InvoiceService(
    IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock, ReservationService reservations,
    FolioBilling billing, ReceiptService receipts, InvoicePrinter printer, IPdfRenderer pdf)
{
    public const string IssueAtCheckOutSetting = "invoice.issue_at_checkout";

    public sealed record ListQuery(string? Q, DateOnly? From, DateOnly? To, Guid? ReservationId, string? Status, int Page, int PageSize);

    // ------------------------------------------------------------------ queries

    /// <summary>Invoices of the property, newest first. Q: invoice number, booking reference, folio number, customer name
    /// or GSTIN. Status: issued, partly-credited, credited or cancelled.</summary>
    public async Task<PagedResult<InvoiceListItemDto>> ListAsync(Guid propertyId, ListQuery q, CancellationToken ct)
    {
        await LoadPropertyAsync(propertyId, ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        if (q.From is { } f && q.To is { } t && t < f)
            throw ReservationService.Field("To", $"The end date ({QuoteEngine.Day(t)}) is before the start date ({QuoteEngine.Day(f)}).");
        var status = q.Status?.Trim().ToUpperInvariant().Replace('-', '_');
        string[] statuses = [InvoiceStatuses.Issued, InvoiceStatuses.PartlyCredited, InvoiceStatuses.Credited, InvoiceStatuses.Cancelled];
        if (status is { Length: > 0 } && !statuses.Contains(status))
            throw ReservationService.Field("Status", $"'{q.Status}' isn't an invoice status. Use issued, partly-credited, credited or cancelled.");

        var rows = db.Invoices.AsNoTracking().Where(i => i.PropertyId == propertyId);
        if (q.From is { } from) rows = rows.Where(i => i.InvoiceDate >= from);
        if (q.To is { } to) rows = rows.Where(i => i.InvoiceDate <= to);
        if (q.ReservationId is { } rid) rows = rows.Where(i => i.ReservationId == rid);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var text = q.Q.Trim();
            var code = text.ToUpperInvariant();
            var like = "%" + text.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var folioIds = db.Folios.Where(x => x.Number == code).Select(x => x.Id);
            var reservationIds = db.Reservations.Where(r => r.Reference == code).Select(r => r.Id);
#pragma warning disable CA1304, CA1311 // translated to SQL lower(), not run in .NET
            rows = rows.Where(i => i.Number == code || i.CustomerGstin == code || folioIds.Contains(i.FolioId) || reservationIds.Contains(i.ReservationId)
                                   || EF.Functions.Like(i.CustomerName.ToLower(), like));
#pragma warning restore CA1304, CA1311
        }

        // the status comes from the credit notes, so filter after summing them
        var all = await rows.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Number).Select(i => i.Id).ToListAsync(ct);
        var sums = await SumsAsync(all, ct);
        var matching = status is { Length: > 0 } ? all.Where(id => sums[id].Status == status).ToList() : all;
        var pageIds = matching.Skip((page - 1) * size).Take(size).ToList();
        var invoices = await db.Invoices.AsNoTracking().Where(i => pageIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var folioNumbers = await FolioNumbersAsync(invoices.Values.Select(i => i.FolioId), ct);
        var references = await ReferencesAsync(invoices.Values.Select(i => i.ReservationId), ct);
        var items = pageIds.Select(id =>
        {
            var i = invoices[id];
            var s = sums[id];
            return new InvoiceListItemDto(i.Id, i.Number, i.DocumentType, i.InvoiceDate, i.FolioId, folioNumbers[i.FolioId], i.ReservationId,
                references[i.ReservationId], i.CustomerName, i.CustomerGstin, i.TaxableValue, i.TaxTotal, i.Total, s.Credited, s.Status,
                s.CreditNotes, s.DebitNotes, i.Currency);
        }).ToList();
        return new PagedResult<InvoiceListItemDto>(items, page, size, matching.Count);
    }

    public async Task<InvoiceDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(propertyId, id, ct), ct);

    /// <summary>What issuing an invoice for the folio would produce now, and why it can't be issued if it can't.</summary>
    public async Task<InvoicePreviewDto> PreviewAsync(Guid propertyId, Guid folioId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var folio = await db.Folios.AsNoTracking().SingleOrDefaultAsync(f => f.Id == folioId && f.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("Folio", folioId);
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == folio.ReservationId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var lines = await billing.UnbilledAsync(folio, ct);
        var registration = await RegistrationAsync(property, today, ct);
        var customer = await DefaultCustomerAsync(folio, reservation, ct);
        var active = await billing.ActiveInvoiceAsync(folio.Id, ct);
        string? cant = null;
        try
        {
            await CheckCanIssueAsync(property, folio, reservation, lines, registration, today, active, ct);
        }
        catch (AppException e)
        {
            cant = e.Message;
        }
        var dtoLines = lines.Select((l, n) => LineDto(null, n + 1, l.Charge.Id, l.Charge, l.HsnSac, l.Discount, l.TaxableValue,
            l.Taxes.Select(t => new DocumentTaxDto(t.Code, t.Rate, t.TaxableValue, t.Amount)).ToList(), 0m, null)).ToList();
        return new InvoicePreviewDto(folio.Id, folio.Number, reservation.Id, reservation.Reference,
            registration?.RegistrationType == RegistrationTypes.Composition ? DocumentTypes.BillOfSupply : DocumentTypes.Invoice,
            registration is null ? null : Supplier(property, registration), customer.GuestId, customer.Name, customer.Gstin, customer.Address,
            customer.StateCode, customer.GstinProblem, dtoLines, dtoLines.Sum(l => l.TaxableValue), dtoLines.Sum(l => l.TaxAmount),
            dtoLines.Sum(l => l.Total), active?.Id, active?.Number, cant);
    }

    // ------------------------------------------------------------------ issue

    /// <summary>Issues the folio's invoice. Under an Idempotency-Key: a retry returns the same invoice.</summary>
    public async Task<(InvoiceDto Invoice, bool Replayed)> IssueAsync(Guid propertyId, Guid folioId, string? idempotencyKey, IssueInvoiceRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/folios/{folioId}/invoice";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "invoice", ct) is { } stored)
            return (JsonSerializer.Deserialize<InvoiceDto>(stored, Idempotency.Json)!, true);
        var (folio, reservation) = await LockFolioAsync(propertyId, folioId, ct);
        var invoice = await IssueCoreAsync(property, folio, reservation, r, ct);
        var dto = await ToDtoAsync(invoice, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, invoice.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>
    /// Check-out (inside its transaction, under its lock): issues the invoice of every folio of the booking that has
    /// charges not billed yet and no invoice, to the bill-to guest on file, plus a receipt for every payment. Skipped
    /// when the property switched it off (setting <see cref="IssueAtCheckOutSetting"/>) or has no GST registration. A folio
    /// that can't be invoiced (the guest's GSTIN on file is wrong …) doesn't stop the check-out: the booking gets a note
    /// saying why, and reception issues it from the bill.
    /// </summary>
    public async Task<List<Invoice>> IssueAtCheckOutAsync(Property property, Reservation reservation, CancellationToken ct)
    {
        var issued = new List<Invoice>();
        if (!await SettingAsync(property.Id, IssueAtCheckOutSetting, true, ct)) return issued;
        var today = property.LocalDate(clock.GetUtcNow());
        if (await RegistrationAsync(property, today, ct) is null) return issued;
        var folios = await db.Folios.Where(f => f.ReservationId == reservation.Id).OrderByDescending(f => f.IsPrimary).ThenBy(f => f.Number).ToListAsync(ct);
        foreach (var folio in folios)
        {
            if (await billing.ActiveInvoiceAsync(folio.Id, ct) is not null) continue;
            if ((await billing.UnbilledAsync(folio, ct)).Count == 0) continue;
            try
            {
                issued.Add(await IssueCoreAsync(property, folio, reservation, new IssueInvoiceRequest(), ct));
            }
            catch (Exception e) when (e is AppException or FluentValidation.ValidationException)
            {
                var why = e is FluentValidation.ValidationException v ? string.Join(" ", v.Errors.Select(x => x.ErrorMessage)) : e.Message;
                reservations.AddNote(reservation, $"No invoice was issued for {folio.Number} at check-out: {why} Issue it from the bill.");
            }
        }
        await receipts.IssueMissingAsync(property, reservation, ct);
        return issued;
    }

    /// <summary>Checks, numbers and adds the invoice (the caller holds the reservation lock and saves). Every check runs
    /// before the number is taken, so a refusal never leaves a gap or a half-written invoice.</summary>
    private async Task<Invoice> IssueCoreAsync(Property property, Folio folio, Reservation reservation, IssueInvoiceRequest r, CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        var lines = await billing.UnbilledAsync(folio, ct);
        var registration = await RegistrationAsync(property, today, ct);
        await CheckCanIssueAsync(property, folio, reservation, lines, registration, today, await billing.ActiveInvoiceAsync(folio.Id, ct), ct);
        var customer = await CustomerAsync(folio, reservation, r, ct);
        var type = registration!.RegistrationType == RegistrationTypes.Composition ? DocumentTypes.BillOfSupply : DocumentTypes.Invoice;

        var (seriesId, number, fy) = await billing.NextNumberAsync(property, type, today, ct);
        var invoice = new Invoice
        {
            OrganizationId = property.OrganizationId,
            PropertyId = property.Id,
            SeriesId = seriesId,
            DocumentType = type,
            Number = number,
            FinancialYear = fy,
            InvoiceDate = today,
            FolioId = folio.Id,
            ReservationId = reservation.Id,
            GstRegistrationId = registration.Id,
            SupplierGstin = registration.Gstin,
            SupplierLegalName = registration.LegalName,
            SupplierTradeName = registration.TradeName ?? property.Name,
            SupplierAddress = Address(property),
            SupplierStateCode = registration.StateCode,
            BillToGuestId = customer.GuestId,
            CustomerName = customer.Name,
            CustomerGstin = customer.Gstin,
            CustomerAddress = customer.Address,
            CustomerStateCode = customer.StateCode,
            // a stay and anything served at the property is supplied where the property is (IGST Act s.12(3))
            PlaceOfSupply = registration.StateCode,
            Currency = folio.Currency,
            Notes = ReservationService.Clean(r.Notes),
            IssuedAt = clock.GetUtcNow(),
            IssuedBy = caller.UserId,
        };
        short n = 0;
        foreach (var l in lines)
        {
            var item = Billed(new InvoiceItem { InvoiceId = invoice.Id }, invoice.OrganizationId, invoice.PropertyId, ++n, l);
            item.Taxes.AddRange(l.Taxes.Select(t => Tax(new InvoiceItemTax { InvoiceItemId = item.Id }, invoice, t)));
            invoice.Items.Add(item);
        }
        InvoiceMath.Total(invoice, invoice.Items.Select(i => (i.TaxableValue, i.Taxes.Select(t => (t.TaxCode, t.Amount)))));
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    private async Task CheckCanIssueAsync(Property property, Folio folio, Reservation reservation, List<BillableCharge> lines,
        GstRegistration? registration, DateOnly today, Invoice? active, CancellationToken ct)
    {
        if (!FolioPosting.Billable.Contains(reservation.Status))
            throw new BusinessRuleAppException("FOLIO_CLOSED",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so {folio.Number} can't be invoiced.");
        if (active is not null)
            throw new ConflictAppException("ALREADY_INVOICED", lines.Count > 0
                ? $"{folio.Number} already has invoice {active.Number}. Charges posted after it go on a debit note: open {active.Number} and issue one."
                : $"{folio.Number} already has invoice {active.Number}. To change it, issue a credit note on {active.Number}.",
                new { invoiceId = active.Id, number = active.Number });
        if (lines.Count == 0)
        {
            var anything = await db.FolioItems.IgnoreQueryFilters().AnyAsync(i => i.FolioId == folio.Id, ct);
            throw new BusinessRuleAppException("NOTHING_TO_INVOICE", anything
                ? $"{folio.Number} has nothing left to invoice: its charges were reversed, moved or credited to nothing."
                : $"{folio.Number} has no charges yet, so there is nothing to invoice.");
        }
        if (registration is null)
            throw new BusinessRuleAppException("NO_GST_REGISTRATION",
                $"{property.Name} has no GST registration on {QuoteEngine.Day(today)}, so it can't issue a GST invoice. Add its GSTIN in Settings → GST.");
        if (registration.RegistrationType == RegistrationTypes.Composition && lines.FirstOrDefault(l => l.TaxAmount > 0) is { } taxed)
            throw new BusinessRuleAppException("TAXED_UNDER_COMPOSITION",
                $"{property.Name} is under the composition scheme, so it issues a bill of supply with no GST, but \"{taxed.Charge.Description}\" " +
                $"was charged {Money.Rupees(taxed.TaxAmount)} GST when it was posted. Reverse it and post it again before invoicing.");
    }

    // ------------------------------------------------------------------ credit notes

    /// <summary>Cancels (CANCEL) or reduces (REDUCE) an invoice with a credit note. Under an Idempotency-Key.</summary>
    public async Task<(InvoiceDto Invoice, bool Replayed)> CreditAsync(Guid propertyId, Guid invoiceId, string? idempotencyKey,
        CreateCreditNoteRequest r, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var type = r.Type!.Trim().ToUpperInvariant();
        if (type == CreditNoteTypes.Cancel) await access.RequireAsync(Perm.InvoiceCancel, propertyId, ct);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var reason = ReservationService.Clean(r.Reason)!;
        var scope = $"POST /api/v1/properties/{propertyId}/invoices/{invoiceId}/credit-notes";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "credit note", ct) is { } stored)
            return (JsonSerializer.Deserialize<InvoiceDto>(stored, Idempotency.Json)!, true);
        var invoice = await LoadAsync(propertyId, invoiceId, ct);
        await reservations.LockAsync(propertyId, invoice.ReservationId, ct);
        var lines = await CreditableAsync(invoice, ct);
        if (lines.Cancelled is { } c)
            throw new ConflictAppException("INVOICE_CANCELLED",
                $"{invoice.Number} was already cancelled by credit note {c.Number} on {QuoteEngine.Day(c.NoteDate)}. Reload to see it.");

        // what to credit: every line in full (CANCEL), or the chosen lines and amounts (REDUCE)
        var picks = new List<(Creditable Line, decimal Value)>();
        if (type == CreditNoteTypes.Cancel)
        {
            if (r.Lines is { Count: > 0 })
                throw ReservationService.Field(nameof(r.Lines), "Cancelling credits the whole invoice, so don't choose lines. To credit some lines, reduce the invoice instead.");
            picks.AddRange(lines.Lines.Where(l => l.Remaining > 0).Select(l => (l, l.Remaining)));
            if (picks.Count == 0)
                throw new BusinessRuleAppException("NOTHING_TO_CREDIT",
                    $"{invoice.Number} has already been credited in full, so there is nothing left to cancel.");
        }
        else
        {
            if (r.Lines is not { Count: > 0 })
                throw ReservationService.Field(nameof(r.Lines), "Choose at least one line to credit.");
            for (var i = 0; i < r.Lines.Count; i++)
            {
                var want = r.Lines[i];
                var field = $"Lines[{i}].Amount";
                var line = lines.Lines.SingleOrDefault(l => l.Id == want.LineId)
                           ?? throw ReservationService.Field($"Lines[{i}].LineId",
                               $"That line isn't on {invoice.Number} or its debit notes. Reload the invoice and choose again.");
                if (line.Remaining <= 0)
                    throw ReservationService.Field(field, $"\"{line.Description}\" has already been credited in full.");
                var value = want.Amount ?? line.Remaining;
                if (value > line.Remaining)
                    throw ReservationService.Field(field,
                        $"\"{line.Description}\" has {Money.Rupees(line.Remaining)} left to credit (before GST); {Money.Rupees(value)} is more than that.");
                if (picks.Any(p => p.Line.Id == line.Id))
                    throw ReservationService.Field($"Lines[{i}].LineId", $"\"{line.Description}\" is chosen twice. Choose each line once.");
                picks.Add((line, value));
            }
        }

        var today = property.LocalDate(clock.GetUtcNow());
        var (seriesId, number, fy) = await billing.NextNumberAsync(property, DocumentTypes.CreditNote, today, ct);
        var note = new CreditNote
        {
            OrganizationId = invoice.OrganizationId,
            PropertyId = invoice.PropertyId,
            SeriesId = seriesId,
            Number = number,
            FinancialYear = fy,
            NoteDate = today,
            InvoiceId = invoice.Id,
            NoteType = type,
            Reason = reason,
            Currency = invoice.Currency,
            IssuedAt = clock.GetUtcNow(),
            IssuedBy = caller.UserId,
        };
        db.CreditNotes.Add(note);
        var folio = type == CreditNoteTypes.Reduce ? await db.Folios.SingleAsync(f => f.Id == invoice.FolioId, ct) : null;
        var charges = await db.FolioItems.AsNoTracking().Where(i => picks.Select(p => p.Line.FolioItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        short n = 0;
        foreach (var (line, value) in picks)
        {
            var shares = InvoiceMath.CreditTaxes(line.Taxes.Select(t => (t.Rate, t.TaxableValue, t.Amount)).ToList(), line.Remaining, value);
            var item = new CreditNoteItem
            {
                OrganizationId = note.OrganizationId,
                PropertyId = note.PropertyId,
                CreditNoteId = note.Id,
                LineNo = ++n,
                InvoiceItemId = line.FromDebitNote ? null : line.Id,
                DebitNoteItemId = line.FromDebitNote ? line.Id : null,
                FolioItemId = line.FolioItemId,
                Category = line.Category,
                Description = line.Description,
                HsnSac = line.HsnSac,
                TaxableValue = value,
            };
            item.Taxes.AddRange(shares.Where(s => s.TaxableValue != 0 || s.Amount != 0).Select(s =>
            {
                var t = line.Taxes[s.Index];
                return new CreditNoteItemTax
                {
                    OrganizationId = note.OrganizationId,
                    PropertyId = note.PropertyId,
                    CreditNoteItemId = item.Id,
                    TaxRuleId = t.TaxRuleId,
                    TaxCode = t.Code,
                    Rate = t.Rate,
                    TaxableValue = s.TaxableValue,
                    Amount = s.Amount,
                };
            }));
            item.TaxAmount = item.Taxes.Sum(t => t.Amount);
            item.Total = item.TaxableValue + item.TaxAmount;
            if (folio is not null) item.FolioCreditItemId = AddFolioCredit(folio, charges[line.FolioItemId], note, item, reason).Id;
            note.Items.Add(item);
        }
        InvoiceMath.Total(note, note.Items.Select(i => (i.TaxableValue, i.Taxes.Select(t => (t.TaxCode, t.Amount)))));
        audit.Reason = reason;
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(invoice, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, note.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>A CREDIT line on the folio for a REDUCE credit note line: the credited value and tax off the charge.</summary>
    private FolioItem AddFolioCredit(Folio folio, FolioItem charge, CreditNote note, CreditNoteItem line, string reason)
    {
        var credit = new FolioItem
        {
            OrganizationId = folio.OrganizationId,
            PropertyId = folio.PropertyId,
            FolioId = folio.Id,
            ReservationId = folio.ReservationId,
            ItemType = FolioItemTypes.Credit,
            Category = charge.Category,
            Description = FolioPosting.Trim($"Credit note {note.Number}: {charge.Description}"),
            ServiceDate = charge.ServiceDate,
            Quantity = -1,
            UnitPrice = line.TaxableValue,
            Amount = -line.TaxableValue,
            TaxAmount = -line.TaxAmount,
            Total = -line.TaxableValue - line.TaxAmount,
            Source = charge.Source,
            PackageId = charge.PackageId,
            GroupId = charge.GroupId,
            AdjustsItemId = charge.Id,
            CreditNoteId = note.Id,
            Reason = reason,
            PostedAt = clock.GetUtcNow(),
            PostedBy = caller.UserId,
        };
        db.FolioItems.Add(credit);
        foreach (var t in line.Taxes)
            db.FolioItemTaxes.Add(new FolioItemTax
            {
                OrganizationId = folio.OrganizationId,
                PropertyId = folio.PropertyId,
                FolioItemId = credit.Id,
                TaxRuleId = t.TaxRuleId,
                TaxCode = t.TaxCode,
                Rate = t.Rate,
                TaxableValue = -t.TaxableValue,
                Amount = -t.Amount,
                CreatedAt = clock.GetUtcNow(),
            });
        return credit;
    }

    // ------------------------------------------------------------------ debit notes

    /// <summary>Bills the folio's charges posted after its invoice. Under an Idempotency-Key.</summary>
    public async Task<(InvoiceDto Invoice, bool Replayed)> DebitAsync(Guid propertyId, Guid invoiceId, string? idempotencyKey,
        CreateDebitNoteRequest r, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var reason = ReservationService.Clean(r.Reason)!;
        var scope = $"POST /api/v1/properties/{propertyId}/invoices/{invoiceId}/debit-notes";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "debit note", ct) is { } stored)
            return (JsonSerializer.Deserialize<InvoiceDto>(stored, Idempotency.Json)!, true);
        var invoice = await LoadAsync(propertyId, invoiceId, ct);
        var (folio, _) = await LockFolioAsync(propertyId, invoice.FolioId, ct);
        if ((await CreditableAsync(invoice, ct)).Cancelled is { } c)
            throw new BusinessRuleAppException("INVOICE_CANCELLED",
                $"{invoice.Number} was cancelled by credit note {c.Number}, so nothing more can be billed on it. Issue a new invoice for {folio.Number} instead.");
        var lines = await billing.UnbilledAsync(folio, ct);
        if (lines.Count == 0)
            throw new BusinessRuleAppException("NOTHING_TO_BILL",
                $"Every charge on {folio.Number} is already on {invoice.Number} or its debit notes, so there is nothing to put on a debit note.");
        if (r.FolioItemIds is { Count: > 0 } ids)
        {
            var missing = ids.Distinct().Where(id => lines.All(l => l.Charge.Id != id)).ToList();
            if (missing.Count > 0)
                throw ReservationService.Field(nameof(r.FolioItemIds),
                    $"{missing.Count} of the chosen charges aren't on {folio.Number} or are already billed. Reload the invoice and choose again.");
            lines = lines.Where(l => ids.Contains(l.Charge.Id)).ToList();
        }
        var today = property.LocalDate(clock.GetUtcNow());
        if (invoice.DocumentType == DocumentTypes.BillOfSupply && lines.FirstOrDefault(l => l.TaxAmount > 0) is { } taxed)
            throw new BusinessRuleAppException("TAXED_UNDER_COMPOSITION",
                $"{invoice.Number} is a bill of supply (no GST), but \"{taxed.Charge.Description}\" was charged {Money.Rupees(taxed.TaxAmount)} GST. " +
                "Reverse it and post it again first.");

        var (seriesId, number, fy) = await billing.NextNumberAsync(property, DocumentTypes.DebitNote, today, ct);
        var note = new DebitNote
        {
            OrganizationId = invoice.OrganizationId,
            PropertyId = invoice.PropertyId,
            SeriesId = seriesId,
            Number = number,
            FinancialYear = fy,
            NoteDate = today,
            InvoiceId = invoice.Id,
            Reason = reason,
            Currency = invoice.Currency,
            IssuedAt = clock.GetUtcNow(),
            IssuedBy = caller.UserId,
        };
        short n = 0;
        foreach (var l in lines)
        {
            var item = Billed(new DebitNoteItem { DebitNoteId = note.Id }, note.OrganizationId, note.PropertyId, ++n, l);
            item.Taxes.AddRange(l.Taxes.Select(t => Tax(new DebitNoteItemTax { DebitNoteItemId = item.Id }, invoice, t)));
            note.Items.Add(item);
        }
        InvoiceMath.Total(note, note.Items.Select(i => (i.TaxableValue, i.Taxes.Select(t => (t.TaxCode, t.Amount)))));
        db.DebitNotes.Add(note);
        audit.Reason = reason;
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(invoice, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, note.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ PDFs

    public async Task<PrintDocument> InvoicePdfAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var dto = await GetAsync(propertyId, id, ct);
        return printer.Invoice(dto, await PropertyContactAsync(propertyId, ct));
    }

    public async Task<PrintDocument> CreditNotePdfAsync(Guid propertyId, Guid noteId, CancellationToken ct)
    {
        var invoiceId = await db.CreditNotes.AsNoTracking().Where(n => n.Id == noteId && n.PropertyId == propertyId).Select(n => (Guid?)n.InvoiceId)
                            .SingleOrDefaultAsync(ct) ?? throw new NotFoundAppException("Credit note", noteId);
        var dto = await GetAsync(propertyId, invoiceId, ct);
        return printer.Note(dto, dto.CreditNotes.Single(n => n.Id == noteId), await PropertyContactAsync(propertyId, ct));
    }

    public async Task<PrintDocument> DebitNotePdfAsync(Guid propertyId, Guid noteId, CancellationToken ct)
    {
        var invoiceId = await db.DebitNotes.AsNoTracking().Where(n => n.Id == noteId && n.PropertyId == propertyId).Select(n => (Guid?)n.InvoiceId)
                            .SingleOrDefaultAsync(ct) ?? throw new NotFoundAppException("Debit note", noteId);
        var dto = await GetAsync(propertyId, invoiceId, ct);
        return printer.Note(dto, dto.DebitNotes.Single(n => n.Id == noteId), await PropertyContactAsync(propertyId, ct));
    }

    public byte[] Render(PrintDocument document) => pdf.Render(document);

    // ------------------------------------------------------------------ helpers

    /// <summary>A line of the invoice or of one of its debit notes, with what is left to credit on it (value and each tax row).</summary>
    private sealed record Creditable(
        Guid Id, bool FromDebitNote, Guid FolioItemId, string Category, string Description, string? HsnSac, decimal Remaining,
        IReadOnlyList<(Guid? TaxRuleId, string Code, decimal Rate, decimal TaxableValue, decimal Amount)> Taxes);

    private sealed record CreditState(IReadOnlyList<Creditable> Lines, CreditNote? Cancelled);

    private async Task<CreditState> CreditableAsync(Invoice invoice, CancellationToken ct)
    {
        var notes = await db.CreditNotes.AsNoTracking().Include(n => n.Items).ThenInclude(i => i.Taxes)
            .Where(n => n.InvoiceId == invoice.Id).ToListAsync(ct);
        var debits = await db.DebitNotes.AsNoTracking().Include(n => n.Items).ThenInclude(i => i.Taxes)
            .Where(n => n.InvoiceId == invoice.Id).ToListAsync(ct);
        var credited = notes.SelectMany(n => n.Items).ToList();
        var lines = invoice.Items.Select(i => (Line: (BilledLine)i, FromDebit: false, Taxes: i.Taxes.Cast<DocumentTax>().ToList()))
            .Concat(debits.SelectMany(d => d.Items).Select(i => (Line: (BilledLine)i, FromDebit: true, Taxes: i.Taxes.Cast<DocumentTax>().ToList())))
            .Select(x =>
            {
                var done = credited.Where(c => (x.FromDebit ? c.DebitNoteItemId : c.InvoiceItemId) == x.Line.Id).ToList();
                var doneTaxes = done.SelectMany(c => c.Taxes).ToList();
                var taxes = x.Taxes.Select(t => (t.TaxRuleId, t.TaxCode, t.Rate,
                    t.TaxableValue - doneTaxes.Where(d => d.TaxCode == t.TaxCode && d.Rate == t.Rate && d.TaxRuleId == t.TaxRuleId).Sum(d => d.TaxableValue),
                    t.Amount - doneTaxes.Where(d => d.TaxCode == t.TaxCode && d.Rate == t.Rate && d.TaxRuleId == t.TaxRuleId).Sum(d => d.Amount))).ToList();
                return new Creditable(x.Line.Id, x.FromDebit, x.Line.FolioItemId, x.Line.Category, x.Line.Description, x.Line.HsnSac,
                    x.Line.TaxableValue - done.Sum(c => c.TaxableValue), taxes);
            }).ToList();
        return new CreditState(lines, notes.FirstOrDefault(n => n.NoteType == CreditNoteTypes.Cancel));
    }

    private sealed record Sums(decimal Billed, decimal Credited, string Status, int CreditNotes, int DebitNotes);

    private async Task<Dictionary<Guid, Sums>> SumsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var totals = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.Total }).ToDictionaryAsync(i => i.Id, i => i.Total, ct);
        var credits = await db.CreditNotes.AsNoTracking().Where(n => ids.Contains(n.InvoiceId))
            .Select(n => new { n.InvoiceId, n.Total, n.NoteType }).ToListAsync(ct);
        var debits = await db.DebitNotes.AsNoTracking().Where(n => ids.Contains(n.InvoiceId)).Select(n => new { n.InvoiceId, n.Total }).ToListAsync(ct);
        return ids.ToDictionary(id => id, id =>
        {
            var c = credits.Where(x => x.InvoiceId == id).ToList();
            var d = debits.Where(x => x.InvoiceId == id).ToList();
            var billed = totals[id] + d.Sum(x => x.Total);
            var credited = c.Sum(x => x.Total);
            var status = c.Any(x => x.NoteType == CreditNoteTypes.Cancel) ? InvoiceStatuses.Cancelled
                : credited == 0 ? InvoiceStatuses.Issued
                : credited >= billed ? InvoiceStatuses.Credited : InvoiceStatuses.PartlyCredited;
            return new Sums(billed, credited, status, c.Count, d.Count);
        });
    }

    internal async Task<InvoiceDto> ToDtoAsync(Invoice invoice, CancellationToken ct)
    {
        if (invoice.Items.Count == 0)
            invoice = await db.Invoices.AsNoTracking().Include(i => i.Items).ThenInclude(i => i.Taxes).SingleAsync(i => i.Id == invoice.Id, ct);
        var folio = await db.Folios.AsNoTracking().SingleAsync(f => f.Id == invoice.FolioId, ct);
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == invoice.ReservationId, ct);
        var credits = await db.CreditNotes.AsNoTracking().Include(n => n.Items).ThenInclude(i => i.Taxes)
            .Where(n => n.InvoiceId == invoice.Id).OrderBy(n => n.IssuedAt).ToListAsync(ct);
        var debits = await db.DebitNotes.AsNoTracking().Include(n => n.Items).ThenInclude(i => i.Taxes)
            .Where(n => n.InvoiceId == invoice.Id).OrderBy(n => n.IssuedAt).ToListAsync(ct);
        var userIds = new[] { invoice.IssuedBy }.Concat(credits.Select(n => n.IssuedBy)).Concat(debits.Select(n => n.IssuedBy))
            .Where(u => u != null).Select(u => u!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        string? Who(Guid? id) => id is { } u ? users.GetValueOrDefault(u) : null;
        var creditedItems = credits.SelectMany(n => n.Items).ToList();

        DocumentLineDto Line(BilledLine l, IEnumerable<DocumentTax> taxes, bool fromDebit, string? debitNumber, int lineNo)
        {
            var done = creditedItems.Where(c => (fromDebit ? c.DebitNoteItemId : c.InvoiceItemId) == l.Id).Sum(c => c.TaxableValue);
            return new DocumentLineDto(l.Id, lineNo, l.FolioItemId, l.Category, l.Description, l.HsnSac, l.ServiceDate, l.Quantity, l.UnitPrice,
                l.GrossAmount, l.Discount, l.TaxableValue, l.TaxAmount, l.Total, TaxDtos(taxes), done, l.TaxableValue - done, debitNumber);
        }

        var items = invoice.Items.OrderBy(i => i.LineNo).Select(i => Line(i, i.Taxes, false, null, i.LineNo)).ToList();
        var debitLines = debits.SelectMany(d => d.Items.OrderBy(i => i.LineNo).Select(i => (d, i))).ToList();
        items.AddRange(debitLines.Select((x, k) => Line(x.i, x.i.Taxes, true, x.d.Number, invoice.Items.Count + k + 1)));
        var summary = invoice.Items.SelectMany(i => i.Taxes.Select(t => (i.HsnSac, t)))
            .GroupBy(x => (x.HsnSac, x.t.TaxCode, x.t.Rate))
            .Select(g => new TaxSummaryDto(g.Key.HsnSac, g.Key.TaxCode, g.Key.Rate, g.Sum(x => x.t.TaxableValue), g.Sum(x => x.t.Amount)))
            .OrderBy(s => s.HsnSac).ThenBy(s => s.Rate).ThenBy(s => Array.IndexOf(FolioBilling.TaxOrder, s.Code)).ToList();

        var notes = credits.Select(n => new NoteDto(n.Id, DocumentTypes.CreditNote, n.Number, n.NoteType, n.NoteDate, n.Reason, n.TaxableValue,
            n.TaxTotal, n.Total, n.IssuedAt, Who(n.IssuedBy),
            n.Items.OrderBy(i => i.LineNo).Select(i => new NoteLineDto(i.LineNo, i.Description, i.HsnSac, i.TaxableValue, i.TaxAmount, i.Total,
                TaxDtos(i.Taxes))).ToList())).ToList();
        var debitDtos = debits.Select(n => new NoteDto(n.Id, DocumentTypes.DebitNote, n.Number, null, n.NoteDate, n.Reason, n.TaxableValue,
            n.TaxTotal, n.Total, n.IssuedAt, Who(n.IssuedBy),
            n.Items.OrderBy(i => i.LineNo).Select(i => new NoteLineDto(i.LineNo, i.Description, i.HsnSac, i.TaxableValue, i.TaxAmount, i.Total,
                TaxDtos(i.Taxes))).ToList())).ToList();
        var sums = (await SumsAsync([invoice.Id], ct))[invoice.Id];
        var unbilled = sums.Status == InvoiceStatuses.Cancelled ? [] : await billing.UnbilledAsync(folio, ct);
        var einvoice = await db.EInvoiceRecords.AsNoTracking()
            .Where(e => e.DocumentType == DocumentTypes.Invoice && e.DocumentId == invoice.Id)
            .Select(e => new EInvoiceDto(e.Status, e.Irn, e.AckNo, e.AckDate, e.LastError)).SingleOrDefaultAsync(ct);

        var supplier = new DocumentPartyDto(invoice.SupplierLegalName, invoice.SupplierTradeName, invoice.SupplierGstin, invoice.SupplierAddress,
            invoice.SupplierStateCode, GstStates.Words(invoice.SupplierStateCode));
        var customer = new DocumentPartyDto(invoice.CustomerName, null, invoice.CustomerGstin, invoice.CustomerAddress, invoice.CustomerStateCode,
            invoice.CustomerStateCode is { } s ? GstStates.Words(s) : null);
        return new InvoiceDto(invoice.Id, invoice.Number, invoice.DocumentType, DocumentTypes.Words(invoice.DocumentType), invoice.FinancialYear,
            invoice.InvoiceDate, folio.Id, folio.Number, reservation.Id, reservation.Reference, supplier, customer, invoice.IsB2B,
            invoice.PlaceOfSupply, GstStates.Words(invoice.PlaceOfSupply), items, summary, invoice.TaxableValue, invoice.Cgst, invoice.Sgst,
            invoice.Igst, invoice.Cess, invoice.TaxTotal, invoice.Total, InvoiceMath.AmountInWords(invoice.Total), invoice.Currency, invoice.Notes,
            invoice.IssuedAt, Who(invoice.IssuedBy), sums.Status, sums.Billed, sums.Credited, sums.Billed - sums.Credited, notes, debitDtos,
            await receipts.ForReservationAsync(reservation.Id, ct), einvoice, unbilled.Count, unbilled.Sum(l => l.TaxableValue + l.TaxAmount));
    }

    private static List<DocumentTaxDto> TaxDtos(IEnumerable<DocumentTax> taxes) =>
        taxes.OrderBy(t => t.Rate).ThenBy(t => Array.IndexOf(FolioBilling.TaxOrder, t.TaxCode))
            .Select(t => new DocumentTaxDto(t.TaxCode, t.Rate, t.TaxableValue, t.Amount)).ToList();

    private static DocumentLineDto LineDto(Guid? id, int lineNo, Guid folioItemId, FolioItem charge, string? hsn, decimal discount, decimal taxable,
        List<DocumentTaxDto> taxes, decimal credited, string? debitNumber)
    {
        var tax = taxes.Sum(t => t.Amount);
        return new DocumentLineDto(id, lineNo, folioItemId, charge.Category, charge.Description, hsn, charge.ServiceDate, charge.Quantity,
            charge.UnitPrice, charge.Amount, discount, taxable, tax, taxable + tax, taxes, credited, taxable - credited, debitNumber);
    }

    /// <summary>A billed line (invoice or debit note) from a folio charge, as billable now.</summary>
    private static T Billed<T>(T line, Guid orgId, Guid propertyId, short lineNo, BillableCharge l) where T : BilledLine
    {
        line.OrganizationId = orgId;
        line.PropertyId = propertyId;
        line.LineNo = lineNo;
        line.FolioItemId = l.Charge.Id;
        line.Category = l.Charge.Category;
        line.Description = l.Charge.Description;
        line.HsnSac = l.HsnSac;
        line.ServiceDate = l.Charge.ServiceDate;
        line.Quantity = l.Charge.Quantity;
        line.UnitPrice = l.Charge.UnitPrice;
        line.GrossAmount = l.Charge.Amount;
        line.Discount = l.Discount;
        line.TaxableValue = l.TaxableValue;
        line.TaxAmount = l.TaxAmount;
        line.Total = l.TaxableValue + l.TaxAmount;
        return line;
    }

    private static T Tax<T>(T row, Invoice invoice, NetTax t) where T : DocumentTax
    {
        row.OrganizationId = invoice.OrganizationId;
        row.PropertyId = invoice.PropertyId;
        row.TaxRuleId = t.TaxRuleId;
        row.TaxCode = t.Code;
        row.Rate = t.Rate;
        row.TaxableValue = t.TaxableValue;
        row.Amount = t.Amount;
        return row;
    }

    private sealed record Customer(Guid? GuestId, string Name, string? Gstin, string? Address, string? StateCode, string? GstinProblem);

    /// <summary>The folio's bill-to guest (else the booking's guest) as on file: their company and GSTIN make it B2B.</summary>
    private async Task<Customer> DefaultCustomerAsync(Folio folio, Reservation reservation, CancellationToken ct)
    {
        var guestId = folio.BillToGuestId ?? reservation.PrimaryGuestId;
        var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().Include(g => g.Addresses).SingleAsync(g => g.Id == guestId, ct);
        var address = guest.Addresses.FirstOrDefault(a => !a.IsEmpty);
        var gstin = string.IsNullOrWhiteSpace(guest.Gstin) ? null : Gstin.Normalize(guest.Gstin);
        var problem = gstin is null ? null : Gstin.Problem(gstin);
        var name = gstin is not null && !string.IsNullOrWhiteSpace(guest.CompanyName) ? guest.CompanyName.Trim() : guest.FullName;
        var state = gstin is not null && problem is null ? gstin[..2]
            : address?.StateCode is { } sc && GstStates.Names.ContainsKey(sc) ? sc : null;
        return new Customer(guest.Id, name, gstin, AddressText(address), state, problem is null ? null : $"The GSTIN on {guest.FullName}'s profile ({gstin}) isn't valid: {problem}");
    }

    private async Task<Customer> CustomerAsync(Folio folio, Reservation reservation, IssueInvoiceRequest r, CancellationToken ct)
    {
        var fallback = await DefaultCustomerAsync(folio, reservation, ct);
        if (string.IsNullOrWhiteSpace(r.CustomerName))
        {
            if (fallback.GstinProblem is { } p)
                throw ReservationService.Field(nameof(r.CustomerGstin), $"{p} Correct it, or clear it to issue a B2C invoice.");
            return fallback;
        }
        var gstin = string.IsNullOrWhiteSpace(r.CustomerGstin) ? null : Gstin.Normalize(r.CustomerGstin);
        if (gstin is not null && Gstin.Problem(gstin) is { } problem)
            throw ReservationService.Field(nameof(r.CustomerGstin), problem);
        var state = ReservationService.Clean(r.CustomerStateCode);
        if (state is not null && !GstStates.Names.ContainsKey(state))
            throw ReservationService.Field(nameof(r.CustomerStateCode), $"{state} isn't a GST state code. Choose the state from the list.");
        if (gstin is not null && state is not null && state != gstin[..2])
            throw ReservationService.Field(nameof(r.CustomerStateCode),
                $"The GSTIN {gstin} is registered in {GstStates.Words(gstin[..2])}, not {GstStates.Words(state)}. A B2B customer's state is its GSTIN's state.");
        return new Customer(fallback.GuestId, r.CustomerName.Trim(), gstin, ReservationService.Clean(r.CustomerAddress), gstin?[..2] ?? state, null);
    }

    private static string? AddressText(GuestAddress? a) =>
        a is null ? null
            : string.Join(", ", new[] { a.Line1, a.Line2, a.City, a.State, a.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } s ? s : null;

    internal static string? Address(Property p) =>
        string.Join(", ", new[] { p.AddressLine1, p.AddressLine2, p.City, p.StateName, p.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } s
            ? s : null;

    private static DocumentPartyDto Supplier(Property property, GstRegistration registration) =>
        new(registration.LegalName, registration.TradeName ?? property.Name, registration.Gstin, Address(property), registration.StateCode,
            GstStates.Words(registration.StateCode));

    private async Task<GstRegistration?> RegistrationAsync(Property property, DateOnly day, CancellationToken ct) =>
        await db.GstRegistrations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.PropertyId == property.Id && r.EffectiveFrom <= day && (r.EffectiveTo == null || r.EffectiveTo >= day), ct);

    /// <summary>Phone and e-mail printed under the supplier.</summary>
    private async Task<(string? Phone, string? Email)> PropertyContactAsync(Guid propertyId, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        return (p.Phone, p.Email);
    }

    private async Task<(Folio Folio, Reservation Reservation)> LockFolioAsync(Guid propertyId, Guid folioId, CancellationToken ct)
    {
        var reservationId = await db.Folios.AsNoTracking().Where(f => f.Id == folioId && f.PropertyId == propertyId).Select(f => (Guid?)f.ReservationId)
                                .SingleOrDefaultAsync(ct)
                            ?? throw new NotFoundAppException("Folio", folioId);
        var reservation = await reservations.LockAsync(propertyId, reservationId, ct);
        return (await db.Folios.SingleAsync(f => f.Id == folioId, ct), reservation);
    }

    private async Task<Invoice> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.Invoices.AsNoTracking().Include(i => i.Items).ThenInclude(i => i.Taxes).SingleOrDefaultAsync(i => i.Id == id && i.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Invoice", id);

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<Dictionary<Guid, string>> FolioNumbersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Folios.AsNoTracking().Where(f => list.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Number, ct);
    }

    private async Task<Dictionary<Guid, string>> ReferencesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Reservations.AsNoTracking().Where(r => list.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
    }

    private async Task<bool> SettingAsync(Guid propertyId, string key, bool fallback, CancellationToken ct)
    {
        var json = await db.PropertySettings.IgnoreQueryFilters().AsNoTracking().Where(s => s.PropertyId == propertyId && s.Key == key)
            .Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (json is null) return fallback;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    internal static string Amount(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
}
