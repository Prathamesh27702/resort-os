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
