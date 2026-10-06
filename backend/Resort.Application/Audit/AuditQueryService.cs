using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;

namespace Resort.Application.Audit;

public sealed record AuditQuery(string? EntityType, string? EntityId, Guid? UserId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 50);

public sealed class AuditQueryService(IAppDbContext db, ICurrentUser caller)
{
    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditQuery q, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        var rows = db.AuditLogs.Where(a => a.OrganizationId == orgId);
        if (!string.IsNullOrWhiteSpace(q.EntityType)) rows = rows.Where(a => a.EntityType == q.EntityType);
        if (!string.IsNullOrWhiteSpace(q.EntityId)) rows = rows.Where(a => a.EntityId == q.EntityId);
        if (q.UserId is { } uid) rows = rows.Where(a => a.UserId == uid);
        if (q.From is { } from) rows = rows.Where(a => a.OccurredAt >= from);
        if (q.To is { } to) rows = rows.Where(a => a.OccurredAt < to);

        var total = await rows.LongCountAsync(ct);
        var items = await rows.OrderByDescending(a => a.Id)
            .Skip((page - 1) * size).Take(size)
            .GroupJoin(db.Users.IgnoreQueryFilters(), a => a.UserId, u => u.Id, (a, us) => new { a, us })
            .SelectMany(x => x.us.DefaultIfEmpty(), (x, u) => new { x.a, UserName = u != null ? u.FullName : null })
            .ToListAsync(ct);

        return new PagedResult<AuditLogDto>(
            items.Select(x => new AuditLogDto(x.a.Id, x.a.OccurredAt, x.a.UserId, x.UserName, x.a.PropertyId,
                x.a.Action, x.a.EntityType, x.a.EntityId,
                x.a.Changes is null ? null : JsonDocument.Parse(x.a.Changes).RootElement.Clone(),
                x.a.Reason, x.a.UserAgent, x.a.IpAddress?.ToString(), x.a.CorrelationId, x.a.RequestId)).ToList(),
            page, size, total);
    }
}
