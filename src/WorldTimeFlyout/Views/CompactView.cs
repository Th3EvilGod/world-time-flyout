using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// Compact view for the collapsed calendar: three cities as tiles side by side,
/// with ‹ › on the sides to cycle through the rest (or swipe / sideways wheel),
/// and a strip below with the shift from now, page pips and time steps.
/// Click a tile to copy its time; right-click for more; the mouse wheel moves
/// the time.
/// </summary>
public sealed partial class CompactView : Grid
{
    public const int PageSize = 3;
    public const double TileHeight = 66;
    public const double StripHeight = 26;
    private const double PadTop = 6, PadBottom = 2;

    /// <summary>
    /// Height for the tiles plus the strip (DIPs). At 100% text size this is
    /// exactly the collapsed calendar's 100 px; larger text sizes grow it.
    /// </summary>
    public static double RequiredHeightFor(double textScale) =>
        PadTop + (TileHeight + StripHeight) * Math.Max(1, textScale) + PadBottom;

    private readonly IPanelHost _host;
    private readonly RowDefinition _tileRow, _stripRow;
    private readonly Button _prevPage, _nextPage;
    private readonly Grid _viewport;
    private readonly Grid _page;
    private readonly CompactTile[] _tiles;
    private readonly PipsPager _pips;
    private readonly Button _shift, _earlier, _later;
    private readonly TextBlock _shiftText;
    private int _pageIndex;
    private int _columns = PageSize;

    private bool _pointerDown, _swiping;
    private double _downX;
    private CompactTile? _downTile;

