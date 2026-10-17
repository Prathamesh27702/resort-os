using System.Net;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Integration.FrontDeskTests;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 7 — the front desk's "today" is the property's business date. These tests stand the API's clock at
/// chosen moments in India (IST, UTC+5:30): 23:30 and 00:30 are on different days at the resort but the same day in UTC,
/// so a list that used the server's (UTC) date would show the wrong guests. Own database, because the clock is shared.</summary>
public class FrontDeskClockTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    /// <summary>A moment on a day at the property (IST).</summary>
    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), Ist).ToUniversalTime();

    [Fact]
    public async Task Arrivals_use_the_propertys_date_at_23_30_and_00_30_IST()
    {
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var day = Today;
        try
        {
            app.Clock.At = At(day, 23, 30);   // 18:00 UTC on the same day
            var stay = await SetupAsync(owner, pid, rooms: 2);
            var guest = await CreateGuestAsync(owner);
            var source = await WalkInAsync(owner, pid);
            var tonight = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, day, day.AddDays(2))));
            var tomorrow = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, day.AddDays(1), day.AddDays(3))));

            var late = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
            Assert.Equal(day, late.Today);
            Assert.Contains(late.Items, i => i.ReservationId == tonight.Id);
            Assert.DoesNotContain(late.Items, i => i.ReservationId == tomorrow.Id);

            app.Clock.At = At(day.AddDays(1), 0, 30);   // 19:00 UTC — still the first day in UTC, already the next day at the resort
            Assert.Equal(day, DateOnly.FromDateTime(app.Clock.At.Value.UtcDateTime));
            var early = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
            Assert.Equal(day.AddDays(1), early.Today);
            Assert.Contains(early.Items, i => i.ReservationId == tomorrow.Id && !i.Late);
            // last night's guest who never came is now a late arrival, still on the list
            Assert.Contains(early.Items, i => i.ReservationId == tonight.Id && i.Late);

            // and check-in follows the same date: tomorrow's booking can be checked in at 00:30
            await OkAsync(await owner.PostAsync(CheckInUrl(pid, tomorrow), new CheckInRequest(null)));
        }
        finally { app.Clock.At = null; }
    }

    [Fact]
    public async Task Moving_an_in_house_guest_keeps_the_nights_already_slept_on_the_first_room()
    {
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var day = Today;
        try
        {
            app.Clock.At = At(day, 14, 0);
            var stay = await SetupAsync(owner, pid, rooms: 2, price: 2000m);
            var guest = await CreateGuestAsync(owner);
            var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), day, day.AddDays(3)) with
                { Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[0].Id, 1, 0)] }));
            var checkedIn = await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));

            app.Clock.At = At(day.AddDays(1), 10, 0);   // the next morning
            var moved = await OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{booked.Id}/assign-room",
                new AssignRoomRequest(checkedIn.Units.Single().Id, stay.Rooms[1].Id, null, "AC not working", checkedIn.Version)));

            var current = moved.Units.Where(u => u.Current).OrderBy(u => u.StartDate).ToList();
            Assert.Equal(2, current.Count);
            Assert.Equal((stay.Rooms[0].Id, day, day.AddDays(1)), (current[0].UnitId, current[0].StartDate, current[0].EndDate));
            Assert.Equal((stay.Rooms[1].Id, day.AddDays(1), day.AddDays(3)), (current[1].UnitId, current[1].StartDate, current[1].EndDate));
            Assert.Equal(6000m, moved.TotalAmount);
            Assert.Equal((2000m, 4000m), (current[0].Total, current[1].Total));
            Assert.Contains(moved.Notes, n => n.Note.StartsWith($"Moved from {stay.Rooms[0].Name} to {stay.Rooms[1].Name} from {Day(day.AddDays(1))}"));

            var inHouse = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
            Assert.Equal(stay.Rooms[1].Id, Assert.Single(inHouse.Items.Single(i => i.ReservationId == booked.Id).Rooms).UnitId);
            Assert.Equal("DIRTY", await ScalarAsync($"SELECT status FROM unit_housekeeping_status WHERE unit_id = '{stay.Rooms[0].Id}'"));

            // leaving a day early: check-out marks the room they are in now for cleaning
            app.Clock.At = At(day.AddDays(2), 9, 0);
            var left = await OkAsync(await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null, "Flight moved up")));
            Assert.Equal(day.AddDays(2), left.CheckOut);
            Assert.Equal(4000m, left.TotalAmount);
            Assert.Equal("DIRTY", await ScalarAsync($"SELECT status FROM unit_housekeeping_status WHERE unit_id = '{stay.Rooms[1].Id}'"));
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM unit_housekeeping_history WHERE reservation_id = '{booked.Id}' AND source = 'CHECK_OUT'"));
        }
        finally { app.Clock.At = null; }
    }

    [Fact]
    public async Task A_guest_who_hasnt_checked_out_blocks_the_next_check_in_and_shows_as_overdue()
    {
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var day = Today;
        try
        {
            app.Clock.At = At(day, 15, 0);
            var stay = await SetupAsync(owner, pid, rooms: 1);
            var guest = await CreateGuestAsync(owner, "Anil", "Kumar");
            var source = await WalkInAsync(owner, pid);
            var first = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, day, day.AddDays(1))));
            await OkAsync(await owner.PostAsync(CheckInUrl(pid, first), new CheckInRequest(null)));
            var next = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, day.AddDays(1), day.AddDays(3))));

            app.Clock.At = At(day.AddDays(1), 13, 0);   // the first guest's departure day; they are still in the room
            var arrivals = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
            Assert.Equal($"{stay.Rooms[0].Name} is still occupied by {first.Reference}, which hasn't checked out.",
                arrivals.Items.Single(i => i.ReservationId == next.Id).CheckInProblem);
            var refused = await owner.PostAsync(CheckInUrl(pid, next), new CheckInRequest(null));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var (code, message) = await ProblemAsync(refused);
            Assert.Equal("ROOM_OCCUPIED", code);
            Assert.Equal($"{stay.Rooms[0].Name} is still occupied: {first.Reference} (Anil Kumar) was due out {Day(day.AddDays(1))} and hasn't checked out. " +
                         "Check them out first, or assign this guest another room.", message);

            app.Clock.At = At(day.AddDays(2), 9, 0);   // a day later they still haven't left
            var departures = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/departures");
            Assert.True(departures.Items.Single(i => i.ReservationId == first.Id).Overdue);
            Assert.True((await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house"))
                .Items.Single(i => i.ReservationId == first.Id).Overdue);

            var left = await OkAsync(await owner.PostAsync(CheckOutUrl(pid, first), new CheckOutRequest(null)));
            Assert.Equal(day.AddDays(1), left.CheckOut);   // a late check-out doesn't change the booked nights
        }
        finally { app.Clock.At = null; }
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
