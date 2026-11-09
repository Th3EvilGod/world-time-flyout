using System.Text.Json;
using System.Text.Json.Serialization;
using WorldTime.Core.Catalog;

namespace WorldTime.Core;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<SavedLocation> Locations { get; set; } = [];
    /// <summary>Id of the location the timeline is anchored to (defaults to the system zone).</summary>
    public string? ReferenceId { get; set; }
    public int StepMinutes { get; set; } = 30;
    public ClockStyle Clock { get; set; } = ClockStyle.System;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool WelcomeShown { get; set; }
    /// <summary>Look for a new version on GitHub once a day (install only on request).</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>A version whose "update available" bar was closed: not shown again in the panel.</summary>
    public string? DismissedUpdate { get; set; }
    /// <summary>The version that last ran, to say "Updated to ..." once after an update.</summary>
    public string? LastRunVersion { get; set; }

    public SavedLocation? Reference =>
        Locations.FirstOrDefault(l => l.Id == ReferenceId) ?? Locations.FirstOrDefault();

    /// <summary>Repairs anything a hand-edited or older file might contain.</summary>
    public void Normalize()
    {
        StepMinutes = TimeMath.NormalizeStep(StepMinutes);
        if (!Enum.IsDefined(Clock)) Clock = ClockStyle.System;
        var seen = new HashSet<string>();
        Locations = Locations
            .Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name) && Zones.IsValid(l.ZoneId))
            .Select(l =>
            {
                if (string.IsNullOrWhiteSpace(l.Id) || !seen.Add(l.Id))
                {
                    l.Id = Guid.NewGuid().ToString("N");
                    seen.Add(l.Id);
                }
                return l;
            })
            .ToList();
        if (Locations.All(l => l.Id != ReferenceId)) ReferenceId = Locations.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// First-run defaults: the system zone first (as reference), then
    /// New York, London and Tokyo unless already covered.
    /// </summary>
    public static AppSettings CreateDefault(string? systemZoneId, CityCatalog? catalog)
    {
        var s = new AppSettings();
        var defaults = new List<SavedLocation>
        {
            new() { Name = "Yerevan", ZoneId = "Asia/Yerevan", CountryCode = "AM", Detail = "Armenia", Latitude = 40.18, Longitude = 44.51 },
            new() { Name = "New York", ZoneId = "America/New_York", CountryCode = "US", Detail = "New York, United States", Latitude = 40.71, Longitude = -74.01 },
            new() { Name = "London", ZoneId = "Europe/London", CountryCode = "GB", Detail = "United Kingdom", Latitude = 51.51, Longitude = -0.13 },
            new() { Name = "Tokyo", ZoneId = "Asia/Tokyo", CountryCode = "JP", Detail = "Japan", Latitude = 35.69, Longitude = 139.69 },
        };
        SavedLocation? system = null;
        if (systemZoneId is not null && Zones.IsValid(systemZoneId))
        {
            var canonical = Zones.Canonical(systemZoneId);
            system = defaults.FirstOrDefault(d => Zones.Canonical(d.ZoneId) == canonical);
            if (system is null)
            {
                system = catalog?.LocationForZone(systemZoneId)
                         ?? new SavedLocation { Name = Zones.DisplayNameFromId(systemZoneId), ZoneId = systemZoneId };
                defaults.Insert(0, system);
            }
            else
            {
                defaults.Remove(system);
                defaults.Insert(0, system);
            }
        }
        s.Locations = defaults;
        s.ReferenceId = (system ?? defaults[0]).Id;
        return s;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Reads and atomically writes settings.json in the user's local app data.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorldTimeFlyout");

    public static SettingsStore CreateDefault() => new(System.IO.Path.Combine(DefaultDirectory, "settings.json"));

    /// <summary>Loads settings; returns null when there is no usable file (first run).</summary>
    public AppSettings? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var json = File.ReadAllText(Path);
            var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            if (s is null) return null;
            s.Normalize();
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the broken file for inspection and start fresh.
            try { File.Copy(Path, Path + ".bad", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tmp, Path, overwrite: true);
    }
}
