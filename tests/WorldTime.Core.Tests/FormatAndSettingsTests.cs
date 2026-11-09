using System.Globalization;
using NodaTime;
using WorldTime.Core;
using WorldTime.Core.Catalog;
using Xunit;

namespace WorldTime.Core.Tests;

public class FormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly LocalDateTime Evening = new(2026, 11, 9, 18, 5);

    [Fact]
    public void Clock_styles()
    {
        Assert.Equal("6:05 PM", new TimeFormat(ClockStyle.H12, En).Time(Evening));
        Assert.Equal("18:05", new TimeFormat(ClockStyle.H24, En).Time(Evening));
        Assert.Equal("6:05 PM", new TimeFormat(ClockStyle.System, En).Time(Evening));
        Assert.Equal("18:05", new TimeFormat(ClockStyle.System, CultureInfo.GetCultureInfo("de-DE")).Time(Evening));
        // 12-hour on a culture without AM/PM designators still shows them
        Assert.EndsWith("PM", new TimeFormat(ClockStyle.H12, CultureInfo.GetCultureInfo("de-DE")).Time(Evening));
        Assert.False(new TimeFormat(ClockStyle.H12, En).Is24Hour);
        Assert.True(new TimeFormat(ClockStyle.H24, En).Is24Hour);
    }

    [Fact]
    public void Dates_and_labels()
    {
        var f = new TimeFormat(ClockStyle.H12, En);
        Assert.Equal("Mon, Nov 9", f.ShortDate(Evening.Date));
        Assert.Equal("Mon, Nov 9, 6:05 PM", f.DateTime(Evening));
        Assert.Equal("6 PM", f.HourLabel(new LocalTime(18, 0)));
        Assert.Equal("18", new TimeFormat(ClockStyle.H24, En).HourLabel(new LocalTime(18, 0)));
        Assert.Equal("Mon 9 Nov", new TimeFormat(ClockStyle.H24, CultureInfo.GetCultureInfo("en-GB")).ShortDate(Evening.Date));
    }

    [Theory]
    [InlineData(4 * 3600, "UTC+4")]
    [InlineData(5 * 3600 + 1800, "UTC+5:30")]
    [InlineData(-3 * 3600 - 1800, "UTC−3:30")]
    [InlineData(0, "UTC")]
    public void Utc_offsets(int seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.UtcOffset(Offset.FromSeconds(seconds)));

    [Theory]
    [InlineData(9 * 3600, "+9h")]
    [InlineData(-4 * 3600 - 1800, "−4h 30m")]
    [InlineData(45 * 60, "+45m")]
    [InlineData(0, "Same time")]
    public void Offset_differences(int seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.OffsetDifference(Offset.FromSeconds(seconds)));

    [Fact]
    public void Shift_descriptions()
    {
        Assert.Equal("Now", TimeFormat.ShiftFromNow(Duration.Zero));
        Assert.Equal("+3h", TimeFormat.ShiftFromNow(Duration.FromHours(3)));
        Assert.Equal("−1d 2h 30m", TimeFormat.ShiftFromNow(-Duration.FromMinutes(1590)));
    }

    [Fact]
    public void No_em_dashes_anywhere_in_output()
    {
        var f = new TimeFormat(ClockStyle.H12, En);
        var t = TimeMath.FromLocal(new LocalDateTime(2026, 11, 9, 18, 0), Zones.Get("Asia/Yerevan"));
        var rows = WorldClock.Read(
            [
                new SavedLocation { Id = "y", Name = "Yerevan", ZoneId = "Asia/Yerevan" },
                new SavedLocation { Id = "n", Name = "New York", ZoneId = "America/New_York" },
                new SavedLocation { Id = "t", Name = "Tokyo", ZoneId = "Asia/Tokyo" },
            ], "y", t);
        var text = WorldClock.CopyLines(rows, f, withDate: false);
        Assert.Equal("Yerevan: 6:00 PM\nNew York: 9:00 AM\nTokyo: 11:00 PM".Replace("\n", Environment.NewLine), text);
        var dated = WorldClock.CopyLines(rows, f, withDate: true);
        Assert.Contains("Tokyo: Mon, Nov 9, 11:00 PM", dated);
        Assert.DoesNotContain('—', text + dated);
    }
}

