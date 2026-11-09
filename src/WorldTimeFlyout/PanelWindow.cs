using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NodaTime;
using WorldTime.Core;
using WorldTime.Shell;
using WorldTimeFlyout.Views;

namespace WorldTimeFlyout;

public enum PanelLayout { Expanded, Compact }

/// <summary>
/// The companion panel: a borderless, rounded, acrylic, always-on-top window
/// that does not take activation when clicked (so the native calendar stays
/// open). It only activates when the user needs the keyboard (search) or a menu.
/// </summary>
public sealed partial class PanelWindow : Window, IPanelHost
{
    private readonly ThemeManager _theme;
    private readonly Grid _root;
    private readonly ExpandedView _expanded;
    private readonly CompactView _compact;
    private readonly AlwaysActiveAcrylic _backdrop;
    private readonly OverlappedPresenter _presenter;
    private PanelLayout _layout = PanelLayout.Expanded;
    private bool _shown;
    private bool _active;

    public PanelWindow(AppModel model, ThemeManager theme)
    {
        Model = model;
        _theme = theme;
        Title = "World Time";
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        _presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        Win32Util.MakeNonActivating(Hwnd);
        Win32Util.SetCornerPreference(Hwnd, round: true);

        _backdrop = new AlwaysActiveAcrylic();
        SystemBackdrop = _backdrop;

        _root = new Grid();
        _expanded = new ExpandedView(this);
        _compact = new CompactView(this) { Visibility = Visibility.Collapsed };
        _root.Children.Add(_expanded);
        _root.Children.Add(_compact);
        _root.KeyDown += OnRootKey;
        _root.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => LastPointerDown = DateTime.UtcNow), handledEventsToo: true);
        Content = _root;

        Activated += (_, e) =>
        {
            bool active = e.WindowActivationState != WindowActivationState.Deactivated;
            if (active == _active) return;
            _active = active;
            DiagLog.Detail($"panel {(active ? "activated" : "deactivated")}");
            if (!active)
            {
                // back to "clicks don't steal focus" for the next time it sits beside the calendar
                Win32Util.AllowClickActivation = false;
                Win32Util.ArmActivation(Hwnd, false);
                Deactivated?.Invoke();
            }
        };
        model.Changed += Render;
        theme.Changed += ApplyTheme;
        ApplyTheme();
        Render(ChangeKind.Locations);
    }

    public nint Hwnd { get; }
    public AppModel Model { get; }
    public Palette Palette => _theme.Current;
    public bool AnimationsEnabled => _theme.AnimationsEnabled;
    public bool IsShown => _shown;
    /// <summary>
    /// True only when the panel is the foreground window. A click can give the
    /// panel a thread-local WM_ACTIVATE without taking the foreground from the
    /// calendar; that must not count as "the user is working in the panel".
    /// </summary>
    public bool IsActive => _active && Win32Util.ForegroundWindow() == Hwnd;
    public PanelLayout Layout => _layout;
    public bool IsSearching => _expanded.IsSearching;
    /// <summary>When the user last pressed a mouse button or touched the panel.</summary>
    public DateTime LastPointerDown { get; private set; }

    /// <summary>The window lost activation (the user clicked somewhere else).</summary>
    public event Action? Deactivated;
    /// <summary>The panel needs a different height for search; the controller re-places it.</summary>
    public event Action? SearchChanged;
    /// <summary>Esc: the controller hides the panel (animated).</summary>
    public event Action? HideRequested;
    /// <summary>The user chose Exit in a menu.</summary>
    public event Action? ExitRequested;
    /// <summary>A setting that the controller owns changed (startup, tray icon).</summary>
    public event Action? PreferencesChanged;

    // ---------------------------------------------------------------- placement & motion
    // The window itself moves and resizes, one step per rendered frame, on the
    // Windows 11 motion curves (see Motion). Animating content inside a window
    // that is already in place just looks like it pops up there.

    private PixelRect _rect;          // the visible frame, screen pixels
    private PixelRect _outer;         // the window including its invisible resize borders
    private PixelRect _motionFrom, _motionTo;
    private Motion _motion;
    private long _motionStart;        // Stopwatch timestamp
    private Action? _motionDone;
    private bool _moving;
    private int _frames;
    private FrameworkElement? _outgoing;

    public PixelRect Frame => _rect;
    public bool IsExiting => _moving && _motion.FadeOut;

    /// <summary>Places immediately, without animation.</summary>
    public void Place(PixelRect rect, PanelLayout layout)
    {
        CancelMotion();
        SetLayout(layout);
        _rect = rect;
        ApplyBounds();
    }

    /// <summary>How far right a frame has to move to leave its monitor.</summary>
    public static int OffscreenDistance(PixelRect r) =>
        Math.Max(1, Win32Util.MonitorFor(r).Bounds.Right - r.Left);

    /// <summary>
    /// Slides in from the right ("direct entrance": 333 ms, decelerating).
    /// <paramref name="distance"/>: how far right it starts. <paramref name="startTick"/>
    /// (Environment.TickCount64) backdates the start to when the calendar began its
    /// own slide, so the two stay in step even though we hear about it a bit later.
    /// </summary>
    public void EnterFromRight(PixelRect target, PanelLayout layout, int distance, long? startTick = null, bool activate = false)
    {
        bool wasShown = _shown;
        CancelMotion();
        SetLayout(layout);
        if (!AnimationsEnabled)
        {
            _rect = target;
            ApplyBounds();
            Show(activate);
            return;
        }
        if (!wasShown)
        {
            _rect = Shift(target, distance);
            ApplyBounds();
        }
        Show(activate);
        // Already on screen (it was sliding out): turn around from where it is.
        RunMotion(target, Motion.Entrance, null, wasShown ? null : startTick);
    }

    /// <summary>
    /// Moves/resizes to a new frame: expand/collapse ("point to point", 250 ms,
    /// bottom edge fixed) or a small correction.
    /// </summary>
    public void MoveTo(PixelRect target, PanelLayout layout, Motion motion)
    {
        if (!_shown || !AnimationsEnabled)
        {
            Place(target, layout);
            return;
        }
        if (_moving && _motion == Motion.Entrance)
        {
            // Still sliding in: aim the slide at the new frame, keep its timing.
            EndTransition();
            SetLayout(layout);
            int dx = _motionFrom.Left - _motionTo.Left;
            _motionFrom = Shift(target, dx);
            _motionTo = target;
            return;
        }
        var current = _moving ? _motionTo : _rect;
        if (target == current && layout == _layout && !IsExiting) return;
        var fromLayout = _layout;
        var fromRect = _rect;
        CancelMotion();
        SetLayout(layout);
        if (_layout != fromLayout) BeginTransition(fromLayout, fromRect, target);
        RunMotion(target, motion, EndTransition);
    }

    /// <summary>Slides out to the right with a fade ("exit": 167 ms, accelerating), then hides.</summary>
    public void ExitToRight(int distance, Action? done = null)
    {
        if (!_shown) { done?.Invoke(); return; }
        if (!AnimationsEnabled)
        {
            HidePanel();
            done?.Invoke();
            return;
        }
        if (IsExiting)
        {
            _motionDone += done;
            return;
        }
        var from = _rect;
        CancelMotion();
        RunMotion(Shift(from, distance), Motion.Exit, () =>
        {
            HidePanel();
            done?.Invoke();
        });
    }

    private static PixelRect Shift(PixelRect r, int dx) => new(r.Left + dx, r.Top, r.Right + dx, r.Bottom);

    private void RunMotion(PixelRect target, Motion motion, Action? done, long? startTick = null)
    {
        _motionFrom = _rect;
        _motionTo = target;
        _motion = motion;
        _motionDone = done;
        _frames = 0;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long age = startTick is long t ? Math.Clamp(Environment.TickCount64 - t, 0, (long)motion.Milliseconds) : 0;
        _motionStart = now - age * System.Diagnostics.Stopwatch.Frequency / 1000;
        if (!_moving)
        {
            _moving = true;
            CompositionTarget.Rendering += OnMotionFrame;
        }
        Step();
    }

    private void OnMotionFrame(object? sender, object e) => Step();

    private void Step()
    {
        if (!_moving) return;
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_motionStart).TotalMilliseconds;
        double t = Math.Clamp(elapsed / _motion.Milliseconds, 0, 1);
        double p = _motion.Ease(t);
        static int Lerp(int a, int b, double p) => (int)Math.Round(a + (b - a) * p);
        var r = new PixelRect(
            Lerp(_motionFrom.Left, _motionTo.Left, p), Lerp(_motionFrom.Top, _motionTo.Top, p),
            Lerp(_motionFrom.Right, _motionTo.Right, p), Lerp(_motionFrom.Bottom, _motionTo.Bottom, p));
        if (_motion.FadeOut) _root.Opacity = 1 - t;
        SetFrameFast(r);
        _frames++;
        if (t < 1) return;
        _moving = false;
        CompositionTarget.Rendering -= OnMotionFrame;
        DiagLog.Detail($"motion {_motion.Name}: {_frames} frames in {elapsed:0} ms");
        var done = _motionDone;
        _motionDone = null;
        done?.Invoke();
    }

    /// <summary>Stops any motion where it is, drops its completion and ends a view transition.</summary>
    private void CancelMotion()
    {
        if (_moving)
        {
            _moving = false;
            CompositionTarget.Rendering -= OnMotionFrame;
        }
        _motionDone = null;
        EndTransition();
        _root.Opacity = 1;
    }

    /// <summary>Per-frame placement: a plain SetWindowPos, without resizing when only moving.</summary>
    private void SetFrameFast(PixelRect frame)
    {
        if (frame == _rect) return;
        bool sized = frame.Width != _rect.Width || frame.Height != _rect.Height;
        _rect = frame;
        _outer = Win32Util.OuterRectForFrame(Hwnd, frame);
        Win32Util.MoveWindowNoActivate(Hwnd, _outer, resize: sized);
    }

    private FrameworkElement ViewFor(PanelLayout l) => l == PanelLayout.Expanded ? _expanded : _compact;

    /// <summary>
    /// Expand/collapse: both views stay laid out at their own final heights,
    /// pinned to the bottom edge (which does not move), so the moving top edge
    /// reveals or covers them like the calendar's own card. The old view fades
    /// out quickly, the new one fades in.
    /// </summary>
    private void BeginTransition(PanelLayout from, PixelRect fromRect, PixelRect toRect)
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        var incoming = ViewFor(_layout);
        var outgoing = ViewFor(from);
        Pin(incoming, toRect.Height / scale);
        Pin(outgoing, fromRect.Height / scale);
        outgoing.Visibility = Visibility.Visible;
        outgoing.IsHitTestVisible = false;
        _outgoing = outgoing;
        Fade(outgoing, 1, 0, 83);
        Fade(incoming, 0, 1, 167);
    }

    private void EndTransition()
    {
        if (_outgoing is null) return;
        _outgoing = null;
        foreach (var v in new FrameworkElement[] { _expanded, _compact })
        {
            v.Height = double.NaN;
            v.VerticalAlignment = VerticalAlignment.Stretch;
            v.IsHitTestVisible = true;
            var visual = ElementCompositionPreview.GetElementVisual(v);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
        }
        _expanded.Visibility = _layout == PanelLayout.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _compact.Visibility = _layout == PanelLayout.Compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Pin(FrameworkElement v, double height)
    {
        v.Height = height;
        v.VerticalAlignment = VerticalAlignment.Bottom;
    }

    private static void Fade(UIElement el, float from, float to, double ms)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var a = visual.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, from);
        a.InsertKeyFrame(1f, to, visual.Compositor.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromMilliseconds(ms);
        visual.StartAnimation("Opacity", a);
    }

    /// <summary>
    /// <see cref="_rect"/> is the visible frame we want. The bordered window has
    /// invisible resize borders around it, so size the outer window to match.
    /// </summary>
    private void ApplyBounds()
    {
        _outer = Win32Util.OuterRectForFrame(Hwnd, _rect);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_outer.Left, _outer.Top, _outer.Width, _outer.Height));
    }

    /// <summary>After the first show the real border size is known; correct the bounds once.</summary>
    private void FixBoundsAfterShow()
    {
        var frame = Win32Util.WindowRect(Hwnd);
        if (frame != _rect) ApplyBounds();
    }

    /// <summary>Shows without taking activation (beside the calendar).</summary>
    public void ShowPassive() => Show(activate: false);

    /// <summary>Shows and takes keyboard focus (tray click, search, menus).</summary>
    public void ShowActive() => Show(activate: true);

    private void Show(bool activate)
    {
        _shown = true;
        if (!activate) Win32Util.AllowClickActivation = false;
        AppWindow.Show(activate);
        Win32Util.SetTopmostNoActivate(Hwnd, _outer, show: true);
        FixBoundsAfterShow();
        if (activate) ActivateForInput();
        Render(ChangeKind.Time);
    }

    public void HidePanel()
    {
        if (!_shown) return;
        CancelMotion();
        _shown = false;
        CloseOpenPopups();
        if (_expanded.IsSearching) _expanded.SetSearching(false);
        _expanded.CloseDatePicker();
        Win32Util.AllowClickActivation = false;
        Win32Util.ArmActivation(Hwnd, false);
        AppWindow.Hide();
    }

    private void CloseOpenPopups()
    {
        if (_root.XamlRoot is null) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(_root.XamlRoot))
            popup.IsOpen = false;
    }

    /// <summary>Takes activation so keyboard input and menus work; the calendar will close.</summary>
    public void ActivateForInput()
    {
        Win32Util.AllowClickActivation = true;
        Win32Util.SetNoActivate(Hwnd, false);
        if (Win32Util.ForegroundWindow() != Hwnd && !Win32Util.SetForeground(Hwnd))
            DiagLog.Detail("SetForegroundWindow refused (expected unless triggered by a click on the panel)");
        Activate();
    }

    public void ArmActivation(bool armed)
    {
        if (!_active) Win32Util.ArmActivation(Hwnd, armed);
    }

    private void SetLayout(PanelLayout layout)
    {
        if (_expanded.IsSearching) layout = PanelLayout.Expanded;
        if (layout == _layout) return;
        _layout = layout;
        _expanded.Visibility = layout == PanelLayout.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _compact.Visibility = layout == PanelLayout.Compact ? Visibility.Visible : Visibility.Collapsed;
        if (layout == PanelLayout.Compact) _expanded.CloseDatePicker();
        Render(ChangeKind.Locations);
    }

    public void ShowWelcome(bool show) => _expanded.ShowWelcome(show);

    public void OpenDatePicker()
    {
        SetLayout(PanelLayout.Expanded);
        _expanded.OpenDatePicker();
    }

    // ---------------------------------------------------------------- rendering & theme

    private void Render(ChangeKind kind)
    {
        if (_layout == PanelLayout.Expanded) _expanded.Render(kind);
        else _compact.Render(kind);
    }

    private void ApplyTheme()
    {
        var p = _theme.Current;
        // The panel follows the taskbar (Windows mode), like the calendar beside it.
        var wanted = p.Dark ? ElementTheme.Dark : ElementTheme.Light;
        if (_root.RequestedTheme == wanted)
        {
            // force built-in controls to re-resolve accent resources
            _root.RequestedTheme = wanted == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        }
        _root.RequestedTheme = wanted;
        _backdrop.SetTheme(p.Dark);
        Win32Util.SetDarkFrame(Hwnd, p.Dark);
        // In contrast themes acrylic is replaced by the solid system color.
        _root.Background = p.HighContrast ? Ui.Brush(p.SolidBackground) : null;
        _expanded.ApplyTheme(p);
        _compact.ApplyTheme(p);
        _compact.SetTextScale(_theme.TextScale);
    }

    /// <summary>For screenshots: paint an opaque stand-in for the acrylic backdrop.</summary>
    public void UseSolidBackground(bool solid) =>
        _root.Background = solid ? Ui.Brush(Palette.SolidBackground) : null;

    public FrameworkElement RootElement => _root;

    private void OnRootKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (_expanded.IsSearching) EndSearch();
            else if (_expanded.IsPickingDate) _expanded.CloseDatePicker();
            else HideRequested?.Invoke();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- IPanelHost

    public bool Copy(string text) => Win32Util.CopyText(Hwnd, text);

    public void BeginSearch()
    {
        ActivateForInput();
        SetLayout(PanelLayout.Expanded);
        _expanded.SetSearching(true);
        SearchChanged?.Invoke();
    }

    public void EndSearch()
    {
        if (!_expanded.IsSearching) return;
        _expanded.SetSearching(false);
        SearchChanged?.Invoke();
    }

    public void ShowRowMenu(LocationReading reading, UIElement target, Windows.Foundation.Point? at)
    {
        ActivateForInput();
        var loc = reading.Location;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Copy", Glyph.Copy, () => Copy(Model.CopyText(reading, withDate: false))));
        menu.Items.Add(Item("Copy with date", null, () => Copy(Model.CopyText(reading, withDate: true))));
        menu.Items.Add(Item("Copy all times", null, () => Copy(Model.CopyText(Model.Readings, withDate: false))));
        menu.Items.Add(new MenuFlyoutSeparator());
        var reference = Item("Use as reference", Glyph.Pin, () => Model.SetReference(loc.Id));
        reference.IsEnabled = !reading.IsReference;
        menu.Items.Add(reference);
        int index = Model.Locations.ToList().FindIndex(l => l.Id == loc.Id);
        var up = Item("Move up", Glyph.Up, () => Model.Move(loc.Id, -1));
        up.IsEnabled = index > 0;
        var down = Item("Move down", Glyph.Down, () => Model.Move(loc.Id, 1));
        down.IsEnabled = index >= 0 && index < Model.Locations.Count - 1;
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Remove", Glyph.Delete, () => Model.Remove(loc.Id)));
        menu.ShowAt(target, new FlyoutShowOptions { Position = at });
    }

    public void ShowMoreMenu(FrameworkElement target)
    {
        ActivateForInput();
        var s = Model.Settings;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Add city", Glyph.Add, BeginSearch));
        menu.Items.Add(Item("Copy all times", Glyph.Copy, () => Copy(Model.CopyText(Model.Readings, withDate: false))));
        menu.Items.Add(Item("Copy all with dates", null, () => Copy(Model.CopyText(Model.Readings, withDate: true))));
        menu.Items.Add(new MenuFlyoutSeparator());

        var step = new MenuFlyoutSubItem { Text = "Time step", Icon = new FontIcon { Glyph = Glyph.Clock } };
        foreach (var (minutes, label) in new[] { (15, "15 minutes"), (30, "30 minutes"), (60, "1 hour") })
        {
            var r = new RadioMenuFlyoutItem { Text = label, GroupName = "step", IsChecked = s.StepMinutes == minutes };
            r.Click += (_, _) => Model.SetStep(minutes);
            step.Items.Add(r);
        }
        menu.Items.Add(step);

        var clock = new MenuFlyoutSubItem { Text = "Clock format" };
        foreach (var (style, label) in new[] { (ClockStyle.System, "Windows setting"), (ClockStyle.H12, "12-hour"), (ClockStyle.H24, "24-hour") })
        {
            var r = new RadioMenuFlyoutItem { Text = label, GroupName = "clock", IsChecked = s.Clock == style };
            r.Click += (_, _) => Model.SetClock(style);
            clock.Items.Add(r);
        }
        menu.Items.Add(clock);
        menu.Items.Add(new MenuFlyoutSeparator());

        var startup = new ToggleMenuFlyoutItem { Text = "Start with Windows", IsChecked = s.StartWithWindows };
        startup.Click += (_, _) => { Model.SetFlag(x => x.StartWithWindows = startup.IsChecked); PreferencesChanged?.Invoke(); };
        menu.Items.Add(startup);
        var tray = new ToggleMenuFlyoutItem { Text = "Show icon in system tray", IsChecked = s.ShowTrayIcon };
        tray.Click += (_, _) => { Model.SetFlag(x => x.ShowTrayIcon = tray.IsChecked); PreferencesChanged?.Invoke(); };
        menu.Items.Add(tray);
        menu.Items.Add(new MenuFlyoutSeparator());
        var about = new MenuFlyoutItem
        {
            Text = $"Version {typeof(PanelWindow).Assembly.GetName().Version?.ToString(3)} · tz {Zones.TzdbVersion}",
            IsEnabled = false,
        };
        menu.Items.Add(about);
        menu.Items.Add(Item("Exit", null, () => ExitRequested?.Invoke()));
        menu.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => onClick();
        return item;
    }
}

/// <summary>
/// Desktop acrylic that stays "active" even though the panel is never the
/// active window (it must look like the flyout next to it, not go flat).
/// </summary>
public sealed partial class AlwaysActiveAcrylic : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private bool _dark = true;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        _config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    // We drive our own configuration (always "active"); ignore the framework's.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    public void SetTheme(bool dark)
    {
        _dark = dark;
        if (_config is not null) _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
