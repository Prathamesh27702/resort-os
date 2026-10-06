using Microsoft.EntityFrameworkCore;
using Resort.Domain.Common;

namespace Resort.Application.Common;

public static class ConcurrencyGuard
{
    /// <summary>
    /// Sets the version the client last saw as the concurrency token's original value, so the UPDATE is
    /// issued as <c>... WHERE id = @id AND xmin = @clientVersion</c>. If someone else saved in between,
    /// zero rows match and EF raises DbUpdateConcurrencyException (mapped to HTTP 409).
    /// </summary>
    public static void ExpectVersion<T>(IAppDbContext db, T entity, uint clientVersion) where T : class, IVersioned
    {
        if (entity.Version != clientVersion) throw new ConcurrencyAppException();
        if (db is DbContext ctx)
            ctx.Entry(entity).Property(e => e.Version).OriginalValue = clientVersion;
    }
}
