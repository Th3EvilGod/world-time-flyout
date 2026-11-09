using NodaTime;

namespace WorldTime.Core;

/// <summary>
/// The single shared time cursor. Every displayed location renders this one
/// absolute instant, so all rows always describe the same moment.
/// "Live" means it follows the clock; any adjustment detaches it until
/// <see cref="ResetToNow"/>.
/// </summary>
public sealed class TimeSelection
{
    private readonly IClock _clock;

    public TimeSelection(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        Selected = TimeMath.TruncateToMinute(_clock.GetCurrentInstant());
    }

    public Instant Selected { get; private set; }

    public bool IsLive { get; private set; } = true;

    public Instant Now => _clock.GetCurrentInstant();

    /// <summary>Raised after <see cref="Selected"/> or <see cref="IsLive"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Selected minus the current minute (zero while live).</summary>
    public Duration OffsetFromNow => Selected - TimeMath.TruncateToMinute(Now);

    /// <summary>
    /// The current time rounded to the nearest step in the reference zone
    /// (10:27 with 30 minute steps is 10:30). Stepping away from "now" starts
    /// here, so the steps land on clean times and count in whole steps.
    /// </summary>
    public Instant Anchor(DateTimeZone reference, int stepMinutes) =>
        TimeMath.Snap(TimeMath.TruncateToMinute(Now), reference, stepMinutes);

    /// <summary>How far the cursor is from <see cref="Anchor"/>: "+30m", "-1h", "+1d" for display.</summary>
    public Duration OffsetFromAnchor(DateTimeZone reference, int stepMinutes) =>
        IsLive ? Duration.Zero : Selected - Anchor(reference, stepMinutes);

    /// <summary>Call on minute ticks; advances the cursor only while live.</summary>
    public void Tick()
    {
        if (!IsLive) return;
        var now = TimeMath.TruncateToMinute(Now);
        if (now == Selected) return;
        Selected = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToNow()
    {
        var now = TimeMath.TruncateToMinute(Now);
        if (IsLive && now == Selected) return;
        IsLive = true;
        Selected = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets an explicit moment. Setting the current minute returns to live mode.</summary>
    public void Set(Instant instant)
    {
        var now = TimeMath.TruncateToMinute(Now);
        bool live = instant == now;
        if (instant == Selected && live == IsLive) return;
        Selected = instant;
        IsLive = live;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves by whole steps. From "now" it starts at the rounded current time,
    /// so with 30 minute steps at 10:27, back is 10:00 and forward is 11:00
    /// (shown as -30m / +30m), never 10:27 to 10:30 (+3m).
    /// </summary>
    public void Step(int count, DateTimeZone reference, int stepMinutes)
    {
        var from = IsLive ? Anchor(reference, stepMinutes) : Selected;
        Set(TimeMath.Step(from, reference, stepMinutes, count));
    }

    /// <summary>Moves by whole days, keeping the wall-clock time in the reference zone.</summary>
    public void StepDays(int count, DateTimeZone reference, int stepMinutes)
    {
        var from = IsLive ? Anchor(reference, stepMinutes) : Selected;
        var local = from.InZone(reference).LocalDateTime.PlusDays(count);
        Set(TimeMath.FromLocal(local, reference));
    }

    /// <summary>Sets the time from a wall-clock value in the reference zone.</summary>
    public void SetLocal(LocalDateTime local, DateTimeZone reference) => Set(TimeMath.FromLocal(local, reference));

    /// <summary>
    /// Moves to another day in the reference zone, keeping the time of day
    /// (from "now", the rounded time). Picking today while live changes nothing.
    /// </summary>
    public void SetDate(LocalDate date, DateTimeZone reference, int stepMinutes)
    {
        var from = IsLive ? Anchor(reference, stepMinutes) : Selected;
        var local = from.InZone(reference).LocalDateTime;
        if (local.Date == date && (IsLive || from == Selected)) return;
        Set(TimeMath.FromLocal(date + local.TimeOfDay, reference));
    }
}
