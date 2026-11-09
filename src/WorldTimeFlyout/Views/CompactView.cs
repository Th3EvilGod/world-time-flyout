using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// Compact view for the collapsed calendar: exactly three cities per page.
/// Swipe/drag sideways or use the pips to page; click a time to copy it;
/// the small step buttons (or the mouse wheel) move the time.
/// </summary>
public sealed partial class CompactView : Grid
{
    public const int PageSize = 3;
    public const double RowHeight = 24;
    public const double StripHeight = 22;
    private const double PadTop = 3, PadBottom = 1;

    /// <summary>
    /// Height for three rows plus the paging strip (DIPs). At 100% text size this
    /// is exactly the collapsed calendar's 100 px; larger text sizes grow it.
    /// </summary>
    public static double RequiredHeightFor(double textScale) =>
        PadTop + (PageSize * RowHeight + StripHeight) * Math.Max(1, textScale) + PadBottom;

    private double _rowHeight = RowHeight;
    private readonly RowDefinition _stripRow;

    private readonly IPanelHost _host;
    private readonly Grid _viewport;
    private readonly StackPanel _page;
    private readonly CompactRow[] _rows;
    private readonly PipsPager _pips;
    private readonly Button _shift, _earlier, _later;
    private readonly TextBlock _shiftText;
    private Palette? _palette;
    private int _pageIndex;

    private bool _pointerDown, _swiping;
    private double _downX, _downY;
    private CompactRow? _downRow;

    public CompactView(IPanelHost host)
    {
        _host = host;
        Padding = new Thickness(4, PadTop, 4, PadBottom);
        RowDefinitions.Add(Ui.RowStar());
        _stripRow = Ui.Row(StripHeight);
        RowDefinitions.Add(_stripRow);

        _page = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top };
        _rows = new CompactRow[PageSize];
        for (int i = 0; i < PageSize; i++)
        {
            _rows[i] = new CompactRow(host);
            _page.Children.Add(_rows[i]);
        }
        _viewport = new Grid { Background = Ui.Transparent };
        _viewport.Children.Add(_page);
        _viewport.SizeChanged += (_, e) =>
            _viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        ElementCompositionPreview.SetIsTranslationEnabled(_page, true);
        _viewport.PointerPressed += OnPressed;
        _viewport.PointerMoved += OnMoved;
        _viewport.PointerReleased += OnReleased;
        _viewport.PointerCanceled += (_, _) => CancelSwipe();
        _viewport.PointerCaptureLost += (_, _) => CancelSwipe();
        _viewport.PointerWheelChanged += OnWheel;
        Children.Add(_viewport.At(0));

        // bottom strip: [shift chip]   (pips)   [- +]
        var strip = new Grid();
        strip.ColumnDefinitions.Add(Ui.ColStar());
        strip.ColumnDefinitions.Add(Ui.ColAuto());
        strip.ColumnDefinitions.Add(Ui.ColStar());
        _shiftText = Ui.Text("", 12, semibold: true);
        var shiftContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        shiftContent.Children.Add(_shiftText);
        shiftContent.Children.Add(Ui.Icon(Glyph.Cancel, 9));
        _shift = Ui.SubtleButton(shiftContent, "Back to the current time", (_, _) => _host.Model.ResetToNow(), 24);
        _shift.Width = double.NaN;
        _shift.Padding = new Thickness(8, 0, 8, 0);
        _shift.HorizontalAlignment = HorizontalAlignment.Left;
        _shift.Margin = new Thickness(4, 0, 0, 0);

        _pips = new PipsPager
        {
            MaxVisiblePips = 5,
            PreviousButtonVisibility = PipsPagerButtonVisibility.VisibleOnPointerOver,
            NextButtonVisibility = PipsPagerButtonVisibility.VisibleOnPointerOver,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            AllowFocusOnInteraction = false,
        };
        AutomationProperties.SetName(_pips, "Pages of cities");
        _pips.SelectedIndexChanged += (_, _) =>
        {
            if (_pips.SelectedPageIndex != _pageIndex) GoToPage(_pips.SelectedPageIndex, animate: true);
        };

