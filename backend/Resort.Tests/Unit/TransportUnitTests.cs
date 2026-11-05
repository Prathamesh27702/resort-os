using System.Text.RegularExpressions;
using Resort.Domain.Transport;

namespace Resort.Tests.Unit;

/// <summary>Sprint 21 — the trip and passenger moves: the C# lists (which the services follow and answer first with the
/// reason) and the database triggers (the final guard) must allow exactly the same moves.</summary>
public class TransportUnitTests
{
    [Fact]
    public void Trip_moves_in_code_match_the_database_trigger() =>
        Assert.Equal(TripStatuses.Moves.Order().ToList(), MovesInTrigger("fn_vehicle_trips_status()"));

    [Fact]
    public void Passenger_moves_in_code_match_the_database_trigger() =>
        Assert.Equal(PassengerStatuses.Moves.Order().ToList(), MovesInTrigger("fn_trip_passengers_seats()", "a passenger can''t go"));

    [Fact]
    public void A_departed_or_cancelled_trip_never_goes_back()
    {
        Assert.DoesNotContain(TripStatuses.Moves, m => m.To == TripStatuses.Scheduled);
        Assert.DoesNotContain(TripStatuses.Moves, m => m.From is TripStatuses.Cancelled or TripStatuses.Completed);
        Assert.DoesNotContain(PassengerStatuses.Moves, m => m.From is PassengerStatuses.Cancelled or PassengerStatuses.NoShow);
    }

    /// <summary>The ('FROM','TO') pairs listed in a trigger function, up to the RAISE that names <paramref name="until"/>.</summary>
    private static List<(string, string)> MovesInTrigger(string function, string? until = null)
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V023__transport.sql"));
        var body = sql[sql.IndexOf($"CREATE FUNCTION {function}", StringComparison.Ordinal)..];
        body = body[..body.IndexOf(until ?? "RAISE EXCEPTION", StringComparison.Ordinal)];
        if (until is not null) body = body[body.LastIndexOf("NOT (OLD.status, NEW.status) IN", StringComparison.Ordinal)..];
        return Regex.Matches(body, @"\('([A-Z_]+)','([A-Z_]+)'\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Order().ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "migrations"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("database/migrations not found above the test folder.");
    }
}
