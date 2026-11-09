using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NodaTime;
using WorldTime.Core;
using WorldTime.Core.Catalog;
using WorldTime.Shell;

namespace WorldTimeFlyout;

/// <summary>
/// Wires everything together and owns the panel's lifecycle:
///   Hidden     - nothing on screen, nothing running except event subscriptions
///   Attached   - shown beside the open taskbar calendar, follows it
///   Standalone - the calendar closed because the user started typing or opened
///                a menu in the panel, or the panel was opened from the tray;
///                it closes when the user clicks elsewhere or presses Esc
/// </summary>
public sealed class AppController : IDisposable
{
    private enum PanelState { Hidden, Attached, Standalone }

    private readonly AppOptions _options;
    private readonly DispatcherQueue _dq;
    private readonly ThemeManager _theme;
    private readonly AppModel _model;
    private readonly PanelWindow _panel;
    private readonly CalendarFlyoutTracker _tracker = new();
    private readonly ShellMessageWindow _messages = new();
    private readonly DispatcherQueueTimer _minuteTimer;
    private readonly DispatcherQueueTimer _hideCheck;
    private readonly DispatcherQueueTimer _trimTimer;
    private PanelState _state = PanelState.Hidden;
    private CalendarState? _calendar;
    private readonly string _exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WorldTimeFlyout.exe");

    public AppController(AppOptions options, IClock? clock = null)
    {
        _options = options;
        _dq = DispatcherQueue.GetForCurrentThread();
        _theme = new ThemeManager(_dq);

        var store = options.SettingsPath is { } p ? new SettingsStore(p) : SettingsStore.CreateDefault();
        var settings = store.Load();
        bool firstRun = settings is null;
        if (settings is null)
        {
            CityCatalog? catalog = null;
            try { catalog = CityCatalog.LoadEmbedded(); } catch (Exception e) { DiagLog.Write($"catalog: {e.Message}"); }
            settings = AppSettings.CreateDefault(Zones.SystemZoneId(), catalog);
        }
        _model = new AppModel(settings, store, clock ?? SystemClock.Instance, _dq);
        if (firstRun && !options.Screenshot) _model.SaveNow();
        FirstRun = firstRun;

        _panel = new PanelWindow(_model, _theme);
        _panel.Deactivated += OnPanelDeactivated;
        _panel.SearchChanged += () => { if (_state != PanelState.Hidden) PlaceStandaloneOrAttached(); };
        _panel.ExitRequested += () => Application.Current.Exit();
        _panel.PreferencesChanged += ApplyPreferences;

        _minuteTimer = _dq.CreateTimer();
        _minuteTimer.IsRepeating = false;
        _minuteTimer.Tick += (_, _) => { _model.Tick(); ScheduleMinuteTick(); };

        _trimTimer = _dq.CreateTimer();
        _trimTimer.IsRepeating = false;
        _trimTimer.Interval = TimeSpan.FromSeconds(20);
        _trimTimer.Tick += (_, _) => { if (_state == PanelState.Hidden) Win32Util.TrimWorkingSet(); };

        _hideCheck = _dq.CreateTimer();
        _hideCheck.IsRepeating = false;
        _hideCheck.Interval = TimeSpan.FromMilliseconds(120);
        _hideCheck.Tick += (_, _) => HideIfOrphaned();
    }

    public bool FirstRun { get; }
    public PanelWindow Panel => _panel;
    public AppModel Model => _model;
    public ThemeManager Theme => _theme;

    public void Start()
    {
        _messages.Create();
        _messages.ShowRequested += () => ShowStandalone();
        _messages.TrayActivated += (_, _) => ToggleStandalone();
        _messages.TrayContextMenu += (_, _) => ShowTrayMenu();
        _messages.SettingChanged += area =>
        {
            if (area is "ImmersiveColorSet" or "Colorization" or "WindowsThemeElement" or "HighContrast" or "") _theme.RequestRefresh();
            if (area == "intl") _model.RefreshFormat();
        };
        _messages.DisplayChanged += () => _tracker.RequestRefresh();
        ApplyPreferences();

        _tracker.Log += m => DiagLog.Detail(m);
        _tracker.Opened += s => _dq.TryEnqueue(() => OnCalendarOpened(s));
        _tracker.Changed += s => _dq.TryEnqueue(() => OnCalendarChanged(s));
        _tracker.Closed += r => _dq.TryEnqueue(() => OnCalendarClosed(r));
        _tracker.Start();
        DiagLog.Write($"started v{typeof(App).Assembly.GetName().Version?.ToString(3)}, tzdb {Zones.TzdbVersion}, " +
                      $"UIA {(_tracker.UiaAvailable ? "on" : "off")}, system zone {Zones.SystemZoneId()}");

        _trimTimer.Start(); // after startup work is done, if nothing gets shown
        if (FirstRun && !_options.Background)
        {
            _panel.ShowWelcome(true);
            ShowStandalone();
        }
        else if (_options.ShowOnStart)
        {
            ShowStandalone();
        }
    }

    // ---------------------------------------------------------------- calendar events

    private void OnCalendarOpened(CalendarState s)
    {
        _calendar = s;
        _state = PanelState.Attached;
        PlaceBesideCalendar(s);
        _panel.ShowPassive(animate: true);
        StartClock();
    }

    private void OnCalendarChanged(CalendarState s)
    {
        _calendar = s;
        if (_state == PanelState.Attached) PlaceBesideCalendar(s);
    }

