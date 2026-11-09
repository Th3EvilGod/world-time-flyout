using System.Globalization;
using NodaTime;

namespace WorldTime.Core;

public enum ClockStyle
{
    /// <summary>Follow the Windows regional "short time" format.</summary>
    System = 0,
    H12 = 1,
    H24 = 2,
}

/// <summary>Formats times and dates for display and copying.</summary>
public sealed class TimeFormat
{
    private const char Minus = '−';
    private readonly CultureInfo _culture;
    private readonly string _timePattern;
    private readonly string _shortDatePattern;
    private readonly bool _dayFirst;

    public TimeFormat(ClockStyle style, CultureInfo? culture = null)
    {
        _culture = culture ?? CultureInfo.CurrentCulture;
        var dtf = _culture.DateTimeFormat;
        bool hasDesignators = !string.IsNullOrEmpty(dtf.AMDesignator) && !string.IsNullOrEmpty(dtf.PMDesignator);
        if (style == ClockStyle.H12 && !hasDesignators)
        {
            // Culture has no AM/PM text (e.g. de-DE): borrow the invariant ones.
            _culture = (CultureInfo)_culture.Clone();
            _culture.DateTimeFormat.AMDesignator = "AM";
            _culture.DateTimeFormat.PMDesignator = "PM";
        }
        _timePattern = style switch
        {
            ClockStyle.H12 => "h:mm tt",
            ClockStyle.H24 => "HH:mm",
            _ => StripSeconds(dtf.ShortTimePattern),
        };
        // Month/day order from the culture ("MMMM d" vs "d MMMM").
        var md = dtf.MonthDayPattern;
        _dayFirst = md.IndexOf('d') >= 0 && md.IndexOf('d') < md.IndexOf('M');
        _shortDatePattern = _dayFirst ? "ddd d MMM" : "ddd, MMM d";
        Style = style;
    }

    public ClockStyle Style { get; }

    public bool Is24Hour => !_timePattern.Contains('h') && !_timePattern.Contains('t');

    private static string StripSeconds(string pattern)
    {
        // Some regional formats include seconds; the panel shows minutes only.
        // Newer ICU data puts a narrow no-break space before AM/PM; use a plain
        // space so copied text pastes cleanly everywhere.
        var p = pattern.Replace(":ss", "").Replace(".ss", "").Replace(":s", "")
            .Replace('\u202F', ' ').Replace('\u00A0', ' ');
        return string.IsNullOrWhiteSpace(p) ? "HH:mm" : p;
    }

    public string Time(LocalDateTime t) => t.ToDateTimeUnspecified().ToString(_timePattern, _culture);

    public string Time(LocalTime t) => Time(new LocalDate(2000, 1, 1).At(t));

    /// <summary>Hour label for the timeline: "6 PM" or "18".</summary>
    public string HourLabel(LocalTime t)
    {
        var dt = new LocalDate(2000, 1, 1).At(t).ToDateTimeUnspecified();
        if (Is24Hour) return dt.ToString(t.Minute == 0 ? "HH" : "HH:mm", _culture);
        return dt.ToString(t.Minute == 0 ? "h tt" : "h:mm", _culture).Trim();
    }

    /// <summary>"Mon, Nov 9" or "Mon 9 Nov" depending on the culture.</summary>
    public string ShortDate(LocalDate d) => d.AtMidnight().ToDateTimeUnspecified().ToString(_shortDatePattern, _culture);

    /// <summary>"Mon"</summary>
    public string DayName(LocalDate d) => d.AtMidnight().ToDateTimeUnspecified().ToString("ddd", _culture);

    /// <summary>"Mon, Nov 9, 6:00 PM"</summary>
    public string DateTime(LocalDateTime t) => $"{ShortDate(t.Date)}, {Time(t)}";

    /// <summary>"UTC+4", "UTC+5:30", "UTC−3", "UTC".</summary>
    public static string UtcOffset(Offset o)
    {
        if (o == Offset.Zero) return "UTC";
        int s = o.Seconds;
        char sign = s >= 0 ? '+' : Minus;
        s = Math.Abs(s);
        int h = s / 3600, m = s % 3600 / 60;
        return m == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{h}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{h}:{m:00}");
    }

    /// <summary>Difference from the reference zone: "+9h", "−4h 30m", "+45m", "Same time".</summary>
    public static string OffsetDifference(Offset diff)
    {
        if (diff == Offset.Zero) return "Same time";
        int s = diff.Seconds;
        char sign = s >= 0 ? '+' : Minus;
        s = Math.Abs(s);
        int h = s / 3600, m = s % 3600 / 60;
        if (h == 0) return string.Create(CultureInfo.InvariantCulture, $"{sign}{m}m");
        return m == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{h}h")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{h}h {m}m");
    }

    /// <summary>"Today", "Tomorrow", "Yesterday", "+2 days".</summary>
    public static string RelativeDay(int days) => days switch
    {
        0 => "Today",
        1 => "Tomorrow",
        -1 => "Yesterday",
        > 0 => $"+{days} days",
        _ => $"{Minus}{-days} days",
    };

    /// <summary>"+2h 30m from now", "3h ago" style description of a shifted cursor.</summary>
    public static string ShiftFromNow(Duration d)
    {
        long minutes = (long)Math.Round(d.TotalMinutes);
        if (minutes == 0) return "Now";
        char sign = minutes > 0 ? '+' : Minus;
        minutes = Math.Abs(minutes);
        long days = minutes / 1440, h = minutes % 1440 / 60, m = minutes % 60;
        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days}d");
        if (h > 0) parts.Add($"{h}h");
        if (m > 0) parts.Add($"{m}m");
        return sign + string.Join(' ', parts);
    }
}
