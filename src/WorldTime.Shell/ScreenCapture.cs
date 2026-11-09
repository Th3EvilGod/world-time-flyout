using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace WorldTime.Shell;

/// <summary>Copies a screen region (what DWM composed, including acrylic) for screenshots/tests.</summary>
public static unsafe class ScreenCapture
{
    /// <summary>Returns top-down BGRA pixels for the region, or null on failure.</summary>
    public static byte[]? Capture(PixelRect r)
    {
        if (r.IsEmpty) return null;
        var screen = PInvoke.GetDC(default);
        if (screen.IsNull) return null;
        var mem = PInvoke.CreateCompatibleDC(screen);
        var bmp = PInvoke.CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = PInvoke.SelectObject(mem, bmp);
        try
        {
            if (!PInvoke.BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, ROP_CODE.SRCCOPY)) return null;
            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = r.Width;
            info.bmiHeader.biHeight = -r.Height; // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0; // BI_RGB
            var pixels = new byte[r.Width * r.Height * 4];
            fixed (byte* p = pixels)
            {
                if (PInvoke.GetDIBits(mem, bmp, 0, (uint)r.Height, p, &info, DIB_USAGE.DIB_RGB_COLORS) == 0) return null;
            }
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // opaque
            return pixels;
        }
        finally
        {
            PInvoke.SelectObject(mem, old);
            PInvoke.DeleteObject(bmp);
            PInvoke.DeleteDC(mem);
            PInvoke.ReleaseDC(default, screen);
        }
    }
}
