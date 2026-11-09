using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NodaTime;
using WorldTime.Core;
using WorldTime.Core.Catalog;

namespace WorldTimeFlyout.Views;

/// <summary>
/// Offline city search. Typing needs keyboard focus, so opening search
/// activates the panel (the native calendar closes; the panel stays).
/// </summary>
public sealed partial class SearchView : Grid
{
    private static Task<CityCatalog>? s_catalog;

    private readonly IPanelHost _host;
    private readonly TextBox _box;
    private readonly Button _close;
    private readonly StackPanel _results;
    private readonly TextBlock _status;
    private readonly List<ResultRow> _rows = [];
    private Palette? _palette;
    private int _selected = -1;
    private int _generation;
    private IReadOnlyList<SearchResult> _current = [];

    public SearchView(IPanelHost host)
    {
        _host = host;
        Padding = new Thickness(8, 8, 8, 8);
        RowDefinitions.Add(Ui.RowAuto());
        RowDefinitions.Add(Ui.RowAuto());
        RowDefinitions.Add(Ui.RowStar());

        var top = new Grid { ColumnSpacing = 4 };
        top.ColumnDefinitions.Add(Ui.ColStar());
        top.ColumnDefinitions.Add(Ui.ColAuto());
        _box = new TextBox
        {
            PlaceholderText = "Search city, country or time zone",
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_box, "Search for a city, country or time zone");
        _box.TextChanged += (_, _) => Query();
        _box.KeyDown += OnKey;
        _close = Ui.SubtleButton(Ui.Icon(Glyph.Cancel, 12), "Close search", (_, _) => _host.EndSearch());
        top.Children.Add(_box.At(0, 0));
        top.Children.Add(_close.At(0, 1));
        Children.Add(top.At(0));

        _status = Ui.Text("", 12);
        _status.Margin = new Thickness(8, 10, 8, 4);
        _status.TextWrapping = TextWrapping.Wrap;
        Children.Add(_status.At(1));

        _results = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        var scroll = new ScrollViewer
        {
            Content = _results,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        Children.Add(scroll.At(2));
    }

    public static Task<CityCatalog> Catalog => s_catalog ??= Task.Run(CityCatalog.LoadEmbedded);

    public void Open()
    {
        _ = Catalog; // start loading
        _box.Text = "";
        _status.Text = "Type a city like Tokyo, a country like Armenia, a zone like America/New_York, or EST, UTC+4.";
        _box.Focus(FocusState.Programmatic);
    }

    public void Reset()
    {
        _box.Text = "";
        _results.Children.Clear();
        _rows.Clear();
        _current = [];
    }

    public void RefreshTimes()
    {
        for (int i = 0; i < _rows.Count && i < _current.Count; i++) _rows[i].Update(_current[i], _host.Model, i == _selected);
    }

    private async void Query()
    {
        var text = _box.Text;
        int gen = ++_generation;
        if (string.IsNullOrWhiteSpace(text))
        {
            Show([]);
            _status.Visibility = Visibility.Visible;
            return;
        }
        await Task.Delay(60);
        if (gen != _generation) return;
        CityCatalog catalog;
        try
        {
            if (!Catalog.IsCompleted) _status.Text = "Loading cities…";
            catalog = await Catalog;
        }
        catch (Exception e)
        {
            _status.Text = "The city list could not be loaded.";
            DiagLog.Write($"catalog load failed: {e}");
            return;
        }
        var results = await Task.Run(() => catalog.Search(text, 12));
        if (gen != _generation) return;
        Show(results);
        _status.Text = results.Count == 0 ? "No matches. Try another spelling, a country, or an offset like UTC+4." : "";
        _status.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Show(IReadOnlyList<SearchResult> results)
    {
        _current = results;
        _selected = results.Count > 0 ? 0 : -1;
        while (_rows.Count < results.Count)
        {
            var row = new ResultRow(this);
            if (_palette is not null) row.ApplyTheme(_palette);
            _rows.Add(row);
            _results.Children.Add(row);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Visibility = i < results.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < results.Count) _rows[i].Update(results[i], _host.Model, i == _selected);
        }
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                Move(1); e.Handled = true; break;
            case Windows.System.VirtualKey.Up:
                Move(-1); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter:
                if (_selected >= 0 && _selected < _current.Count) Pick(_current[_selected]);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                _host.EndSearch(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_current.Count == 0) return;
        _selected = Math.Clamp(_selected + delta, 0, _current.Count - 1);
        for (int i = 0; i < _current.Count; i++) _rows[i].Update(_current[i], _host.Model, i == _selected);
        _rows[_selected].StartBringIntoView();
    }

    internal void Pick(SearchResult r)
    {
        _host.Model.Add(r);
        _host.EndSearch();
    }

    public void ApplyTheme(Palette p)
    {
        _palette = p;
        Ui.StyleSubtle(_close, p);
        _status.Foreground = Ui.Brush(p.TextSecondary);
        foreach (var r in _rows) r.ApplyTheme(p);
    }

    /// <summary>One search result: name and country, current local time and UTC offset.</summary>
    private sealed partial class ResultRow : Grid
    {
        private readonly SearchView _owner;
        private readonly TextBlock _name = Ui.Text("", 14);
        private readonly TextBlock _detail = Ui.Text("", 12);
        private readonly TextBlock _time = Ui.Text("", 14);
        private readonly TextBlock _offset = Ui.Text("", 12);
        private SearchResult? _result;
        private bool _hover, _selected;
        private Palette? _palette;

        public ResultRow(SearchView owner)
        {
            _owner = owner;
            Height = 48;
            Padding = new Thickness(12, 0, 12, 0);
            CornerRadius = new CornerRadius(4);
            Background = Ui.Transparent;
            ColumnDefinitions.Add(Ui.ColStar());
            ColumnDefinitions.Add(Ui.ColAuto());
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_name);
            left.Children.Add(_detail);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _time.HorizontalAlignment = HorizontalAlignment.Right;
            _offset.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(_time);
            right.Children.Add(_offset);
            Children.Add(left.At(0, 0));
            Children.Add(right.At(0, 1));
            PointerEntered += (_, _) => { _hover = true; Paint(); };
            PointerExited += (_, _) => { _hover = false; Paint(); };
            Tapped += (_, _) => { if (_result is not null) _owner.Pick(_result); };
        }

        public void Update(SearchResult r, AppModel model, bool selected)
        {
            _result = r;
            _selected = selected;
            _name.Text = r.Name;
            _detail.Text = r.Detail;
            var zone = Zones.Get(r.ZoneId);
            var zoned = model.Selection.Selected.InZone(zone);
            _time.Text = model.Format.Time(zoned.LocalDateTime);
            _offset.Text = TimeFormat.UtcOffset(zoned.Offset);
            AutomationProperties.SetName(this, $"{r.Name}, {r.Detail}, {_time.Text}");
            Paint();
        }

        public void ApplyTheme(Palette p)
        {
            _palette = p;
            Paint();
        }

        private void Paint()
        {
            if (_palette is not { } p) return;
            Background = _hover || _selected ? Ui.Brush(p.SubtleHover) : Ui.Transparent;
            _name.Foreground = Ui.Brush(p.TextPrimary);
            _detail.Foreground = Ui.Brush(p.TextSecondary);
            _time.Foreground = Ui.Brush(p.TextPrimary);
            _offset.Foreground = Ui.Brush(p.TextSecondary);
        }
    }
}
