using System.Text.RegularExpressions;
using Resort.Domain.Activities;

namespace Resort.Tests.Unit;

/// <summary>Sprint 20 — the equipment status moves: the C# list (which answers first with the reason) and the database
/// trigger (the final guard) must allow exactly the same moves.</summary>
public class ActivityUnitTests
{
    [Fact]
    public void Equipment_moves_in_code_match_the_database_trigger()
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V022__activities.sql"));
        var body = sql[sql.IndexOf("fn_equipment_status_transition()", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal)];
        var inDb = Regex.Matches(body, @"\('([A-Z]+)','([A-Z]+)'\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Order().ToList();
        var inCode = EquipmentStatuses.Transitions.SelectMany(t => t.Value.Select(to => (t.Key, to))).Order().ToList();
        Assert.Equal(20, inCode.Count);
        Assert.Equal(inCode, inDb);
    }

    [Theory]
    [InlineData("AVAILABLE", "ISSUED", true)]
    [InlineData("ISSUED", "DAMAGED", true)]
    [InlineData("DAMAGED", "MAINTENANCE", true)]
    [InlineData("MAINTENANCE", "AVAILABLE", true)]
    [InlineData("LOST", "AVAILABLE", true)]
    [InlineData("ISSUED", "MAINTENANCE", false)]
    [InlineData("RESERVED", "LOST", false)]
    [InlineData("RETIRED", "AVAILABLE", false)]
    public void An_item_moves_only_along_the_allowed_steps(string from, string to, bool allowed) =>
        Assert.Equal(allowed, EquipmentStatuses.CanMove(from, to));

    [Fact]
    public void Reserved_and_issued_are_never_set_by_hand() =>
        Assert.DoesNotContain(EquipmentStatuses.ByHand, s => s is EquipmentStatuses.Reserved or EquipmentStatuses.Issued);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "migrations"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("database/migrations not found above the test folder.");
    }
}
