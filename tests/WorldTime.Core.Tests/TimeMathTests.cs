using NodaTime;
using NodaTime.Text;
using WorldTime.Core;
using Xunit;

namespace WorldTime.Core.Tests;

internal sealed class TestClock(Instant now) : IClock
{
    public Instant Now { get; set; } = now;
    public Instant GetCurrentInstant() => Now;
}

public class TimeMathTests
{
    internal static Instant I(string iso) => InstantPattern.ExtendedIso.Parse(iso).Value;
    private static DateTimeZone Z(string id) => Zones.Get(id);
    private static string Local(Instant i, string zone) =>
        LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-dd HH:mm").Format(i.InZone(Z(zone)).LocalDateTime);

    [Fact]
    public void Yerevan_6pm_matches_other_cities()
    {
        // 2026-11-09 18:00 in Yerevan (UTC+4) = 14:00Z
        var t = TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, 18, 0), Z("Asia/Yerevan"));
        Assert.Equal(I("2026-11-09T14:00:00Z"), t);
        Assert.Equal("2026-11-09 09:00", Local(t, "America/New_York"));  // EST after Nov 1
        Assert.Equal("2026-11-09 14:00", Local(t, "Europe/London"));     // GMT
        Assert.Equal("2026-11-09 23:00", Local(t, "Asia/Tokyo"));
    }

    [Fact]
    public void Odd_offsets_are_exact()
    {
        var t = I("2026-01-15T12:00:00Z");
        Assert.Equal("2026-01-15 17:45", Local(t, "Asia/Kathmandu"));     // +5:45
        Assert.Equal("2026-01-15 17:30", Local(t, "Asia/Kolkata"));       // +5:30
        Assert.Equal("2026-01-15 22:30", Local(t, "Australia/Adelaide")); // +10:30 (DST)
        Assert.Equal("2026-01-15 23:00", Local(t, "Australia/Lord_Howe")); // +11 (30-min DST)
        Assert.Equal("2026-01-15 08:30", Local(t, "America/St_Johns"));   // -3:30
    }

    [Theory]
    [InlineData("2026-03-08T06:59:00Z", "2026-03-08 01:59")] // NY just before spring forward
    [InlineData("2026-03-08T07:00:00Z", "2026-03-08 03:00")] // ... and just after (EDT)
    [InlineData("2026-11-01T05:30:00Z", "2026-11-01 01:30")] // first 1:30 (EDT)
    [InlineData("2026-11-01T06:30:00Z", "2026-11-01 01:30")] // second 1:30 (EST)
    public void New_York_dst_transitions(string utc, string local) => Assert.Equal(local, Local(I(utc), "America/New_York"));

    [Fact]
    public void Steps_go_to_next_boundary_first()
    {
        var yvn = Z("Asia/Yerevan");
        var t = TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, 18, 17), yvn);
        Assert.Equal("2026-11-09 18:30", Local(TimeMath.Step(t, yvn, 30, 1), "Asia/Yerevan"));
        Assert.Equal("2026-11-09 18:00", Local(TimeMath.Step(t, yvn, 30, -1), "Asia/Yerevan"));
        Assert.Equal("2026-11-09 19:00", Local(TimeMath.Step(t, yvn, 60, 1), "Asia/Yerevan"));
        Assert.Equal("2026-11-09 18:30", Local(TimeMath.Step(t, yvn, 15, 1), "Asia/Yerevan"));
        Assert.Equal("2026-11-09 19:30", Local(TimeMath.Step(t, yvn, 30, 3), "Asia/Yerevan"));
        var onBoundary = TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, 18, 0), yvn);
        Assert.Equal("2026-11-09 17:00", Local(TimeMath.Step(onBoundary, yvn, 60, -1), "Asia/Yerevan"));
    }

    [Fact]
    public void Steps_cross_midnight_and_year()
    {
        var yvn = Z("Asia/Yerevan");
        var t = TimeMath.FromLocal(new LocalDateTime(2026, 12, 31, 23, 30), yvn);
        Assert.Equal("2027-01-01 00:00", Local(TimeMath.Step(t, yvn, 30, 1), "Asia/Yerevan"));
        Assert.Equal("2027-01-01 05:00", Local(TimeMath.Step(t, yvn, 30, 1), "Asia/Tokyo"));
    }

    [Fact]
    public void Steps_respect_half_hour_reference_zone()
    {
        var kol = Z("Asia/Kolkata");
        var t = I("2026-05-01T10:10:00Z"); // 15:40 IST
        var next = TimeMath.Step(t, kol, 60, 1);
        Assert.Equal("2026-05-01 16:00", Local(next, "Asia/Kolkata"));
        Assert.Equal("2026-05-01 14:30", Local(next, "Asia/Yerevan"));
    }

    [Fact]
    public void Steps_through_spring_forward_gap()
    {
        var ny = Z("America/New_York");
        var t = I("2026-03-08T06:30:00Z"); // 01:30 EST
        var next = TimeMath.Step(t, ny, 30, 1);
        Assert.Equal(I("2026-03-08T07:00:00Z"), next); // 03:00 EDT (02:00 does not exist)
        Assert.Equal("2026-03-08 03:00", Local(next, "America/New_York"));
        Assert.Equal(t, TimeMath.Step(next, ny, 30, -1));
    }

    [Fact]
    public void Steps_through_fall_back_overlap_visit_both_one_thirties()
    {
        var ny = Z("America/New_York");
        var t = I("2026-11-01T05:00:00Z"); // 01:00 EDT
        var a = TimeMath.Step(t, ny, 30, 1);
        var b = TimeMath.Step(a, ny, 30, 1);
        var c = TimeMath.Step(b, ny, 30, 1);
        Assert.Equal(I("2026-11-01T05:30:00Z"), a); // 01:30 EDT
        Assert.Equal(I("2026-11-01T06:00:00Z"), b); // 01:00 EST
        Assert.Equal(I("2026-11-01T06:30:00Z"), c); // 01:30 EST
        Assert.Equal(b, TimeMath.Step(c, ny, 30, -1));
        Assert.Equal(a, TimeMath.Step(b, ny, 30, -1));
    }

    [Fact]
    public void Snap_rounds_to_nearest_boundary()
    {
        var yvn = Z("Asia/Yerevan");
        var at = (int h, int m) => TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, h, m), yvn);
        Assert.Equal(at(18, 0), TimeMath.Snap(at(18, 14), yvn, 30));
        Assert.Equal(at(18, 30), TimeMath.Snap(at(18, 15), yvn, 30));
        Assert.Equal(at(19, 0), TimeMath.Snap(at(18, 31), yvn, 60));
        Assert.Equal(at(18, 45), TimeMath.Snap(at(18, 45), yvn, 15));
    }

    [Fact]
    public void Day_difference_across_dateline()
    {
        var t = TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, 23, 0), Z("Asia/Yerevan"));
        var rows = WorldClock.Read(
            [
                new SavedLocation { Id = "y", Name = "Yerevan", ZoneId = "Asia/Yerevan" },
                new SavedLocation { Id = "t", Name = "Tokyo", ZoneId = "Asia/Tokyo" },
                new SavedLocation { Id = "h", Name = "Honolulu", ZoneId = "Pacific/Honolulu" },
                new SavedLocation { Id = "k", Name = "Kiritimati", ZoneId = "Pacific/Kiritimati" },
            ], "y", t);
        Assert.Equal(0, rows[0].DayDifference);
        Assert.True(rows[0].IsReference);
        Assert.Equal(1, rows[1].DayDifference);   // Tokyo: Tue 04:00
        Assert.Equal(0, rows[2].DayDifference);   // Honolulu: Mon 09:00
        Assert.Equal(1, rows[3].DayDifference);   // Kiritimati: Tue 09:00 (+14)
        Assert.Equal(Offset.FromHours(5), rows[1].DifferenceFromReference);
        Assert.Equal(Offset.FromHours(-14), rows[2].DifferenceFromReference);
    }

    [Fact]
    public void Truncate_to_minute_handles_negative_and_positive()
    {
        Assert.Equal(I("2026-11-09T14:00:00Z"), TimeMath.TruncateToMinute(I("2026-11-09T14:00:59.999Z")));
        Assert.Equal(I("1969-12-31T23:59:00Z"), TimeMath.TruncateToMinute(I("1969-12-31T23:59:30Z")));
    }
}

