using Resort.Domain.FrontDesk;
using Resort.Domain.Organizations;

namespace Resort.Tests.Unit;

/// <summary>Sprint 7 — the front desk rules that need no database: the business date, the check-in window, the departure
/// day of an early check-out, and the housekeeping words used in messages.</summary>
public class FrontDeskUnitTests
{
    private static readonly DateOnly D = new(2027, 1, 7);

    [Theory]
    [InlineData("2027-01-07T17:59:00Z", "2027-01-07")]   // 23:29 IST
    [InlineData("2027-01-07T18:00:00Z", "2027-01-07")]   // 23:30 IST
    [InlineData("2027-01-07T18:29:59Z", "2027-01-07")]   // 23:59:59 IST
    [InlineData("2027-01-07T18:30:00Z", "2027-01-08")]   // midnight IST, still 7 Jan in UTC
    [InlineData("2027-01-07T19:00:00Z", "2027-01-08")]   // 00:30 IST
    public void The_business_date_is_the_propertys_local_date_not_the_utc_date(string utc, string expected)
    {
        var p = new Property { Timezone = "Asia/Kolkata" };
        Assert.Equal(DateOnly.Parse(expected), p.LocalDate(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_guest_can_check_in_from_the_arrival_day_until_the_last_night()
    {
        Assert.Null(FrontDeskRules.CheckInDateProblem("R00001", D, D.AddDays(3), D));
        Assert.Null(FrontDeskRules.CheckInDateProblem("R00001", D, D.AddDays(3), D.AddDays(2)));   // a late arrival
        Assert.Equal("R00001 arrives on Fri 8 Jan 2027; it is Thu 7 Jan 2027 at the property. " +
                     "Change the arrival date on the booking to check the guest in today.",
            FrontDeskRules.CheckInDateProblem("R00001", D.AddDays(1), D.AddDays(2), D));
        Assert.Equal("R00001 was due to leave on Sun 10 Jan 2027, so it can't be checked in now. Mark it no-show, or change its dates first.",
            FrontDeskRules.CheckInDateProblem("R00001", D, D.AddDays(3), D.AddDays(3)));
    }

    [Theory]
    [InlineData(0, 3, 0, 1)]    // leaving on the arrival day: the first night stays
    [InlineData(0, 3, 1, 1)]    // leaving the next morning: one night
    [InlineData(0, 3, 2, 2)]
    [InlineData(0, 3, 3, 3)]    // on the departure day: nothing released
    [InlineData(0, 3, 5, 3)]    // late check-out: the booked nights stay as they are
    [InlineData(0, 1, 0, 1)]
    public void An_early_check_out_ends_the_stay_today_but_never_before_one_night(int checkIn, int checkOut, int today, int expected) =>
        Assert.Equal(D.AddDays(expected), FrontDeskRules.DepartureOnCheckOut(D.AddDays(checkIn), D.AddDays(checkOut), D.AddDays(today)));

    [Fact]
    public void Housekeeping_states_read_as_words()
    {
        Assert.Equal("dirty", HousekeepingStatuses.Words(HousekeepingStatuses.Dirty));
        Assert.Equal("being cleaned", HousekeepingStatuses.Words(HousekeepingStatuses.Cleaning));
        Assert.Equal("out of order", HousekeepingStatuses.Words(HousekeepingStatuses.OutOfOrder));
        Assert.Equal(6, HousekeepingStatuses.All.Distinct().Count());
    }
}
