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
    private readonly DispatcherQueueTimer _reattachCheck;
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
        _panel.HideRequested += () => HidePanel(animate: true);
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

        // The panel starts sliding out as soon as the calendar loses the
        // foreground. If the calendar turns out to still be open, come back.
        _reattachCheck = _dq.CreateTimer();
        _reattachCheck.IsRepeating = false;
        _reattachCheck.Interval = TimeSpan.FromMilliseconds(700);
        _reattachCheck.Tick += (_, _) =>
        {
            if (_state == PanelState.Hidden && _tracker.FlyoutWindow != 0 && _tracker.Current is { } cal)
            {
                DiagLog.Detail("calendar still open after closing started: re-attaching");
                OnCalendarOpened(cal);
            }
        };
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
        _tracker.Opening += s => _dq.TryEnqueue(() => OnCalendarOpening(s));
        _tracker.Opened += s => _dq.TryEnqueue(() => OnCalendarOpened(s));
        _tracker.Closing += r => _dq.TryEnqueue(() => OnCalendarClosing(r));
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
    // Motion follows the calendar card beside it: both slide in from the right
    // edge together, resize together on expand/collapse (bottom edge fixed),
    // and slide back out to the right together.

    /// <summary>The calendar is uncloaking with a known layout: start sliding in right away.</summary>
    private void OnCalendarOpening(CalendarState s)
    {
        _reattachCheck.Stop();
        Attach(s, Motion.Entrance);
    }

    private void OnCalendarOpened(CalendarState s)
    {
        _reattachCheck.Stop();
        Attach(s, Motion.Correction);
    }

    private void OnCalendarChanged(CalendarState s)
    {
        _calendar = s;
        if (_state != PanelState.Attached) return;
        var (rect, layout) = PlacementBeside(s);
        bool layoutChange = layout != _panel.Layout || Math.Abs(rect.Height - _panel.Frame.Height) > 24 * s.Monitor.Scale;
        _panel.MoveTo(rect, layout, layoutChange ? Motion.PointToPoint : Motion.Correction);
    }

    /// <summary>
    /// Shows the panel beside the calendar: slides in when it was hidden (or
    /// sliding out), otherwise moves to the new place.
    /// </summary>
    private void Attach(CalendarState s, Motion ifShown)
    {
        _calendar = s;
        var (rect, layout) = PlacementBeside(s);
        bool visible = _panel.IsShown && !_panel.IsExiting && _state != PanelState.Hidden;
        _state = PanelState.Attached;
        if (visible) _panel.MoveTo(rect, layout, ifShown);
        else _panel.EnterFromRight(rect, layout, CalendarSlide(s), _tracker.OpenStartTick);
        StartClock();
    }

    /// <summary>
    /// The calendar slides in from the right screen edge: its window starts one
    /// window-width to the right. The panel moves the same distance on the same
    /// curve so the two travel as one surface.
    /// </summary>
    private static int CalendarSlide(CalendarState s) =>
        Math.Max(1, s.Monitor.Bounds.Right - Math.Min(s.Window.Left, s.Card.Left - (int)Math.Round(12 * s.Monitor.Scale)));

    /// <summary>The calendar lost the foreground and is about to slide out: go with it.</summary>
    private void OnCalendarClosing(string reason)
    {
        if (_state != PanelState.Attached || _panel.IsActive || _panel.IsSearching) return;
        HidePanel(animate: true);
        _reattachCheck.Stop();
        _reattachCheck.Start();
    }

    private void OnCalendarClosed(string reason)
    {
        _reattachCheck.Stop();
        if (_state == PanelState.Attached)
        {
            if (_panel.IsActive || _panel.IsSearching)
                _state = PanelState.Standalone; // the user is working in the panel (search, menu): keep it open on its own
            else
                HidePanel(animate: true);       // clicked elsewhere / Esc / clicked the clock again: close together
        }
        _calendar = null;
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
        if (_state == PanelState.Standalone && !_panel.IsActive && _tracker.FlyoutWindow == 0) HidePanel(animate: true);
    }

    private (PixelRect Rect, PanelLayout Layout) PlacementBeside(CalendarState s)
    {
        var layout = s.Expanded ? PanelLayout.Expanded : PanelLayout.Compact;
        if (_panel.IsSearching) layout = PanelLayout.Expanded;
        double minHeight = layout == PanelLayout.Expanded
            ? Views.ExpandedView.RequiredHeight
            : Views.CompactView.RequiredHeightFor(_theme.TextScale);
        if (_panel.IsSearching) minHeight = SearchHeight;
        var rect = CalendarGeometry.PanelRect(s, minWidthDip: 320, minHeightDip: (int)Math.Ceiling(minHeight));
        return (rect, layout);
    }

    private void PlaceBesideCalendar(CalendarState s)
    {
        var (rect, layout) = PlacementBeside(s);
        _panel.MoveTo(rect, layout, Motion.PointToPoint);
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
        else if (_panel.IsShown) _panel.MoveTo(StandaloneRect(), PanelLayout.Expanded, Motion.PointToPoint);
    }

    public void ShowStandalone()
    {
        if (_state == PanelState.Attached) { _panel.ActivateForInput(); _state = PanelState.Standalone; return; }
        _reattachCheck.Stop();
        _state = PanelState.Standalone;
        var rect = StandaloneRect();
        _panel.EnterFromRight(rect, PanelLayout.Expanded, PanelWindow.OffscreenDistance(rect), activate: true);
        StartClock();
    }

    private void ToggleStandalone()
    {
        if (_state == PanelState.Standalone && _panel.IsShown && !_panel.IsExiting) HidePanel(animate: true);
        else ShowStandalone();
    }

    private void HidePanel(bool animate = false)
    {
        // Beside the calendar the panel travels with it; on its own it leaves the screen.
        int distance = _state == PanelState.Attached && _calendar is { } cal
            ? CalendarSlide(cal)
            : PanelWindow.OffscreenDistance(_panel.Frame);
        _state = PanelState.Hidden;
        _minuteTimer.Stop();
        _trimTimer.Stop();
        _trimTimer.Start();
        if (animate) _panel.ExitToRight(distance, AfterHidden);
        else
        {
            _panel.HidePanel();
            AfterHidden();
        }
    }

    private void AfterHidden()
    {
        // Return to "now" next time.
        if (_state == PanelState.Hidden) _model.ResetToNow();
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
