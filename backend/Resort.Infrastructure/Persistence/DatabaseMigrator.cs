using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Resort.Infrastructure.Persistence;

/// <summary>
/// Applies the versioned SQL files from /db/migrations (embedded in this assembly).
///  * pg_advisory_lock serialises concurrent API instances starting together
///  * each file runs in its own transaction; a failure rolls that file back and stops startup
///  * a SHA-256 checksum of every applied file is stored; if an already-applied file is edited
///    afterwards, startup fails (accepted migrations are immutable — add a new one instead)
/// </summary>
public sealed partial class DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> log)
{
    private const long LockKey = 0x5245_534F_5254_4F53; // "RESORTOS"

    [GeneratedRegex(@"^migrations/V(\d{3,})__([a-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    public sealed record Migration(int Version, string Name, string Sql, string Checksum);

    public static IReadOnlyList<Migration> Discover()
    {
        var asm = typeof(DatabaseMigrator).Assembly;
        var list = new List<Migration>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            var m = FileName().Match(res);
            if (!m.Success) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            var sql = r.ReadToEnd().Replace("\r\n", "\n");
            var sum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            list.Add(new Migration(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value, sql, sum));
        }
        var dup = list.GroupBy(x => x.Version).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"Duplicate migration version V{dup.Key:000}.");
        return list.OrderBy(x => x.Version).ToList();
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        var migrations = Discover();
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await Exec(conn, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await Exec(conn, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version     integer     PRIMARY KEY,
                    name        text        NOT NULL,
                    checksum    text        NOT NULL,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            var applied = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", conn))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) applied[rd.GetInt32(0)] = rd.GetString(1);

            foreach (var m in migrations)
            {
                if (applied.TryGetValue(m.Version, out var sum))
                {
                    if (sum != m.Checksum)
                        throw new InvalidOperationException(
                            $"Migration V{m.Version:000}__{m.Name} was modified after it was applied. " +
                            "Applied migrations must not change — create a new migration instead.");
                    continue;
                }

                log.LogInformation("Applying migration V{Version:000}__{Name}", m.Version, m.Name);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES ($1, $2, $3)", conn, tx))
                {
                    cmd.Parameters.AddWithValue(m.Version);
                    cmd.Parameters.AddWithValue(m.Name);
                    cmd.Parameters.AddWithValue(m.Checksum);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await Exec(conn, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static async Task Exec(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
