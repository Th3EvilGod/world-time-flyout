using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NodaTime;
using Duration = NodaTime.Duration;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// The shared timeline: a horizontal tape of the reference city's hours under
/// a fixed accent needle. Drag to scrub, click an hour to jump there, scroll
/// to step. The needle always marks the selected moment, so every row in the
/// panel shows the same instant.
/// </summary>
public sealed partial class TimeRuler : Grid
{
    private const double PxPerHour = 52;
    private const double Baseline = 22;

    private readonly IPanelHost _host;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { Height = 1 };
    private readonly Rectangle _needle = new() { Width = 2, RadiusX = 1, RadiusY = 1 };
    private readonly Rectangle _cap = new() { Width = 10, Height = 4, RadiusX = 2, RadiusY = 2 };
    private readonly List<Rectangle> _ticks = [];
    private readonly List<TextBlock> _labels = [];
    private Palette? _palette;

    private Instant _center;
    private bool _dragging, _moved;
    private double _dragStartX;
    private Instant _dragStartCenter;
    private Instant _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

    public TimeRuler(IPanelHost host)
    {
        _host = host;
        Height = 46;
        Background = Ui.Transparent;
        IsTabStop = true;
        AllowFocusOnInteraction = false;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Timeline. Drag or use arrow keys to change the time.");
        Children.Add(_canvas);
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_needle);
        _canvas.Children.Add(_cap);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        SizeChanged += (_, _) => Render();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => { if (_dragging) EndDrag(null); };
        PointerWheelChanged += OnWheel;
        KeyDown += OnKey;
        _center = host.Model.Selection.Selected;
    }

    /// <summary>Follow the model (no-op while the user is dragging or an animation runs).</summary>
    public void Sync()
    {
        if (_dragging || _animating) return;
        _center = _host.Model.Selection.Selected;
        Render();
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        Render();
    }

    private void Render()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _palette is not { } p) return;
        var model = _host.Model;
        var zone = model.ReferenceZone;
        int step = model.StepMinutes;
        var f = model.Format;
        double mid = Math.Round(w / 2);

        _track.Width = w;
        _track.Fill = Ui.Brush(p.Divider);
        Canvas.SetTop(_track, Baseline);

        double halfHours = mid / PxPerHour + 1;
        var start = _center - Duration.FromSeconds(halfHours * 3600);
        var end = _center + Duration.FromSeconds(halfHours * 3600);
        var t = TimeMath.CeilBoundary(start, zone, step);
        int ti = 0, li = 0;
        // Label every hour, or every 2 hours when 24-hour labels would crowd.
        int labelEvery = f.Is24Hour ? 1 : 2;
        var tickMinor = Ui.Brush(p.TextTertiary);
        var tickMajor = Ui.Brush(p.TextSecondary);
        var labelBrush = Ui.Brush(p.TextSecondary);
        var dayBrush = Ui.Brush(p.TextPrimary);

        for (int guard = 0; t <= end && guard < 400; guard++)
        {
            var local = t.InZone(zone).LocalDateTime;
            double x = mid + (t - _center).TotalHours * PxPerHour;
            bool hour = local.Minute == 0;
            bool midnight = hour && local.Hour == 0;
            var tick = Tick(ti++);
            tick.Height = midnight ? 14 : hour ? 8 : 4;
            tick.Fill = hour ? tickMajor : tickMinor;
            Canvas.SetLeft(tick, Math.Round(x));
            Canvas.SetTop(tick, Baseline - tick.Height);

            if (hour && (midnight || local.Hour % labelEvery == 0))
            {
                var label = Label(li++);
                label.Text = midnight ? f.DayName(local.Date) : f.HourLabel(local.TimeOfDay);
                label.FontWeight = midnight ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                label.Foreground = midnight ? dayBrush : labelBrush;
                Canvas.SetLeft(label, Math.Round(x - 24));
                Canvas.SetTop(label, Baseline + 4);
            }
            t = TimeMath.NextBoundary(t, zone, step, StepDirection.Forward);
        }
        for (int i = ti; i < _ticks.Count; i++) _ticks[i].Visibility = Visibility.Collapsed;
        for (int i = li; i < _labels.Count; i++) _labels[i].Visibility = Visibility.Collapsed;

        var accent = Ui.Brush(p.Accent);
        _needle.Fill = accent;
        _needle.Height = Baseline + 4;
        Canvas.SetLeft(_needle, mid - 1);
        Canvas.SetTop(_needle, 0);
        _cap.Fill = accent;
        Canvas.SetLeft(_cap, mid - 5);
        Canvas.SetTop(_cap, 0);
        Canvas.SetZIndex(_needle, 10);
        Canvas.SetZIndex(_cap, 11);
    }

    private Rectangle Tick(int i)
    {
        while (_ticks.Count <= i)
        {
            var r = new Rectangle { Width = 1 };
            _ticks.Add(r);
            _canvas.Children.Add(r);
        }
        _ticks[i].Visibility = Visibility.Visible;
        return _ticks[i];
    }

    private TextBlock Label(int i)
    {
        while (_labels.Count <= i)
        {
            var tb = new TextBlock
            {
                Width = 48,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };
            _labels.Add(tb);
            _canvas.Children.Add(tb);
        }
        _labels[i].Visibility = Visibility.Visible;
        return _labels[i];
    }

    // ---------------------------------------------------------------- input

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
        _dragging = true;
        _moved = false;
        _dragStartX = pt.Position.X;
        _dragStartCenter = _center;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double dx = e.GetCurrentPoint(this).Position.X - _dragStartX;
        if (Math.Abs(dx) > 3) _moved = true;
        if (!_moved) return;
        _center = _dragStartCenter - Duration.FromSeconds(dx / PxPerHour * 3600);
        Render();
        _host.Model.SetInstant(TimeMath.TruncateToMinute(_center));
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        EndDrag(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void EndDrag(double? x)
    {
        _dragging = false;
        var model = _host.Model;
        Instant target;
        if (!_moved && x is { } clickX)
        {
            // click: jump to the time under the pointer
            double hours = (clickX - Math.Round(ActualWidth / 2)) / PxPerHour;
            target = TimeMath.Snap(_center + Duration.FromSeconds(hours * 3600), model.ReferenceZone, model.StepMinutes);
        }
        else
        {
            target = TimeMath.Snap(_center, model.ReferenceZone, model.StepMinutes);
        }
        AnimateTo(target);
    }

    private void AnimateTo(Instant target)
    {
        if (!_host.AnimationsEnabled || Math.Abs((target - _center).TotalMinutes) < 0.5)
        {
            _center = target;
            Render();
            _host.Model.SetInstant(target);
            return;
        }
        _animFrom = _center;
        _animTo = target;
        _animStart = DateTime.UtcNow;
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        double t = Math.Clamp((DateTime.UtcNow - _animStart).TotalMilliseconds / 160.0, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        _center = _animFrom + Duration.FromSeconds((_animTo - _animFrom).TotalSeconds * eased);
        Render();
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            _center = _animTo;
            _host.Model.SetInstant(_animTo);
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        // wheel up = later (like turning a value up), tilt/swipe right = later
        int dir = Math.Sign(delta);
        _host.Model.StepTime(dir);
        e.Handled = true;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: _host.Model.StepTime(-1); e.Handled = true; break;
            case Windows.System.VirtualKey.Right: _host.Model.StepTime(1); e.Handled = true; break;
            case Windows.System.VirtualKey.Home: _host.Model.ResetToNow(); e.Handled = true; break;
        }
    }
}
