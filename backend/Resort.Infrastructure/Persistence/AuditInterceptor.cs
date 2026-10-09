using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Resort.Application.Common;
using Resort.Domain.Audit;
using Resort.Domain.Common;
using Resort.Domain.Identity;
using Resort.Domain.Organizations;

namespace Resort.Infrastructure.Persistence;

/// <summary>
/// Runs inside SaveChanges, so audit rows are written in the same transaction as the change they describe:
/// either both commit or neither does.
///  * stamps created_at/updated_at/created_by/updated_by
///  * turns DELETE of soft-deletable entities into deleted_at = now
///  * writes one audit_logs row per changed [IAudited] entity with old/new values (secrets masked)
/// </summary>
public sealed class AuditInterceptor(ICurrentUser currentUser, AuditContext auditContext, TimeProvider clock) : SaveChangesInterceptor
{
    private static readonly HashSet<string> Masked =
        [nameof(User.PasswordHash), nameof(User.SecurityStamp), nameof(RefreshToken.TokenHash)];

    private static readonly HashSet<string> Skipped =
        [nameof(IVersioned.Version), nameof(IAuditableEntity.UpdatedAt), nameof(IAuditableEntity.UpdatedBy),
         nameof(IAuditableEntity.CreatedAt), nameof(IAuditableEntity.CreatedBy)];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx) Process(ctx);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx) Process(ctx);
        return base.SavingChanges(eventData, result);
    }

    private void Process(DbContext ctx)
    {
        var now = clock.GetUtcNow();
        var userId = currentUser.UserId;
        var logs = new List<AuditLog>();

        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged) continue;

            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete soft)
            {
                entry.State = EntityState.Modified;
                soft.DeletedAt = now;
            }

            if (entry.Entity is IAuditableEntity a)
            {
                if (entry.State == EntityState.Added)
                {
                    a.CreatedAt = now;
                    a.CreatedBy ??= userId;
                }
                else
                {
                    entry.Property(nameof(IAuditableEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
                }
                a.UpdatedAt = now;
                a.UpdatedBy = userId;
            }
            if (entry.Entity is UserRole ur && entry.State == EntityState.Added && ur.CreatedAt == default)
                ur.CreatedAt = now;

            if (entry.Entity is IAudited)
                logs.Add(BuildLog(entry, now, userId));
        }

        if (logs.Count > 0) ctx.Set<AuditLog>().AddRange(logs);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private AuditLog BuildLog(EntityEntry entry, DateTimeOffset now, Guid? userId)
    {
        var action = entry.State switch
        {
            EntityState.Added => AuditActions.Insert,
            EntityState.Deleted => AuditActions.Delete,
            _ when entry.Entity is ISoftDelete { DeletedAt: not null } && entry.Property(nameof(ISoftDelete.DeletedAt)).IsModified
                => AuditActions.SoftDelete,
            _ => AuditActions.Update,
        };

        var changes = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (Skipped.Contains(name)) continue;
            var include = entry.State switch
            {
                EntityState.Added or EntityState.Deleted => true,
                _ => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue),
            };
            if (!include) continue;

            object? Val(object? v) => Masked.Contains(name) && v is not null ? "***" : v;
            changes[name] = entry.State switch
            {
                EntityState.Added => new { @new = Val(p.CurrentValue) },
                EntityState.Deleted => new { old = Val(p.OriginalValue) },
                _ => new { old = Val(p.OriginalValue), @new = Val(p.CurrentValue) },
            };
        }

        var (orgId, propertyId) = entry.Entity switch
        {
            Organization o => (o.Id, (Guid?)null),
            Property prop => (prop.OrganizationId, prop.Id),
            IPropertyScoped ps => (ps.OrganizationId, ps.PropertyId),
            IOrganizationScoped os => (os.OrganizationId, (Guid?)null),
            UserRole r => (currentUser.OrganizationId ?? Guid.Empty, r.PropertyId),
            _ => (currentUser.OrganizationId ?? Guid.Empty, (Guid?)null),
        };

        return new AuditLog
        {
            OrganizationId = orgId == Guid.Empty ? null : orgId,
            PropertyId = propertyId,
            UserId = userId,
            Action = action,
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString(),
            Changes = changes.Count == 0 ? null : JsonSerializer.Serialize(changes),
            CorrelationId = currentUser.CorrelationId,
            RequestId = currentUser.RequestId,
            UserAgent = Truncate(currentUser.UserAgent, 500),
            // A reason set by the service (e.g. the block reason) wins over the optional X-Change-Reason header.
            Reason = Truncate(auditContext.Reason ?? currentUser.ChangeReason, 500),
            IpAddress = currentUser.IpAddress,
            OccurredAt = now,
        };
    }
}
