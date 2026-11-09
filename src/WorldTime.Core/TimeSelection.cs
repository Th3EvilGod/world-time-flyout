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

    /// <summary>Steps to the next/previous boundary in the reference zone.</summary>
    public void Step(int count, DateTimeZone reference, int stepMinutes)
    {
        var from = IsLive ? Now : Selected;
        Set(TimeMath.Step(from, reference, stepMinutes, count));
    }

    /// <summary>Sets the time from a wall-clock value in the reference zone.</summary>
    public void SetLocal(LocalDateTime local, DateTimeZone reference) => Set(TimeMath.FromLocal(local, reference));
}
