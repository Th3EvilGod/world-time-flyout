using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WorldTime.Shell;

/// <summary>
/// Watches for the Windows 11 taskbar calendar flyout (hosted by
/// ShellExperienceHost.exe) and reports where the calendar card is and
/// whether it is expanded.
///
/// How: out-of-process WinEvent subscriptions (the documented accessibility
/// mechanism; no DLL is injected anywhere) scoped to the shell process, plus
/// a targeted UI Automation query to find the calendar card. While the flyout
/// is open its layout is re-read every <see cref="PollInterval"/> ms to follow
/// expand/collapse; when it is closed nothing runs.
///
/// All events are raised on the tracker's own thread.
/// </summary>
public sealed unsafe class CalendarFlyoutTracker : IDisposable
{
    public const uint PollInterval = 100;
    private const string ShellProcess = "shellexperiencehost.exe";

    private static CalendarFlyoutTracker? s_instance;
    private static uint s_foundShellPid;

    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private uint _threadId;
    private HWINEVENTHOOK _foregroundHook;
    private readonly List<HWINEVENTHOOK> _shellHooks = [];
    private uint _shellPid;
    private UiaCalendarReader? _reader;
    private nint _flyout;
    private CalendarState? _state;
    private List<UiNode> _lastAncestors = [];
    private string _lastKeySignature = "";
    private nuint _timer;
    private int _misses;
    private CalendarState? _pending;
    private bool _opening;

    private static bool Same(CalendarState a, CalendarState b) =>
        a.Card == b.Card && a.Expanded == b.Expanded && a.Window == b.Window;

    /// <summary>The calendar flyout was opened (or found already open).</summary>
    public event Action<CalendarState>? Opened;
    /// <summary>The card moved, resized, expanded or collapsed.</summary>
    public event Action<CalendarState>? Changed;
    /// <summary>The calendar flyout closed; the argument is a short reason for logs.</summary>
    public event Action<string>? Closed;
    /// <summary>Diagnostic messages.</summary>
    public event Action<string>? Log;

