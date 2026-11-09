namespace WorldTime.Core.Catalog;

/// <summary>
/// Common time-zone abbreviations. Several are ambiguous (IST, CST, AMT), so a
/// lookup returns every plausible zone, most common first, and the user picks.
/// </summary>
public static class Abbreviations
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = ["UTC"],
        ["GMT"] = ["UTC", "Europe/London"],
        ["ET"] = ["America/New_York"], ["EST"] = ["America/New_York"], ["EDT"] = ["America/New_York"],
        ["CT"] = ["America/Chicago"], ["CST"] = ["America/Chicago", "Asia/Shanghai"], ["CDT"] = ["America/Chicago"],
        ["MT"] = ["America/Denver", "America/Phoenix"], ["MST"] = ["America/Denver", "America/Phoenix"], ["MDT"] = ["America/Denver"],
        ["PT"] = ["America/Los_Angeles"], ["PST"] = ["America/Los_Angeles"], ["PDT"] = ["America/Los_Angeles"],
        ["AKST"] = ["America/Anchorage"], ["AKDT"] = ["America/Anchorage"],
        ["HST"] = ["Pacific/Honolulu"],
        ["AST"] = ["America/Halifax", "Asia/Riyadh"], ["ADT"] = ["America/Halifax"],
        ["NST"] = ["America/St_Johns"], ["NDT"] = ["America/St_Johns"],
        ["BST"] = ["Europe/London"],
        ["IST"] = ["Asia/Kolkata", "Europe/Dublin", "Asia/Jerusalem"],
        ["CET"] = ["Europe/Berlin", "Europe/Paris"], ["CEST"] = ["Europe/Berlin", "Europe/Paris"],
        ["EET"] = ["Europe/Athens", "Europe/Helsinki"], ["EEST"] = ["Europe/Athens", "Europe/Helsinki"],
        ["WET"] = ["Europe/Lisbon"], ["WEST"] = ["Europe/Lisbon"],
        ["MSK"] = ["Europe/Moscow"],
        ["AMT"] = ["Asia/Yerevan", "America/Manaus"],
        ["GET"] = ["Asia/Tbilisi"], ["AZT"] = ["Asia/Baku"],
        ["TRT"] = ["Europe/Istanbul"],
        ["GST"] = ["Asia/Dubai"],
        ["IRST"] = ["Asia/Tehran"], ["AFT"] = ["Asia/Kabul"],
        ["PKT"] = ["Asia/Karachi"], ["NPT"] = ["Asia/Kathmandu"],
        ["ICT"] = ["Asia/Bangkok"], ["WIB"] = ["Asia/Jakarta"],
        ["SGT"] = ["Asia/Singapore"], ["HKT"] = ["Asia/Hong_Kong"], ["PHT"] = ["Asia/Manila"],
        ["KST"] = ["Asia/Seoul"], ["JST"] = ["Asia/Tokyo"],
        ["AEST"] = ["Australia/Sydney", "Australia/Brisbane"], ["AEDT"] = ["Australia/Sydney"],
        ["ACST"] = ["Australia/Adelaide", "Australia/Darwin"], ["ACDT"] = ["Australia/Adelaide"],
        ["AWST"] = ["Australia/Perth"],
        ["NZST"] = ["Pacific/Auckland"], ["NZDT"] = ["Pacific/Auckland"],
        ["BRT"] = ["America/Sao_Paulo"], ["ART"] = ["America/Argentina/Buenos_Aires"],
        ["SAST"] = ["Africa/Johannesburg"], ["WAT"] = ["Africa/Lagos"],
        ["EAT"] = ["Africa/Nairobi"], ["CAT"] = ["Africa/Maputo"],
    };

    public static IReadOnlyList<string> Lookup(string text)
    {
        var t = text.Trim();
        if (t.Length is < 1 or > 5) return [];
        return Map.TryGetValue(t, out var zones) ? zones : [];
    }

    public static IEnumerable<string> AllZoneIds => Map.Values.SelectMany(v => v).Distinct();
}