public class ZoneTests
{
    [Theory]
    [InlineData("UTC+4", "UTC+04:00")]
    [InlineData("gmt-5", "UTC-05:00")]
    [InlineData("UTC+05:30", "UTC+05:30")]
    [InlineData("utc +545", "UTC+05:45")]
    [InlineData("UTC", "UTC")]
    public void Fixed_offsets_parse(string text, string id)
    {
        Assert.True(Zones.TryParseFixed(text, out var o));
        Assert.Equal(id, Zones.FixedId(o));
        Assert.NotNull(Zones.TryGet(id));
    }

    [Fact]
    public void Links_and_bad_ids()
    {
        Assert.Equal("Asia/Kolkata", Zones.Canonical("Asia/Calcutta"));
        Assert.Null(Zones.TryGet("Mars/Olympus_Mons"));
        Assert.Equal(DateTimeZone.Utc, Zones.Get("nope"));
        Assert.Equal("Buenos Aires", Zones.DisplayNameFromId("America/Argentina/Buenos_Aires"));
        Assert.False(string.IsNullOrEmpty(Zones.TzdbVersion));
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_put_system_zone_first_as_reference()
    {
        var s = AppSettings.CreateDefault("Asia/Yerevan", null);
        Assert.Equal(["Yerevan", "New York", "London", "Tokyo"], s.Locations.Select(l => l.Name));
        Assert.Equal("Yerevan", s.Reference!.Name);

        var cat = CatalogTests.Catalog;
        var berlin = AppSettings.CreateDefault("Europe/Berlin", cat);
        Assert.Equal("Berlin", berlin.Locations[0].Name);
        Assert.Equal(5, berlin.Locations.Count);
        Assert.Equal(berlin.Locations[0].Id, berlin.ReferenceId);

        var ny = AppSettings.CreateDefault("America/New_York", cat);
        Assert.Equal("New York", ny.Locations[0].Name);
        Assert.Equal(4, ny.Locations.Count);
    }

    [Fact]
    public void Round_trip_and_repair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtf-tests-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        Assert.Null(store.Load());

        var s = AppSettings.CreateDefault("Asia/Yerevan", null);
        s.StepMinutes = 15;
        s.Clock = ClockStyle.H24;
        store.Save(s);
        var back = store.Load()!;
        Assert.Equal(15, back.StepMinutes);
        Assert.Equal(ClockStyle.H24, back.Clock);
        Assert.Equal(s.Locations.Select(l => l.ZoneId), back.Locations.Select(l => l.ZoneId));
        Assert.Equal(s.ReferenceId, back.ReferenceId);
        Assert.Contains("\"clock\": \"H24\"", File.ReadAllText(store.Path));

        // hand-edited junk gets repaired, not crashed on
        File.WriteAllText(store.Path, """
            { "stepMinutes": 7, "referenceId": "missing",
              "locations": [ { "id": "a", "name": "Nowhere", "zoneId": "Mars/Base" },
                             { "id": "b", "name": "Tokyo", "zoneId": "Asia/Tokyo" },
                             { "id": "b", "name": "Kolkata", "zoneId": "Asia/Calcutta" } ] }
            """);
        var fixedUp = store.Load()!;
        Assert.Equal(30, fixedUp.StepMinutes);
        Assert.Equal(["Tokyo", "Kolkata"], fixedUp.Locations.Select(l => l.Name));
        Assert.Equal(2, fixedUp.Locations.Select(l => l.Id).Distinct().Count());
        Assert.Equal(fixedUp.Locations[0].Id, fixedUp.ReferenceId);

        File.WriteAllText(store.Path, "{ not json");
        Assert.Null(store.Load());
        Assert.True(File.Exists(store.Path + ".bad"));
        Directory.Delete(dir, true);
    }
}
