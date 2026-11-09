using WorldTime.Shell;
using Xunit;

namespace WorldTime.Shell.Tests;

/// <summary>Values recorded by the flyout probe on Windows 11 26H2, 2560x1440 at 100%.</summary>
public class CalendarGeometryTests
{
    private static readonly PixelRect Window = R(2200, 0, 360, 1392);
    private static readonly MonitorInfo Mon = new(R(0, 0, 2560, 1440), R(0, 0, 2560, 1392), 96);

    private static PixelRect R(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    private static UiNode Card(PixelRect r) => new("CalendarCenterGrid", "NamedContainerAutomationPeer", 50026, r, false, -1, "Calendar");
    private static UiNode Button(PixelRect r, bool off = false) => new("ExpandCollapseButton", "Button", 50000, r, off, -1, "Collapse Calendar");
    private static UiNode Grid(PixelRect r, bool off = false) => new("CalendarControl", "CalendarView", 50001, r, off, -1, "");

    private static CalendarState? Resolve(params UiNode[] keys) => CalendarGeometry.Resolve(1, Window, keys, [], Mon);

    [Fact]
    public void Expanded_card_and_panel_position()
    {
        var s = Resolve(Card(R(2212, 925, 336, 455)), Button(R(2507, 939, 24, 24)), Grid(R(2213, 980, 334, 350)))!;
        Assert.True(s.Expanded);
        Assert.Equal(R(2212, 925, 336, 455), s.Card);
        var panel = CalendarGeometry.PanelRect(s);
        Assert.Equal(R(1864, 925, 336, 455), panel); // 12 px gap, same size, same top and bottom
    }

    [Fact]
    public void Collapsed_card()
    {
        var s = Resolve(Card(R(2212, 1280, 336, 100)), Button(R(2507, 1294, 24, 24)), Grid(R(0, 0, 0, 0), off: true))!;
        Assert.False(s.Expanded);
        Assert.Equal(R(2212, 1280, 336, 100), s.Card);
        Assert.Equal(R(1864, 1280, 336, 100), CalendarGeometry.PanelRect(s));
        // compact needs a bit more at large text sizes: grows upward, bottom stays aligned
        Assert.Equal(R(1864, 1250, 336, 130), CalendarGeometry.PanelRect(s, minHeightDip: 130));
    }

    [Fact]
    public void Slide_in_is_pinned_to_the_final_column_position()
    {
        // second frame of the open animation: full width, 28 px right of its final place
        var s = Resolve(Card(R(2240, 925, 336, 455)), Button(R(2535, 939, 24, 24)), Grid(R(2241, 980, 334, 350)))!;
        Assert.Equal(R(2212, 925, 336, 455), s.Card);
    }

    [Fact]
    public void Not_the_calendar()
    {
        Assert.Null(Resolve(new UiNode("NotificationCenterGrid", "NamedContainerAutomationPeer", 50026, R(2212, 8, 336, 905), false, -1, "Notifications")));
    }

    [Fact]
    public void Scales_with_dpi()
    {
        // 150%: everything 1.5x
        var mon = new MonitorInfo(R(0, 0, 3840, 2160), R(0, 0, 3840, 2088), 144);
        var window = R(3300, 0, 540, 2088);
        var s = CalendarGeometry.Resolve(1, window, [Card(R(3318, 1388, 504, 682)), Grid(R(3320, 1470, 500, 525))], [], mon)!;
        var panel = CalendarGeometry.PanelRect(s);
        Assert.Equal(504, panel.Width);
        Assert.Equal(3318 - 18, panel.Right); // 12 DIP gap = 18 px
        Assert.Equal(s.Card.Bottom, panel.Bottom);
    }

    [Fact]
    public void Narrow_screen_stacks_above_the_card()
    {
        var mon = new MonitorInfo(R(0, 0, 400, 800), R(0, 0, 400, 752), 96);
        var window = R(40, 0, 360, 752);
        var s = CalendarGeometry.Resolve(1, window, [Card(R(52, 640, 336, 100))], [], mon)!;
        var panel = CalendarGeometry.PanelRect(s);
        Assert.Equal(640 - 12, panel.Bottom);
        Assert.True(panel.Left >= 0);
    }

    [Fact]
    public void Without_card_container_falls_back_to_button_and_grid()
    {
        var s = Resolve(Button(R(2507, 939, 24, 24)), Grid(R(2213, 980, 334, 350)))!;
        Assert.True(s.Expanded);
        Assert.True(s.Card.Contains(R(2213, 980, 334, 350)));
    }
}
