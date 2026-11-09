using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// One city in the expanded list: name and relative day/offset on the left,
/// the time on the right. Click copies "City: time"; right-click opens the
/// menu. The reference city carries the accent pill, like a selected item in
/// Windows 11 lists.
/// </summary>
public sealed partial class LocationRow : Grid
{
    private readonly IPanelHost _host;
    private readonly Border _pill;
    private readonly FontIcon _dayIcon;
    private readonly TextBlock _name;
    private readonly TextBlock _sub;
    private readonly TextBlock _time;
    private readonly FontIcon _copied;
    private LocationReading _reading;
    private bool _hover, _pressed;
    private Palette? _palette;
    private DateTime _copiedUntil;

    public LocationRow(IPanelHost host)
    {
        _host = host;
        Height = 52;
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(12, 0, 12, 0);
        Background = Ui.Transparent;
        ColumnDefinitions.Add(Ui.Col(22));
        ColumnDefinitions.Add(Ui.ColStar());
        ColumnDefinitions.Add(Ui.ColAuto());

        _pill = new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-12, 0, 0, 0),
        };
        _dayIcon = Ui.Icon(Glyph.Sun, 13);
        _dayIcon.HorizontalAlignment = HorizontalAlignment.Left;

        _name = Ui.Text("", 14);
        _sub = Ui.Text("", 12);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        stack.Children.Add(_name);
        stack.Children.Add(_sub);

        _time = Ui.Text("", 20, semibold: true);
        _time.HorizontalAlignment = HorizontalAlignment.Right;
        _time.Margin = new Thickness(8, 0, 0, 2);
        _copied = Ui.Icon(Glyph.Check, 16);
        _copied.HorizontalAlignment = HorizontalAlignment.Right;
        _copied.Visibility = Visibility.Collapsed;

        Children.Add(_pill.At(0, 0));
        Children.Add(_dayIcon.At(0, 0));
        Children.Add(stack.At(0, 1));
        Children.Add(_time.At(0, 2));
        Children.Add(_copied.At(0, 2));

        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = false; _pressed = false; Paint(); };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = true; Paint(); } };
        PointerReleased += (_, _) => { _pressed = false; Paint(); };
        PointerCanceled += (_, _) => { _pressed = false; Paint(); };
        Tapped += OnTapped;
        RightTapped += (_, e) =>
        {
            e.Handled = true;
            _host.ShowRowMenu(_reading, this, e.GetPosition(this));
        };
        ToolTipService.SetToolTip(this, "Click to copy · right-click for more");
    }

    public string LocationId => _reading.Location.Id;

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_host.Copy(_host.Model.CopyText(_reading, withDate: false)))
            ShowCopied();
    }

    public void ShowCopied()
    {
        _copiedUntil = DateTime.UtcNow.AddMilliseconds(1100);
        _time.Visibility = Visibility.Collapsed;
        _copied.Visibility = Visibility.Visible;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow < _copiedUntil) return;
            _time.Visibility = Visibility.Visible;
            _copied.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    public void Update(LocationReading r, TimeFormat f, bool live)
    {
        _reading = r;
        _name.Text = r.Location.Name;
        _name.FontWeight = r.IsReference ? FontWeights.SemiBold : FontWeights.Normal;
        _time.Text = f.Time(r.Local);
        _sub.Text = Subtitle(r, f, live);
        _dayIcon.Glyph = r.IsDaytime ? Glyph.Sun : Glyph.Moon;
        ToolTipService.SetToolTip(_dayIcon, r.IsDaytime ? "Daytime" : "Night");
        AutomationProperties.SetName(this, $"{r.Location.Name}, {_time.Text}, {_sub.Text}");
        Paint();
    }

    internal static string Subtitle(LocationReading r, TimeFormat f, bool live)
    {
        if (r.IsReference)
            return $"{(live ? "Now" : f.ShortDate(r.Local.Date))} · {TimeFormat.UtcOffset(r.Offset)}";
        return $"{TimeFormat.RelativeDay(r.DayDifference)} · {TimeFormat.OffsetDifference(r.DifferenceFromReference)}";
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        Paint();
    }

    private void Paint()
    {
        if (_palette is not { } p) return;
        Background = _pressed ? Ui.Brush(p.SubtlePressed)
            : _hover ? Ui.Brush(p.SubtleHover)
            : _reading.IsReference ? Ui.Brush(p.SubtleHover) : Ui.Transparent;
        _pill.Background = _reading.IsReference ? Ui.Brush(p.Accent) : Ui.Transparent;
        _name.Foreground = Ui.Brush(p.TextPrimary);
        _sub.Foreground = Ui.Brush(p.TextSecondary);
        _time.Foreground = Ui.Brush(p.TextPrimary);
        _dayIcon.Foreground = Ui.Brush(p.TextTertiary);
        _copied.Foreground = Ui.Brush(p.AccentText);
    }
}

