using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Resort.Contracts;

namespace Resort.Tests.Support;

/// <summary>Builds what booking tests need through the real API: a room type with rooms (or a dorm with beds), a rate plan
/// with prices, guests, and bookings. Shared by the reservation (Sprint 5) and calendar (Sprint 6) tests.</summary>
internal static class BookingKit
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    /// <summary>Today at the test property (Asia/Kolkata).</summary>
    internal static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Ist).DateTime);

    internal sealed record Stay(AccommodationTypeDto Type, List<RoomDto> Rooms, RatePlanDto Plan);

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>A new room type with rooms (or one dorm with beds), a rate plan and prices for the next year.</summary>
    internal static async Task<Stay> SetupAsync(TestClient c, Guid pid, int rooms, string mode = "UNIT", int beds = 0, decimal price = 3500m,
        decimal extraAdult = 0m, short baseOcc = 2, short maxOcc = 3, bool makeDefault = false)
    {
        var bed = mode == "BED";
        var typeRes = await c.PostAsync($"/api/v1/properties/{pid}/accommodation-types",
            new CreateAccommodationTypeRequest("T_" + Suffix(), bed ? "Dorm " + Suffix() : "Garden room " + Suffix(), null, mode,
                bed ? (short)1 : baseOcc, bed ? (short)1 : maxOcc, null, 0, null));
        Assert.Equal(HttpStatusCode.Created, typeRes.StatusCode);
        var type = (await typeRes.Content.ReadFromJsonAsync<AccommodationTypeDto>())!;

        var list = new List<RoomDto>();
        for (var i = 0; i < rooms; i++)
        {
            var code = "R" + Suffix();
            var res = await c.PostAsync($"/api/v1/properties/{pid}/rooms", new CreateRoomRequest(code, "Room " + code, type.Id, null, null, i));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var room = (await res.Content.ReadFromJsonAsync<RoomDto>())!;
            for (var b = 0; b < beds; b++)
                Assert.Equal(HttpStatusCode.Created, (await c.PostAsync($"/api/v1/properties/{pid}/rooms/{room.Id}/beds",
                    new CreateBedRequest($"B{b + 1:00}", $"Bed {b + 1}", "SINGLE", b))).StatusCode);
            list.Add(room);
        }

        var planRes = await c.PostAsync($"/api/v1/properties/{pid}/rate-plans",
            new CreateRatePlanRequest("P_" + Suffix(), "Plan " + Suffix(), null, "EP", makeDefault, 0));
        Assert.Equal(HttpStatusCode.Created, planRes.StatusCode);
        var plan = (await planRes.Content.ReadFromJsonAsync<RatePlanDto>())!;
        var today = Today;
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices",
            new SetRatePricesRequest([type.Id], today, today.AddDays(200), null, price, bed ? 0 : extraAdult, 0, 1))).StatusCode);
        return new Stay(type, list, plan);
    }

    internal static CreateReservationRequest NewBooking(Stay s, Guid guestId, Guid sourceId, DateOnly from, DateOnly to, short adults = 1, short children = 0) =>
        new(from, to, [new ReservationRoomRequest(s.Type.Id, null, adults, children)], [guestId], sourceId, s.Plan.Id, null, null, null);

    internal static Task<HttpResponseMessage> BookAsync(TestClient c, Guid pid, CreateReservationRequest body, string? key = null) =>
        c.PostAsync($"/api/v1/properties/{pid}/reservations", body,
            new Dictionary<string, string> { ["Idempotency-Key"] = key ?? "test-" + Guid.NewGuid() });

    internal static async Task<ReservationDto> CreatedAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<ReservationDto>())!;
    }

    internal static async Task<ReservationDto> PostAsync(TestClient c, string url, object body)
    {
        var res = await c.PostAsync(url, body);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<ReservationDto>())!;
    }

    internal static async Task<AvailabilityTypeDto> AvailabilityAsync(TestClient c, Guid pid, DateOnly from, DateOnly to, Guid typeId, int adults = 2, int children = 0)
    {
        var a = await c.GetJsonAsync<AvailabilityDto>(
            $"/api/v1/properties/{pid}/availability?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&adults={adults}&children={children}");
        return a.Types.Single(t => t.AccommodationTypeId == typeId);
    }

    internal static async Task<Guid> WalkInAsync(TestClient c, Guid pid) =>
        (await c.GetJsonAsync<List<ReservationSourceDto>>($"/api/v1/properties/{pid}/reservation-sources")).Single(s => s.Code == "WALK_IN").Id;

    internal static async Task<GuestDto> CreateGuestAsync(TestClient c, string first = "Guest", string? last = null)
    {
        var res = await c.PostAsync("/api/v1/guests", new CreateGuestRequest(first, last ?? "G" + Suffix().ToLowerInvariant(), null, null, null, "IN",
            null, null, null, false, null, null, IgnoreDuplicates: true));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<GuestDto>())!;
    }

    internal static async Task<string?> FieldErrorAsync(HttpResponseMessage res, string field)
    {
        Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        return body.GetProperty("details").GetProperty(field)[0].GetString();
    }
}