        var steps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
        _earlier = Ui.SubtleButton("", "Earlier", (_, _) => _host.Model.StepTime(-1), 24);
        _later = Ui.SubtleButton("", "Later", (_, _) => _host.Model.StepTime(1), 24);
        foreach (var b in new[] { _earlier, _later })
        {
            b.FontSize = 12;
            b.Padding = new Thickness(6, 0, 6, 0);
            steps.Children.Add(b);
        }
        strip.Children.Add(_shift.At(0, 0));
        strip.Children.Add(_pips.At(0, 1));
        strip.Children.Add(steps.At(0, 2));
        Children.Add(strip.At(1));
    }

    private int PageCount => Math.Max(1, (int)Math.Ceiling(_host.Model.Readings.Count / (double)PageSize));

    public void Render(ChangeKind kind)
    {
        var model = _host.Model;
        var readings = model.Readings;
        if (_pageIndex >= PageCount) _pageIndex = PageCount - 1;
        for (int i = 0; i < PageSize; i++)
        {
            int idx = _pageIndex * PageSize + i;
            bool has = idx < readings.Count;
            _rows[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has) _rows[i].Update(readings[idx], model.Format);
        }
        _pips.NumberOfPages = PageCount;
        _pips.SelectedPageIndex = _pageIndex;
        _pips.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;

        bool live = model.Selection.IsLive;
        _shift.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _shiftText.Text = TimeFormat.ShiftFromNow(model.Selection.OffsetFromNow);
        string step = model.StepMinutes == 60 ? "1h" : $"{model.StepMinutes}m";
        _earlier.Content = "−" + step;
        _later.Content = "+" + step;
        ToolTipService.SetToolTip(_earlier, $"{step} earlier");
        ToolTipService.SetToolTip(_later, $"{step} later");
    }

    private void GoToPage(int page, bool animate, int direction = 0)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (direction == 0) direction = Math.Sign(page - _pageIndex);
        _pageIndex = page;
        Render(ChangeKind.Locations);
        if (animate && direction != 0 && _host.AnimationsEnabled) SlideIn(direction);
        else ResetTranslation();
    }

    private void SlideIn(int direction)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        var comp = visual.Compositor;
        var anim = comp.CreateVector3KeyFrameAnimation();
        var ease = comp.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));
        anim.InsertKeyFrame(0f, new System.Numerics.Vector3(direction * 40f, 0, 0));
        anim.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
        anim.Duration = TimeSpan.FromMilliseconds(220);
        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Translation", anim);
        visual.StartAnimation("Opacity", fade);
    }

    private void ResetTranslation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
        visual.Opacity = 1;
    }

    private void SetDragOffset(double dx)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3((float)dx, 0, 0));
    }

    // swipe / click handling on the rows
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_viewport);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _pointerDown = true;
        _swiping = false;
        _downX = pt.Position.X;
        _downY = pt.Position.Y;
        _downRow = RowAt(_downY);
        _viewport.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        double dx = e.GetCurrentPoint(_viewport).Position.X - _downX;
        if (!_swiping && Math.Abs(dx) > 8 && PageCount > 1) _swiping = true;
        if (_swiping)
        {
            // resist at the ends
            bool edge = (dx > 0 && _pageIndex == 0) || (dx < 0 && _pageIndex == PageCount - 1);
            SetDragOffset(edge ? dx / 4 : dx);
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _viewport.ReleasePointerCapture(e.Pointer);
        double dx = e.GetCurrentPoint(_viewport).Position.X - _downX;
        if (_swiping)
        {
            _swiping = false;
            int target = _pageIndex + (dx < -40 ? 1 : dx > 40 ? -1 : 0);
            target = Math.Clamp(target, 0, PageCount - 1);
            if (target != _pageIndex) GoToPage(target, animate: true, direction: target > _pageIndex ? 1 : -1);
            else ResetTranslation();
            return;
        }
        _downRow?.Activate();
    }

    private void CancelSwipe()
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _swiping = false;
        ResetTranslation();
    }

    private CompactRow? RowAt(double y)
    {
        int i = (int)(y / _rowHeight);
        return i >= 0 && i < PageSize && _rows[i].Visibility == Visibility.Visible ? _rows[i] : null;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(_viewport).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        if (props.IsHorizontalMouseWheel)
            GoToPage(_pageIndex + Math.Sign(delta), animate: true);
        else
            _host.Model.StepTime(-Math.Sign(delta));
        e.Handled = true;
    }

    public void SetTextScale(double scale)
    {
        scale = Math.Max(1, scale);
        _rowHeight = RowHeight * scale;
        foreach (var r in _rows) r.Height = _rowHeight;
        _stripRow.Height = new GridLength(StripHeight * scale);
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        foreach (var b in new[] { _shift, _earlier, _later }) Ui.StyleSubtle(b, p);
        _shift.Foreground = Ui.Brush(p.AccentText);
        _shift.Resources["ButtonForeground"] = Ui.Brush(p.AccentText);
        _shift.Resources["ButtonForegroundPointerOver"] = Ui.Brush(p.AccentText);
        _shiftText.Foreground = Ui.Brush(p.AccentText);
        _earlier.Foreground = Ui.Brush(p.TextSecondary);
        _later.Foreground = Ui.Brush(p.TextSecondary);
        foreach (var r in _rows) r.ApplyTheme(p);
    }
}