/// <summary>
/// One city as a tile in the compact (collapsed calendar) view: name, time and
/// the difference from the reference city. The reference city carries the
/// accent pill along its bottom edge, like a selected tab in Windows 11.
/// </summary>
public sealed partial class CompactTile : Grid
{
    private readonly IPanelHost _host;
    private readonly Border _pill;
    private readonly TextBlock _name;
    private readonly TextBlock _time;
    private readonly TextBlock _sub;
    private readonly FontIcon _copied;
    private LocationReading _reading;
    private bool _hover, _pressed;
    private Palette? _palette;
    private DateTime _copiedUntil;

    public CompactTile(IPanelHost host)
    {
        _host = host;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6, 0, 6, 0);

        _name = Ui.Text("", 12);
        _time = Ui.Text("", 17, semibold: true);
        _sub = Ui.Text("", 11);
        foreach (var t in new[] { _name, _time, _sub })
        {
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextAlignment = TextAlignment.Center;
        }
        _time.Margin = new Thickness(0, -1, 0, 1);
        _copied = Ui.Icon(Glyph.Check, 16);
        _copied.Visibility = Visibility.Collapsed;
        var timeCell = new Grid { Height = 24 };
        timeCell.Children.Add(_time);
        timeCell.Children.Add(_copied);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(_name);
        stack.Children.Add(timeCell);
        stack.Children.Add(_sub);

        _pill = new Border
        {
            Width = 16,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
        };
        Children.Add(stack);
        Children.Add(_pill);

        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = false; _pressed = false; Paint(); };
        RightTapped += (_, e) =>
        {
            e.Handled = true;
            _host.ShowRowMenu(_reading, this, e.GetPosition(this));
        };
        ToolTipService.SetToolTip(this, "Click to copy · right-click for more");
    }

    public void SetPressed(bool pressed)
    {
        _pressed = pressed;
        Paint();
    }

    /// <summary>A click that was not part of a swipe: copy "City: time".</summary>
    public void Activate()
    {
        if (!_host.Copy(_host.Model.CopyText(_reading, withDate: false))) return;
        _copiedUntil = DateTime.UtcNow.AddMilliseconds(1000);
        _time.Visibility = Visibility.Collapsed;
        _copied.Visibility = Visibility.Visible;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1050);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow < _copiedUntil) return;
            _time.Visibility = Visibility.Visible;
            _copied.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    public void Update(LocationReading r, TimeFormat f)
    {
        _reading = r;
        _name.Text = r.Location.Name;
        _time.Text = f.Time(r.Local);
        _sub.Text = Subtitle(r, f);
        AutomationProperties.SetName(this, $"{r.Location.Name}, {_time.Text}, {_sub.Text}");
        Paint();
    }

    /// <summary>"UTC+4" for the reference city, "−9h" or "Tue · +5h" for the others.</summary>
    internal static string Subtitle(LocationReading r, TimeFormat f)
    {
        if (r.IsReference) return TimeFormat.UtcOffset(r.Offset);
        string diff = TimeFormat.OffsetDifference(r.DifferenceFromReference);
        return r.DayDifference == 0 ? diff : $"{f.DayName(r.Local.Date)} · {diff}";
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        Paint();
    }

    private void Paint()
    {
        if (_palette is not { } p) return;
        Background = Ui.Brush(_pressed ? p.TilePressed : _hover ? p.TileHover : p.TileFill);
        BorderBrush = Ui.Brush(p.ControlStroke);
        _pill.Background = _reading.IsReference ? Ui.Brush(p.Accent) : Ui.Transparent;
        _name.Foreground = Ui.Brush(_reading.IsReference ? p.TextPrimary : p.TextSecondary);
        _time.Foreground = Ui.Brush(p.TextPrimary);
        _sub.Foreground = Ui.Brush(p.TextTertiary);
        _copied.Foreground = Ui.Brush(p.AccentText);
    }
}
