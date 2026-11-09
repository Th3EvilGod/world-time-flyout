using System.Globalization;
using System.IO.Compression;
using System.Text;
using NodaTime;

namespace WorldTime.Core.Catalog;

public sealed record CityEntry(
    string Name,
    string CountryCode,
    string Admin1,
    int Population,
    string ZoneId,
    double Latitude,
    double Longitude)
{
    internal string FoldedName = "";
    internal string[] FoldedAlternates = [];
    internal string FoldedQualifiers = ""; // admin1 + country name + country code
}

public enum ResultKind { City, Country, TimeZone, Abbreviation, Offset }

public sealed record SearchResult(
    string Name,
    string ZoneId,
    string? CountryCode,
    string Detail,
    ResultKind Kind,
    int Population,
    double Score,
    double? Latitude = null,
    double? Longitude = null)
{
    public SavedLocation ToLocation() => new()
    {
        Name = Name,
        ZoneId = ZoneId,
        CountryCode = CountryCode,
        Detail = Detail,
        Latitude = Latitude,
        Longitude = Longitude,
    };
}

/// <summary>
/// Offline catalog of ~34,000 cities (GeoNames, population over 15,000) plus
/// countries, IANA zone ids, common abbreviations and UTC offsets.
/// Loaded on demand (only while the user searches).
/// </summary>
public sealed class CityCatalog
{
    private readonly List<CityEntry> _cities;
    private readonly Dictionary<string, string> _countryNames;          // ISO2 -> name
    private readonly List<(string Folded, string Code)> _countryKeys;   // folded names and aliases
    private readonly Dictionary<string, CityEntry> _largestByZone;
    private readonly List<(string Folded, string Id)> _zoneKeys;

    private CityCatalog(List<CityEntry> cities, Dictionary<string, string> countryNames,
        List<(string, string)> countryKeys)
    {
        _cities = cities;
        _countryNames = countryNames;
        _countryKeys = countryKeys;
        _largestByZone = new Dictionary<string, CityEntry>(StringComparer.Ordinal);
        foreach (var c in cities) // cities arrive sorted by population, descending
        {
            _largestByZone.TryAdd(c.ZoneId, c);
            _largestByZone.TryAdd(Zones.Canonical(c.ZoneId), c);
        }
        _zoneKeys = Zones.Tzdb.Ids
            .Where(id => id.Contains('/') && !id.StartsWith("Etc/", StringComparison.Ordinal))
            .Select(id => (TextFold.Fold(id.Replace('/', ' ').Replace('_', ' ')), id))
            .ToList();
    }

    public IReadOnlyList<CityEntry> Cities => _cities;

    public static CityCatalog LoadEmbedded()
    {
        using var s = typeof(CityCatalog).Assembly.GetManifestResourceStream("WorldTime.Core.catalog.tsv.gz")
                      ?? throw new InvalidOperationException("City catalog resource is missing.");
        return Load(s);
    }

    public static CityCatalog Load(Stream gzip)
    {
        using var gz = new GZipStream(gzip, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var cities = new List<CityEntry>(34_500);
        var countryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var countryAliases = new List<(string Code, string Alias)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f[0] == "C" && f.Length >= 3)
            {
                countryNames[f[1]] = f[2];
                if (f.Length > 3 && f[3].Length > 0)
                    foreach (var a in f[3].Split('|')) countryAliases.Add((f[1], a));
                continue;
            }
            if (f.Length < 7) continue;
            var inv = CultureInfo.InvariantCulture;
            var city = new CityEntry(f[0], f[1], f[2], int.Parse(f[3], inv), f[4],
                double.Parse(f[5], inv), double.Parse(f[6], inv))
            {
                FoldedName = TextFold.Fold(f[0]),
                FoldedAlternates = f.Length > 7 && f[7].Length > 0
                    ? f[7].Split('|').Select(TextFold.Fold).Where(a => a.Length > 0).Distinct().ToArray()
                    : [],
            };
            cities.Add(city);
        }

        var keys = new List<(string, string)>();
        foreach (var (code, name) in countryNames) keys.Add((TextFold.Fold(name), code));
        foreach (var (code, alias) in countryAliases) keys.Add((TextFold.Fold(alias), code));

