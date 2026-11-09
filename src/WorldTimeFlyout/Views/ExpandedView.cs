using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// Full view, shown next to the expanded calendar:
/// header (title, copy all, add, more), scrolling city list, the selected
/// time with previous/next day buttons, and the timeline. The date opens an
/// inline month view (no popup, so the calendar beside it stays open).
/// </summary>
public sealed partial class ExpandedView : Grid
{
    private readonly IPanelHost _host;
    private readonly TextBlock _title;
    private readonly Button _copyAll, _add, _more;
    private readonly FontIcon _copyIcon;
    private readonly InfoBar _welcome;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list;
    private readonly StackPanel _empty;
    private readonly Button _prev, _next, _chip, _now;
    private readonly TextBlock _chipText, _chipSub;
    private readonly TimeRuler _ruler;
    private readonly Grid _timeArea;
    private readonly SearchView _search;
    private readonly Grid _datePage;
    private readonly CalendarView _dates;
    private readonly Button _today, _cancelDate;
    private bool _pickingDate, _settingDate;
    private readonly List<LocationRow> _rows = [];
    private Palette? _palette;
    private bool _searching;

    public ExpandedView(IPanelHost host)
    {
        _host = host;
        RowDefinitions.Add(Ui.Row(48));   // header
        RowDefinitions.Add(Ui.RowAuto()); // welcome
        RowDefinitions.Add(Ui.RowStar()); // list / search
        RowDefinitions.Add(Ui.RowAuto()); // time area

        // header
        var header = new Grid { Padding = new Thickness(16, 0, 8, 0) };
        header.ColumnDefinitions.Add(Ui.ColStar());
        header.ColumnDefinitions.Add(Ui.ColAuto());
        header.ColumnDefinitions.Add(Ui.ColAuto());
        header.ColumnDefinitions.Add(Ui.ColAuto());
        _title = Ui.Text("World time", 14, semibold: true);
        AutomationProperties.SetHeadingLevel(_title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        _copyIcon = Ui.Icon(Glyph.Copy, 16);
        _copyAll = Ui.SubtleButton(_copyIcon, "Copy all times", (_, _) => CopyAll());
        _add = Ui.SubtleButton(Ui.Icon(Glyph.Add, 16), "Add a city", (_, _) => _host.BeginSearch());
        _more = Ui.SubtleButton(Ui.Icon(Glyph.More, 16), "More options", (_, _) => _host.ShowMoreMenu(_more!));
        _add.ActivatesPanel(host);
        _more.ActivatesPanel(host);
        header.Children.Add(_title.At(0, 0));
        header.Children.Add(_copyAll.At(0, 1));
        header.Children.Add(_add.At(0, 2));
        header.Children.Add(_more.At(0, 3));
        Children.Add(header.At(0));

        _welcome = new InfoBar
        {
            Title = "World time opens with your calendar",
            Message = "Click the clock on the taskbar. Click a time to copy it, drag the timeline to compare.",
            Severity = InfoBarSeverity.Informational,
            IsOpen = false,
            IsClosable = true,
            Margin = new Thickness(8, 0, 8, 6),
        };
        _welcome.Closed += (_, _) => _host.Model.SetFlag(s => s.WelcomeShown = true);
        Children.Add(_welcome.At(1));

        // list
        _list = new StackPanel { Spacing = 2, Padding = new Thickness(4, 0, 4, 4) };
        _scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0),
        };
        Children.Add(_scroll.At(2));

