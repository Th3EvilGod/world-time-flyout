using NodaTime;

namespace WorldTime.Core;

/// <summary>A saved place the user compares times for.</summary>
public sealed class SavedLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ZoneId { get; set; } = "UTC";
    public string? CountryCode { get; set; }
    /// <summary>Secondary text such as "Armenia" or "Oregon, United States".</summary>
    public string? Detail { get; set; }
    /// <summary>Optional coordinates, used for the day/night indicator.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public SavedLocation Clone() => (SavedLocation)MemberwiseClone();
}

/// <summary>What one location shows for the selected instant.</summary>
public readonly record struct LocationReading(
    SavedLocation Location,
    ZonedDateTime Zoned,
    Offset Offset,
    Offset DifferenceFromReference,
    int DayDifference,
    bool IsReference,
    bool IsDaylightSaving)
{
    public LocalDateTime Local => Zoned.LocalDateTime;
    /// <summary>Sun above the horizon (from coordinates when known, else 07:00-19:00).</summary>
    public bool IsDaytime => Location.Latitude is { } lat && Location.Longitude is { } lon
        ? Sun.IsUp(Zoned.ToInstant(), lat, lon)
        : TimeMath.IsDaytime(Zoned.TimeOfDay);
    public bool IsWorkingHours => TimeMath.IsWorkingHours(Zoned.TimeOfDay) &&
                                  Zoned.DayOfWeek is not (IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday);
}

public static class WorldClock
{
    /// <summary>Computes every location's reading for one instant.</summary>
    public static List<LocationReading> Read(IEnumerable<SavedLocation> locations, string? referenceId, Instant instant)
    {
        var list = locations.ToList();
        var reference = list.FirstOrDefault(l => l.Id == referenceId) ?? list.FirstOrDefault();
        var refZone = Zones.Get(reference?.ZoneId);
        var refZoned = instant.InZone(refZone);
        var result = new List<LocationReading>(list.Count);
        foreach (var loc in list)
        {
            var zone = Zones.Get(loc.ZoneId);
            var zoned = instant.InZone(zone);
            var interval = zone.GetZoneInterval(instant);
            result.Add(new LocationReading(
                loc,
                zoned,
                zoned.Offset,
                zoned.Offset - refZoned.Offset,
                TimeMath.DayDifference(zoned.Date, refZoned.Date),
                ReferenceEquals(loc, reference),
                interval.Savings != Offset.Zero));
        }
        return result;
    }

    /// <summary>"New York: 9:00 AM"</summary>
    public static string CopyLine(LocationReading r, TimeFormat f, bool withDate) =>
        withDate ? $"{r.Location.Name}: {f.DateTime(r.Local)}" : $"{r.Location.Name}: {f.Time(r.Local)}";

    public static string CopyLines(IEnumerable<LocationReading> readings, TimeFormat f, bool withDate) =>
        string.Join(Environment.NewLine, readings.Select(r => CopyLine(r, f, withDate)));
}
