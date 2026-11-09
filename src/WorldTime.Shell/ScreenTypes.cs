namespace WorldTime.Shell;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect Union(PixelRect a, PixelRect b) =>
        a.IsEmpty ? b : b.IsEmpty ? a :
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    public bool Contains(PixelRect o) => o.Left >= Left && o.Top >= Top && o.Right <= Right && o.Bottom <= Bottom;

    public override string ToString() => $"({Left},{Top} {Width}x{Height})";

}

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public readonly record struct MonitorInfo(PixelRect Bounds, PixelRect WorkArea, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}