    public bool UiaAvailable { get; private set; }
    public CalendarState? Current => Volatile.Read(ref _state);
    public nint FlyoutWindow => Volatile.Read(ref _flyout);

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "CalendarFlyoutTracker" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>Re-read the layout as soon as possible (e.g. after our panel moved).</summary>
    public void RequestRefresh()
    {
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_APP + 7, 0, 0);
    }

    private void Run()
    {
        s_instance = this;
        _threadId = PInvoke.GetCurrentThreadId();
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        try
        {
            _reader = new UiaCalendarReader();
            UiaAvailable = true;
        }
        catch (Exception e)
        {
            Log?.Invoke($"UI Automation unavailable, using window bounds only: {e.Message}");
        }

        _foregroundHook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            default(HMODULE), &OnWinEvent, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        AttachToShell(FindShellPid());
        _started.Set();

        MSG msg;
        while (PInvoke.GetMessage(&msg, default, 0, 0))
        {
            if (msg.hwnd == default && msg.message == PInvoke.WM_TIMER) { Poll("timer"); continue; }
            if (msg.hwnd == default && msg.message == PInvoke.WM_APP + 7) { Poll("refresh"); continue; }
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        StopTimer();
        foreach (var h in _shellHooks) PInvoke.UnhookWinEvent(h);
        if (!_foregroundHook.IsNull) PInvoke.UnhookWinEvent(_foregroundHook);
        _reader?.Dispose();
        PInvoke.CoUninitialize();
        s_instance = null;
    }

    // ---------------------------------------------------------------- shell process

    private static uint FindShellPid()
    {
        s_foundShellPid = 0;
        PInvoke.EnumWindows(&EnumShellWindow, 0);
        return s_foundShellPid;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumShellWindow(HWND hwnd, LPARAM _)
    {
        uint pid = Win32Util.ProcessIdOf(hwnd);
        if (Win32Util.ProcessName(pid) == ShellProcess)
        {
            s_foundShellPid = pid;
            return false;
        }
        return true;
    }

    /// <summary>Scopes show/hide/cloak/move events to the shell process only.</summary>
    private void AttachToShell(uint pid)
    {
        if (pid == 0 || pid == _shellPid) return;
        foreach (var h in _shellHooks) PInvoke.UnhookWinEvent(h);
        _shellHooks.Clear();
        _shellPid = pid;
        const uint flags = PInvoke.WINEVENT_OUTOFCONTEXT;
        _shellHooks.Add(PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_SHOW, PInvoke.EVENT_OBJECT_HIDE, default(HMODULE), &OnWinEvent, pid, 0, flags));
        _shellHooks.Add(PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, default(HMODULE), &OnWinEvent, pid, 0, flags));
        _shellHooks.Add(PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED, default(HMODULE), &OnWinEvent, pid, 0, flags));
        Log?.Invoke($"watching {ShellProcess} pid {pid}");
    }

    // ---------------------------------------------------------------- events

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_instance?.Handle(ev, hwnd, idObject, idChild);
        }
        catch (Exception e)
        {
            s_instance?.Log?.Invoke($"event handler error: {e}");
        }
    }

    private void Handle(uint ev, nint hwnd, int idObject, int idChild)
    {
        if (hwnd == 0 || idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF) return;
        switch (ev)
        {
            case PInvoke.EVENT_SYSTEM_FOREGROUND:
            {
                uint pid = Win32Util.ProcessIdOf(hwnd);
                if (Win32Util.ProcessName(pid) == ShellProcess)
                {
                    AttachToShell(pid);
                    Consider(hwnd, "foreground");
                }
                else if (_flyout != 0)
                {
                    Poll("foreground moved");
                }
                break;
            }
            case PInvoke.EVENT_OBJECT_SHOW:
            case PInvoke.EVENT_OBJECT_UNCLOAKED:
                Consider(hwnd, ev == PInvoke.EVENT_OBJECT_SHOW ? "show" : "uncloak");
                break;
            case PInvoke.EVENT_OBJECT_HIDE:
            case PInvoke.EVENT_OBJECT_CLOAKED:
                if (hwnd == _flyout) CloseFlyout(ev == PInvoke.EVENT_OBJECT_HIDE ? "hidden" : "cloaked");
                break;
            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                if (hwnd == _flyout) Poll("moved");
                break;
        }
    }

    private void Consider(nint hwnd, string reason)
    {
        if (hwnd == _flyout) { Poll(reason); return; }
        if (!Win32Util.IsTopLevel(hwnd) || !Win32Util.IsVisible(hwnd) || Win32Util.IsCloaked(hwnd)) return;
        var rect = Win32Util.WindowRect(hwnd);
        if (rect.Width < 200 || rect.Height < 60) return;

        var state = ReadState(hwnd, fresh: true);
        if (state is null) return; // some other shell surface, not the calendar

        if (_flyout != 0) CloseFlyout("replaced");
        Volatile.Write(ref _flyout, hwnd);
        _misses = 0;
        StartTimer();
        Log?.Invoke($"flyout window ({reason}): class={Win32Util.ClassName(hwnd)} {state}");
        _opening = true;
        TryCompleteOpen(state);
    }

    /// <summary>Reports Opened once the card has a usable size (it grows in during the open animation).</summary>
    private void TryCompleteOpen(CalendarState state)
    {
        if (state.Card.Height < 60 * state.Monitor.Scale) return;
        _opening = false;
        Volatile.Write(ref _state, state);
        Log?.Invoke($"opened: {state}");
        Opened?.Invoke(state);
    }

    private void Poll(string reason)
    {
        var hwnd = _flyout;
        if (hwnd == 0) return;
        if (!Win32Util.IsWindow(hwnd) || !Win32Util.IsVisible(hwnd) || Win32Util.IsCloaked(hwnd))
        {
            CloseFlyout($"gone ({reason})");
            return;
        }
        var state = ReadState(hwnd, fresh: false);
        if (state is null)
        {
            if (++_misses >= 3) CloseFlyout("calendar no longer found");
            return;
        }
        _misses = 0;
        if (_opening) { TryCompleteOpen(state); return; }
        var prev = _state;
        if (prev is not null && Same(prev, state)) { _pending = null; return; }
        // Expand/collapse animates for ~300 ms: wait until two reads agree, so the
        // panel switches once instead of resizing along with every frame.
        if (_pending is null || !Same(_pending, state))
        {
            _pending = state;
            return;
        }
        _pending = null;
        Volatile.Write(ref _state, state);
        Log?.Invoke($"changed ({reason}): {state}");
        Changed?.Invoke(state);
    }

    private CalendarState? ReadState(nint hwnd, bool fresh)
    {
        var window = Win32Util.WindowRect(hwnd);
        var monitor = Win32Util.MonitorFor(window);
        if (_reader is null)
        {
            // Without UI Automation we cannot tell the calendar from other shell
            // surfaces; accept the classic CoreWindow host only.
            return Win32Util.ClassName(hwnd) == "Windows.UI.Core.CoreWindow"
                ? CalendarGeometry.FromWindowOnly(hwnd, window, monitor)
                : null;
        }

        var read = _reader.Read(hwnd, withAncestors: false);
        if (read is null) return null;
        var (keys, ancestors) = read.Value;
        var signature = string.Join(";", keys.Select(k => $"{k.AutomationId}{k.ControlType}{k.Rect}{k.Offscreen}"));
        bool hasCard = keys.Any(k => k.IsCard && !k.Rect.IsEmpty);
        if (!hasCard && (fresh || _lastAncestors.Count == 0 || signature != _lastKeySignature) && keys.Any(k => k.IsExpandButton))
        {
            // layout changed: refresh the container chain too
            read = _reader.Read(hwnd, withAncestors: true);
            if (read is null) return null;
            (keys, ancestors) = read.Value;
        }
        if (ancestors.Count > 0) _lastAncestors = ancestors;
        if (fresh || signature != _lastKeySignature)
            Log?.Invoke("uia keys: " + string.Join(" | ", keys.Select(k =>
                $"{k.AutomationId}/{k.ClassName}/{k.ControlType} {k.Rect} off={k.Offscreen} ec={k.ExpandState} '{k.Name}'")) +
                " || ancestors: " + string.Join(" > ", _lastAncestors.Select(a => $"{a.ClassName}/{a.ControlType} {a.Rect}")));
        _lastKeySignature = signature;
        return CalendarGeometry.Resolve(hwnd, window, keys, _lastAncestors, monitor);
    }

    private void CloseFlyout(string reason)
    {
        if (_flyout == 0) return;
        StopTimer();
        Volatile.Write(ref _flyout, 0);
        Volatile.Write(ref _state, null);
        _pending = null;
        bool wasOpening = _opening;
        _opening = false;
        _lastAncestors = [];
        _lastKeySignature = "";
        Log?.Invoke($"closed: {reason}");
        if (!wasOpening) Closed?.Invoke(reason);
    }

    private void StartTimer()
    {
        if (_timer == 0) _timer = PInvoke.SetTimer(default, 0, PollInterval, null);
    }

    private void StopTimer()
    {
        if (_timer != 0) PInvoke.KillTimer(default, _timer);
        _timer = 0;
    }
}
