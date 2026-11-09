namespace WorldTimeFlyout;

/// <summary>
/// Command line: --background, --show, --exit (close the running instance), --diag,
/// --no-update-check. For tests: --screenshot DIR, --settings FILE, --fixed-time ISO,
/// --update-feed URL (loopback only), --update-now, --pretend-version X.
/// </summary>
public sealed record AppOptions
{
    public bool Background { get; init; }
    public bool ShowOnStart { get; init; }
    public bool Diagnostics { get; init; }
    public string? ScreenshotDir { get; init; }
    public bool Screenshot => ScreenshotDir is not null;
    public string? SettingsPath { get; init; }
    public string? FixedTime { get; init; }
    public bool Exit { get; init; }
    public bool NoUpdateCheck { get; init; }
    public string? UpdateFeed { get; init; }
    public bool UpdateNow { get; init; }
    public string? PretendVersion { get; init; }

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
                case "--exit": o = o with { Exit = true }; break;
                case "--no-update-check": o = o with { NoUpdateCheck = true }; break;
                case "--update-feed" when next is not null: o = o with { UpdateFeed = next }; i++; break;
                case "--update-now": o = o with { UpdateNow = true }; break;
                case "--pretend-version" when next is not null: o = o with { PretendVersion = next }; i++; break;
            }
        }
        return o;
    }
}
