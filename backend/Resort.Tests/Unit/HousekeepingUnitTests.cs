using Resort.Domain.FrontDesk;
using Resort.Domain.Housekeeping;

namespace Resort.Tests.Unit;

/// <summary>Sprint 13 — the housekeeping and maintenance rules that need no database: which step is allowed when and what
/// the refusal says, how a task maps to the room's state, the one-tap checklist, ticket steps, the out-of-order date and
/// telling a photo from its bytes.</summary>
public class HousekeepingUnitTests
{
    private static readonly DateOnly D = new(2027, 1, 7);

    [Theory]
    [InlineData("start", "OPEN", "DIRTY", null)]
    [InlineData("start", "IN_PROGRESS", "CLEANING", "Room 101 is already being cleaned.")]
    [InlineData("start", "CLEANED", "CLEAN", "Room 101 is already cleaned; it is waiting for inspection.")]
    [InlineData("finish", "IN_PROGRESS", "CLEANING", null)]
    [InlineData("finish", "OPEN", "DIRTY", "Cleaning of Room 101 hasn't been started. Tap Start first.")]
    [InlineData("inspect", "CLEANED", "CLEAN", null)]
    [InlineData("inspect", "CLEANED", "INSPECTION", null)]
    [InlineData("inspect", "IN_PROGRESS", "CLEANING", "Room 101 is being cleaned; it can be inspected once the cleaning is finished.")]
    [InlineData("start", "DONE", "READY", "Room 101 is already cleaned and ready.")]
    [InlineData("finish", "CANCELLED", "READY", "This task for Room 101 was cancelled. Reload to see the room's current task.")]
    [InlineData("start", "OPEN", "OUT_OF_ORDER", "Room 101 is out of order. It goes back to cleaning when its repair is resolved.")]
    public void Each_step_is_allowed_only_in_its_place_and_a_refusal_says_why(string action, string task, string room, string? expected) =>
        Assert.Equal(expected, HousekeepingFlow.Problem(action, "Room 101", task, room));

    [Fact]
    public void A_task_and_the_room_state_map_both_ways()
    {
        Assert.Equal(["DIRTY", "CLEANING", "CLEAN", "READY", "READY"],
            HousekeepingTaskStatuses.All.Select(HousekeepingFlow.RoomStatusOf));
        Assert.Equal(["OPEN", "IN_PROGRESS", "CLEANED", "CLEANED", null, null],
            HousekeepingStatuses.All.Select(HousekeepingFlow.TaskStatusFor));
        Assert.True(HousekeepingTaskStatuses.IsActive("CLEANED"));
        Assert.False(HousekeepingTaskStatuses.IsActive("DONE"));
        Assert.Equal("Room 201 bed B02", HousekeepingFlow.Place("Room 201", "B02"));
        Assert.Equal("cleaned and waiting for inspection", HousekeepingStatuses.Words("CLEAN"));
        Assert.Equal("being inspected", HousekeepingStatuses.Words("INSPECTION"));
    }

    [Fact]
    public void Done_in_one_tap_ticks_every_line_and_a_partial_tick_must_include_the_required_ones()
    {
        var lines = new List<ChecklistLine>
        {
            new(Guid.NewGuid(), "Fresh linen", false, false),
            new(Guid.NewGuid(), "Check the minibar", true, false),
            new(Guid.NewGuid(), "Empty bins", true, false),
        };
        var (all, none) = HousekeepingFlow.Complete(lines, null);
        Assert.All(all, l => Assert.True(l.Done));
        Assert.Empty(none);

        var (some, missing) = HousekeepingFlow.Complete(lines, [lines[0].Id, lines[2].Id]);
        Assert.Equal([true, false, true], some.Select(l => l.Done));
        Assert.Equal(["Check the minibar"], missing);
        Assert.Equal(["Check the minibar", "Empty bins"], HousekeepingFlow.Complete(lines, []).MissingRequired);
    }

    [Theory]
    [InlineData("assign", "OPEN", null)]
    [InlineData("progress", "ON_HOLD", null)]
    [InlineData("resolve", "IN_PROGRESS", null)]
    [InlineData("close", "RESOLVED", null)]
    [InlineData("close", "IN_PROGRESS", "M00007 is in progress; resolve it before closing it.")]
    [InlineData("resolve", "RESOLVED", "M00007 is already resolved.")]
    [InlineData("progress", "RESOLVED", "M00007 is resolved. Close it, or report a new problem if the repair didn't hold.")]
    [InlineData("assign", "CLOSED", "M00007 is closed. Report a new problem if it is broken again.")]
    public void Ticket_steps_follow_the_order(string action, string status, string? expected) =>
        Assert.Equal(expected, MaintenanceFlow.Problem(action, "M00007", status));

    [Fact]
    public void An_out_of_order_room_needs_a_return_date_after_today_and_within_90_days()
    {
        Assert.Null(MaintenanceFlow.ExpectedDateProblem(D.AddDays(1), D));
        Assert.Null(MaintenanceFlow.ExpectedDateProblem(D.AddDays(90), D));
        Assert.Equal("A room can be taken out of order for at most 90 days at a time.", MaintenanceFlow.ExpectedDateProblem(D.AddDays(91), D));
        Assert.Equal("The expected date must be after today: the room is blocked from tonight until that day.", MaintenanceFlow.ExpectedDateProblem(D, D));
        Assert.Equal("Say when the room should be usable again (the room is blocked until then).", MaintenanceFlow.ExpectedDateProblem(null, D));
    }

    [Fact]
    public void A_photo_is_known_by_its_first_bytes_not_its_name()
    {
        Assert.Equal("image/jpeg", MaintenanceFlow.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", MaintenanceFlow.SniffImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/webp", MaintenanceFlow.SniffImage("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(MaintenanceFlow.SniffImage("%PDF-1.7"u8.ToArray()));
        Assert.Null(MaintenanceFlow.SniffImage("GIF89a"u8.ToArray()));
        Assert.Null(MaintenanceFlow.SniffImage([]));
        Assert.Equal(["URGENT", "HIGH", "NORMAL", "LOW"], MaintenancePriorities.All.OrderBy(MaintenancePriorities.Rank));
    }
}