        _empty = new StackPanel
        {
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var emptyText = Ui.Text("Add a city to compare times", 14);
        emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        var emptyButton = new Button { Content = "Add city", HorizontalAlignment = HorizontalAlignment.Center, AllowFocusOnInteraction = false };
        emptyButton.Click += (_, _) => _host.BeginSearch();
        emptyButton.ActivatesPanel(host);
        _empty.Children.Add(emptyText);
        _empty.Children.Add(emptyButton);
        Children.Add(_empty.At(2));

        _search = new SearchView(host) { Visibility = Visibility.Collapsed };
        Children.Add(_search.At(0, rowSpan: 4));

        // time area: [<] [selected time chip] [now] [>] over the ruler
        _timeArea = new Grid { Padding = new Thickness(8, 4, 8, 8) };
        _timeArea.RowDefinitions.Add(Ui.Row(40));
        _timeArea.RowDefinitions.Add(Ui.RowAuto());
        var bar = new Grid();
        bar.ColumnDefinitions.Add(Ui.ColAuto());
        bar.ColumnDefinitions.Add(Ui.ColStar());
        bar.ColumnDefinitions.Add(Ui.ColAuto());
        bar.ColumnDefinitions.Add(Ui.ColAuto());
        _prev = Ui.SubtleButton(Ui.Icon(Glyph.ChevronLeft, 12), "Previous day", (_, _) => _host.Model.StepDays(-1));
        _next = Ui.SubtleButton(Ui.Icon(Glyph.ChevronRight, 12), "Next day", (_, _) => _host.Model.StepDays(1));
        _chipText = Ui.Text("", 14, semibold: true);
        _chipText.HorizontalAlignment = HorizontalAlignment.Center;
        _chipSub = Ui.Text("", 12);
        _chipSub.HorizontalAlignment = HorizontalAlignment.Center;
        var chipStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        chipStack.Children.Add(_chipText);
        chipStack.Children.Add(_chipSub);
        _chip = Ui.SubtleButton(chipStack, "Pick a date", (_, _) => OpenDatePicker(), 36);
        _chip.Width = double.NaN;
        _chip.HorizontalAlignment = HorizontalAlignment.Stretch;
        _chip.HorizontalContentAlignment = HorizontalAlignment.Center;
        _chip.Padding = new Thickness(8, 0, 8, 0);
        _now = Ui.SubtleButton("Now", "Back to the current time", (_, _) => _host.Model.ResetToNow(), 28);
        _now.FontSize = 12;
        _now.Margin = new Thickness(4, 0, 4, 0);
        bar.Children.Add(_prev.At(0, 0));
        bar.Children.Add(_chip.At(0, 1));
        bar.Children.Add(_now.At(0, 2));
        bar.Children.Add(_next.At(0, 3));
        _ruler = new TimeRuler(host) { Margin = new Thickness(0, 2, 0, 0) };
        AutomationProperties.SetAutomationId(_prev, "PreviousDay");
        AutomationProperties.SetAutomationId(_next, "NextDay");
        AutomationProperties.SetAutomationId(_chip, "DateChip");
        AutomationProperties.SetAutomationId(_now, "NowButton");
        _timeArea.Children.Add(bar.At(0));
        _timeArea.Children.Add(_ruler.At(1));
        Children.Add(_timeArea.At(3));

        // inline date picker (replaces the list and the time area while open)
        _datePage = new Grid { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 0, 8, 8) };
        _datePage.RowDefinitions.Add(Ui.RowStar());
        _datePage.RowDefinitions.Add(Ui.Row(36));
        _dates = new CalendarView
        {
            SelectionMode = CalendarViewSelectionMode.Single,
            IsTodayHighlighted = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Ui.Transparent,
        };
        _dates.SelectedDatesChanged += OnDatePicked;
        AutomationProperties.SetName(_dates, "Pick a date");
        AutomationProperties.SetAutomationId(_dates, "DatePicker");
        var dateBar = new Grid();
        dateBar.ColumnDefinitions.Add(Ui.ColStar());
        dateBar.ColumnDefinitions.Add(Ui.ColAuto());
        _today = Ui.SubtleButton("Today", "Back to the current time", (_, _) => { _host.Model.ResetToNow(); CloseDatePicker(); }, 32);
        _today.HorizontalAlignment = HorizontalAlignment.Left;
        _cancelDate = Ui.SubtleButton("Cancel", "Close the date picker", (_, _) => CloseDatePicker(), 32);
        AutomationProperties.SetAutomationId(_today, "DateToday");
        AutomationProperties.SetAutomationId(_cancelDate, "DateCancel");
        dateBar.Children.Add(_today.At(0, 0));
        dateBar.Children.Add(_cancelDate.At(0, 1));
        _datePage.Children.Add(_dates.At(0));
        _datePage.Children.Add(dateBar.At(1));
        Children.Add(_datePage.At(2, rowSpan: 2));
    }

    public bool IsPickingDate => _pickingDate;

    public void OpenDatePicker()
    {
        if (_searching || _host.Model.ReferenceReading is not { } r) return;
        try
        {
            _pickingDate = true;
            _settingDate = true;
            var date = r.Local.Date;
            var dto = new DateTimeOffset(date.Year, date.Month, date.Day, 12, 0, 0, TimeSpan.Zero);
            _dates.SelectedDates.Clear();
            _dates.SelectedDates.Add(dto);
            _dates.SetDisplayDate(dto);
        }
        catch (Exception e)
        {
            DiagLog.Write($"date picker: {e}");
        }
        finally
        {
            _settingDate = false;
        }
        _title.Text = "Pick a date";
        _scroll.Visibility = Visibility.Collapsed;
        _empty.Visibility = Visibility.Collapsed;
        _timeArea.Visibility = Visibility.Collapsed;
        _datePage.Visibility = Visibility.Visible;
        DiagLog.Detail("date picker opened");
    }

    public void CloseDatePicker()
    {
        if (!_pickingDate) return;
        _pickingDate = false;
        _title.Text = "World time";
        _datePage.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        _timeArea.Visibility = Visibility.Visible;
        Render(ChangeKind.Locations);
    }

    private void OnDatePicked(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs e)
    {
        if (_settingDate || e.AddedDates.Count == 0) return;
        var d = e.AddedDates[0];
        _host.Model.SetDate(new NodaTime.LocalDate(d.Year, d.Month, d.Day));
        CloseDatePicker();
    }

    /// <summary>Minimum height (DIPs) for header, a few rows and the timeline.</summary>
    public const double RequiredHeight = 48 + 3 * 54 + 40 + 46 + 14;

    public bool IsSearching => _searching;

    public void ShowWelcome(bool show) => _welcome.IsOpen = show;

    public void SetSearching(bool on)
    {
        _searching = on;
        _search.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var rest = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) CloseDatePicker();
        foreach (var child in Children)
            if (child != _search && child != _welcome && child != _empty && child != _datePage) ((UIElement)child).Visibility = rest;
        if (on) _search.Open();
        else
        {
            _search.Reset();
            Render(ChangeKind.Locations);
        }
    }

    private void CopyAll()
    {
        var model = _host.Model;
        if (!_host.Copy(model.CopyText(model.Readings, withDate: false))) return;
        _copyIcon.Glyph = Glyph.Check;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1100);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _copyIcon.Glyph = Glyph.Copy;
        timer.Start();
    }

    public void Render(ChangeKind kind)
    {
        var model = _host.Model;
        var readings = model.Readings;
        if (_rows.Count != readings.Count)
        {
            while (_rows.Count < readings.Count)
            {
                var row = new LocationRow(_host);
                if (_palette is not null) row.ApplyTheme(_palette);
                _rows.Add(row);
                _list.Children.Add(row);
            }
            while (_rows.Count > readings.Count)
            {
                _list.Children.Remove(_rows[^1]);
                _rows.RemoveAt(_rows.Count - 1);
            }
        }
        bool live = model.Selection.IsLive;
        for (int i = 0; i < readings.Count; i++) _rows[i].Update(readings[i], model.Format, live);
        _empty.Visibility = readings.Count == 0 && !_searching && !_pickingDate ? Visibility.Visible : Visibility.Collapsed;

        // selected time, in the reference city
        if (model.ReferenceReading is { } r)
        {
            _chipText.Text = $"{model.Format.ShortDate(r.Local.Date)}, {model.Format.Time(r.Local)}";
            _chipSub.Text = live ? r.Location.Name : TimeFormat.ShiftFromNow(model.Shift);
            AutomationProperties.SetName(_chip, live
                ? $"Selected time {_chipText.Text} in {r.Location.Name}. Pick a date."
                : $"Selected time {_chipText.Text}, {_chipSub.Text} from now, in {r.Location.Name}. Pick a date.");
        }
        _now.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _ruler.Sync();
        if (kind == ChangeKind.Locations) _search.RefreshTimes();
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        foreach (var b in new[] { _copyAll, _add, _more, _prev, _next, _chip, _now, _today, _cancelDate }) Ui.StyleSubtle(b, p);
        _today.Foreground = Ui.Brush(p.AccentText);
        _today.Resources["ButtonForeground"] = Ui.Brush(p.AccentText);
        _today.Resources["ButtonForegroundPointerOver"] = Ui.Brush(p.AccentText);
        _title.Foreground = Ui.Brush(p.TextPrimary);
        _chipText.Foreground = Ui.Brush(p.TextPrimary);
        _chipSub.Foreground = Ui.Brush(p.TextSecondary);
        _now.Foreground = Ui.Brush(p.AccentText);
        _now.Resources["ButtonForeground"] = Ui.Brush(p.AccentText);
        _now.Resources["ButtonForegroundPointerOver"] = Ui.Brush(p.AccentText);
        foreach (var r in _rows) r.ApplyTheme(p);
        _ruler.ApplyTheme(p);
        _search.ApplyTheme(p);
    }

    /// <summary>Scrolls so the given city is visible (after adding it).</summary>
    public void BringIntoView(string locationId)
    {
        var row = _rows.FirstOrDefault(r => r.LocationId == locationId);
        row?.StartBringIntoView();
    }
}