        foreach (var c in cities)
        {
            countryNames.TryGetValue(c.CountryCode, out var cn);
            c.FoldedQualifiers = TextFold.Fold($"{c.Admin1} {cn} {c.CountryCode}");
        }
        foreach (var (code, alias) in countryAliases)
        {
            // "usa", "uk": let qualifiers match aliases too
            foreach (var c in cities.Where(x => x.CountryCode == code && x.Population > 50_000))
                c.FoldedQualifiers += " " + TextFold.Fold(alias);
        }
        return new CityCatalog(cities, countryNames, keys);
    }

    public string CountryName(string? code) =>
        code is not null && _countryNames.TryGetValue(code, out var n) ? n : code ?? "";

    public CityEntry? LargestCityInZone(string zoneId) =>
        _largestByZone.TryGetValue(zoneId, out var c) ? c : null;

    /// <summary>"Oregon, United States" or "Armenia".</summary>
    public string DetailFor(CityEntry c) =>
        string.IsNullOrEmpty(c.Admin1) ? CountryName(c.CountryCode) : $"{c.Admin1}, {CountryName(c.CountryCode)}";

    /// <summary>A location for an IANA zone named after its largest city (or the zone id).</summary>
    public SavedLocation LocationForZone(string zoneId)
    {
        var city = LargestCityInZone(zoneId);
        return city is not null
            ? new SavedLocation
            {
                Name = city.Name, ZoneId = zoneId, CountryCode = city.CountryCode, Detail = DetailFor(city),
                Latitude = city.Latitude, Longitude = city.Longitude,
            }
            : new SavedLocation { Name = Zones.DisplayNameFromId(zoneId), ZoneId = zoneId, Detail = zoneId };
    }

    private static double PopBonus(int population) => 25 * Math.Log10(Math.Max(population, 1000));

    /// <summary>Ranked, typo-tolerant search over cities, countries, zone ids, abbreviations and UTC offsets.</summary>
    public IReadOnlyList<SearchResult> Search(string query, int limit = 12)
    {
        var raw = query?.Trim() ?? "";
        var q = TextFold.Fold(raw);
        if (q.Length == 0) return [];
        var results = new List<SearchResult>();

        // UTC offsets: "UTC+4", "gmt-5:30"
        if (Zones.TryParseFixed(raw, out var fixedOffset))
        {
            var id = Zones.FixedId(fixedOffset);
            results.Add(new SearchResult(TimeFormat.UtcOffset(fixedOffset), id, null,
                "Fixed offset, no daylight saving", ResultKind.Offset, 0, 2000));
        }

        // Abbreviations: "EST", "CET", "IST"
        // Upper-case input ("EST") ranks first; lower-case ("cat") must not bury city prefixes.
        bool upper = raw.Any(char.IsLetter) && raw == raw.ToUpperInvariant();
        int rank = 0;
        foreach (var zid in Abbreviations.Lookup(raw))
        {
            var city = LargestCityInZone(zid);
            var name = city?.Name ?? Zones.DisplayNameFromId(zid);
            var detail = (city is null ? zid : DetailFor(city)) + " · " + raw.ToUpperInvariant();
            results.Add(new SearchResult(name, zid, city?.CountryCode, detail, ResultKind.Abbreviation,
                city?.Population ?? 0, (upper ? 1900 : 700) - rank++ * 10, city?.Latitude, city?.Longitude));
        }

        // IANA zone ids: "America/New_York", "europe/ber"
        if (q.Length >= 3)
        {
            bool looksLikeId = raw.Contains('/') || raw.Contains('_');
            foreach (var (folded, id) in _zoneKeys)
            {
                double s = folded == q ? 1500 : folded.StartsWith(q, StringComparison.Ordinal) && looksLikeId ? 1200 : 0;
                if (s == 0) continue;
                var city = LargestCityInZone(id);
                results.Add(new SearchResult(city?.Name ?? Zones.DisplayNameFromId(id), id, city?.CountryCode,
                    id, ResultKind.TimeZone, city?.Population ?? 0, s + PopBonus(city?.Population ?? 0) / 10,
                    city?.Latitude, city?.Longitude));
            }
        }

        // Countries: "Armenia", "usa" -> the largest city in each of its zones
        var countryHits = new Dictionary<string, double>();
        foreach (var (folded, code) in _countryKeys)
        {
            double s = folded == q ? 1050 : (q.Length >= 3 && folded.StartsWith(q, StringComparison.Ordinal)) ? 600 : 0;
            if (s > 0 && (!countryHits.TryGetValue(code, out var prev) || prev < s)) countryHits[code] = s;
        }
        foreach (var (code, s) in countryHits)
        {
            var perZone = _cities.Where(c => c.CountryCode == code)
                .GroupBy(c => c.ZoneId).Select(g => g.First()) // largest per zone
                .OrderByDescending(c => c.Population).Take(8);
            foreach (var c in perZone)
                results.Add(new SearchResult(c.Name, c.ZoneId, c.CountryCode, DetailFor(c), ResultKind.Country,
                    c.Population, s + PopBonus(c.Population), c.Latitude, c.Longitude));
        }

        // Cities
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int strongMatches = 0;
        var scored = new List<(CityEntry City, double Score)>();
        foreach (var c in _cities)
        {
            double s = ScoreCity(c, q, tokens);
            if (s <= 0) continue;
            if (s >= 500) strongMatches++;
            scored.Add((c, s + PopBonus(c.Population)));
        }

        // Typo tolerance when nothing matched well: "londn", "yerevn", "tokio"
        if (strongMatches < limit && q.Length >= 4)
        {
            int max = q.Length >= 7 ? 2 : 1;
            foreach (var c in _cities)
            {
                if (c.Population < 50_000) continue;
                var name = c.FoldedName;
                var target = name.Length > q.Length + max ? name.AsSpan(0, q.Length) : name.AsSpan();
                int d = TextFold.Distance(q, target, max);
                if (d <= max) scored.Add((c, 250 - 60 * d + PopBonus(c.Population)));
            }
        }

        foreach (var (c, s) in scored)
            results.Add(new SearchResult(c.Name, c.ZoneId, c.CountryCode, DetailFor(c), ResultKind.City, c.Population, s,
                c.Latitude, c.Longitude));

        // Best first; one entry per (name, zone)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Population)
            .Where(r => seen.Add(r.Name + "|" + Zones.Canonical(r.ZoneId)))
            .Take(limit)
            .ToList();
    }

    private static double ScoreCity(CityEntry c, string q, string[] tokens)
    {
        var name = c.FoldedName;
        if (name == q) return 1000;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 800;
        double best = 0;
        foreach (var a in c.FoldedAlternates)
        {
            if (a == q) { best = Math.Max(best, 760); break; }
            if (q.Length >= 3 && a.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 520);
        }
        if (best >= 760) return best;
        if (q.Length >= 2 && WordPrefix(name, q)) best = Math.Max(best, 650);

        // "london canada", "portland oregon", "paris fr"
        if (tokens.Length >= 2)
        {
            for (int split = tokens.Length - 1; split >= 1; split--)
            {
                var head = string.Join(' ', tokens, 0, split);
                bool headOk = name == head || name.StartsWith(head, StringComparison.Ordinal) ||
                              c.FoldedAlternates.Any(a => a == head);
                if (!headOk) continue;
                bool tailOk = true;
                for (int i = split; i < tokens.Length && tailOk; i++)
                    tailOk = WordPrefix(c.FoldedQualifiers, tokens[i]);
                if (tailOk) { best = Math.Max(best, name == head ? 900 : 700); break; }
            }
        }

        if (best == 0 && q.Length >= 4 && name.Contains(q, StringComparison.Ordinal)) best = 300;
        return best;
    }

    private static bool WordPrefix(string text, string prefix)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (string.CompareOrdinal(text, i, prefix, 0, prefix.Length) == 0) return true;
            int next = text.IndexOf(' ', i);
            if (next < 0) break;
            i = next + 1;
        }
        return false;
    }
}
