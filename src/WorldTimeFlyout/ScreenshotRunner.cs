using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WorldTime.Core;
using WorldTime.Core.Catalog;
using WorldTime.Shell;

namespace WorldTimeFlyout;

/// <summary>
/// CI helper (--screenshot DIR): renders the panel in each layout/theme with a
/// fixed clock and fixed cities, saves PNGs, then exits. This is how the UI is
/// reviewed without a person at a Windows desktop.
/// </summary>
public sealed class ScreenshotRunner(AppController controller, string dir)
{
    private readonly List<string> _log = [];

    /// <summary>Writes the settings file the screenshot run uses (before the controller loads it).</summary>
    public static string PrepareSettings(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var s = AppSettings.CreateDefault("Asia/Yerevan", null);
        var catalog = CityCatalog.LoadEmbedded();
        foreach (var q in new[] { "Los Angeles", "Kathmandu", "Sydney", "Dubai" })
            s.Locations.Add(catalog.Search(q, 1)[0].ToLocation());
        s.WelcomeShown = true;
        new SettingsStore(path).Save(s);
        return path;
    }

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(dir);
            var panel = controller.Panel;
            var model = controller.Model;
            var blue = Windows.UI.Color.FromArgb(255, 0, 120, 212);
            var red = Windows.UI.Color.FromArgb(255, 226, 36, 26);
            var orange = Windows.UI.Color.FromArgb(255, 247, 99, 12);
            var purple = Windows.UI.Color.FromArgb(255, 136, 23, 152);

            await Shot("expanded-dark-red", true, red, PanelLayout.Expanded, 336, 455);
            await Shot("expanded-light", false, blue, PanelLayout.Expanded, 336, 455);
            await Shot("compact-dark-red", true, red, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));
            await Shot("compact-light", false, blue, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));

            model.StepDays(1);
            await Shot("expanded-next-day-dark-red", true, red, PanelLayout.Expanded, 336, 455);
            model.ResetToNow();

            panel.Place(Rect(336, 455), PanelLayout.Expanded);
            panel.OpenDatePicker();
            await Shot("date-picker-dark-red", true, red, PanelLayout.Expanded, 336, 455, keepState: true);
            await Shot("date-picker-light", false, blue, PanelLayout.Expanded, 336, 455, keepState: true);
            panel.Place(Rect(336, 455), PanelLayout.Compact); // closes the picker
            panel.Place(Rect(336, 455), PanelLayout.Expanded);

            model.StepTime(6); // +3h with 30 minute steps
            await Shot("expanded-shifted-dark-orange", true, orange, PanelLayout.Expanded, 336, 455);
            await Shot("compact-shifted-light-purple", false, purple, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));
            model.ResetToNow();

            model.StepTime(-1);
            await Shot("compact-back-one-step-dark-red", true, red, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));
            model.ResetToNow();

            model.SetClock(ClockStyle.H24);
            await Shot("expanded-24h-light", false, blue, PanelLayout.Expanded, 336, 455);
            await Shot("compact-24h-dark-red", true, red, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));
            model.SetClock(ClockStyle.H12);

            // three cities: tiles fill the width, no paging
            while (model.Locations.Count > 3) model.Remove(model.Locations[^1].Id);
            await Shot("compact-three-dark-red", true, red, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));
            await Shot("compact-three-light", false, blue, PanelLayout.Compact, 336, (int)Math.Ceiling(Views.CompactView.RequiredHeightFor(1)));

            panel.Place(Rect(336, 455), PanelLayout.Expanded);
            panel.BeginSearch();
            await Task.Delay(400);
            await Shot("search-dark", true, blue, PanelLayout.Expanded, 336, 455, keepState: true, typed: "tok");
            panel.EndSearch();

            _log.Add($"tzdb {Zones.TzdbVersion}");
        }
        catch (Exception e)
        {
            _log.Add("FAILED: " + e);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(dir, "screenshots.txt"), _log);
            Application.Current.Exit();
        }
    }

    private static PixelRect Rect(int wDip, int hDip)
    {
        var mon = Win32Util.PrimaryMonitor();
        double s = mon.Scale;
        int w = (int)Math.Round(wDip * s), h = (int)Math.Round(hDip * s);
        return new PixelRect(mon.WorkArea.Right - 24 - w, mon.WorkArea.Bottom - 24 - h, mon.WorkArea.Right - 24, mon.WorkArea.Bottom - 24);
    }

    private async Task Shot(string name, bool dark, Windows.UI.Color accent, PanelLayout layout, int wDip, int hDip,
        bool keepState = false, string? typed = null)
    {
        var panel = controller.Panel;
        controller.Theme.Override(dark, accent);
        var rect = Rect(wDip, hDip);
        if (!keepState) panel.Place(rect, layout);
        panel.ShowPassive();
        if (typed is not null) TypeIntoSearch(panel, typed);
        await Task.Delay(typed is null ? 700 : 1500);

        // 1) XAML content on an opaque stand-in for acrylic (deterministic)
        panel.UseSolidBackground(true);
        await Task.Delay(150);
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(panel.RootElement);
        var buffer = await rtb.GetPixelsAsync();
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        await SavePng(Path.Combine(dir, name + ".png"), bytes, rtb.PixelWidth, rtb.PixelHeight, premultiplied: true);
        panel.UseSolidBackground(false);
        await Task.Delay(250);

        // 2) what is actually on screen (acrylic, rounded corners, shadow) when available
        var pad = 16;
        var capRect = new PixelRect(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad);
        var screen = ScreenCapture.Capture(capRect);
        if (screen is not null)
            await SavePng(Path.Combine(dir, name + ".screen.png"), screen, capRect.Width, capRect.Height, premultiplied: false);
        _log.Add($"{name}: {rtb.PixelWidth}x{rtb.PixelHeight} dpi-scale {Win32Util.PrimaryMonitor().Scale:0.##}");
    }

    private static void TypeIntoSearch(PanelWindow panel, string text)
    {
        // find the search TextBox and set its text
        static Microsoft.UI.Xaml.Controls.TextBox? Find(DependencyObject root)
        {
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (c is Microsoft.UI.Xaml.Controls.TextBox tb) return tb;
                if (Find(c) is { } found) return found;
            }
            return null;
        }
        if (Find(panel.RootElement) is { } box) box.Text = text;
    }

    private static async Task SavePng(string path, byte[] bgra, int w, int h, bool premultiplied)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, premultiplied ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore,
            (uint)w, (uint)h, 96, 96, bgra);
        await encoder.FlushAsync();
        var data = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(data);
        }
        await File.WriteAllBytesAsync(path, data);
    }
}
