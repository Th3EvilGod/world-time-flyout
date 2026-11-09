using System.Numerics;
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
    private PixelRect _rect;
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
    /// <summary>The user chose Exit in a menu.</summary>
    public event Action? ExitRequested;
    /// <summary>A setting that the controller owns changed (startup, tray icon).</summary>
    public event Action? PreferencesChanged;

    // ---------------------------------------------------------------- placement

    public void Place(PixelRect rect, PanelLayout layout)
    {
        SetLayout(layout);
        _rect = rect;
        ApplyBounds();
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

    private PixelRect _outer;

    /// <summary>Shows without taking activation (beside the calendar).</summary>
    public void ShowPassive(bool animate)
    {
        bool wasShown = _shown;
        _shown = true;
        Win32Util.AllowClickActivation = false;
        AppWindow.Show(false);
        Win32Util.SetTopmostNoActivate(Hwnd, _outer, show: true);
        FixBoundsAfterShow();
        if (!wasShown && animate) PlayEntrance();
        Render(ChangeKind.Time);
    }

    /// <summary>Shows and takes keyboard focus (tray click, search, menus).</summary>
    public void ShowActive(bool animate)
    {
        bool wasShown = _shown;
        _shown = true;
        AppWindow.Show(true);
        Win32Util.SetTopmostNoActivate(Hwnd, _outer, show: true);
        FixBoundsAfterShow();
        ActivateForInput();
        if (!wasShown && animate) PlayEntrance();
        Render(ChangeKind.Time);
    }

    public void HidePanel()
    {
        if (!_shown) return;
        _shown = false;
        if (_expanded.IsSearching) _expanded.SetSearching(false);
        Win32Util.AllowClickActivation = false;
        Win32Util.ArmActivation(Hwnd, false);
        AppWindow.Hide();
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
        Render(ChangeKind.Locations);
    }

    public void ShowWelcome(bool show) => _expanded.ShowWelcome(show);

    private void PlayEntrance()
    {
        if (!AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        ElementCompositionPreview.SetIsTranslationEnabled(_root, true);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var slide = comp.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0, 16, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(250);
        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
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
            else HidePanel();
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

    public void ShowDateTimePicker(FrameworkElement target)
    {
        ActivateForInput();
        var reference = Model.ReferenceReading;
        if (reference is null) return;
        var local = reference.Value.Local;
        var calendar = new CalendarView
        {
            SelectionMode = CalendarViewSelectionMode.Single,
            IsTodayHighlighted = true,
        };
        var selectedDate = new DateTimeOffset(local.Year, local.Month, local.Day, 12, 0, 0, TimeSpan.Zero);
        calendar.SelectedDates.Add(selectedDate);
        calendar.SetDisplayDate(selectedDate);
        var time = new TimePicker
        {
            Time = new TimeSpan(local.Hour, local.Minute, 0),
            MinuteIncrement = 5,
            ClockIdentifier = Model.Format.Is24Hour ? "24HourClock" : "12HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var caption = Ui.Text($"Time in {reference.Value.Location.Name}", 12);
        caption.Foreground = Ui.Brush(Palette.TextSecondary);
        var go = new Button { Content = "Show this time", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var panel = new StackPanel { Spacing = 8, Width = 300 };
        panel.Children.Add(calendar);
        panel.Children.Add(caption);
        panel.Children.Add(time);
        panel.Children.Add(go);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        go.Click += (_, _) =>
        {
            var date = calendar.SelectedDates.Count > 0 ? calendar.SelectedDates[0] : selectedDate;
            var t = time.Time;
            var picked = new LocalDateTime(date.Year, date.Month, date.Day, t.Hours, t.Minutes);
            Model.Selection.SetLocal(picked, Model.ReferenceZone);
            flyout.Hide();
        };
        flyout.ShowAt(target);
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
