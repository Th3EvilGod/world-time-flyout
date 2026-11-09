using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using NodaTime;
using NodaTime.Text;

namespace WorldTimeFlyout;

/// <summary>
/// The WinUI application object. There is no App.xaml: Fluent control styles
/// are merged in code, and this class provides the framework's XAML type
/// metadata so built-in controls resolve their templates.
/// </summary>
public sealed partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _metadata = new();
    private AppOptions _options;
    private AppController? _controller;

    public App(AppOptions options)
    {
        _options = options;
        UnhandledException += (_, e) =>
        {
            DiagLog.Write($"unhandled: {e.Exception}");
            e.Handled = true; // keep running; a clock utility should not vanish
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception e)
        {
            DiagLog.Write($"launch failed: {e}");
            Exit();
        }
    }

    private void Launch()
    {
        DiagLog.Detail("OnLaunched");
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        if (_options.Screenshot)
            _options = _options with { SettingsPath = ScreenshotRunner.PrepareSettings(_options.ScreenshotDir!) };
        IClock? clock = null;
        if (_options.FixedTime is { } iso && InstantPattern.ExtendedIso.Parse(iso) is { Success: true } parsed)
            clock = new FixedClock(parsed.Value);
        _controller = new AppController(_options, clock);
        if (_options.Screenshot)
        {
            _ = new ScreenshotRunner(_controller, _options.ScreenshotDir!).RunAsync();
            return;
        }
        _controller.Start();
    }

    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
