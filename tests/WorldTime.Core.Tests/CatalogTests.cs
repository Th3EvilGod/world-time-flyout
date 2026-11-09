using System.Diagnostics;
using WorldTime.Core;
using WorldTime.Core.Catalog;
using Xunit;

namespace WorldTime.Core.Tests;

public class CatalogTests
{
    internal static readonly CityCatalog Catalog = CityCatalog.LoadEmbedded();

    private static SearchResult Top(string q) => Catalog.Search(q).First();

    [Theory]
    [InlineData("Yerevan", "Yerevan", "Asia/Yerevan")]
    [InlineData("yerevan", "Yerevan", "Asia/Yerevan")]
    [InlineData("New York", "New York", "America/New_York")]
    [InlineData("nyc", "New York", "America/New_York")]
    [InlineData("Los Angeles", "Los Angeles", "America/Los_Angeles")]
    [InlineData("London", "London", "Europe/London")]
    [InlineData("Tokyo", "Tokyo", "Asia/Tokyo")]
    [InlineData("Dubai", "Dubai", "Asia/Dubai")]
    [InlineData("Sydney", "Sydney", "Australia/Sydney")]
    [InlineData("Armenia", "Yerevan", "Asia/Yerevan")]
    [InlineData("Japan", "Tokyo", "Asia/Tokyo")]
    [InlineData("America/New_York", "New York", "America/New_York")]
    [InlineData("EST", "New York", "America/New_York")]
    [InlineData("JST", "Tokyo", "Asia/Tokyo")]
    [InlineData("kathmandu", "Kathmandu", "Asia/Kathmandu")]
    [InlineData("Sao Paulo", "São Paulo", "America/Sao_Paulo")]
    [InlineData("zurich", "Zürich", "Europe/Zurich")]
    [InlineData("londn", "London", "Europe/London")]
    [InlineData("tokio", "Tokyo", "Asia/Tokyo")]
    [InlineData("san fran", "San Francisco", "America/Los_Angeles")]
    public void Finds(string query, string name, string zone)
    {
        var r = Top(query);
        Assert.Equal(name, r.Name);
        Assert.Equal(zone, Zones.Canonical(r.ZoneId));
    }

    [Fact]
    public void Qualified_queries_disambiguate()
    {
        var r = Top("london canada");
        Assert.Equal("London", r.Name);
        Assert.Equal("CA", r.CountryCode);
        var p = Top("portland oregon");
        Assert.Equal("America/Los_Angeles", p.ZoneId);
    }

    [Fact]
    public void Utc_offset_query()
    {
        var r = Top("UTC+5:30");
        Assert.Equal(ResultKind.Offset, r.Kind);
        Assert.Equal("UTC+05:30", r.ZoneId);
    }

    [Fact]
    public void Ambiguous_abbreviation_offers_all()
    {
        var zones = Catalog.Search("IST").Take(3).Select(r => Zones.Canonical(r.ZoneId)).ToList();
        Assert.Equal(["Asia/Kolkata", "Europe/Dublin", "Asia/Jerusalem"], zones);
    }

    [Fact]
    public void Lowercase_word_is_not_hijacked_by_abbreviation()
    {
        Assert.NotEqual("Africa/Maputo", Top("cat").ZoneId);
    }

    [Fact]
    public void United_states_lists_multiple_zones()
    {
        var zones = Catalog.Search("United States").Select(r => r.ZoneId).Distinct().ToList();
        Assert.Contains("America/New_York", zones);
        Assert.Contains("America/Los_Angeles", zones);
        Assert.Contains("America/Chicago", zones);
    }

    [Fact]
    public void Every_catalog_zone_resolves()
    {
        var bad = Catalog.Cities.Select(c => c.ZoneId).Distinct().Where(z => !Zones.IsValid(z)).ToList();
        Assert.Empty(bad);
        var badAbbr = Abbreviations.AllZoneIds.Where(z => !Zones.IsValid(z)).ToList();
        Assert.Empty(badAbbr);
    }

    [Fact]
    public void Search_is_fast()
    {
        Catalog.Search("warm up");
        var sw = Stopwatch.StartNew();
        foreach (var q in new[] { "y", "ye", "yer", "yere", "yerev", "yereva", "yerevan", "londn", "san fran" })
            Catalog.Search(q);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"9 searches took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Location_for_zone_uses_largest_city()
    {
        Assert.Equal("Yerevan", Catalog.LocationForZone("Asia/Yerevan").Name);
        Assert.Equal("Mumbai", Catalog.LocationForZone("Asia/Kolkata").Name);
    }
}
