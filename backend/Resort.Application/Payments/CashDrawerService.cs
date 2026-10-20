using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Rates;

namespace Resort.Application.Payments;

/// <summary>
/// The cash drawer (Sprint 10, brief §39). A shift opens with the cash counted in the drawer (the float); every cash
/// payment, cash refund and voided cash payment is a line of the session's ledger, so the cash expected is always the sum
/// of those lines. The shift closes with the cash counted now: when it matches, the session is closed; when it is short
/// or over, whoever closes says why and the session waits until someone with cash_drawer.approve_variance approves it.
///
/// Every change locks the session row (FOR UPDATE): a cash payment and the close can't interleave, so the expected
/// amount fixed at the close includes every payment the session took.
/// </summary>
public sealed class CashDrawerService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, PaymentLedger ledger)
{
    public const int RecentSessions = 10;

    // ------------------------------------------------------------------ queries

    public async Task<CashDrawerDto> GetAsync(Guid propertyId, Guid? drawerId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var drawer = await DrawerAsync(property, drawerId, create: false, ct);
        if (drawer is null) return new CashDrawerDto(Guid.Empty, CashDrawer.DefaultName, null, [], []);
        var sessions = await db.CashDrawerSessions.AsNoTracking().Where(s => s.CashDrawerId == drawer.Id)
            .OrderByDescending(s => s.OpenedAt).ToListAsync(ct);
        var current = sessions.FirstOrDefault(s => s.Status == DrawerSessionStatuses.Open);
        var pending = sessions.Where(s => s.Status == DrawerSessionStatuses.VariancePending).ToList();
        var recent = sessions.Where(s => s.Status == DrawerSessionStatuses.Closed).Take(RecentSessions).ToList();
        var shown = (current is null ? pending : pending.Prepend(current)).Concat(recent).ToList();
        var dtos = await ToDtosAsync(drawer, shown, false, ct);
        return new CashDrawerDto(drawer.Id, drawer.Name, current is null ? null : dtos[current.Id],
            pending.Select(s => dtos[s.Id]).ToList(), recent.Select(s => dtos[s.Id]).ToList());
    }