public class TimeSelectionTests
{
    [Fact]
    public void Live_follows_clock_until_adjusted()
    {
        var clock = new TestClock(TimeMathTests.I("2026-11-09T14:00:20Z"));
        var sel = new TimeSelection(clock);
        Assert.True(sel.IsLive);
        Assert.Equal(TimeMathTests.I("2026-11-09T14:00:00Z"), sel.Selected);
        int changes = 0;
        sel.Changed += (_, _) => changes++;
        clock.Now = TimeMathTests.I("2026-11-09T14:01:05Z");
        sel.Tick();
        Assert.Equal(TimeMathTests.I("2026-11-09T14:01:00Z"), sel.Selected);
        Assert.Equal(1, changes);

        sel.Step(2, Zones.Get("Asia/Yerevan"), 60); // 18:01 -> 19:00 -> 20:00
        Assert.False(sel.IsLive);
        Assert.Equal(TimeMathTests.I("2026-11-09T16:00:00Z"), sel.Selected);
        clock.Now = TimeMathTests.I("2026-11-09T14:05:00Z");
        sel.Tick(); // detached: does not move
        Assert.Equal(TimeMathTests.I("2026-11-09T16:00:00Z"), sel.Selected);

        sel.ResetToNow();
        Assert.True(sel.IsLive);
        Assert.Equal(TimeMathTests.I("2026-11-09T14:05:00Z"), sel.Selected);
    }

    [Fact]
    public void Setting_current_minute_is_live()
    {
        var clock = new TestClock(TimeMathTests.I("2026-11-09T14:00:00Z"));
        var sel = new TimeSelection(clock);
        sel.Set(TimeMathTests.I("2026-11-09T15:00:00Z"));
        Assert.False(sel.IsLive);
        sel.Set(TimeMathTests.I("2026-11-09T14:00:00Z"));
        Assert.True(sel.IsLive);
    }
}
