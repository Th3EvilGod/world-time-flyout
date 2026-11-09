using Microsoft.UI.Xaml;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>Services the panel window offers to its views.</summary>
public interface IPanelHost
{
    AppModel Model { get; }
    Palette Palette { get; }
    bool AnimationsEnabled { get; }

    /// <summary>Puts text on the clipboard; returns false when the clipboard was busy.</summary>
    bool Copy(string text);

    /// <summary>Opens the row context menu (activates the panel, which closes the calendar).</summary>
    void ShowRowMenu(LocationReading reading, UIElement target, Windows.Foundation.Point? at);

    void ShowMoreMenu(FrameworkElement target);

    void ShowDateTimePicker(FrameworkElement target);

    void BeginSearch();

    void EndSearch();

    /// <summary>Hovering a control that needs focus (search, menus): let the next click activate the panel.</summary>
    void ArmActivation(bool armed);
}
