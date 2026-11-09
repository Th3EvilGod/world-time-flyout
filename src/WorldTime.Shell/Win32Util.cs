using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

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

    internal static PixelRect From(RECT r) => new(r.left, r.top, r.right, r.bottom);
}

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public readonly record struct MonitorInfo(PixelRect Bounds, PixelRect WorkArea, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>Small, allocation-light Win32 helpers.</summary>
public static unsafe class Win32Util
{
    private static readonly Dictionary<uint, string> ProcessNames = new();
    private static readonly Lock ProcessNamesLock = new();

    public static uint ProcessIdOf(nint hwnd)
    {
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId((HWND)hwnd, &pid);
        return pid;
    }

    /// <summary>Lower-case image file name ("shellexperiencehost.exe"); cached per pid.</summary>
    public static string ProcessName(uint pid)
    {
        lock (ProcessNamesLock)
        {
            if (ProcessNames.TryGetValue(pid, out var cached)) return cached;
        }
        string name = "";
        var h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (!h.IsNull)
        {
            char* buf = stackalloc char[520];
            uint len = 520;
            if (PInvoke.QueryFullProcessImageName(h, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, &len))
            {
                var full = new string(buf, 0, (int)len);
                name = Path.GetFileName(full).ToLowerInvariant();
            }
            PInvoke.CloseHandle(h);
        }
        lock (ProcessNamesLock)
        {
            if (ProcessNames.Count > 512) ProcessNames.Clear(); // pids get reused; keep it small
            ProcessNames[pid] = name;
        }
        return name;
    }

    public static string ClassName(nint hwnd)
    {
        char* buf = stackalloc char[256];
        int n = PInvoke.GetClassName((HWND)hwnd, buf, 256);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static bool IsWindow(nint hwnd) => hwnd != 0 && PInvoke.IsWindow((HWND)hwnd);

    public static bool IsVisible(nint hwnd) => PInvoke.IsWindowVisible((HWND)hwnd);

    public static bool IsTopLevel(nint hwnd) => PInvoke.GetAncestor((HWND)hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == (HWND)hwnd;

    public static bool IsCloaked(nint hwnd)
    {
        uint v = 0;
        var hr = PInvoke.DwmGetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &v, sizeof(uint));
        return hr.Succeeded && v != 0;
    }

    /// <summary>Visible frame bounds (excludes invisible resize borders).</summary>
    public static PixelRect WindowRect(nint hwnd)
    {
        RECT r;
        var hr = PInvoke.DwmGetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT));
        if (hr.Failed) PInvoke.GetWindowRect((HWND)hwnd, &r);
        return PixelRect.From(r);
    }

    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();

    public static bool SetForeground(nint hwnd) => PInvoke.SetForegroundWindow((HWND)hwnd);

    public static MonitorInfo MonitorFor(PixelRect r)
    {
        var rect = new RECT { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom };
        var mon = PInvoke.MonitorFromRect(&rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return Describe(mon);
    }

    public static MonitorInfo MonitorForWindow(nint hwnd) =>
        Describe(PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    public static MonitorInfo PrimaryMonitor() =>
        Describe(PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));

    private static MonitorInfo Describe(HMONITOR mon)
    {
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, &mi);
        uint dx = 96, dy = 96;
        PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dx, &dy);
        return new MonitorInfo(PixelRect.From(mi.rcMonitor), PixelRect.From(mi.rcWork), dx == 0 ? 96 : dx);
    }

    /// <summary>The primary taskbar's edge and bounds.</summary>
    public static (TaskbarEdge Edge, PixelRect Bounds, bool AutoHide) Taskbar()
    {
        var abd = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        var state = PInvoke.SHAppBarMessage(PInvoke.ABM_GETSTATE, &abd);
        var pos = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        PInvoke.SHAppBarMessage(PInvoke.ABM_GETTASKBARPOS, &pos);
        return ((TaskbarEdge)Math.Clamp((int)pos.uEdge, 0, 3), PixelRect.From(pos.rc), (state & 1) != 0);
    }

    // ---- window styling for the companion panel ----

    public static void SetCornerPreference(nint hwnd, bool round)
    {
        var pref = round ? DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND : DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &pref, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    public static void SetDarkFrame(nint hwnd, bool dark)
    {
        BOOL v = dark;
        PInvoke.DwmSetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &v, (uint)sizeof(BOOL));
    }

    /// <summary>Adds WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW so clicks don't steal focus from the calendar.</summary>
    public static void MakeNonActivating(nint hwnd)
    {
        var ex = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        ex &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        PInvoke.SetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)ex);
        PInvoke.SetWindowSubclass((HWND)hwnd, &NoActivateSubclass, 0x5754, 0);
    }

    /// <summary>When true, a click is allowed to activate the panel (e.g. while typing a search).</summary>
    public static volatile bool AllowClickActivation;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT NoActivateSubclass(HWND hwnd, uint msg, WPARAM wp, LPARAM lp, nuint id, nuint data)
    {
        if (msg == PInvoke.WM_MOUSEACTIVATE && !AllowClickActivation)
            return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
        return PInvoke.DefSubclassProc(hwnd, msg, wp, lp);
    }

    public static void SetTopmostNoActivate(nint hwnd, PixelRect r, bool show)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (show ? SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW : 0);
        PInvoke.SetWindowPos((HWND)hwnd, (HWND)(nint)(-1), r.Left, r.Top, r.Width, r.Height, flags);
    }

    // ---- clipboard ----

    public static bool CopyText(nint owner, string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (PInvoke.OpenClipboard((HWND)owner)) break;
            if (attempt == 4) return false;
            Thread.Sleep(20); // another app may hold the clipboard briefly
        }
        try
        {
            PInvoke.EmptyClipboard();
            nuint bytes = (nuint)((text.Length + 1) * 2);
            var mem = PInvoke.GlobalAlloc(Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);
            if (mem.IsNull) return false;
            var dst = (char*)PInvoke.GlobalLock(mem);
            if (dst is null) { PInvoke.GlobalFree(mem); return false; }
            fixed (char* src = text) Buffer.MemoryCopy(src, dst, (long)bytes, text.Length * 2);
            dst[text.Length] = '\0';
            PInvoke.GlobalUnlock(mem);
            const uint CF_UNICODETEXT = 13;
            if (PInvoke.SetClipboardData(CF_UNICODETEXT, (HANDLE)(nint)mem.Value).IsNull)
            {
                PInvoke.GlobalFree(mem);
                return false;
            }
            return true;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }
}