    private void OnCalendarClosed(string reason)
    {
        _calendar = null;
        if (_state != PanelState.Attached) return;
        if (_panel.IsActive || _panel.IsSearching)
        {
            // the user clicked into the panel (search, menu): keep it open on its own
            _state = PanelState.Standalone;
            return;
        }
        if (DateTime.UtcNow - _panel.LastPointerDown < TimeSpan.FromMilliseconds(600))
        {
            // The calendar closed because of a click in the panel (some Windows builds
            // dismiss it even for non-activating windows): keep the panel, take focus
            // so that clicking elsewhere later closes it.
            DiagLog.Detail($"calendar closed after a click in the panel ({reason}); staying open");
            _state = PanelState.Standalone;
            _panel.ActivateForInput();
            return;
        }
        // clicked elsewhere / Esc / clicked the clock again: close together
        HidePanel();
    }

    private void OnPanelDeactivated()
    {
        if (_state != PanelState.Standalone) return;
        // Give the tracker a moment: if the user clicked the clock, the calendar
        // is opening and the panel should re-attach instead of disappearing.
        _hideCheck.Stop();
        _hideCheck.Start();
    }

    private void HideIfOrphaned()
    {
        if (_state == PanelState.Standalone && !_panel.IsActive && _tracker.FlyoutWindow == 0) HidePanel();
    }

    private void PlaceBesideCalendar(CalendarState s)
    {
        var layout = s.Expanded ? PanelLayout.Expanded : PanelLayout.Compact;
        if (_panel.IsSearching) layout = PanelLayout.Expanded;
        double minHeight = layout == PanelLayout.Expanded
            ? Views.ExpandedView.RequiredHeight
            : Views.CompactView.RequiredHeightFor(_theme.TextScale);
        if (_panel.IsSearching) minHeight = SearchHeight;
        var rect = CalendarGeometry.PanelRect(s, minWidthDip: 320, minHeightDip: (int)Math.Ceiling(minHeight));
        _panel.Place(rect, layout);
    }

    private const double StandaloneWidth = 360;
    private const double StandaloneHeight = 470;
    private const double SearchHeight = 420;

    /// <summary>Bottom-right above the taskbar, where the calendar would be.</summary>
    private PixelRect StandaloneRect()
    {
        var mon = Win32Util.PrimaryMonitor();
        double s = mon.Scale;
        int w = (int)Math.Round(StandaloneWidth * s), h = (int)Math.Round(StandaloneHeight * s);
        int margin = (int)Math.Round(12 * s);
        var work = mon.WorkArea;
        return new PixelRect(work.Right - margin - w, work.Bottom - margin - h, work.Right - margin, work.Bottom - margin);
    }

    private void PlaceStandaloneOrAttached()
    {
        if (_state == PanelState.Attached && _calendar is { } cal) PlaceBesideCalendar(cal);
        else if (_panel.IsShown)
        {
            var r = StandaloneRect();
            _panel.Place(r, PanelLayout.Expanded);
        }
    }

    public void ShowStandalone()
    {
        if (_state == PanelState.Attached) { _panel.ActivateForInput(); _state = PanelState.Standalone; return; }
        _state = PanelState.Standalone;
        _panel.Place(StandaloneRect(), PanelLayout.Expanded);
        _panel.ShowActive(animate: true);
        StartClock();
    }

    private void ToggleStandalone()
    {
        if (_state == PanelState.Standalone && _panel.IsShown) HidePanel();
        else ShowStandalone();
    }

    private void HidePanel()
    {
        _state = PanelState.Hidden;
        _panel.HidePanel();
        _minuteTimer.Stop();
        _trimTimer.Stop();
        _trimTimer.Start();
        // Return to "now" next time unless the user is mid-comparison within a minute.
        _model.ResetToNow();
    }

    // ---------------------------------------------------------------- clock

    private void StartClock()
    {
        _model.Tick();
        ScheduleMinuteTick();
    }

    /// <summary>One timer per minute boundary while visible; nothing while hidden.</summary>
    private void ScheduleMinuteTick()
    {
        if (_state == PanelState.Hidden) return;
        var now = DateTime.UtcNow;
        var next = now.AddTicks(TimeSpan.TicksPerMinute - now.Ticks % TimeSpan.TicksPerMinute);
        _minuteTimer.Interval = next - now + TimeSpan.FromMilliseconds(30);
        _minuteTimer.Start();
    }

    // ---------------------------------------------------------------- tray & preferences

    private void ApplyPreferences()
    {
        var s = _model.Settings;
        if (_options.Screenshot) return;
        try
        {
            if (StartupRegistration.IsEnabled(_exePath) != s.StartWithWindows)
                StartupRegistration.Set(s.StartWithWindows, _exePath);
        }
        catch (Exception e)
        {
            DiagLog.Write($"startup registration failed: {e.Message}");
        }
        if (s.ShowTrayIcon)
            _messages.ShowTrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), "World Time");
        else
            _messages.HideTrayIcon();
    }

    private void ShowTrayMenu()
    {
        var s = _model.Settings;
        int cmd = _messages.ShowMenu(
        [
            new TrayMenuItem(1, "Open World Time"),
            TrayMenuItem.Line,
            new TrayMenuItem(2, "Start with Windows", s.StartWithWindows),
            new TrayMenuItem(3, "Open log folder"),
            TrayMenuItem.Line,
            new TrayMenuItem(9, "Exit"),
        ]);
        switch (cmd)
        {
            case 1: ShowStandalone(); break;
            case 2: _model.SetFlag(x => x.StartWithWindows = !x.StartWithWindows); ApplyPreferences(); break;
            case 3:
                try { System.Diagnostics.Process.Start("explorer.exe", SettingsStore.DefaultDirectory); } catch { }
                break;
            case 9: Application.Current.Exit(); break;
        }
    }

    public void Dispose()
    {
        _model.SaveNow();
        _tracker.Dispose();
        _messages.Dispose();
    }
}
