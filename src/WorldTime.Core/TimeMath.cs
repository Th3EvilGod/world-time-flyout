using NodaTime;
using NodaTime.TimeZones;

namespace WorldTime.Core;

public enum StepDirection { Backward = -1, Forward = 1 }

/// <summary>
/// Pure time arithmetic. The selected moment is always an absolute
/// <see cref="Instant"/>. Step boundaries are defined in the reference zone's
/// local time (every :00 and :30 in Yerevan, for example), which keeps
/// stepping intuitive across DST changes and 30/45-minute offsets.
/// </summary>
public static class TimeMath
{
    public static readonly int[] AllowedSteps = [15, 30, 60];

    // Boundary math runs on 100ns ticks, so "strictly after" means one tick later.
    private static readonly Duration OneTick = Duration.FromTicks(1);

    public static int NormalizeStep(int minutes) => AllowedSteps.Contains(minutes) ? minutes : 30;

    public static Instant TruncateToMinute(Instant i)
    {
        const long tpm = NodaConstants.TicksPerMinute;
        return Instant.FromUnixTimeTicks(FloorDiv(i.ToUnixTimeTicks(), tpm) * tpm);
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && ((a < 0) ^ (b < 0))) ? q - 1 : q;
    }

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    /// <summary>Smallest instant at or after <paramref name="t"/> on a local step boundary.</summary>
    public static Instant CeilBoundary(Instant t, DateTimeZone zone, int stepMinutes)
    {
        long stepTicks = NormalizeStep(stepMinutes) * NodaConstants.TicksPerMinute;
        for (int guard = 0; guard < 8; guard++)
        {
            var interval = zone.GetZoneInterval(t);
            long off = interval.WallOffset.Ticks;
            long b = CeilDiv(t.ToUnixTimeTicks() + off, stepTicks) * stepTicks;
            var cand = Instant.FromUnixTimeTicks(b - off);
            if (!interval.HasEnd || cand < interval.End) return cand;
            t = interval.End; // boundary falls past an offset change: continue in the next interval
        }
        return t;
    }

    /// <summary>Largest instant at or before <paramref name="t"/> on a local step boundary.</summary>
    public static Instant FloorBoundary(Instant t, DateTimeZone zone, int stepMinutes)
    {
        long stepTicks = NormalizeStep(stepMinutes) * NodaConstants.TicksPerMinute;
        for (int guard = 0; guard < 8; guard++)
        {
            var interval = zone.GetZoneInterval(t);
            long off = interval.WallOffset.Ticks;
            long b = FloorDiv(t.ToUnixTimeTicks() + off, stepTicks) * stepTicks;
            var cand = Instant.FromUnixTimeTicks(b - off);
            if (!interval.HasStart || cand >= interval.Start) return cand;
            t = interval.Start - OneTick; // continue in the previous interval
        }
        return t;
    }

    /// <summary>
    /// The next boundary strictly after (forward) or before (backward) <paramref name="from"/>.
    /// From 6:17 with a 30 minute step: forward is 6:30, backward is 6:00.
    /// </summary>
    public static Instant NextBoundary(Instant from, DateTimeZone zone, int stepMinutes, StepDirection dir) =>
        dir == StepDirection.Forward
            ? CeilBoundary(from + OneTick, zone, stepMinutes)
            : FloorBoundary(from - OneTick, zone, stepMinutes);

    /// <summary>Moves <paramref name="count"/> boundaries (negative = backward).</summary>
    public static Instant Step(Instant from, DateTimeZone zone, int stepMinutes, int count)
    {
        var dir = count >= 0 ? StepDirection.Forward : StepDirection.Backward;
        var t = from;
        for (int i = 0; i < Math.Abs(count); i++) t = NextBoundary(t, zone, stepMinutes, dir);
        return t;
    }

    /// <summary>Nearest step boundary in the zone's local time (ties go forward).</summary>
    public static Instant Snap(Instant instant, DateTimeZone zone, int stepMinutes)
    {
        var back = FloorBoundary(instant, zone, stepMinutes);
        if (back == instant) return instant;
        var fwd = CeilBoundary(instant, zone, stepMinutes);
        return (instant - back) < (fwd - instant) ? back : fwd;
    }

    /// <summary>Resolves a wall-clock time in a zone (gaps shift forward, overlaps take the earlier).</summary>
    public static Instant FromLocal(LocalDateTime local, DateTimeZone zone) =>
        zone.ResolveLocal(local, Resolvers.LenientResolver).ToInstant();

    /// <summary>Whole calendar days between two local dates (a minus b).</summary>
    public static int DayDifference(LocalDate a, LocalDate b) => Period.Between(b, a, PeriodUnits.Days).Days;

    public static bool IsDaytime(LocalTime t) => t.Hour >= 7 && t.Hour < 19;

    public static bool IsWorkingHours(LocalTime t) => t.Hour >= 9 && t.Hour < 18;
}
