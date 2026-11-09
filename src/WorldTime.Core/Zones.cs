using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.TimeZones;

namespace WorldTime.Core;

/// <summary>
/// Resolves time-zone identifiers. Locations store IANA ids ("Asia/Yerevan")
/// or fixed offsets ("UTC+05:30"). Rules come from the TZDB bundled with
/// NodaTime, so DST history and future changes do not depend on the Windows
/// registry time-zone data.
/// </summary>
public static partial class Zones
{
    public static IDateTimeZoneProvider Tzdb => DateTimeZoneProviders.Tzdb;

    /// <summary>The tzdb release the app was built with, e.g. "2026a".</summary>
    public static string TzdbVersion => TzdbDateTimeZoneSource.Default.TzdbVersion;

    public static DateTimeZone? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (TryParseFixed(id, out var offset)) return DateTimeZone.ForOffset(offset);
        return Tzdb.GetZoneOrNull(id);
    }

    public static DateTimeZone Get(string? id) => TryGet(id) ?? DateTimeZone.Utc;

    public static bool IsValid(string? id) => TryGet(id) is not null;

    [GeneratedRegex(@"^\s*(?:utc|gmt|z)\s*(?:(?<sign>[+\-−])\s*(?<h>\d{1,2})(?:\s*[:.]?\s*(?<m>\d{2}))?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixedRegex();

    /// <summary>Parses "UTC", "UTC+4", "gmt-5", "UTC+05:30", "utc +545".</summary>
    public static bool TryParseFixed(string text, out Offset offset)
    {
        offset = Offset.Zero;
        var m = FixedRegex().Match(text);
        if (!m.Success) return false;
        if (!m.Groups["sign"].Success) return true; // plain UTC / GMT
        int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (h > 14 || min >= 60 || (h == 14 && min > 0)) return false;
        int seconds = (h * 3600 + min * 60) * (m.Groups["sign"].Value == "+" ? 1 : -1);
        offset = Offset.FromSeconds(seconds);
        return true;
    }

    /// <summary>Canonical id for a fixed offset: "UTC", "UTC+04:00", "UTC-03:30".</summary>
    public static string FixedId(Offset offset)
    {
        if (offset == Offset.Zero) return "UTC";
        int total = offset.Seconds;
        char sign = total >= 0 ? '+' : '-';
        total = Math.Abs(total);
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{total / 3600:00}:{total % 3600 / 60:00}");
    }

    /// <summary>The IANA id of the Windows (system) time zone, when it can be mapped.</summary>
    public static string? SystemZoneId()
    {
        try
        {
            var local = TimeZoneInfo.Local;
            if (local.HasIanaId && Tzdb.GetZoneOrNull(local.Id) is not null) return local.Id;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) &&
                Tzdb.GetZoneOrNull(iana) is not null)
                return iana;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        // last resort: NodaTime's own mapping of the BCL local zone
        return Tzdb.GetSystemDefault().Id;
    }

    /// <summary>Canonical tzdb id (resolves links such as Asia/Calcutta to Asia/Kolkata).</summary>
    public static string Canonical(string id) =>
        TzdbDateTimeZoneSource.Default.CanonicalIdMap.TryGetValue(id, out var c) ? c : id;

    /// <summary>The last segment of an IANA id as a readable name: "America/Argentina/Buenos_Aires" -> "Buenos Aires".</summary>
    public static string DisplayNameFromId(string id)
    {
        if (TryParseFixed(id, out _)) return id;
        var last = id.Split('/').Last();
        return last.Replace('_', ' ');
    }
}
