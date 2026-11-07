using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Purchasing;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Finance;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Tax;

namespace Resort.Application.Finance;

/// <summary>
/// Expenses (Sprint 22, brief §40): staff record money spent from a phone, with a photo of the receipt and, for a GST bill,
/// the vendor's GSTIN, the bill number and the GST on it. Someone with expense.approve approves or rejects it; only approved
/// expenses count in the budget's actuals.
/// <list type="bullet">
/// <item>GST: the GST shown on the bill is split into CGST + SGST when the vendor's GSTIN is from the property's state,
/// otherwise IGST. It can be claimed as input credit (ItcEligible) when the property is a regular GST registrant on the
/// expense's date and the category allows it; then only the taxable value is a cost.</item>
/// <item>Approval is audited (the audit trail has the old and new status, who and when, and the reason). Nobody approves an
/// expense they recorded themselves unless they also manage the budget (budget.manage: the owner or general manager).
/// An expense over the receipt limit (expenses.receipt_required_over, ₹500) needs a receipt first.</item>
/// <item>A decided expense is frozen by a database trigger: what was approved is what the actuals add up.</item>
/// <item>Recording is idempotent (Idempotency-Key), so a retried tap on a weak signal records it once.</item>
/// </list>
/// Receipts are checked by their first bytes (JPEG, PNG, WebP or PDF, at most 2 MB, four per expense) and stored encrypted
/// through <see cref="IFileStore"/>; the database keeps only the key.
/// </summary>
public sealed class ExpenseService(IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock,
    IFieldEncryptor crypto, IFileStore files)
{
    public const string ReceiptRequiredOverSetting = "expenses.receipt_required_over";
    public const decimal DefaultReceiptRequiredOver = 500m;

    // ------------------------------------------------------------------ set-up

    public async Task<ExpenseSetupDto> SetupAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        await EnsureDefaultsAsync(property, ct);
        var a = await access.GetAsync(ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var reg = await RegistrationAsync(propertyId, today, ct);
        return new ExpenseSetupDto(await CategoryDtosAsync(propertyId, ct), await CostCentreDtosAsync(propertyId, ct), ExpensePaymentMethods.All,
            await ReceiptLimitAsync(propertyId, ct), reg?.RegistrationType == RegistrationTypes.Regular, reg?.StateCode ?? property.GstStateCode,
            a.Has(Perm.ExpenseApprove, propertyId), a.Has(Perm.BudgetManage, propertyId));
    }

    public async Task<ExpenseCategoryDto> SaveCategoryAsync(Guid propertyId, Guid? id, SaveExpenseCategoryRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        await EnsureDefaultsAsync(property, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        ExpenseCategory c;
        if (id is { } existing)
        {
            c = await db.ExpenseCategories.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That category doesn't exist at this property. Reload the page.");
            if (r.Version is { } ver) ConcurrencyGuard.ExpectVersion(db, c, ver);
        }
        else
        {
            c = new ExpenseCategory { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.ExpenseCategories.Add(c);
        }
        var others = await db.ExpenseCategories.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != c.Id).ToListAsync(ct);
        if (others.FirstOrDefault(x => x.Code == code) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode.Name} already has the code {code}. Choose another code.");
        if (others.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a category called {name}.");
        c.Code = code;
        c.Name = name;
        c.GstClaimable = r.GstClaimable;
        c.SortOrder = r.SortOrder;
        c.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await CategoryDtosAsync(propertyId, ct)).Single(x => x.Id == c.Id);
    }

    public async Task<CostCentreDto> SaveCostCentreAsync(Guid propertyId, Guid? id, SaveCostCentreRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        await EnsureDefaultsAsync(property, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        CostCentre c;
        if (id is { } existing)
        {
            c = await db.CostCentres.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That department doesn't exist at this property. Reload the page.");
            if (r.Version is { } ver) ConcurrencyGuard.ExpectVersion(db, c, ver);
        }
        else
        {
            c = new CostCentre { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.CostCentres.Add(c);
        }
        var others = await db.CostCentres.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != c.Id).ToListAsync(ct);
        if (others.FirstOrDefault(x => x.Code == code) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode.Name} already has the code {code}. Choose another code.");
        if (others.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a department called {name}.");
        c.Code = code;
        c.Name = name;
        c.SortOrder = r.SortOrder;
        c.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await CostCentreDtosAsync(propertyId, ct)).Single(x => x.Id == c.Id);
    }

    // ------------------------------------------------------------------ expenses

    /// <summary>Expenses newest first (at most 500). Status: a status, WAITING (= SUBMITTED) or ALL (the default). Mine:
    /// only the ones the caller recorded.</summary>
    public async Task<ExpenseListDto> ListAsync(Guid propertyId, DateOnly? from, DateOnly? to, string? status, Guid? costCentreId, Guid? categoryId,
        bool mine, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var q = db.Expenses.AsNoTracking().Where(e => e.PropertyId == propertyId);
        if (from is { } f) q = q.Where(e => e.ExpenseDate >= f);
        if (to is { } t)
        {
            if (from is { } f2 && t < f2) throw ReservationService.Field("To", "The last day is before the first day.");
            q = q.Where(e => e.ExpenseDate <= t);
        }
        var s = string.IsNullOrWhiteSpace(status) ? "ALL" : status.Trim().ToUpperInvariant();
        if (s == "WAITING") s = ExpenseStatuses.Submitted;
        if (s != "ALL")
        {
            if (!ExpenseStatuses.Moves.Select(m => m.To).Append(ExpenseStatuses.Submitted).Contains(s))
                throw ReservationService.Field("Status", $"'{status}' isn't an expense status. Use ALL, WAITING, APPROVED, REJECTED or CANCELLED.");
            q = q.Where(e => e.Status == s);
        }
        if (costCentreId is { } cc) q = q.Where(e => e.CostCentreId == cc);
        if (categoryId is { } cat) q = q.Where(e => e.CategoryId == cat);
        if (mine)
        {
            var me = caller.RequireUserId();
            q = q.Where(e => e.CreatedBy == me);
        }
        var list = await q.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Code).Take(500).ToListAsync(ct);
        var waiting = await db.Expenses.AsNoTracking().Where(e => e.PropertyId == propertyId && e.Status == ExpenseStatuses.Submitted)
            .GroupBy(e => 1).Select(g => new { Count = g.Count(), Amount = g.Sum(e => e.Amount) }).FirstOrDefaultAsync(ct);
        var dtos = await DtosAsync(property, list, ct);
        return new ExpenseListDto(dtos, list.Sum(e => e.Amount), list.Sum(e => e.CostAmount), waiting?.Count ?? 0, waiting?.Amount ?? 0);
    }

    public async Task<ExpenseDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var e = await db.Expenses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That expense doesn't exist at this property. Reload the list.");
        return (await DtosAsync(property, [e], ct))[0];
    }

    /// <summary>Records an expense, waiting for approval. A retried tap with the same Idempotency-Key gives the first one back.</summary>
    public async Task<(ExpenseDto Expense, bool Replayed)> CreateAsync(Guid propertyId, string? idempotencyKey, SaveExpenseRequest r, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await RequirePropertyAsync(propertyId, ct);
        await EnsureDefaultsAsync(property, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/expenses";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "expense", ct) is { } stored)
            return (JsonSerializer.Deserialize<ExpenseDto>(stored, Idempotency.Json)!, true);

        var e = new Expense
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            Code = await PurchasingService.NextNumberAsync(db, propertyId, "expense", "EX", ct),
        };
        await ApplyAsync(property, e, r, ct);
        db.Expenses.Add(e);
        audit.Reason = $"{e.Code}: {Money.Rupees(e.Amount)} to {e.Payee} recorded";
        await db.SaveChangesAsync(ct);

        var dto = (await DtosAsync(property, [e], ct))[0];
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, e.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>Changes an expense that waits for approval: the person who recorded it, or an approver.</summary>
    public async Task<ExpenseDto> UpdateAsync(Guid propertyId, Guid id, SaveExpenseRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var e = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, e, v);
        if (e.Status != ExpenseStatuses.Submitted)
            throw new BusinessRuleAppException("EXPENSE_DECIDED", $"{e.Code} is {ExpenseStatuses.Words(e.Status).ToLowerInvariant()}, so it can't be changed. Record a new expense if something was wrong.");
        if (!await MayChangeAsync(e, ct))
            throw new ForbiddenAppException($"{e.Code} was recorded by someone else. Only they, or someone who approves expenses, can change it.");
        await ApplyAsync(property, e, r, ct);
        audit.Reason = $"{e.Code} changed before approval";
        await db.SaveChangesAsync(ct);
        return (await DtosAsync(property, [e], ct))[0];
    }

    public async Task<ExpenseDto> ApproveAsync(Guid propertyId, Guid id, ExpenseDecisionRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var e = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, e, v);
        var blocked = await ApproveProblemAsync(property, e, await ReceiptCountAsync(e.Id, ct), ct);
        if (blocked is not null) throw new BusinessRuleAppException(blocked.Value.Code, blocked.Value.Message);
        Decide(e, ExpenseStatuses.Approved, null);
        audit.Reason = ReservationService.Clean(r.Reason) is { } note
            ? $"{e.Code} approved: {note}"
            : $"{e.Code} approved: {Money.Rupees(e.CostAmount)} for {await CostCentreNameAsync(e.CostCentreId, ct)}";
        await db.SaveChangesAsync(ct);
        return (await DtosAsync(property, [e], ct))[0];
    }

    public async Task<ExpenseDto> RejectAsync(Guid propertyId, Guid id, ExpenseDecisionRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var e = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, e, v);
        if (e.Status != ExpenseStatuses.Submitted)
            throw new BusinessRuleAppException("EXPENSE_DECIDED", $"{e.Code} is already {ExpenseStatuses.Words(e.Status).ToLowerInvariant()}.");
        var reason = ReservationService.Clean(r.Reason)!;
        Decide(e, ExpenseStatuses.Rejected, reason);
        audit.Reason = $"{e.Code} rejected: {reason}";
        await db.SaveChangesAsync(ct);
        return (await DtosAsync(property, [e], ct))[0];
    }

    /// <summary>Withdraws an expense that waits for approval: the person who recorded it, or an approver.</summary>
    public async Task<ExpenseDto> CancelAsync(Guid propertyId, Guid id, ExpenseDecisionRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var e = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, e, v);
        if (e.Status != ExpenseStatuses.Submitted)
            throw new BusinessRuleAppException("EXPENSE_DECIDED", $"{e.Code} is already {ExpenseStatuses.Words(e.Status).ToLowerInvariant()}, so it can't be cancelled.");
        if (!await MayChangeAsync(e, ct))
            throw new ForbiddenAppException($"{e.Code} was recorded by someone else. Only they, or someone who approves expenses, can cancel it.");
        var reason = ReservationService.Clean(r.Reason)!;
        Decide(e, ExpenseStatuses.Cancelled, reason);
        audit.Reason = $"{e.Code} cancelled: {reason}";
        await db.SaveChangesAsync(ct);
        return (await DtosAsync(property, [e], ct))[0];
    }

    // ------------------------------------------------------------------ receipts

    public async Task<ExpenseReceiptDto> AddReceiptAsync(Guid propertyId, Guid id, UploadedFile file, CancellationToken ct)
    {
        var e = await LoadAsync(propertyId, id, ct);
        if (e.Status != ExpenseStatuses.Submitted)
            throw new BusinessRuleAppException("EXPENSE_DECIDED", $"{e.Code} is {ExpenseStatuses.Words(e.Status).ToLowerInvariant()}; receipts can't be added any more.");
        if (!await MayChangeAsync(e, ct))
            throw new ForbiddenAppException($"{e.Code} was recorded by someone else. Only they, or someone who approves expenses, can add its receipt.");
        if (file.Content.Length == 0) throw ReservationService.Field("File", "The receipt is empty. Take the photo again.");
        if (file.Content.Length > FinanceMath.MaxReceiptBytes)
            throw ReservationService.Field("File", $"The receipt is {file.Content.Length / 1024 / 1024.0:0.0} MB; at most 2 MB. The app shrinks photos before " +
                                                   "sending; for a PDF, save it smaller or photograph the bill instead.");
        var contentType = SniffReceipt(file.Content)
                          ?? throw ReservationService.Field("File", "That file isn't a receipt photo or a PDF. Send a JPEG, PNG or WebP photo, or a PDF bill.");
        if (await ReceiptCountAsync(e.Id, ct) >= FinanceMath.MaxReceiptsPerExpense)
            throw new BusinessRuleAppException("TOO_MANY_RECEIPTS", $"{e.Code} already has {FinanceMath.MaxReceiptsPerExpense} receipts, the most it can have.");

        var receipt = new ExpenseAttachment
        {
            OrganizationId = e.OrganizationId,
            ExpenseId = e.Id,
            ContentType = contentType,
            SizeBytes = file.Content.Length,
            FileName = Path.ChangeExtension(GuestDocumentService.SafeFileName(file.FileName, "image/jpeg"),
                contentType switch { "image/png" => ".png", "image/webp" => ".webp", "application/pdf" => ".pdf", _ => ".jpg" }),
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
            KeyId = crypto.ActiveKeyId,
        };
        receipt.StorageKey = $"expenses/{e.OrganizationId:N}/{receipt.Id:N}";
        await files.SaveAsync(receipt.StorageKey, crypto.Encrypt(file.Content, Context(receipt)), ct);
        db.ExpenseAttachments.Add(receipt);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            await files.DeleteAsync(receipt.StorageKey, CancellationToken.None);
            throw;
        }
        return new ExpenseReceiptDto(receipt.Id, receipt.FileName, receipt.ContentType, receipt.SizeBytes, receipt.CreatedAt, await NameAsync(caller.UserId, ct));
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> GetReceiptAsync(Guid propertyId, Guid id, Guid receiptId, CancellationToken ct)
    {
        var e = await LoadAsync(propertyId, id, ct, tracked: false);
        var receipt = await db.ExpenseAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == receiptId && a.ExpenseId == e.Id, ct)
                      ?? throw new NotFoundAppException($"That receipt isn't on {e.Code}. Reload the expense.");
        var encrypted = await files.ReadAsync(receipt.StorageKey, ct)
                        ?? throw new NotFoundAppException($"The file of {receipt.FileName} is missing from storage.");
        return (crypto.Decrypt(encrypted, Context(receipt)), receipt.ContentType, receipt.FileName);
    }

    /// <summary>JPEG, PNG, WebP or PDF from the first bytes, never from the client's claim.</summary>
    internal static string? SniffReceipt(byte[] b) =>
        Domain.Housekeeping.MaintenanceFlow.SniffImage(b) ?? (GuestDocumentService.Sniff(b) == "application/pdf" ? "application/pdf" : null);

    // ------------------------------------------------------------------ rules

    /// <summary>Fills the expense from the request: checks the date, the category and department, and the GST.</summary>
    private async Task ApplyAsync(Property property, Expense e, SaveExpenseRequest r, CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.ExpenseDate > today)
            throw ReservationService.Field(nameof(r.ExpenseDate), $"{QuoteEngine.Day(r.ExpenseDate)} is in the future. Enter the day the money was spent.");
        if (r.ExpenseDate < today.AddDays(-FinanceMath.MaxDaysBack))
            throw ReservationService.Field(nameof(r.ExpenseDate), $"An expense can be dated at most {FinanceMath.MaxDaysBack} days back (from {QuoteEngine.Day(today.AddDays(-FinanceMath.MaxDaysBack))}).");
        var category = await db.ExpenseCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CategoryId && c.PropertyId == property.Id, ct)
                       ?? throw ReservationService.Field(nameof(r.CategoryId), "Choose what the money was for from this property's list.");
        if (!category.IsActive && category.Id != e.CategoryId)
            throw ReservationService.Field(nameof(r.CategoryId), $"{category.Name} is switched off. Choose another category.");
        var centre = await db.CostCentres.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CostCentreId && c.PropertyId == property.Id, ct)
                     ?? throw ReservationService.Field(nameof(r.CostCentreId), "Choose the department from this property's list.");
        if (!centre.IsActive && centre.Id != e.CostCentreId)
            throw ReservationService.Field(nameof(r.CostCentreId), $"{centre.Name} is switched off. Choose another department.");

        var gstin = string.IsNullOrWhiteSpace(r.VendorGstin) ? null : Gstin.Normalize(r.VendorGstin);
        var bill = ReservationService.Clean(r.BillNumber);
        if (r.GstAmount > 0 && gstin is null)
            throw ReservationService.Field(nameof(r.VendorGstin), "GST can only be entered for a GST bill. Enter the vendor's GSTIN from the bill, or set the GST to ₹0.");
        if (r.GstAmount > 0 && bill is null)
            throw ReservationService.Field(nameof(r.BillNumber), "Enter the bill number printed on the GST bill.");
        if (gstin is not null && bill is not null && (await db.Expenses.AsNoTracking()
                .Where(x => x.PropertyId == property.Id && x.Id != e.Id && x.VendorGstin == gstin && x.BillNumber != null
                            && (x.Status == ExpenseStatuses.Submitted || x.Status == ExpenseStatuses.Approved))
                .Select(x => new { x.Code, x.BillNumber }).ToListAsync(ct))
                .FirstOrDefault(x => string.Equals(x.BillNumber, bill, StringComparison.OrdinalIgnoreCase))?.Code is { } dup)
            throw ReservationService.Field(nameof(r.BillNumber), $"Bill {bill} of {gstin} is already recorded as {dup}. A bill is recorded once.");

        var gst = gstin is null ? 0 : r.GstAmount;
        var reg = gst > 0 ? await RegistrationAsync(property.Id, r.ExpenseDate, ct) : null;
        var buyerState = reg?.StateCode ?? property.GstStateCode;
        var intra = gstin is null || buyerState is null || gstin[..2] == buyerState;
        var (cgst, sgst, igst) = FinanceMath.SplitGst(gst, intra);
        var itc = gst > 0 && category.GstClaimable && reg?.RegistrationType == RegistrationTypes.Regular;

        e.ExpenseDate = r.ExpenseDate;
        e.CategoryId = category.Id;
        e.CostCentreId = centre.Id;
        e.Description = r.Description.Trim();
        e.Payee = r.Payee.Trim();
        e.PaymentMethod = r.PaymentMethod.Trim().ToUpperInvariant();
        e.PaymentReference = ReservationService.Clean(r.PaymentReference);
        e.Amount = r.Amount;
        e.VendorGstin = gstin;
        e.BillNumber = gstin is null ? null : bill;
        e.GstAmount = gst;
        e.TaxableValue = r.Amount - gst;
        e.Cgst = cgst;
        e.Sgst = sgst;
        e.Igst = igst;
        e.ItcEligible = itc;
        e.CostAmount = FinanceMath.Cost(r.Amount, gst, itc);
        e.Note = ReservationService.Clean(r.Note);
    }

    /// <summary>Why the caller can't approve <paramref name="e"/> now, or null.</summary>
    private async Task<(string Code, string Message)?> ApproveProblemAsync(Property property, Expense e, int receipts, CancellationToken ct)
    {
        if (e.Status != ExpenseStatuses.Submitted)
            return ("EXPENSE_DECIDED", $"{e.Code} is already {ExpenseStatuses.Words(e.Status).ToLowerInvariant()}.");
        var a = await access.GetAsync(ct);
        if (!a.Has(Perm.ExpenseApprove, property.Id))
            return ("FORBIDDEN", "Approving expenses needs the expense.approve permission.");
        if (e.CreatedBy == caller.UserId && !a.Has(Perm.BudgetManage, property.Id))
            return ("OWN_EXPENSE", $"You recorded {e.Code} yourself, so someone else who approves expenses has to approve it.");
        var limit = await ReceiptLimitAsync(property.Id, ct);
        if (receipts == 0 && e.Amount > limit)
            return ("RECEIPT_REQUIRED", $"{e.Code} is {Money.Rupees(e.Amount)}, over {Money.Rupees(limit)}, so it needs a photo of the receipt before it can be approved. Add the receipt first.");
        return null;
    }

    private void Decide(Expense e, string status, string? reason)
    {
        e.Status = status;
        e.DecidedAt = clock.GetUtcNow();
        e.DecidedBy = caller.RequireUserId();
        e.DecisionReason = reason;
    }

    private async Task<bool> MayChangeAsync(Expense e, CancellationToken ct) =>
        e.CreatedBy == caller.UserId || (await access.GetAsync(ct)).Has(Perm.ExpenseApprove, e.PropertyId);

    // ------------------------------------------------------------------ shared

    internal async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<Expense> LoadAsync(Guid propertyId, Guid id, CancellationToken ct, bool tracked = true) =>
        await (tracked ? db.Expenses : db.Expenses.AsNoTracking()).SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("That expense doesn't exist at this property. Reload the list.");

    private Task<int> ReceiptCountAsync(Guid expenseId, CancellationToken ct) => db.ExpenseAttachments.CountAsync(a => a.ExpenseId == expenseId, ct);

    private Task<decimal> ReceiptLimitAsync(Guid propertyId, CancellationToken ct) =>
        PropertySettingsReader.DecimalAsync(db, propertyId, ReceiptRequiredOverSetting, DefaultReceiptRequiredOver, ct);

    private Task<GstRegistration?> RegistrationAsync(Guid propertyId, DateOnly day, CancellationToken ct) =>
        db.GstRegistrations.AsNoTracking()
            .Where(x => x.PropertyId == propertyId && x.EffectiveFrom <= day && (x.EffectiveTo == null || x.EffectiveTo >= day))
            .FirstOrDefaultAsync(ct);

    private Task<string> CostCentreNameAsync(Guid id, CancellationToken ct) =>
        db.CostCentres.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).SingleAsync(ct);

    private static string Context(ExpenseAttachment a) => $"expense_attachment:{a.Id:N}:file";

    private async Task<string?> NameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).SingleOrDefaultAsync(ct);

    internal async Task<List<ExpenseCategoryDto>> CategoryDtosAsync(Guid propertyId, CancellationToken ct) =>
        (await db.ExpenseCategories.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(c => new ExpenseCategoryDto(c.Id, c.Code, c.Name, c.GstClaimable, c.SortOrder, c.IsActive, c.Version)).ToList();

    internal async Task<List<CostCentreDto>> CostCentreDtosAsync(Guid propertyId, CancellationToken ct) =>
        (await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(c => new CostCentreDto(c.Id, c.Code, c.Name, c.SortOrder, c.IsActive, c.Version)).ToList();

    private async Task<List<ExpenseDto>> DtosAsync(Property property, List<Expense> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var ids = list.Select(e => e.Id).ToList();
        var receipts = (await db.ExpenseAttachments.AsNoTracking().Where(a => ids.Contains(a.ExpenseId)).ToListAsync(ct))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName).ToList();
        var userIds = list.SelectMany(e => new[] { e.CreatedBy, e.DecidedBy }).Concat(receipts.Select(r => r.CreatedBy)).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var categories = await db.ExpenseCategories.AsNoTracking().Where(c => c.PropertyId == property.Id).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == property.Id).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var a = await access.GetAsync(ct);
        var canApprove = a.Has(Perm.ExpenseApprove, property.Id);
        var limit = await ReceiptLimitAsync(property.Id, ct);
        string? User(Guid? id) => id is { } x ? users.GetValueOrDefault(x) : null;

        var result = new List<ExpenseDto>(list.Count);
        foreach (var e in list)
        {
            var mine = receipts.Where(r => r.ExpenseId == e.Id).ToList();
            var actions = new List<string>();
            var blocked = new Dictionary<string, string>();
            if (e.Status == ExpenseStatuses.Submitted)
            {
                var mayChange = e.CreatedBy == caller.UserId || canApprove;
                if (mayChange)
                {
                    actions.Add("edit");
                    if (mine.Count < FinanceMath.MaxReceiptsPerExpense) actions.Add("receipt");
                    actions.Add("cancel");
                }
                if (canApprove)
                {
                    var problem = await ApproveProblemAsync(property, e, mine.Count, ct);
                    if (problem is null) actions.Add("approve");
                    else blocked["approve"] = problem.Value.Message;
                    actions.Add("reject");
                }
            }
            result.Add(new ExpenseDto(e.Id, e.Code, e.ExpenseDate, e.CategoryId, categories.GetValueOrDefault(e.CategoryId, ""), e.CostCentreId,
                centres.GetValueOrDefault(e.CostCentreId, ""), e.Description, e.Payee, e.PaymentMethod, e.PaymentReference, e.Amount, e.VendorGstin,
                e.BillNumber, e.GstAmount, e.TaxableValue, e.Cgst, e.Sgst, e.Igst, e.ItcEligible, e.CostAmount, e.Note, e.Status, User(e.CreatedBy),
                e.CreatedBy, e.CreatedAt, e.DecidedAt, User(e.DecidedBy), e.DecisionReason,
                e.Status == ExpenseStatuses.Submitted && mine.Count == 0 && e.Amount > limit,
                mine.Select(r => new ExpenseReceiptDto(r.Id, r.FileName, r.ContentType, r.SizeBytes, r.CreatedAt, User(r.CreatedBy))).ToList(),
                actions, blocked, e.Version));
        }
        return result;
    }

    /// <summary>The usual categories and departments, the first time a property's expenses are opened.</summary>
    internal async Task EnsureDefaultsAsync(Property property, CancellationToken ct)
    {
        if (await db.CostCentres.AnyAsync(c => c.PropertyId == property.Id, ct) && await db.ExpenseCategories.AnyAsync(c => c.PropertyId == property.Id, ct)) return;
        var outer = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"expense-defaults:" + property.Id}))", ct);
            if (!await db.ExpenseCategories.AnyAsync(c => c.PropertyId == property.Id, ct))
            {
                var i = 0;
                foreach (var (code, name, claimable) in DefaultCategories)
                    db.ExpenseCategories.Add(new ExpenseCategory
                    {
                        OrganizationId = property.OrganizationId,
                        PropertyId = property.Id,
                        Code = code,
                        Name = name,
                        GstClaimable = claimable,
                        SortOrder = (++i) * 10,
                    });
            }
            if (!await db.CostCentres.AnyAsync(c => c.PropertyId == property.Id, ct))
            {
                var i = 0;
                foreach (var (code, name) in DefaultCostCentres)
                    db.CostCentres.Add(new CostCentre { OrganizationId = property.OrganizationId, PropertyId = property.Id, Code = code, Name = name, SortOrder = (++i) * 10 });
            }
            await db.SaveChangesAsync(ct);
            await MapDepartmentsAsync(property.Id, ct);
            if (outer is not null) await outer.CommitAsync(ct);
        }
        finally
        {
            if (outer is not null) await outer.DisposeAsync();
        }
    }

    /// <summary>Sprint 23: the employee departments not yet given a budget department get the one with the same name or
    /// code, and a kitchen or restaurant gets Food and beverage, so pay counts in the budget from the start.</summary>
    private async Task MapDepartmentsAsync(Guid propertyId, CancellationToken ct)
    {
        var departments = await db.Departments.Where(d => d.PropertyId == propertyId && d.CostCentreId == null).ToListAsync(ct);
        if (departments.Count == 0) return;
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId && c.IsActive).ToListAsync(ct);
        foreach (var d in departments)
        {
            var name = d.Name.Trim();
            var match = centres.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) || c.Code == d.Code)
                        ?? (FoodWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? centres.FirstOrDefault(c => c.Code == "FB") : null);
            d.CostCentreId = match?.Id;
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly string[] FoodWords = ["kitchen", "restaurant", "food", "bar"];

    internal static readonly (string Code, string Name, bool GstClaimable)[] DefaultCategories =
    [
        ("UTIL", "Electricity and water", true),
        ("REPAIR", "Repairs and maintenance", true),
        ("FUEL", "Fuel", true),
        ("SUPPLY", "Supplies and consumables", true),
        ("MKT", "Marketing and commission", true),
        ("OFFICE", "Office and stationery", true),
        ("TRAVEL", "Travel and local transport", true),
        ("STAFFW", "Staff welfare (food)", false),
        ("BANK", "Bank and card charges", true),
        ("LICENCE", "Licences and fees", true),
        ("OTHER", "Other", true),
    ];

    internal static readonly (string Code, string Name)[] DefaultCostCentres =
    [
        ("FO", "Front office"),
        ("HK", "Housekeeping"),
        ("FB", "Food and beverage"),
        ("MNT", "Maintenance"),
        ("ACT", "Activities"),
        ("TRN", "Transport"),
        ("SALES", "Sales and marketing"),
        ("ADMIN", "Administration"),
    ];
}
