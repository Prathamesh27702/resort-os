using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Resort.Infrastructure.Persistence;

public sealed class DevSeedOptions
{
    public const string Section = "DevSeed";
    /// <summary>Only honoured when the host environment is Development (checked in Program.cs).</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Applies the demo-data SQL files in database/seed/dev (embedded in this assembly) in name order, each in its own
/// transaction. Every file must be idempotent: it checks for existing data and does nothing if it finds any, so the
/// seeder can run on every start-up. It targets the bootstrap organisation/property (Bootstrap:OrganizationCode /
/// Bootstrap:PropertyCode). Development only — never enabled in staging or production.
/// </summary>
public sealed partial class DevSeeder(NpgsqlDataSource dataSource, IOptions<BootstrapOptions> bootstrap, ILogger<DevSeeder> log)
{
    [GeneratedRegex(@"^seed/dev/(S\d{3,}__[a-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    [GeneratedRegex("^[A-Z0-9_]{2,30}$")]
    private static partial Regex SafeCode();

    public static IReadOnlyList<(string Name, string Sql)> Discover()
    {
        var asm = typeof(DevSeeder).Assembly;
        var list = new List<(string, string)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            var m = FileName().Match(res);
            if (!m.Success) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            list.Add((m.Groups[1].Value, r.ReadToEnd()));
        }
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var org = bootstrap.Value.OrganizationCode.ToUpperInvariant();
        var prop = bootstrap.Value.PropertyCode.ToUpperInvariant();
        // Codes are substituted into SQL text, so they must match the same pattern the database enforces.
        if (!SafeCode().IsMatch(org) || !SafeCode().IsMatch(prop))
            throw new InvalidOperationException("DevSeed needs valid Bootstrap:OrganizationCode and Bootstrap:PropertyCode.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var (name, sql) in Discover())
        {
            var text = sql.Replace("{{ORGANIZATION_CODE}}", org, StringComparison.Ordinal)
                          .Replace("{{PROPERTY_CODE}}", prop, StringComparison.Ordinal);
            var notices = new List<string>();
            void OnNotice(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
            conn.Notice += OnNotice;
            try
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(text, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            finally
            {
                conn.Notice -= OnNotice;
            }
            log.LogInformation("Dev seed {Seed}: {Result}", name, notices.Count > 0 ? string.Join("; ", notices) : "done");
        }
    }
}
