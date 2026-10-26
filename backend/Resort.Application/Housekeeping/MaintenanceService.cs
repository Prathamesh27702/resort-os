using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Accommodation;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.FrontDesk;
using Resort.Domain.Housekeeping;
using Resort.Domain.Organizations;

namespace Resort.Application.Housekeeping;

/// <summary>
/// Maintenance tickets (Sprint 13, brief §34): anyone on staff reports a problem from their phone, with photos; maintenance
/// works it; a supervisor closes it. OPEN → ASSIGNED → IN_PROGRESS (or ON_HOLD) → RESOLVED → CLOSED.
///
/// Out of order: a ticket on a room can take it out of order. In the same transaction the room gets an OUT_OF_ORDER block
/// from tonight until the expected fix date (so it can't be sold, <see cref="BlockService.AddAsync"/>) and its cleaning
/// state becomes OUT_OF_ORDER (so it can't be checked into). Resolving the ticket releases the block and sends the room
/// to cleaning: it becomes DIRTY with an after-repair task.
///
/// Photos are compressed on the phone, checked here by their first bytes (JPEG, PNG or WebP, at most 2 MB, six per
/// ticket) and stored encrypted through <see cref="IFileStore"/>; the database keeps only the key.
/// </summary>
public sealed class MaintenanceService(
    IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock, BlockService blocks,
    HousekeepingState housekeeping, IFieldEncryptor crypto, IFileStore files)
{
    // ------------------------------------------------------------------ tickets

    /// <summary>Tickets, newest first. Status: OPEN (every unresolved one, the default), a status, or ALL. Someone who can
    /// only report problems (maintenance.create without maintenance.read) sees the ones they reported.</summary>
    public async Task<IReadOnlyList<MaintenanceTicketSummaryDto>> ListAsync(Guid propertyId, string? status, bool mine, Guid? unitId, CancellationToken ct)
    {
        var q = db.MaintenanceTickets.AsNoTracking().Where(t => t.PropertyId == propertyId);
        var me = caller.RequireUserId();
        if (!await CanAsync(Perm.MaintenanceRead, propertyId, ct)) q = q.Where(t => t.ReportedBy == me);
        var s = string.IsNullOrWhiteSpace(status) ? "OPEN" : status.Trim().ToUpperInvariant();
        if (s == "OPEN") q = q.Where(t => MaintenanceStatuses.Unresolved.Contains(t.Status));
        else if (s != "ALL")
        {
            if (!MaintenanceStatuses.All.Contains(s))
                throw ReservationService.Field("Status", $"'{status}' isn't a ticket status. Use OPEN, ALL or one of {string.Join(", ", MaintenanceStatuses.All)}.");
            q = q.Where(t => t.Status == s);
        }
        if (mine) q = q.Where(t => t.AssignedTo == me || t.ReportedBy == me);
        if (unitId is { } u) q = q.Where(t => t.UnitId == u);
        var list = await q.OrderByDescending(t => t.CreatedAt).Take(300).ToListAsync(ct);
        var ids = list.Select(t => t.Id).ToList();
        var photos = await db.MaintenanceAttachments.AsNoTracking().Where(a => ids.Contains(a.TicketId))
            .GroupBy(a => a.TicketId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var names = await NamesAsync(propertyId, list, ct);
        return list
            .OrderBy(t => MaintenanceStatuses.IsUnresolved(t.Status) ? 0 : 1).ThenBy(t => MaintenancePriorities.Rank(t.Priority)).ThenByDescending(t => t.CreatedAt)
            .Select(t => new MaintenanceTicketSummaryDto(t.Id, t.Number, t.Title, t.Category, t.Priority, t.Status, names.Place(t), t.UnitId,
                t.OutOfOrder, t.ExpectedReadyOn, names.User(t.AssignedTo), names.User(t.ReportedBy), photos.GetValueOrDefault(t.Id), t.CreatedAt))
            .ToList();
    }

    public async Task<MaintenanceTicketDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var ticket = await db.MaintenanceTickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct);
        if (ticket is null || (!await CanAsync(Perm.MaintenanceRead, propertyId, ct) && ticket.ReportedBy != caller.UserId))
            throw new NotFoundAppException("That ticket doesn't exist at this property. Reload the list.");
        return await ToDtoAsync(ticket, ct);
    }

    /// <summary>Reports a problem. Out of order: the room is blocked from tonight and can't be checked into until resolved.</summary>
    public async Task<(MaintenanceTicketDto Ticket, bool Replayed)> CreateAsync(Guid propertyId, string? idempotencyKey, CreateTicketRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/maintenance/tickets";
        var key = idempotencyKey is null ? null : Idempotency.RequireKey(idempotencyKey);

        AccommodationUnit? unit = null;
        Bed? bed = null;
        if (r.UnitId is { } uid)
        {
            unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
                       .SingleOrDefaultAsync(u => u.Id == uid && u.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.UnitId), "That room doesn't exist at this property. Reload and choose again.");
            if (r.BedId is { } bid)
                bed = unit.Beds.FirstOrDefault(b => b.Id == bid)
                      ?? throw ReservationService.Field(nameof(r.BedId), $"That bed isn't in {unit.Name}. Reload and choose again.");
        }
        else if (r.BedId is not null)
            throw ReservationService.Field(nameof(r.UnitId), "Choose the room the bed is in.");
        MaintenanceAsset? asset = null;
        if (r.AssetId is { } aid)
        {
            asset = await db.MaintenanceAssets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == aid && a.PropertyId == propertyId, ct)
                    ?? throw ReservationService.Field(nameof(r.AssetId), "That equipment isn't on this property's list. Reload and choose again.");
            if (!asset.IsActive) throw ReservationService.Field(nameof(r.AssetId), $"{asset.Name} is switched off in the equipment list.");
        }
        if (r.OutOfOrder)
        {
            if (unit is null) throw ReservationService.Field(nameof(r.UnitId), "Choose the room to take out of order.");
            if (MaintenanceFlow.ExpectedDateProblem(r.ExpectedReadyOn, today) is { } why) throw ReservationService.Field(nameof(r.ExpectedReadyOn), why);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (key is not null && await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "problem report", ct) is { } stored)
            return (JsonSerializer.Deserialize<MaintenanceTicketDto>(stored, Idempotency.Json)!, true);

        var now = clock.GetUtcNow();
        var ticket = new MaintenanceTicket
        {
            OrganizationId = orgId,
            PropertyId = propertyId,
            Number = await NextNumberAsync(propertyId, ct),
            Title = r.Title.Trim(),
            Description = ReservationService.Clean(r.Description),
            Category = r.Category,
            Priority = r.Priority,
            UnitId = unit?.Id,
            BedId = bed?.Id,
            AssetId = asset?.Id,
            Location = ReservationService.Clean(r.Location),
            OutOfOrder = r.OutOfOrder,
            ExpectedReadyOn = r.OutOfOrder ? r.ExpectedReadyOn : null,
            ReportedBy = caller.UserId,
        };
        db.MaintenanceTickets.Add(ticket);
        AddComment(ticket, "STATUS", r.OutOfOrder ? $"Reported; out of order until {QuoteDay(r.ExpectedReadyOn!.Value)}" : "Reported", null,
            MaintenanceStatuses.Open, now);

        if (r.OutOfOrder)
        {
            var place = HousekeepingFlow.Place(unit!.Name, bed?.Code);
            try
            {
                var block = await blocks.AddAsync(propertyId, unit, bed?.Id, "OUT_OF_ORDER", today, r.ExpectedReadyOn!.Value,
                    $"{ticket.Number}: {ticket.Title}", ct);
                ticket.BlockId = block.Id;
            }
            catch (ConflictAppException ex) when (ex.Code is "BLOCK_OVERLAPS_RESERVATION" or "BLOCK_OVERLAP")
            {
                throw new ConflictAppException(ex.Code,
                    $"{place} can't be taken out of order from tonight: {ex.Message.Replace(" Move that booking to another room or cancel it first.", "")} " +
                    (ex.Code == "BLOCK_OVERLAP"
                        ? "Release that block in Rooms first (or choose a return date before it), or report the problem without taking the room out of order."
                        : "Move that booking to another room first, or report the problem without taking the room out of order."));
            }
            foreach (var b in PlacesOf(unit, bed))
                await housekeeping.ChangeAsync(property, unit.Id, b, HousekeepingStatuses.OutOfOrder, HousekeepingSources.Maintenance, null,
                    ticket.Id, $"{ticket.Number}: {ticket.Title}", ct);
        }

        await db.SaveChangesAsync(ct);
        var dto = await ToDtoAsync(ticket, ct);
        if (key is not null) await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, ticket.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    public async Task<MaintenanceTicketDto> AssignAsync(Guid propertyId, Guid id, AssignTicketRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAsync(propertyId, id, r.Version, "assign", ct);
        var now = clock.GetUtcNow();
        var from = ticket.Status;
        string body;
        if (r.UserId is { } uid)
        {
            var staff = await StaffQuery.WithPermissionAsync(db, propertyId, Perm.MaintenanceUpdate, ct);
            var who = staff.FirstOrDefault(s => s.Id == uid);
            if (who.Id == Guid.Empty)
            {
                var name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).SingleOrDefaultAsync(ct);
                throw ReservationService.Field(nameof(r.UserId), name is null
                    ? "That person isn't on the staff list. Reload and choose again."
                    : $"{name} can't work maintenance tickets at this property (no maintenance role here). Choose someone from maintenance.");
            }
            ticket.AssignedTo = who.Id;
            ticket.AssignedAt = now;
            if (ticket.Status == MaintenanceStatuses.Open) ticket.Status = MaintenanceStatuses.Assigned;
            body = $"Assigned to {who.Name}";
        }
        else
        {
            ticket.AssignedTo = null;
            ticket.AssignedAt = null;
            if (ticket.Status == MaintenanceStatuses.Assigned) ticket.Status = MaintenanceStatuses.Open;
            body = "Unassigned";
        }
        if (ReservationService.Clean(r.Note) is { } note) body += $". {note}";
        AddComment(ticket, "STATUS", body, from, ticket.Status, now);
        audit.Reason = body;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(ticket, ct);
    }

    public async Task<MaintenanceTicketDto> ProgressAsync(Guid propertyId, Guid id, TicketProgressRequest r, CancellationToken ct)
    {
        var to = string.IsNullOrWhiteSpace(r.Status) ? MaintenanceStatuses.InProgress : r.Status.Trim().ToUpperInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAsync(propertyId, id, r.Version, "progress", ct);
        var now = clock.GetUtcNow();
        var from = ticket.Status;
        ticket.Status = to;
        ticket.StartedAt ??= now;
        if (ticket.AssignedTo is null) { ticket.AssignedTo = caller.UserId; ticket.AssignedAt = now; }
        var note = r.Note.Trim();
        AddComment(ticket, from == to ? "NOTE" : "STATUS", note, from == to ? null : from, from == to ? null : to, now);
        audit.Reason = note;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(ticket, ct);
    }

    /// <summary>The repair is done: the room's block is released and the room goes to cleaning (DIRTY, after-repair task).</summary>
    public async Task<MaintenanceTicketDto> ResolveAsync(Guid propertyId, Guid id, ResolveTicketRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAsync(propertyId, id, r.Version, "resolve", ct);
        var now = clock.GetUtcNow();
        var resolution = r.Resolution.Trim();
        var from = ticket.Status;
        ticket.Status = MaintenanceStatuses.Resolved;
        ticket.ResolvedAt = now;
        ticket.ResolvedBy = caller.UserId;
        ticket.Resolution = resolution;
        ticket.StartedAt ??= now;
        AddComment(ticket, "STATUS", $"Resolved: {resolution}", from, ticket.Status, now);
        audit.Reason = resolution;

        if (ticket.OutOfOrder && ticket.UnitId is { } unitId)
        {
            await housekeeping.LockRoomAsync(propertyId, unitId, ct);
            if (ticket.BlockId is { } blockId && await db.UnitBlocks.SingleOrDefaultAsync(b => b.Id == blockId, ct) is { IsActive: true } block)
                blocks.Release(block, $"{ticket.Number} resolved: {resolution}");
            var unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds).SingleAsync(u => u.Id == unitId, ct);
            var bed = ticket.BedId is { } bid ? unit.Beds.FirstOrDefault(b => b.Id == bid) : null;
            // Back to cleaning only where this ticket put the room out of order (another repair may still hold it).
            foreach (var b in PlacesOf(unit, bed))
            {
                if (await housekeeping.StatusOfAsync(unitId, b, ct) != HousekeepingStatuses.OutOfOrder) continue;
                if (await OtherOutOfOrderAsync(ticket, unitId, b, ct)) continue;
                await housekeeping.ChangeAsync(property, unitId, b, HousekeepingStatuses.Dirty, HousekeepingSources.Maintenance, null, ticket.Id,
                    $"Repaired ({ticket.Number}); clean before the next guest", ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(ticket, ct);
    }

    public async Task<MaintenanceTicketDto> CloseAsync(Guid propertyId, Guid id, CloseTicketRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAsync(propertyId, id, r.Version, "close", ct);
        var now = clock.GetUtcNow();
        ticket.Status = MaintenanceStatuses.Closed;
        ticket.ClosedAt = now;
        ticket.ClosedBy = caller.UserId;
        var note = ReservationService.Clean(r.Note);
        AddComment(ticket, "STATUS", note is null ? "Closed" : $"Closed: {note}", MaintenanceStatuses.Resolved, MaintenanceStatuses.Closed, now);
        audit.Reason = note;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(ticket, ct);
    }

    public async Task<MaintenanceTicketDto> CommentAsync(Guid propertyId, Guid id, AddTicketCommentRequest r, CancellationToken ct)
    {
        var ticket = await VisibleAsync(propertyId, id, ct);
        if (ticket.Status == MaintenanceStatuses.Closed)
            throw new BusinessRuleAppException("TICKET_CLOSED", $"{ticket.Number} is closed. Report a new problem if it is broken again.");
        AddComment(ticket, "NOTE", r.Body.Trim(), null, null, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(ticket, ct);
    }

    // ------------------------------------------------------------------ photos

    public async Task<MaintenancePhotoDto> AddPhotoAsync(Guid propertyId, Guid id, UploadedFile file, CancellationToken ct)
    {
        var ticket = await VisibleAsync(propertyId, id, ct);
        if (ticket.Status == MaintenanceStatuses.Closed)
            throw new BusinessRuleAppException("TICKET_CLOSED", $"{ticket.Number} is closed; photos can't be added any more.");
        if (file.Content.Length == 0) throw ReservationService.Field("File", "The photo is empty. Take it again.");
        if (file.Content.Length > MaintenanceFlow.MaxPhotoBytes)
            throw ReservationService.Field("File", $"The photo is {file.Content.Length / 1024 / 1024.0:0.0} MB; at most 2 MB. The app shrinks photos before " +
                                                   "sending; if it didn't, take the photo again or choose a smaller one.");
        var contentType = MaintenanceFlow.SniffImage(file.Content)
                          ?? throw ReservationService.Field("File", "That file isn't a photo. Send a JPEG, PNG or WebP picture.");
        if (await db.MaintenanceAttachments.CountAsync(a => a.TicketId == ticket.Id, ct) >= MaintenanceFlow.MaxPhotosPerTicket)
            throw new BusinessRuleAppException("TOO_MANY_PHOTOS", $"{ticket.Number} already has {MaintenanceFlow.MaxPhotosPerTicket} photos, the most it can have.");

        var photo = new MaintenanceAttachment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            ContentType = contentType,
            SizeBytes = file.Content.Length,
            FileName = Path.ChangeExtension(GuestDocumentService.SafeFileName(file.FileName, "image/jpeg"),
                contentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" }),
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
            KeyId = crypto.ActiveKeyId,
        };
        photo.StorageKey = $"maintenance/{ticket.OrganizationId:N}/{photo.Id:N}";
        await files.SaveAsync(photo.StorageKey, crypto.Encrypt(file.Content, Context(photo)), ct);
        db.MaintenanceAttachments.Add(photo);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            await files.DeleteAsync(photo.StorageKey, CancellationToken.None);
            throw;
        }
        return new MaintenancePhotoDto(photo.Id, photo.FileName, photo.ContentType, photo.SizeBytes, photo.CreatedAt, await NameAsync(caller.UserId, ct));
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> GetPhotoAsync(Guid propertyId, Guid id, Guid photoId, CancellationToken ct)
    {
        var ticket = await VisibleAsync(propertyId, id, ct);
        var photo = await db.MaintenanceAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == photoId && a.TicketId == ticket.Id, ct)
                    ?? throw new NotFoundAppException("That photo isn't on this ticket. Reload the ticket.");
        var encrypted = await files.ReadAsync(photo.StorageKey, ct)
                        ?? throw new NotFoundAppException($"The file of {photo.FileName} is missing from storage. Add the photo again.");
        return (crypto.Decrypt(encrypted, Context(photo)), photo.ContentType, photo.FileName);
    }

    // ------------------------------------------------------------------ assets and staff

    public async Task<IReadOnlyList<MaintenanceAssetDto>> AssetsAsync(Guid propertyId, CancellationToken ct)
    {
        var list = await db.MaintenanceAssets.AsNoTracking().Where(a => a.PropertyId == propertyId)
            .OrderByDescending(a => a.IsActive).ThenBy(a => a.Code).ToListAsync(ct);
        var ids = list.Select(a => a.Id).ToList();
        var open = await db.MaintenanceTickets.AsNoTracking()
            .Where(t => t.AssetId != null && ids.Contains(t.AssetId.Value) && MaintenanceStatuses.Unresolved.Contains(t.Status))
            .GroupBy(t => t.AssetId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var unitIds = list.Where(a => a.UnitId != null).Select(a => a.UnitId!.Value).Distinct().ToList();
        var units = await db.AccommodationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return list.Select(a => ToDto(a, a.UnitId is { } u ? units.GetValueOrDefault(u) : null, open.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<MaintenanceAssetDto> SaveAssetAsync(Guid propertyId, Guid? id, SaveAssetRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        string? unitName = null;
        if (r.UnitId is { } uid)
            unitName = await db.AccommodationUnits.AsNoTracking().Where(u => u.Id == uid && u.PropertyId == propertyId).Select(u => u.Name).SingleOrDefaultAsync(ct)
                       ?? throw ReservationService.Field(nameof(r.UnitId), "That room doesn't exist at this property. Reload and choose again.");
        MaintenanceAsset asset;
        if (id is { } aid)
            asset = await db.MaintenanceAssets.SingleOrDefaultAsync(a => a.Id == aid && a.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That equipment isn't on this property's list. Reload the page.");
        else
        {
            asset = new MaintenanceAsset { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.MaintenanceAssets.Add(asset);
        }
        if (await db.MaintenanceAssets.AsNoTracking().AnyAsync(a => a.PropertyId == propertyId && a.Code == code && a.Id != asset.Id, ct))
            throw ReservationService.Field(nameof(r.Code), $"{code} is already used by another piece of equipment here. Choose another code.");
        asset.Code = code;
        asset.Name = r.Name.Trim();
        asset.Category = r.Category;
        asset.UnitId = r.UnitId;
        asset.Location = ReservationService.Clean(r.Location);
        asset.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(asset, unitName, 0);
    }

    /// <summary>People who can work tickets here (maintenance.update at this property), with their unresolved tickets.</summary>
    public async Task<IReadOnlyList<StaffOptionDto>> StaffAsync(Guid propertyId, CancellationToken ct)
    {
        var users = await StaffQuery.WithPermissionAsync(db, propertyId, Perm.MaintenanceUpdate, ct);
        var open = await db.MaintenanceTickets.AsNoTracking()
            .Where(t => t.PropertyId == propertyId && t.AssignedTo != null && MaintenanceStatuses.Unresolved.Contains(t.Status))
            .GroupBy(t => t.AssignedTo!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.Select(u => new StaffOptionDto(u.Id, u.Name, open.GetValueOrDefault(u.Id))).ToList();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Locks the ticket row and checks <paramref name="action"/> is allowed in its status.</summary>
    private async Task<MaintenanceTicket> LockAsync(Guid propertyId, Guid id, uint? version, string action, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM maintenance_tickets WHERE id = {id} AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} FOR UPDATE", ct);
        var ticket = await db.MaintenanceTickets.SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct)
                     ?? throw new NotFoundAppException("That ticket doesn't exist at this property. Reload the list.");
        if (MaintenanceFlow.Problem(action, ticket.Number, ticket.Status) is { } why)
            throw new BusinessRuleAppException(ticket.Status == MaintenanceStatuses.Closed ? "TICKET_CLOSED" : "INVALID_STATUS_TRANSITION", why,
                new { from = ticket.Status, action });
        if (version is { } v) ConcurrencyGuard.ExpectVersion(db, ticket, v);
        return ticket;
    }

    /// <summary>A ticket the caller may see (maintenance.read, or they reported it), tracked.</summary>
    private async Task<MaintenanceTicket> VisibleAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var ticket = await db.MaintenanceTickets.SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct);
        if (ticket is null || (!await CanAsync(Perm.MaintenanceRead, propertyId, ct) && ticket.ReportedBy != caller.UserId))
            throw new NotFoundAppException("That ticket doesn't exist at this property. Reload the list.");
        return ticket;
    }

    /// <summary>The cleaning-state rows a ticket covers: the bed, a private room, or every bed of a whole dorm.</summary>
    private static List<Guid?> PlacesOf(AccommodationUnit unit, Bed? bed) =>
        bed is not null ? [bed.Id]
        : unit.Type.InventoryMode == InventoryModes.Bed ? unit.Beds.Where(b => b.IsActive).Select(b => (Guid?)b.Id).ToList()
        : [null];

    private async Task<bool> OtherOutOfOrderAsync(MaintenanceTicket ticket, Guid unitId, Guid? bedId, CancellationToken ct) =>
        await db.MaintenanceTickets.AnyAsync(t => t.Id != ticket.Id && t.UnitId == unitId && t.OutOfOrder
                                                  && MaintenanceStatuses.Unresolved.Contains(t.Status) && (t.BedId == null || t.BedId == bedId), ct);

    private void AddComment(MaintenanceTicket ticket, string kind, string body, string? from, string? to, DateTimeOffset at) =>
        db.MaintenanceComments.Add(new MaintenanceComment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            Kind = kind,
            Body = body,
            FromStatus = from,
            ToStatus = to,
            CreatedAt = at,
            CreatedBy = caller.UserId,
        });

    /// <summary>The property's next ticket number: M00001, M00002 … (a counter row, so two at once get different ones).</summary>
    private async Task<string> NextNumberAsync(Guid propertyId, CancellationToken ct)
    {
        var number = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({propertyId}, 'maintenance_ticket', 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        return $"M{number:D5}";
    }

    private static string Context(MaintenanceAttachment a) => $"maintenance_attachment:{a.Id:N}:file";

    private static string QuoteDay(DateOnly d) => Domain.Rates.QuoteEngine.Day(d);

    private async Task<bool> CanAsync(string permission, Guid propertyId, CancellationToken ct) =>
        (await access.GetAsync(ct)).Has(permission, propertyId);

    private async Task<string?> NameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).SingleOrDefaultAsync(ct);

    private sealed class Names(Dictionary<Guid, string> users, Dictionary<Guid, (string Name, Dictionary<Guid, string> Beds)> units,
        Dictionary<Guid, (string Name, string? Location)> assets)
    {
        public string? User(Guid? id) => id is { } x ? users.GetValueOrDefault(x) : null;
        public string? Unit(Guid? id) => id is { } x && units.TryGetValue(x, out var u) ? u.Name : null;
        public string? Bed(Guid? unitId, Guid? bedId) =>
            unitId is { } u && bedId is { } b && units.TryGetValue(u, out var x) ? x.Beds.GetValueOrDefault(b) : null;
        public string? Asset(Guid? id) => id is { } x && assets.TryGetValue(x, out var a) ? a.Name : null;

        public string Place(MaintenanceTicket t)
        {
            if (Unit(t.UnitId) is { } room)
            {
                var place = HousekeepingFlow.Place(room, Bed(t.UnitId, t.BedId));
                return Asset(t.AssetId) is { } a ? $"{place} · {a}" : place;
            }
            if (t.AssetId is { } aid && assets.TryGetValue(aid, out var asset))
                return asset.Location is null ? asset.Name : $"{asset.Name} ({asset.Location})";
            return t.Location ?? "";
        }
    }

    private async Task<Names> NamesAsync(Guid propertyId, IReadOnlyCollection<MaintenanceTicket> tickets, CancellationToken ct)
    {
        var userIds = tickets.SelectMany(t => new[] { t.ReportedBy, t.AssignedTo, t.ResolvedBy, t.ClosedBy }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var unitIds = tickets.Where(t => t.UnitId != null).Select(t => t.UnitId!.Value).Distinct().ToList();
        var units = (await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => unitIds.Contains(u.Id)).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => (u.Name, u.Beds.ToDictionary(b => b.Id, b => b.Code)));
        var assetIds = tickets.Where(t => t.AssetId != null).Select(t => t.AssetId!.Value).Distinct().ToList();
        var assets = await db.MaintenanceAssets.AsNoTracking().Where(a => assetIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Name, a.Location), ct);
        return new Names(users, units, assets);
    }

    private async Task<MaintenanceTicketDto> ToDtoAsync(MaintenanceTicket t, CancellationToken ct)
    {
        var comments = await db.MaintenanceComments.AsNoTracking().Where(c => c.TicketId == t.Id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var photos = await db.MaintenanceAttachments.AsNoTracking().Where(a => a.TicketId == t.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var names = await NamesAsync(t.PropertyId, [t], ct);
        var extra = comments.Select(c => c.CreatedBy).Concat(photos.Select(p => p.CreatedBy)).OfType<Guid>().Distinct().ToList();
        var people = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => extra.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        string? Person(Guid? id) => id is { } x ? people.GetValueOrDefault(x) : null;
        var blockActive = t.BlockId is { } bid && await db.UnitBlocks.AsNoTracking().AnyAsync(b => b.Id == bid && b.Status == BlockStatuses.Active, ct);

        var a = await access.GetAsync(ct);
        var actions = new List<string>();
        if (t.Status != MaintenanceStatuses.Closed)
        {
            if (a.Has(Perm.MaintenanceAssign, t.PropertyId) && MaintenanceStatuses.IsUnresolved(t.Status)) actions.Add("assign");
            if (a.Has(Perm.MaintenanceUpdate, t.PropertyId) && MaintenanceStatuses.IsUnresolved(t.Status)) { actions.Add("progress"); actions.Add("resolve"); }
            if (a.Has(Perm.MaintenanceAssign, t.PropertyId) && t.Status == MaintenanceStatuses.Resolved) actions.Add("close");
            actions.Add("comment");
            if (photos.Count < MaintenanceFlow.MaxPhotosPerTicket) actions.Add("photo");
        }
        return new MaintenanceTicketDto(t.Id, t.Number, t.Title, t.Description, t.Category, t.Priority, t.Status, names.Place(t),
            t.UnitId, names.Unit(t.UnitId), t.BedId, names.Bed(t.UnitId, t.BedId), t.AssetId, names.Asset(t.AssetId), t.Location,
            t.OutOfOrder, t.ExpectedReadyOn, t.BlockId, blockActive,
            t.ReportedBy, names.User(t.ReportedBy), t.AssignedTo, names.User(t.AssignedTo), t.AssignedAt, t.StartedAt, t.ResolvedAt,
            names.User(t.ResolvedBy), t.Resolution, t.ClosedAt, names.User(t.ClosedBy), t.CreatedAt,
            comments.Select(c => new MaintenanceCommentDto(c.Id, c.Kind, c.Body, c.FromStatus, c.ToStatus, c.CreatedAt, Person(c.CreatedBy))).ToList(),
            photos.Select(p => new MaintenancePhotoDto(p.Id, p.FileName, p.ContentType, p.SizeBytes, p.CreatedAt, Person(p.CreatedBy))).ToList(),
            actions, t.Version);
    }

    private static MaintenanceAssetDto ToDto(MaintenanceAsset a, string? unitName, int open) =>
        new(a.Id, a.Code, a.Name, a.Category, a.UnitId, unitName, a.Location, a.IsActive, open);

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
}
