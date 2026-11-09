using NodaTime;
using WorldTime.Core;
using WorldTime.Core.Catalog;

namespace WorldTimeFlyout;

/// <summary>
/// UI-thread state: saved locations, the shared time cursor and preferences.
/// Views read from it and call its commands; it raises <see cref="Changed"/>.
/// </summary>
public sealed class AppModel
{
    private readonly SettingsStore _store;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private List<LocationReading>? _readings;

    public AppModel(AppSettings settings, SettingsStore store, IClock clock, Microsoft.UI.Dispatching.DispatcherQueue dq)
    {
        Settings = settings;
        _store = store;
        Selection = new TimeSelection(clock);
        Selection.Changed += (_, _) => Invalidate(ChangeKind.Time);
        Format = new TimeFormat(settings.Clock);
        _saveTimer = dq.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public AppSettings Settings { get; }
    public TimeSelection Selection { get; }
    public TimeFormat Format { get; private set; }

    /// <summary>Raised on the UI thread after anything visible changed.</summary>
    public event Action<ChangeKind>? Changed;

    public IReadOnlyList<SavedLocation> Locations => Settings.Locations;
    public SavedLocation? Reference => Settings.Reference;
    public DateTimeZone ReferenceZone => Zones.Get(Reference?.ZoneId);
    public int StepMinutes => Settings.StepMinutes;

    /// <summary>Readings for the selected instant, reference first in list order.</summary>
    public IReadOnlyList<LocationReading> Readings =>
        _readings ??= WorldClock.Read(Settings.Locations, Settings.ReferenceId, Selection.Selected);

    /// <summary>The reference city's reading, or null when no cities are saved.</summary>
    public LocationReading? ReferenceReading
    {
        get
        {
            // LocationReading is a struct: FirstOrDefault would hand back an empty one
            foreach (var r in Readings)
                if (r.IsReference) return r;
            return null;
        }
    }

    private void Invalidate(ChangeKind kind)
    {
        _readings = null;
        Changed?.Invoke(kind);
    }

    private void Persist(ChangeKind kind)
    {
        Invalidate(kind);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try { _store.Save(Settings); }
        catch (Exception e) { DiagLog.Write($"saving settings failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- time

    public void Tick() => Selection.Tick();
    public void ResetToNow() => Selection.ResetToNow();
    public void StepTime(int count) => Selection.Step(count, ReferenceZone, StepMinutes);
    public void StepDays(int count) => Selection.StepDays(count, ReferenceZone, StepMinutes);
    public void SetDate(LocalDate date) => Selection.SetDate(date, ReferenceZone, StepMinutes);

    /// <summary>How far the cursor is from now, in clean steps ("-30m", "+1d 2h"); zero while live.</summary>
    public Duration Shift => Selection.OffsetFromAnchor(ReferenceZone, StepMinutes);
    public void SetInstant(Instant i) => Selection.Set(i);

    // ---------------------------------------------------------------- locations

    public SavedLocation Add(SearchResult r)
    {
        var existing = Settings.Locations.FirstOrDefault(l =>
            Zones.Canonical(l.ZoneId) == Zones.Canonical(r.ZoneId) &&
            string.Equals(l.Name, r.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var loc = r.ToLocation();
        Settings.Locations.Add(loc);
        Settings.Normalize();
        Persist(ChangeKind.Locations);
        return loc;
    }

    public void Remove(string id)
    {
        int idx = Settings.Locations.FindIndex(l => l.Id == id);
        if (idx < 0) return;
        Settings.Locations.RemoveAt(idx);
        Settings.Normalize();
        Persist(ChangeKind.Locations);
    }

    public void Move(string id, int delta)
    {
        var list = Settings.Locations;
        int idx = list.FindIndex(l => l.Id == id);
        int to = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (idx < 0 || to == idx) return;
        var item = list[idx];
        list.RemoveAt(idx);
        list.Insert(to, item);
        Persist(ChangeKind.Locations);
    }

    public void SetReference(string id)
    {
        if (Settings.ReferenceId == id || Settings.Locations.All(l => l.Id != id)) return;
        Settings.ReferenceId = id;
        Persist(ChangeKind.Locations);
    }

    // ---------------------------------------------------------------- preferences

    public void SetStep(int minutes)
    {
        Settings.StepMinutes = TimeMath.NormalizeStep(minutes);
        Persist(ChangeKind.Preferences);
    }

    public void SetClock(ClockStyle style)
    {
        Settings.Clock = style;
        Format = new TimeFormat(style);
        Persist(ChangeKind.Preferences);
    }

    /// <summary>Regional format changed in Windows: rebuild the formatter.</summary>
    public void RefreshFormat()
    {
        System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
        Format = new TimeFormat(Settings.Clock);
        Invalidate(ChangeKind.Preferences);
    }

    public void SetFlag(Action<AppSettings> set)
    {
        set(Settings);
        Persist(ChangeKind.Preferences);
    }

    // ---------------------------------------------------------------- copy text

    public string CopyText(LocationReading r, bool withDate) => WorldClock.CopyLine(r, Format, withDate);

    public string CopyText(IEnumerable<LocationReading> rs, bool withDate) => WorldClock.CopyLines(rs, Format, withDate);
}

public enum ChangeKind { Time, Locations, Preferences }
