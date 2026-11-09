using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace WorldTimeFlyout.Views;

/// <summary>Segoe Fluent Icons code points used by the app.</summary>
public static class Glyph
{
    public const string Add = "";
    public const string More = "";
    public const string Copy = "";
    public const string Check = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string Search = "";
    public const string Cancel = "";
    public const string Delete = "";
    public const string Up = "";
    public const string Down = "";
    public const string Pin = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Clock = "";
    public const string Reset = "";      // "UpdateRestore"
    public const string Calendar = "";
    public const string Settings = "";
    public const string Globe = "";
    public const string Back = "";
}

/// <summary>Builders for the small set of visuals the panel uses.</summary>
public static class Ui
{
    public static SolidColorBrush Brush(Color c) => new(c);

    public static readonly SolidColorBrush Transparent = new(Color.FromArgb(0, 255, 255, 255));

    public static TextBlock Text(string text = "", double size = 14, bool semibold = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = true,
        };
        if (semibold) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    public static FontIcon Icon(string glyph, double size = 16) => new()
    {
        Glyph = glyph,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>
    /// A transparent, Windows-11-style icon or text button. It never takes
    /// keyboard focus on click, so clicking it does not pull focus away from
    /// the taskbar calendar.
    /// </summary>
    public static Button SubtleButton(object content, string label, RoutedEventHandler onClick, double size = 32)
    {
        var b = new Button
        {
            Content = content,
            MinWidth = size,
            Height = size,
            Padding = content is string ? new Thickness(10, 0, 10, 0) : new Thickness(0),
            AllowFocusOnInteraction = false,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
        };
        if (content is not string) b.Width = size;
        ToolTipService.SetToolTip(b, label);
        AutomationProperties.SetName(b, label);
        b.Click += onClick;
        return b;
    }

    /// <summary>Lightweight styling so the button is transparent until hovered.</summary>
    public static void StyleSubtle(Button b, Palette p)
    {
        var hover = Brush(p.SubtleHover);
        var pressed = Brush(p.SubtlePressed);
        var fg = Brush(p.TextPrimary);
        var fgPressed = Brush(p.TextSecondary);
        var fgDisabled = Brush(p.TextDisabled);
        b.Resources["ButtonBackground"] = Transparent;
        b.Resources["ButtonBackgroundPointerOver"] = hover;
        b.Resources["ButtonBackgroundPressed"] = pressed;
        b.Resources["ButtonBackgroundDisabled"] = Transparent;
        b.Resources["ButtonBorderBrush"] = Transparent;
        b.Resources["ButtonBorderBrushPointerOver"] = Transparent;
        b.Resources["ButtonBorderBrushPressed"] = Transparent;
        b.Resources["ButtonBorderBrushDisabled"] = Transparent;
        b.Resources["ButtonForeground"] = fg;
        b.Resources["ButtonForegroundPointerOver"] = fg;
        b.Resources["ButtonForegroundPressed"] = fgPressed;
        b.Resources["ButtonForegroundDisabled"] = fgDisabled;
        b.Background = Transparent;
        b.Foreground = fg;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static ColumnDefinition Col(double w) => new() { Width = new GridLength(w) };
    public static ColumnDefinition ColStar(double w = 1) => new() { Width = new GridLength(w, GridUnitType.Star) };
    public static ColumnDefinition ColAuto() => new() { Width = GridLength.Auto };
    public static RowDefinition Row(double h) => new() { Height = new GridLength(h) };
    public static RowDefinition RowStar(double h = 1) => new() { Height = new GridLength(h, GridUnitType.Star) };
    public static RowDefinition RowAuto() => new() { Height = GridLength.Auto };

    public static T At<T>(this T el, int row = 0, int col = 0, int colSpan = 1, int rowSpan = 1) where T : FrameworkElement
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        if (colSpan > 1) Grid.SetColumnSpan(el, colSpan);
        if (rowSpan > 1) Grid.SetRowSpan(el, rowSpan);
        return el;
    }
}
