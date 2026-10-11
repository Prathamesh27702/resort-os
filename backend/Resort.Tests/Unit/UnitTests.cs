using Resort.Domain.Organizations;
using Resort.Infrastructure.Persistence;

namespace Resort.Tests.Unit;

public class PropertyLocalDateTests
{
    [Fact]
    public void Business_date_follows_the_property_timezone_not_the_server()
    {
        var p = new Property { Timezone = "Asia/Kolkata" };
        // 20:00 UTC on 6 Oct = 01:30 IST on 7 Oct
        Assert.Equal(new DateOnly(2026, 10, 7), p.LocalDate(new DateTimeOffset(2026, 10, 6, 20, 0, 0, TimeSpan.Zero)));
        // 18:00 UTC on 6 Oct = 23:30 IST on 6 Oct
        Assert.Equal(new DateOnly(2026, 10, 6), p.LocalDate(new DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero)));
    }
}

public class TimezoneRuleTests
{
    [Theory]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("UTC", true)]
    [InlineData("IST", false)]            // accepted by Windows' ICU, ambiguous (India/Israel/Ireland)
    [InlineData("EST", false)]            // exists as a Linux tzdata file
    [InlineData("India Standard Time", false)] // Windows id, not IANA
    [InlineData("Mars/Olympus", false)]
    [InlineData("", false)]
    public void Only_IANA_area_city_names_or_UTC_are_accepted(string tz, bool ok) =>
        Assert.Equal(ok, Resort.Application.Validation.Rules.IsIanaTimezone(tz));
}

public class MigrationTests
{
    [Fact]
    public void Migrations_are_discovered_in_order_with_unique_versions()
    {
        var m = DatabaseMigrator.Discover();
        Assert.True(m.Count >= 2);
        Assert.Equal(m.Select(x => x.Version).Order().ToList(), m.Select(x => x.Version).ToList());
        Assert.Equal(1, m.First().Version);
    }

    [Fact]
    public void Snake_case_mapping()
    {
        Assert.Equal("organization_id", AppDbContext.ToSnake("OrganizationId"));
        Assert.Equal("gst_state_code", AppDbContext.ToSnake("GstStateCode"));
        Assert.Equal("created_ip", AppDbContext.ToSnake("CreatedIp"));
        Assert.Equal("ip_address", AppDbContext.ToSnake("IpAddress"));
    }
}

public class ErrorMessageTests
{
    [Fact]
    public void Validation_message_says_the_reasons_not_a_generic_line()
    {
        Assert.Equal("New password must differ from the current one.",
            Resort.Api.Infrastructure.Problems.Reasons(["New password must differ from the current one."]));
        Assert.Equal("A. B. C. (and 1 more)", Resort.Api.Infrastructure.Problems.Reasons(["A", "B.", "B", "C", "D"]));
    }
}
