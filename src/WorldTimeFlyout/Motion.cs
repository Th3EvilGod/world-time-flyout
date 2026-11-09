namespace WorldTimeFlyout;

/// <summary>
/// Windows 11 motion values ("Motion in Windows 11", Microsoft Learn):
///   Direct entrance   cubic-bezier(0, 0, 0, 1)        167/250/333 ms
///   Point to point    cubic-bezier(0.55, 0.55, 0, 1)  167/250/333 ms
///   Gentle exit       cubic-bezier(1, 0, 1, 1)        167 ms, with a fade
/// The calendar flyout slides in from the screen edge on the entrance curve and
/// animates its expand/collapse in about 300 ms; the panel uses the same values
/// so the two move as one surface.
/// </summary>
public readonly record struct Motion(string Name, double X1, double Y1, double X2, double Y2, double Milliseconds, bool FadeOut = false)
{
    public static readonly Motion Entrance = new("entrance", 0, 0, 0, 1, 333);
    public static readonly Motion PointToPoint = new("resize", 0.55, 0.55, 0, 1, 250);
    public static readonly Motion Correction = new("correction", 0.55, 0.55, 0, 1, 167);
    public static readonly Motion Exit = new("exit", 1, 0, 1, 1, 167, FadeOut: true);

    /// <summary>Eased progress (0..1) for linear time progress <paramref name="t"/> (0..1).</summary>
    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(s) = t for the curve parameter s (Newton, then bisection), return y(s).
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bezier(X1, X2, s) - t;
            if (Math.Abs(x) < 1e-5) return Bezier(Y1, Y2, s);
            double d = Derivative(X1, X2, s);
            if (Math.Abs(d) < 1e-6) break;
            s = Math.Clamp(s - x / d, 0, 1);
        }
        double lo = 0, hi = 1;
        s = t;
        for (int i = 0; i < 30; i++)
        {
            double x = Bezier(X1, X2, s);
            if (Math.Abs(x - t) < 1e-5) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Bezier(Y1, Y2, s);
    }

    private static double Bezier(double p1, double p2, double s)
    {
        double u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static double Derivative(double p1, double p2, double s)
    {
        double u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }
}