    public CompactView(IPanelHost host)
    {
        _host = host;
        Padding = new Thickness(6, PadTop, 6, PadBottom);
        _tileRow = Ui.Row(TileHeight);
        _stripRow = Ui.Row(StripHeight);
        RowDefinitions.Add(_tileRow);
        RowDefinitions.Add(_stripRow);
        RowDefinitions.Add(Ui.RowStar());

        // tiles: [‹] [tile][tile][tile] [›]
        var top = new Grid();
        top.ColumnDefinitions.Add(Ui.ColAuto());
        top.ColumnDefinitions.Add(Ui.ColStar());
        top.ColumnDefinitions.Add(Ui.ColAuto());
        _prevPage = SideButton(Glyph.ChevronLeft, "Previous cities", -1);
        _nextPage = SideButton(Glyph.ChevronRight, "More cities", 1);
        AutomationProperties.SetAutomationId(_prevPage, "PrevPage");
        AutomationProperties.SetAutomationId(_nextPage, "NextPage");
        _prevPage.Margin = new Thickness(0, 0, 2, 0);
        _nextPage.Margin = new Thickness(2, 0, 0, 0);

        _page = new Grid { ColumnSpacing = 4 };
        _tiles = new CompactTile[PageSize];
        for (int i = 0; i < PageSize; i++)
        {
            _page.ColumnDefinitions.Add(Ui.ColStar());
            _tiles[i] = new CompactTile(host);
            AutomationProperties.SetAutomationId(_tiles[i], $"Tile{i}");
            _page.Children.Add(_tiles[i].At(0, i));
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
        top.Children.Add(_prevPage.At(0, 0));
        top.Children.Add(_viewport.At(0, 1));
        top.Children.Add(_nextPage.At(0, 2));
        Children.Add(top.At(0));

        // strip: [shift from now ×]   (pips)   [−30m] [+30m]
        var strip = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        strip.ColumnDefinitions.Add(Ui.ColStar());
        strip.ColumnDefinitions.Add(Ui.ColAuto());
        strip.ColumnDefinitions.Add(Ui.ColStar());
        _shiftText = Ui.Text("", 12, semibold: true);
        var shiftContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        shiftContent.Children.Add(_shiftText);
        shiftContent.Children.Add(Ui.Icon(Glyph.Cancel, 9));
        _shift = Ui.SubtleButton(shiftContent, "Back to the current time", (_, _) => _host.Model.ResetToNow(), 24);
        _shift.Width = double.NaN;
        _shift.Padding = new Thickness(8, 0, 8, 0);
        _shift.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(_shift, "ShiftChip");

        _pips = new PipsPager
        {
            MaxVisiblePips = 5,
            PreviousButtonVisibility = PipsPagerButtonVisibility.Collapsed,
            NextButtonVisibility = PipsPagerButtonVisibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            AllowFocusOnInteraction = false,
        };
        AutomationProperties.SetName(_pips, "Pages of cities");
        _pips.SelectedIndexChanged += (_, _) =>
        {
            if (_pips.SelectedPageIndex != _pageIndex) GoToPage(_pips.SelectedPageIndex, animate: true);
        };

        var steps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _earlier = Ui.SubtleButton("", "Earlier", (_, _) => _host.Model.StepTime(-1), 24);
        _later = Ui.SubtleButton("", "Later", (_, _) => _host.Model.StepTime(1), 24);
        AutomationProperties.SetAutomationId(_earlier, "Earlier");
        AutomationProperties.SetAutomationId(_later, "Later");
        foreach (var b in new[] { _earlier, _later })
        {
            b.FontSize = 12;
            b.Padding = new Thickness(7, 0, 7, 0);
            steps.Children.Add(b);
        }
        strip.Children.Add(_shift.At(0, 0));
        strip.Children.Add(_pips.At(0, 1));
        strip.Children.Add(steps.At(0, 2));
        Children.Add(strip.At(1));
    }

    private Button SideButton(string glyph, string label, int direction)
    {
        var b = Ui.SubtleButton(Ui.Icon(glyph, 10), label, (_, _) => Cycle(direction), 22);
        b.Height = double.NaN;
        b.VerticalAlignment = VerticalAlignment.Stretch;
        return b;
    }

    private int Count => _host.Model.Readings.Count;
    private int PageCount => Math.Max(1, (int)Math.Ceiling(Count / (double)PageSize));

    public void Render(ChangeKind kind)
    {
        var model = _host.Model;
        var readings = model.Readings;
        if (_pageIndex >= PageCount) _pageIndex = PageCount - 1;

        // up to three cities fill the width; more keep three columns and page
        int columns = Math.Clamp(readings.Count, 1, PageSize);
        if (columns != _columns)
        {
            _columns = columns;
            for (int i = 0; i < PageSize; i++)
                _page.ColumnDefinitions[i].Width = i < columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        bool paging = readings.Count > PageSize;
        _prevPage.Visibility = _nextPage.Visibility = paging ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < PageSize; i++)
        {
            int idx = _pageIndex * PageSize + i;
            bool has = idx < readings.Count && i < columns;
            _tiles[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has) _tiles[i].Update(readings[idx], model.Format);
        }
        _pips.NumberOfPages = PageCount;
        _pips.SelectedPageIndex = _pageIndex;
        _pips.Visibility = paging ? Visibility.Visible : Visibility.Collapsed;

        bool live = model.Selection.IsLive;
        _shift.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _shiftText.Text = TimeFormat.ShiftFromNow(model.Shift);
        string step = model.StepMinutes == 60 ? "1h" : $"{model.StepMinutes}m";
        _earlier.Content = "−" + step;
        _later.Content = "+" + step;
        ToolTipService.SetToolTip(_earlier, $"{step} earlier");
        ToolTipService.SetToolTip(_later, $"{step} later");
    }

    /// <summary>‹ › wrap around: after the last page comes the first.</summary>
    private void Cycle(int direction)
    {
        if (PageCount <= 1) return;
        int target = ((_pageIndex + direction) % PageCount + PageCount) % PageCount;
        GoToPage(target, animate: true, direction: direction);
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

    /// <summary>New page slides in from the side it came from (Windows 11 entrance curve, 250 ms).</summary>
    private void SlideIn(int direction)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
        var anim = comp.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3(direction * 48f, 0, 0));
        anim.InsertKeyFrame(1f, Vector3.Zero, ease);
        anim.Duration = TimeSpan.FromMilliseconds(250);
        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, comp.CreateLinearEasingFunction());
        fade.Duration = TimeSpan.FromMilliseconds(167);
        visual.StartAnimation("Translation", anim);
        visual.StartAnimation("Opacity", fade);
    }

    private void ResetTranslation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Opacity = 1;
    }

    private void SetDragOffset(double dx)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_page);
        visual.Properties.InsertVector3("Translation", new Vector3((float)dx, 0, 0));
    }

    // swipe / click handling on the tiles
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_viewport);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _pointerDown = true;
        _swiping = false;
        _downX = pt.Position.X;
        _downTile = TileFrom(e.OriginalSource as DependencyObject);
        _downTile?.SetPressed(true);
        _viewport.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        double dx = e.GetCurrentPoint(_viewport).Position.X - _downX;
        if (!_swiping && Math.Abs(dx) > 8 && PageCount > 1)
        {
            _swiping = true;
            _downTile?.SetPressed(false);
        }
        if (_swiping) SetDragOffset(dx);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _viewport.ReleasePointerCapture(e.Pointer);
        _downTile?.SetPressed(false);
        double dx = e.GetCurrentPoint(_viewport).Position.X - _downX;
        if (_swiping)
        {
            _swiping = false;
            if (Math.Abs(dx) > 40) Cycle(dx < 0 ? 1 : -1);
            else ResetTranslation();
            return;
        }
        _downTile?.Activate();
    }

    private void CancelSwipe()
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _swiping = false;
        _downTile?.SetPressed(false);
        ResetTranslation();
    }

    private static CompactTile? TileFrom(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is CompactTile t) return t.Visibility == Visibility.Visible ? t : null;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(_viewport).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        if (props.IsHorizontalMouseWheel) Cycle(Math.Sign(delta));
        else _host.Model.StepTime(-Math.Sign(delta));
        e.Handled = true;
    }

    public void SetTextScale(double scale)
    {
        scale = Math.Max(1, scale);
        _tileRow.Height = new GridLength(TileHeight * scale);
        _stripRow.Height = new GridLength(StripHeight * scale);
    }

    public void ApplyTheme(Palette p)
    {
        foreach (var b in new[] { _shift, _earlier, _later, _prevPage, _nextPage }) Ui.StyleSubtle(b, p);
        _shift.Foreground = Ui.Brush(p.AccentText);
        _shift.Resources["ButtonForeground"] = Ui.Brush(p.AccentText);
        _shift.Resources["ButtonForegroundPointerOver"] = Ui.Brush(p.AccentText);
        _shiftText.Foreground = Ui.Brush(p.AccentText);
        _earlier.Foreground = Ui.Brush(p.TextSecondary);
        _later.Foreground = Ui.Brush(p.TextSecondary);
        _prevPage.Foreground = Ui.Brush(p.TextSecondary);
        _nextPage.Foreground = Ui.Brush(p.TextSecondary);
        foreach (var t in _tiles) t.ApplyTheme(p);
    }
}
