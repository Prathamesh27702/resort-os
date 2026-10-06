namespace Resort.Domain.Common;

/// <summary>Base for tables with a uuid PK. Ids are generated in the app (UUIDv7: time-ordered,
/// index-friendly) so audit entries and related rows can reference them before the INSERT.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Tables with created_at / updated_at / created_by / updated_by. Filled by the audit interceptor.</summary>
public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    Guid? CreatedBy { get; set; }
    Guid? UpdatedBy { get; set; }
}

/// <summary>Master data that is soft-deleted (deleted_at) rather than removed.</summary>
public interface ISoftDelete
{
    DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Rows owned by one organisation (tenant). A global query filter scopes every read to the caller's organisation.</summary>
public interface IOrganizationScoped
{
    Guid OrganizationId { get; set; }
}

/// <summary>Rows that belong to one property. Used for audit context.</summary>
public interface IPropertyScoped : IOrganizationScoped
{
    Guid PropertyId { get; set; }
}

/// <summary>Optimistic concurrency via PostgreSQL's xmin system column.</summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>Marks an entity whose changes are written to audit_logs.</summary>
public interface IAudited;

public abstract class AuditableEntity : Entity, IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