    public async Task<CashDrawerSessionDto> GetSessionAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var session = await db.CashDrawerSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("Cash drawer session", id);
        var drawer = await db.CashDrawers.AsNoTracking().SingleAsync(d => d.Id == session.CashDrawerId, ct);
        return (await ToDtosAsync(drawer, [session], true, ct))[session.Id];
    }

    // ------------------------------------------------------------------ open, close, approve

    /// <summary>Opens a shift with the counted float. One open shift per drawer (also a unique index).</summary>
    public async Task<CashDrawerSessionDto> OpenAsync(Guid propertyId, OpenCashDrawerRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var drawer = (await DrawerAsync(property, r.DrawerId, create: true, ct))!;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM cash_drawers WHERE id = {drawer.Id} FOR UPDATE", ct);
        if (await db.CashDrawerSessions.AsNoTracking().SingleOrDefaultAsync(s => s.CashDrawerId == drawer.Id && s.Status == DrawerSessionStatuses.Open, ct)
            is { } open)
        {
            var who = open.OpenedBy is { } u ? (await ledger.UserNamesAsync([u], ct)).GetValueOrDefault(u) : null;
            throw new ConflictAppException("DRAWER_ALREADY_OPEN",
                $"The {drawer.Name} drawer is already open since {When(property, open.OpenedAt)}{(who is null ? "" : $" ({who})")}. " +
                "Close that shift before opening a new one.", new { sessionId = open.Id });
        }

        var now = clock.GetUtcNow();
        var session = new CashDrawerSession
        {
            OrganizationId = property.OrganizationId,
            PropertyId = property.Id,
            CashDrawerId = drawer.Id,
            OpenedAt = now,
            OpenedBy = caller.UserId,
            OpeningFloat = r.OpeningFloat,
            OpenNote = ReservationService.Clean(r.Note),
        };
        db.CashDrawerSessions.Add(session);
        db.CashDrawerMovements.Add(new CashDrawerMovement
        {
            OrganizationId = property.OrganizationId,
            PropertyId = property.Id,
            SessionId = session.Id,
            MovementType = DrawerMovementTypes.OpeningFloat,
            Amount = r.OpeningFloat,
            Description = "Opening float",
            MovedAt = now,
            MovedBy = caller.UserId,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync(drawer, [session], true, ct))[session.Id];
    }

    /// <summary>
    /// Closes the open shift with the cash counted now. Expected = the sum of the session's ledger. When the count
    /// differs, the note (why it is short or over) is required and the session waits for variance approval.
    /// </summary>
    public async Task<CashDrawerSessionDto> CloseAsync(Guid propertyId, CloseCashDrawerRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var drawer = await DrawerAsync(property, r.DrawerId, create: false, ct);
        var session = drawer is null ? null : await LockOpenSessionAsync(drawer.Id, ct);
        if (session is null)
            throw new BusinessRuleAppException("DRAWER_NOT_OPEN",
                $"The {drawer?.Name ?? CashDrawer.DefaultName} drawer isn't open, so there is no shift to close. Reload to see its last shift.");

        var expected = await ExpectedAsync(session.Id, ct);
        var variance = PaymentMath.Variance(expected, r.CountedAmount);
        var note = ReservationService.Clean(r.Note);
        if (variance != 0 && (note is null || note.Length < 3))
            throw ReservationService.Field(nameof(r.Note),
                $"The drawer should hold {Money.Rupees(expected)} but {Money.Rupees(r.CountedAmount)} was counted: " +
                $"{Money.Rupees(Math.Abs(variance))} {(variance < 0 ? "short" : "over")}. Count again, or say why (at least 3 characters) " +
                "so a manager can approve it.");

        session.ClosedAt = clock.GetUtcNow();
        session.ClosedBy = caller.UserId;
        session.ExpectedAmount = expected;
        session.CountedAmount = r.CountedAmount;
        session.Variance = variance;
        session.CloseNote = note;
        session.Status = variance == 0 ? DrawerSessionStatuses.Closed : DrawerSessionStatuses.VariancePending;
        if (note is not null) audit.Reason = note;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync(drawer!, [session], true, ct))[session.Id];
    }

    /// <summary>Approves a short or over shift (cash_drawer.approve_variance, checked by the endpoint) with a note.</summary>
    public async Task<CashDrawerSessionDto> ApproveVarianceAsync(Guid propertyId, Guid id, ApproveVarianceRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orgId = caller.RequireOrganizationId();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM cash_drawer_sessions WHERE id = {id} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        var session = await db.CashDrawerSessions.SingleOrDefaultAsync(s => s.Id == id && s.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("Cash drawer session", id);
        if (session.Status != DrawerSessionStatuses.VariancePending)
        {
            var message = session.Status switch
            {
                DrawerSessionStatuses.Open => "This shift is still open. The variance is known only when it is closed.",
                _ when session.ApprovedAt is { } at =>
                    $"This shift's variance was already approved at {When(property, at)}" +
                    $"{(session.ApprovedBy is { } u && (await ledger.UserNamesAsync([u], ct)).GetValueOrDefault(u) is { } n ? $" by {n}" : "")}. Reload to see it.",
                _ => "This shift closed with no variance, so there is nothing to approve.",
            };
            throw new ConflictAppException("NO_VARIANCE_PENDING", message);
        }
        var note = ReservationService.Clean(r.Note)!;
        session.Status = DrawerSessionStatuses.Closed;
        session.ApprovedAt = clock.GetUtcNow();
        session.ApprovedBy = caller.UserId;
        session.ApprovalNote = note;
        audit.Reason = note;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var drawer = await db.CashDrawers.AsNoTracking().SingleAsync(d => d.Id == session.CashDrawerId, ct);
        return (await ToDtosAsync(drawer, [session], true, ct))[session.Id];
    }

    // ------------------------------------------------------------------ used by payments

    /// <summary>The property's open shift, locked for this transaction (cash payments and refunds go into it), or a field
    /// error under <paramref name="field"/> saying the drawer must be opened first.</summary>
    internal async Task<CashDrawerSession> RequireOpenSessionAsync(Property property, string field, string what, CancellationToken ct)
    {
        var drawer = await DrawerAsync(property, null, create: false, ct);
        return (drawer is null ? null : await LockOpenSessionAsync(drawer.Id, ct))
               ?? throw ReservationService.Field(field,
                   $"The {drawer?.Name ?? CashDrawer.DefaultName} cash drawer isn't open, so {what} in cash can't be recorded. " +
                   "Open it on the front desk (Cash drawer → Open drawer) with the cash in it, or choose another method.");
    }

    /// <summary>Locks a session for a void or refund of its cash; null when it isn't open any more.</summary>
    internal async Task<CashDrawerSession?> LockSessionIfOpenAsync(Guid sessionId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM cash_drawer_sessions WHERE id = {sessionId} FOR UPDATE", ct);
        return await db.CashDrawerSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.Status == DrawerSessionStatuses.Open, ct);
    }

    /// <summary>Adds a line to a session's ledger. Not saved.</summary>
    internal void AddMovement(CashDrawerSession session, string type, decimal amount, string description, Guid? paymentId, Guid? refundId) =>
        db.CashDrawerMovements.Add(new CashDrawerMovement
        {
            OrganizationId = session.OrganizationId,
            PropertyId = session.PropertyId,
            SessionId = session.Id,
            MovementType = type,
            Amount = amount,
            PaymentId = paymentId,
            RefundId = refundId,
            Description = description.Length <= 200 ? description : description[..197] + "…",
            MovedAt = clock.GetUtcNow(),
            MovedBy = caller.UserId,
        });

    /// <summary>The cash a session should hold now: the sum of its ledger.</summary>
    internal async Task<decimal> ExpectedAsync(Guid sessionId, CancellationToken ct) =>
        await db.CashDrawerMovements.Where(m => m.SessionId == sessionId).SumAsync(m => (decimal?)m.Amount, ct) ?? 0m;

    internal static string When(Property property, DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(property.Timezone));
        return $"{local:HH:mm} on {QuoteEngine.Day(DateOnly.FromDateTime(local.DateTime))}";
    }

    // ------------------------------------------------------------------ helpers

    private async Task<CashDrawerSession?> LockOpenSessionAsync(Guid drawerId, CancellationToken ct)
    {
        var id = await db.CashDrawerSessions.AsNoTracking().Where(s => s.CashDrawerId == drawerId && s.Status == DrawerSessionStatuses.Open)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
        return id is null ? null : await LockSessionIfOpenAsync(id.Value, ct);
    }

    /// <summary>The drawer asked for, or the property's only (first) active drawer; created when the property has none and
    /// <paramref name="create"/> (properties added after Sprint 10's migration get theirs on first use).</summary>
    private async Task<CashDrawer?> DrawerAsync(Property property, Guid? drawerId, bool create, CancellationToken ct)
    {
        if (drawerId is { } id)
            return await db.CashDrawers.SingleOrDefaultAsync(d => d.Id == id && d.PropertyId == property.Id, ct)
                   ?? throw ReservationService.Field("DrawerId", "That cash drawer isn't at this property. Reload and choose again.");
        var drawer = await db.CashDrawers.Where(d => d.PropertyId == property.Id && d.IsActive).OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(ct);
        if (drawer is not null || !create) return drawer;
        drawer = new CashDrawer { OrganizationId = property.OrganizationId, PropertyId = property.Id };
        db.CashDrawers.Add(drawer);
        await db.SaveChangesAsync(ct);
        return drawer;
    }

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<Dictionary<Guid, CashDrawerSessionDto>> ToDtosAsync(CashDrawer drawer, IReadOnlyList<CashDrawerSession> sessions,
        bool withMovements, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var movements = await db.CashDrawerMovements.AsNoTracking().Where(m => ids.Contains(m.SessionId)).ToListAsync(ct);
        var paymentIds = movements.Where(m => m.PaymentId != null).Select(m => m.PaymentId!.Value)
            .Concat(await db.Refunds.AsNoTracking().Where(r => r.CashDrawerSessionId != null && ids.Contains(r.CashDrawerSessionId.Value))
                .Select(r => r.PaymentId).ToListAsync(ct)).Distinct().ToList();
        var payments = await (from p in db.Payments.AsNoTracking()
                              join r in db.Reservations.AsNoTracking() on p.ReservationId equals r.Id
                              where paymentIds.Contains(p.Id)
                              select new { p.Id, p.Number, r.Reference }).ToDictionaryAsync(x => x.Id, ct);
        var refundPayments = await db.Refunds.AsNoTracking().Where(r => movements.Select(m => m.RefundId).Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.PaymentId, ct);
        var users = await ledger.UserNamesAsync(sessions.SelectMany(s => new[] { s.OpenedBy, s.ClosedBy, s.ApprovedBy })
            .Concat(movements.Select(m => m.MovedBy)).Where(u => u != null).Select(u => u!.Value).Distinct().ToList(), ct);
        string? Name(Guid? u) => u is { } x ? users.GetValueOrDefault(x) : null;

        return sessions.ToDictionary(s => s.Id, s =>
        {
            var mine = movements.Where(m => m.SessionId == s.Id).OrderBy(m => m.MovedAt).ThenBy(m => m.Id).ToList();
            var cashIn = mine.Where(m => m.MovementType != DrawerMovementTypes.OpeningFloat && m.Amount > 0).Sum(m => m.Amount);
            var cashOut = -mine.Where(m => m.Amount < 0).Sum(m => m.Amount);
            var lines = withMovements
                ? mine.Select(m =>
                {
                    var pid = m.PaymentId ?? (m.RefundId is { } rid ? refundPayments.GetValueOrDefault(rid) : (Guid?)null);
                    var p = pid is { } x ? payments.GetValueOrDefault(x) : null;
                    return new CashDrawerMovementDto(m.Id, m.MovementType, m.Amount, m.Description, pid, p?.Number, p?.Reference, m.MovedAt, Name(m.MovedBy));
                }).ToList()
                : null;
            return new CashDrawerSessionDto(s.Id, s.CashDrawerId, drawer.Name, s.Status, s.OpenedAt, Name(s.OpenedBy), s.OpeningFloat, s.OpenNote,
                cashIn, cashOut, s.ExpectedAmount ?? mine.Sum(m => m.Amount), mine.Count(m => m.MovementType == DrawerMovementTypes.Payment),
                s.ClosedAt, Name(s.ClosedBy), s.CountedAmount, s.Variance, s.CloseNote, s.ApprovedAt, Name(s.ApprovedBy), s.ApprovalNote, lines);
        });
    }
}
