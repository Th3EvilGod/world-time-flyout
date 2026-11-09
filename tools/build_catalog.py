#!/usr/bin/env python3
"""Builds the offline city catalog embedded in WorldTime.Core.

Source: GeoNames cities with population > 15,000 (via the `geonamescache`
package, data CC BY 4.0 GeoNames, https://www.geonames.org/).

Output: src/WorldTime.Core/Data/catalog.tsv.gz, a small tab-separated file:
    #catalog v1 <city count>
    C <tab> ISO2 <tab> Country name <tab> aliases (| separated)
    <name> <tab> ISO2 <tab> admin1 <tab> population <tab> IANA zone <tab> lat <tab> lon <tab> alt names (|)

Run:  pip install geonamescache && python3 tools/build_catalog.py
"""
import gzip
import os
import re
import sys
import unicodedata

import geonamescache

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "WorldTime.Core", "Data", "catalog.tsv.gz")

# Friendlier display names for a handful of very common searches.
DISPLAY_OVERRIDES = {
    5128581: "New York",          # GeoNames: "New York City"
    1566083: "Ho Chi Minh City",
    4140963: "Washington, D.C.",  # GeoNames: "Washington"
}

COUNTRY_ALIASES = {
    "US": ["USA", "United States of America", "America", "US"],
    "GB": ["UK", "Britain", "Great Britain", "England", "Scotland", "Wales", "Northern Ireland"],
    "AE": ["UAE", "Emirates"],
    "KR": ["South Korea", "Korea"],
    "KP": ["North Korea"],
    "RU": ["Russia"],
    "CZ": ["Czech Republic", "Czechia"],
    "NL": ["Holland", "Netherlands"],
    "IR": ["Iran"],
    "SY": ["Syria"],
    "VN": ["Vietnam"],
    "LA": ["Laos"],
    "BO": ["Bolivia"],
    "VE": ["Venezuela"],
    "TZ": ["Tanzania"],
    "MD": ["Moldova"],
    "TW": ["Taiwan"],
    "CD": ["DR Congo", "DRC", "Congo-Kinshasa"],
    "CG": ["Congo-Brazzaville"],
    "CI": ["Ivory Coast", "Cote d'Ivoire"],
    "MK": ["Macedonia", "North Macedonia"],
    "VA": ["Vatican"],
    "BN": ["Brunei"],
}

CODE_RE = re.compile(r"^[A-Z]{3,4}$")


def fold(s: str) -> str:
    s = unicodedata.normalize("NFKD", s)
    s = "".join(c for c in s if not unicodedata.combining(c))
    return re.sub(r"[^0-9a-z]+", " ", s.lower()).strip()


def is_latin(s: str) -> bool:
    try:
        unicodedata.normalize("NFKD", s).encode("ascii", "ignore")
    except Exception:
        return False
    folded = unicodedata.normalize("NFKD", s)
    letters = [c for c in folded if c.isalpha()]
    return bool(letters) and all(ord(c) < 0x250 for c in letters)


def pick_alts(name: str, alts, population: int):
    limit = 12 if population >= 1_000_000 else 5 if population >= 100_000 else 2
    fname = fold(name)
    seen = {fname}
    codes, related, rest = [], [], []
    for a in alts:
        a = a.strip()
        if not (2 <= len(a) <= 40) or "  " in a:
            continue
        if CODE_RE.match(a):
            if a.lower() not in seen:
                codes.append(a)
                seen.add(a.lower())
            continue
        if not is_latin(a):
            continue
        fa = fold(a)
        if not fa or fa in seen:
            continue
        seen.add(fa)
        if fa in fname or fname in fa:
            related.append(a)
        else:
            rest.append(a)
    rest.sort(key=len)
    return (codes[:3] + related + rest)[:limit]


def clean(s: str) -> str:
    return s.replace("\t", " ").replace("|", " ").replace("\n", " ").strip()


def main():
    gc = geonamescache.GeonamesCache()
    countries = gc.get_countries()
    us_states = {code: v["name"] for code, v in gc.get_us_states().items()}
    cities = gc.get_cities()

    lines = []
    for iso, c in sorted(countries.items()):
        aliases = COUNTRY_ALIASES.get(iso, [])
        lines.append("C\t%s\t%s\t%s" % (iso, clean(c["name"]), "|".join(clean(a) for a in aliases)))

    rows = []
    for gid, c in cities.items():
        tz = (c.get("timezone") or "").strip()
        if not tz:
            continue
        name = DISPLAY_OVERRIDES.get(int(c["geonameid"]), c["name"])
        cc = c["countrycode"]
        admin1 = us_states.get(c.get("admin1code", ""), "") if cc == "US" else ""
        alts = pick_alts(name, c.get("alternatenames") or [], int(c["population"]))
        if name != c["name"]:
            alts = [c["name"]] + [a for a in alts if a != c["name"]]
        rows.append((int(c["population"]), name, cc, admin1, tz, alts, float(c["latitude"]), float(c["longitude"])))

    rows.sort(key=lambda r: -r[0])
    for pop, name, cc, admin1, tz, alts, lat, lon in rows:
        lines.append("\t".join([clean(name), cc, clean(admin1), str(pop), tz, "%.2f" % lat, "%.2f" % lon,
                                 "|".join(clean(a) for a in alts)]))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    data = ("#catalog v2 %d\n" % len(rows) + "\n".join(lines) + "\n").encode("utf-8")
    with gzip.GzipFile(OUT, "wb", compresslevel=9, mtime=0) as f:
        f.write(data)
    print("cities: %d, countries: %d, raw %.0f KB, gz %.0f KB" % (
        len(rows), len(countries), len(data) / 1024, os.path.getsize(OUT) / 1024), file=sys.stderr)


if __name__ == "__main__":
    main()
