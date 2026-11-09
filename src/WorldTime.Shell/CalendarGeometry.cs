namespace WorldTime.Shell;

/// <summary>One UI Automation element read from the flyout (screen pixels).</summary>
public readonly record struct UiNode(
    string AutomationId,
    string ClassName,
    int ControlType,
    PixelRect Rect,
    bool Offscreen,
    int ExpandState,
    string Name)
{
    public const int CalendarControlType = 50001;
    public const string CardId = "CalendarCenterGrid";
    public bool IsCard => AutomationId == CardId;
    public bool IsExpandButton => AutomationId == "ExpandCollapseButton";
    public bool IsCalendarGrid => ControlType == CalendarControlType || ClassName == "CalendarView";
}

/// <summary>
/// Where the native calendar card is and whether its month grid is showing.
/// <see cref="Card"/> is where the card settles; <see cref="SlideOffset"/> is how far
/// right of that it is drawn right now (non-zero while it slides in or out).
/// </summary>
public sealed record CalendarState(
    nint Hwnd,
    PixelRect Window,
    PixelRect Card,
    bool Expanded,
    MonitorInfo Monitor,
    string Source,
    int SlideOffset = 0)
{
    public override string ToString() =>
        $"card={Card} window={Window} expanded={Expanded} dpi={Monitor.Dpi} via {Source}";
}

/// <summary>
/// Turns the raw UI Automation elements of the notification center / calendar
/// flyout into the calendar card's bounds. Kept pure so it can be tuned against
/// logs from real machines and unit-tested.
/// </summary>
public static class CalendarGeometry
{
    public static CalendarState? Resolve(
        nint hwnd,
        PixelRect window,
        IReadOnlyList<UiNode> keys,
        IReadOnlyList<UiNode> buttonAncestors,
        MonitorInfo monitor)
    {
        double s = monitor.Scale;
        // (UiNode is a struct: FirstOrDefault would return an empty node, never null)
        UiNode? button = Find(keys, k => k.IsExpandButton && !k.Rect.IsEmpty);
        UiNode? grid = Find(keys, k => k.IsCalendarGrid && !k.Offscreen && !k.Rect.IsEmpty);
        UiNode? cardNode = Find(keys, k => k.IsCard && !k.Offscreen && !k.Rect.IsEmpty);
        if (button is null && grid is null && cardNode is null) return null; // not the calendar flyout

        bool expanded = grid is not null && grid.Value.Rect.Height > 120 * s;

        if (cardNode is { } c)
        {
            // The card slides in from the right when the flyout opens. Its final
            // horizontal position is the window minus the shell's 12px margins, so
            // place against that instead of chasing the animation.
            // While sliding in, the card keeps its full width but sits up to ~28 px
            // to the right, so always pin it to the column when the window is one.
            var r = c.Rect;
            int margin = (int)Math.Round(12 * s);
            int finalLeft = window.Left + margin, finalRight = window.Right - margin;
            bool column = finalRight - finalLeft > 200 * s && window.Width < 520 * s;
            int slide = 0;
            if (column)
            {
                slide = Math.Max(0, r.Left - finalLeft);
                r = r with { Left = finalLeft, Right = finalRight };
            }
            return new CalendarState(hwnd, window, r, expanded, monitor, "uia-card", slide);
        }

        // The card: the outermost ancestor of the expand/collapse button that still
        // starts at the card's header row and stays inside the flyout window.
        PixelRect content = button?.Rect ?? grid!.Value.Rect;
        if (grid is not null) content = PixelRect.Union(content, grid.Value.Rect);
        PixelRect? card = null;
        if (button is not null)
        {
            foreach (var a in buttonAncestors) // nearest first
            {
                var r = a.Rect;
                if (r.IsEmpty || !r.Contains(content)) continue;
                if (r.Top < button.Value.Rect.Top - 64 * s) break;      // grew upwards into the notifications
                if (r.Width > window.Width + 2) break;
                card = r;
            }
        }

        if (card is null)
        {
            // Fallback: pad the visible content the way the card does.
            int pad = (int)Math.Round(12 * s);
            card = new PixelRect(content.Left - pad, content.Top - pad, content.Right + pad, content.Bottom + pad);
            card = Clamp(card.Value, window);
        }

        return new CalendarState(hwnd, window, card.Value, expanded, monitor,
            button is null ? "uia-grid" : "uia-button");
    }

    /// <summary>Used when UI Automation is unavailable: assume the window is the calendar card.</summary>
    private static UiNode? Find(IReadOnlyList<UiNode> nodes, Func<UiNode, bool> match)
    {
        foreach (var n in nodes)
            if (match(n)) return n;
        return null;
    }

    public static CalendarState FromWindowOnly(nint hwnd, PixelRect window, MonitorInfo monitor) =>
        new(hwnd, window, window, window.Height > 300 * monitor.Scale, monitor, "window");

    private static PixelRect Clamp(PixelRect r, PixelRect bounds) =>
        new(Math.Max(r.Left, bounds.Left), Math.Max(r.Top, bounds.Top),
            Math.Min(r.Right, bounds.Right), Math.Min(r.Bottom, bounds.Bottom));

    /// <summary>
    /// Where the companion panel goes: immediately left of the calendar card,
    /// same top and bottom, same width; kept on the card's monitor.
    /// </summary>
    public static PixelRect PanelRect(CalendarState cal, int minWidthDip = 320, int minHeightDip = 0)
    {
        double s = cal.Monitor.Scale;
        int gap = (int)Math.Round(12 * s);
        int width = Math.Max(cal.Card.Width, (int)Math.Round(minWidthDip * s));
        int height = Math.Max(cal.Card.Height, (int)Math.Round(minHeightDip * s));
        int right = cal.Card.Left - gap;
        int left = right - width;
        int bottom = cal.Card.Bottom;
        int top = bottom - height;
        var work = cal.Monitor.WorkArea;
        if (left < work.Left)
        {
            // Not enough room on the left (very narrow screen): stack above the card instead.
            left = Math.Max(work.Left, cal.Card.Right - width);
            bottom = cal.Card.Top - gap;
            top = bottom - height;
        }
        if (top < work.Top) top = work.Top;
        return new PixelRect(left, top, left + width, Math.Max(top + 1, bottom));
    }
}
