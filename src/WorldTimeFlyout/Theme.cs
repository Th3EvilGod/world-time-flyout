using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WorldTimeFlyout;

/// <summary>
/// Colors for custom visuals, taken from the Windows 11 Fluent design tokens.
/// Built-in controls follow the element theme on their own; these cover what
/// the app draws itself so it matches the taskbar flyouts exactly.
/// </summary>
public sealed record Palette(
    bool Dark,
    bool HighContrast,
    Color TextPrimary,
    Color TextSecondary,
    Color TextTertiary,
    Color TextDisabled,
    Color SubtleHover,
    Color SubtlePressed,
    Color ControlFill,
    Color ControlStroke,
    Color Divider,
    Color CardFill,
    Color Accent,
    Color AccentText,
    Color OnAccent,
    Color SolidBackground)
{
    private static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static Palette Create(bool dark, bool highContrast, UISettings? ui)
    {
        Color Accent(UIColorType t, uint fallback)
        {
            try { return ui?.GetColorValue(t) ?? C(fallback); }
            catch { return C(fallback); }
        }

        if (highContrast && ui is not null)
        {
            // Contrast themes: use the system colors directly.
            var fg = ui.UIElementColor(UIElementType.WindowText);
            var bg = ui.UIElementColor(UIElementType.Window);
            var hl = ui.UIElementColor(UIElementType.Highlight);
            var hlText = ui.UIElementColor(UIElementType.HighlightText);
            var gray = ui.UIElementColor(UIElementType.GrayText);
            return new Palette(dark, true, fg, fg, gray, gray, hl, hl, bg, fg, fg, bg, hl, hl, hlText, bg);
        }

        return dark
            ? new Palette(true, false,
                C(0xFFFFFFFF), C(0xC5FFFFFF), C(0x87FFFFFF), C(0x5DFFFFFF),
                C(0x0FFFFFFF), C(0x0AFFFFFF), C(0x0FFFFFFF), C(0x12FFFFFF), C(0x15FFFFFF), C(0x0DFFFFFF),
                Accent(UIColorType.AccentLight2, 0xFF60CDFF), Accent(UIColorType.AccentLight3, 0xFF99EBFF), C(0xFF000000),
                C(0xFF2B2B2B))
            : new Palette(false, false,
                C(0xE4000000), C(0x9E000000), C(0x72000000), C(0x5C000000),
                C(0x09000000), C(0x06000000), C(0xB3FFFFFF), C(0x0F000000), C(0x0F000000), C(0xB3FFFFFF),
                Accent(UIColorType.AccentDark1, 0xFF005FB8), Accent(UIColorType.AccentDark2, 0xFF003E92), C(0xFFFFFFFF),
                C(0xFFF3F3F3));
    }
}

/// <summary>Tracks the taskbar (system) theme, accent color and accessibility settings.</summary>
public sealed class ThemeManager
{
    private readonly UISettings _ui = new();
    private readonly AccessibilitySettings _a11y = new();
    private readonly DispatcherQueue _dq;
    private bool _pending;

    public ThemeManager(DispatcherQueue dq)
    {
        _dq = dq;
        Current = Build();
        // ColorValuesChanged also fires for contrast-theme changes. (AccessibilitySettings
        // events need a CoreWindow and are unavailable to desktop apps; WM_SETTINGCHANGE
        // from the message window covers the rest.)
        try { _ui.ColorValuesChanged += (_, _) => RequestRefresh(); }
        catch (Exception e) { DiagLog.Write($"UISettings.ColorValuesChanged unavailable: {e.Message}"); }
    }

    public Palette Current { get; private set; }

    /// <summary>The taskbar and its flyouts follow the Windows mode, not the app mode.</summary>
    public bool Dark => Current.Dark;

    public bool AnimationsEnabled
    {
        get { try { return _ui.AnimationsEnabled; } catch { return true; } }
    }

    public event Action? Changed;

    private (bool Dark, Color? Accent)? _override;

    /// <summary>Screenshot mode: force a theme and accent instead of following Windows.</summary>
    public void Override(bool dark, Color? accent)
    {
        _override = (dark, accent);
        Current = Build();
        Changed?.Invoke();
    }

    /// <summary>Coalesces bursts of WM_SETTINGCHANGE / ColorValuesChanged into one refresh.</summary>
    public void RequestRefresh()
    {
        if (_pending) return;
        _pending = true;
        _dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pending = false;
            var next = Build();
            if (next == Current) return;
            Current = next;
            Changed?.Invoke();
        });
    }

    private Palette Build()
    {
        if (_override is { } o)
        {
            var p = Palette.Create(o.Dark, false, _ui);
            if (o.Accent is { } a)
            {
                p = o.Dark
                    ? p with { Accent = Mix(a, Color.FromArgb(255, 255, 255, 255), 0.45), AccentText = Mix(a, Color.FromArgb(255, 255, 255, 255), 0.6) }
                    : p with { Accent = Mix(a, Color.FromArgb(255, 0, 0, 0), 0.15), AccentText = Mix(a, Color.FromArgb(255, 0, 0, 0), 0.3) };
            }
            return p;
        }
        bool hc = false;
        try { hc = _a11y.HighContrast; } catch { }
        return Palette.Create(ReadSystemDark(), hc, _ui);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v ? v == 0 : true;
        }
        catch
        {
            return true;
        }
    }
}
