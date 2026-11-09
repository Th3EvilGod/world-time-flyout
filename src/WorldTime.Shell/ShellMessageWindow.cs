using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WorldTime.Shell;

public readonly record struct TrayMenuItem(int Id, string Text, bool Checked = false, bool Separator = false)
{
    public static TrayMenuItem Line => new(0, "", Separator: true);
}

/// <summary>
/// Hidden top-level window owned by the UI thread. It receives system
/// broadcasts (theme, accent, display changes), hosts the optional tray icon
/// and lets a second launch of the app ask this one to open the panel.
/// </summary>
public sealed unsafe class ShellMessageWindow : IDisposable
{
    public const string WindowClass = "WorldTimeFlyout.MessageWindow";
    private const uint WM_TRAY = PInvoke.WM_APP + 1;
    private const uint WM_SHOW_REQUEST = PInvoke.WM_APP + 2;

    private static ShellMessageWindow? s_instance;
    private static uint s_taskbarCreated;

    private HWND _hwnd;
    private HICON _icon;
    private bool _trayAdded;
    private string _iconPath = "";
    private string _tooltip = "World Time";

    public nint Handle => _hwnd;

    /// <summary>Tray icon left click (or keyboard select). Argument: anchor point in screen pixels.</summary>
    public event Action<int, int>? TrayActivated;
    /// <summary>Tray icon right click. Argument: anchor point.</summary>
    public event Action<int, int>? TrayContextMenu;
    /// <summary>WM_SETTINGCHANGE / colorization change. Argument: the setting area, e.g. "ImmersiveColorSet".</summary>
    public event Action<string>? SettingChanged;
    public event Action? DisplayChanged;
    /// <summary>Another instance asked us to open the panel.</summary>
    public event Action? ShowRequested;

    public void Create()
    {
        s_instance = this;
        s_taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        var hinst = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* cls = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)(nint)hinst.Value,
                lpszClassName = cls,
            };
            PInvoke.RegisterClassEx(&wc);
            fixed (char* title = "World Time Flyout")
                _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, cls, title, WINDOW_STYLE.WS_POPUP,
                    0, 0, 0, 0, default, default, (HINSTANCE)(nint)hinst.Value, null);
        }
    }

    /// <summary>Asks an already running instance to open its panel. Returns false when none runs.</summary>
    public static bool SignalExistingInstance()
    {
        fixed (char* cls = WindowClass)
        {
            var other = PInvoke.FindWindow(cls, null);
            if (other.IsNull) return false;
            PInvoke.PostMessage(other, WM_SHOW_REQUEST, 0, 0);
            return true;
        }
    }

    // ---------------------------------------------------------------- tray icon

    public void ShowTrayIcon(string iconPath, string tooltip)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        if (_icon.IsNull) _icon = LoadIcon(iconPath);
        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
                      NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _icon;
        data.szTip = tooltip;
        if (_trayAdded) { PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data); return; }
        _trayAdded = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data);
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data);
    }

    public void HideTrayIcon()
    {
        if (!_trayAdded) return;
        var data = NewData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
        _trayAdded = false;
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private static HICON LoadIcon(string path)
    {
        int cx = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        int cy = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);
        fixed (char* p = path)
        {
            var h = PInvoke.LoadImage(default, p, GDI_IMAGE_TYPE.IMAGE_ICON, cx, cy, IMAGE_FLAGS.LR_LOADFROMFILE);
            return (HICON)(nint)h.Value;
        }
    }

    /// <summary>Shows a native context menu at the cursor and returns the chosen id (0 = none).</summary>
    public int ShowMenu(IEnumerable<TrayMenuItem> items)
    {
        var menu = PInvoke.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Separator)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
                continue;
            }
            var flags = MENU_ITEM_FLAGS.MF_STRING | (item.Checked ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
            fixed (char* t = item.Text) PInvoke.AppendMenu(menu, flags, (nuint)item.Id, t);
        }
        System.Drawing.Point pt;
        PInvoke.GetCursorPos(&pt);
        PInvoke.SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = PInvoke.TrackPopupMenu(menu,
            TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN,
            pt.X, pt.Y, 0, _hwnd, null);
        PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, 0, 0);
        PInvoke.DestroyMenu(menu);
        return cmd;
    }

    // ---------------------------------------------------------------- window procedure

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wp, LPARAM lp)
    {
        try
        {
            if (s_instance is { } self && self.OnMessage(msg, wp, lp)) return (LRESULT)0;
        }
        catch
        {
            // never let an exception cross the native boundary
        }
        return PInvoke.DefWindowProc(hwnd, msg, wp, lp);
    }

    private bool OnMessage(uint msg, WPARAM wp, LPARAM lp)
    {
        if (msg == WM_TRAY)
        {
            uint ev = (uint)(lp.Value & 0xFFFF);
            int x = (short)(wp.Value & 0xFFFF), y = (short)((wp.Value >> 16) & 0xFFFF);
            if (ev == PInvoke.NIN_SELECT || ev == PInvoke.NIN_SELECT + 1 || ev == PInvoke.WM_LBUTTONUP)
                TrayActivated?.Invoke(x, y);
            else if (ev == PInvoke.WM_CONTEXTMENU || ev == PInvoke.WM_RBUTTONUP)
                TrayContextMenu?.Invoke(x, y);
            return true;
        }
        if (msg == WM_SHOW_REQUEST) { ShowRequested?.Invoke(); return true; }
        if (msg == s_taskbarCreated && s_taskbarCreated != 0)
        {
            // Explorer restarted: the tray icon is gone, add it again.
            if (_trayAdded) { _trayAdded = false; ShowTrayIcon(_iconPath, _tooltip); }
            DisplayChanged?.Invoke();
            return true;
        }
        switch (msg)
        {
            case PInvoke.WM_SETTINGCHANGE:
                SettingChanged?.Invoke(lp.Value != 0 ? new string((char*)lp.Value) : "");
                return false;
            case PInvoke.WM_DWMCOLORIZATIONCOLORCHANGED:
                SettingChanged?.Invoke("Colorization");
                return false;
            case PInvoke.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return false;
        }
        return false;
    }

    public void Dispose()
    {
        HideTrayIcon();
        if (!_icon.IsNull) { PInvoke.DestroyIcon(_icon); _icon = default; }
        if (!_hwnd.IsNull) { PInvoke.DestroyWindow(_hwnd); _hwnd = default; }
        s_instance = null;
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorldTimeFlyout";

    public static bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && v.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --background");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
