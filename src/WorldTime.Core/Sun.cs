using NodaTime;

namespace WorldTime.Core;

/// <summary>
/// Low-precision solar position (accurate to a few minutes for sunrise and
/// sunset), enough for a day/night indicator. Based on the NOAA / Astronomical
/// Almanac approximation.
/// </summary>
public static class Sun
{
    private const double Rad = Math.PI / 180;

    /// <summary>Apparent altitude of the sun's center in degrees.</summary>
    public static double Altitude(Instant t, double latitude, double longitude)
    {
        double n = t.ToJulianDate() - 2451545.0;
        double L = Norm(280.460 + 0.9856474 * n);
        double g = Norm(357.528 + 0.9856003 * n) * Rad;
        double lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Rad;
        double eps = (23.439 - 0.0000004 * n) * Rad;
        double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        double gmst = Norm(280.46061837 + 360.98564736629 * n);
        double h = (gmst + longitude) * Rad - ra;
        double lat = latitude * Rad;
        return Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(h)) / Rad;
    }

    /// <summary>True between sunrise and sunset (upper limb with refraction, -0.833 degrees).</summary>
    public static bool IsUp(Instant t, double latitude, double longitude) => Altitude(t, latitude, longitude) > -0.833;

    private static double Norm(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }
}
