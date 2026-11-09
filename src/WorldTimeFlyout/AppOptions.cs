namespace WorldTimeFlyout;

/// <summary>Command line: --background, --show, --diag, --screenshot DIR, --settings FILE, --fixed-time ISO.</summary>
public sealed record AppOptions
{
    public bool Background { get; init; }
    public bool ShowOnStart { get; init; }
    public bool Diagnostics { get; init; }
    public string? ScreenshotDir { get; init; }
    public bool Screenshot => ScreenshotDir is not null;
    public string? SettingsPath { get; init; }
    public string? FixedTime { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--background": o = o with { Background = true }; break;
                case "--show": o = o with { ShowOnStart = true }; break;
                case "--diag": o = o with { Diagnostics = true }; break;
                case "--screenshot" when next is not null: o = o with { ScreenshotDir = next }; i++; break;
                case "--settings" when next is not null: o = o with { SettingsPath = next }; i++; break;
                case "--fixed-time" when next is not null: o = o with { FixedTime = next }; i++; break;
            }
        }
        return o;
    }
}
