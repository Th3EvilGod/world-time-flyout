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
/// mechanism; no DLL is injected anywhere): foreground and cloak changes
/// system-wide (both rare), show/hide/move scoped to the shell process, plus a
/// targeted UI Automation query to find the calendar card. While the flyout is
/// open its layout is re-read every <see cref="PollInterval"/> ms to follow
/// expand/collapse; when it is closed nothing runs.
///
/// The shell host is often not running yet when the app starts at sign-in, is
/// started on the first click on the clock, and can be restarted by Windows at
/// any time, so it is found again from those system-wide events.
///
/// All events are raised on the tracker's own thread.
/// </summary>
public sealed unsafe class CalendarFlyoutTracker : IDisposable
{
    public const uint PollInterval = 33;
    private const string ShellProcess = "shellexperiencehost.exe";

    private static CalendarFlyoutTracker? s_instance;

    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private uint _threadId;
    private HWINEVENTHOOK _foregroundHook, _cloakHook;
    private readonly List<HWINEVENTHOOK> _shellHooks = [];
    private uint _shellPid;
    private UiaCalendarReader? _reader;
    private nint _flyout;
    private CalendarState? _state;
    private List<UiNode> _lastAncestors = [];
    private string _lastKeySignature = "";
    private nuint _timer;
    private int _misses;
    private bool _opening;
    private bool _announced;          // Opening was raised for the current open
    private bool _closingRaised;
    private nint _lastCalendarHwnd;   // the shell keeps the same window across opens
    private CalendarState? _lastSettled;
    // Layout the calendar had when it last opened. Windows reopens it the same
    // way (on 26H2 it reopened expanded even after being collapsed), so this
    // predicts the next open better than the last settled layout.
    private CalendarState? _lastOpened;
    // expand/collapse smoothing
    private PixelRect? _lastRawCard;
    private long _lastHeightChange;   // Environment.TickCount64
    private int _lastSlide;
    private bool? _predictedExpanded;
    /// <summary>
    /// The card's height must stay put this long before it counts as settled: UIA
    /// reports the expand/collapse animation in uneven steps, and two reads a few
    /// ms apart (an event plus the timer) can see the same in-between height.
    /// </summary>
    private const int SettleMs = 120;
    private int? _settledExpandedHeight, _settledCollapsedHeight;

    private static bool Same(CalendarState a, CalendarState b) =>
        a.Card == b.Card && a.Expanded == b.Expanded && a.Window == b.Window;

    /// <summary>
    /// The known calendar window is appearing again: raised the moment it starts
    /// its slide-in, with the layout it had last time, so the companion can move
    /// in sync instead of waiting ~150 ms for the card to be laid out.
    /// </summary>
    public event Action<CalendarState>? Opening;
    /// <summary>The calendar flyout was opened (or found already open).</summary>
    public event Action<CalendarState>? Opened;
    /// <summary>
    /// The foreground moved away from the calendar, so it is starting to close
    /// (its own exit animation runs before it is cloaked).
    /// </summary>
    public event Action<string>? Closing;
    /// <summary>The card moved, resized, expanded or collapsed.</summary>
    public event Action<CalendarState>? Changed;
    /// <summary>The calendar flyout closed; the argument is a short reason for logs.</summary>
    public event Action<string>? Closed;
    /// <summary>Diagnostic messages.</summary>
    public event Action<string>? Log;
    /// <summary>The few messages worth keeping in the log even without --diag.</summary>
    public event Action<string>? Notice;

    // While the shell host is starting cold its windows are shown, sized and
    // uncloaked without events we can rely on: for a few seconds after any sign
    // of it, look through its windows for the calendar instead.
    private long _scanUntil;
    private const int ScanMs = 4000;
    private bool _firstOpenNoticed;

    public bool UiaAvailable { get; private set; }

    /// <summary>
    /// When the current calendar window was uncloaked/shown, in
    /// Environment.TickCount64 milliseconds: its slide-in starts then, so a
    /// companion animation can be started "in the past" to stay in step.
    /// </summary>
    public long OpenStartTick => Volatile.Read(ref _openTick);
    private long _openTick;
    private uint _eventTime;

    private static long EventTick(uint eventTime)
    {
        long now = Environment.TickCount64;
        uint age = unchecked((uint)now - eventTime);
        return eventTime != 0 && age < 5000 ? now - age : now;
    }
    public CalendarState? Current => Volatile.Read(ref _state);
    public nint FlyoutWindow => Volatile.Read(ref _flyout);

    /// <summary>Starts the tracker thread; returns once its event hooks are in place.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "CalendarFlyoutTracker" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
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

        // Hooks first: at sign-in UI Automation can be slow to answer, and events
        // that arrive meanwhile simply queue up on this thread.
        const uint global = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            default(HMODULE), &OnWinEvent, 0, 0, global);
        _cloakHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED,
            default(HMODULE), &OnWinEvent, 0, 0, global);
        uint shell = FindShellPid();
        if (shell != 0) AttachToShell(shell, "running at start");
        else Notice?.Invoke("shell host not running yet; waiting for the first calendar open");
        _started.Set();

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _reader = new UiaCalendarReader();
            _reader.WarmUp(); // the first UIA query in a process is slow; pay for it now, not on the first open
            UiaAvailable = true;
            Notice?.Invoke($"UI Automation ready in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
        }
        catch (Exception e)
        {
            Notice?.Invoke($"UI Automation unavailable, using window bounds only: {e.Message}");
        }

        MSG msg;
        while (PInvoke.GetMessage(&msg, default, 0, 0))
        {
            if (msg.hwnd == default && msg.message == PInvoke.WM_TIMER)
            {
                if (_flyout == 0) Scan();
                else Poll("timer");
                continue;
            }
            if (msg.hwnd == default && msg.message == PInvoke.WM_APP + 7) { Poll("refresh"); continue; }
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        StopTimer();
        foreach (var h in _shellHooks) PInvoke.UnhookWinEvent(h);
        if (!_foregroundHook.IsNull) PInvoke.UnhookWinEvent(_foregroundHook);
        if (!_cloakHook.IsNull) PInvoke.UnhookWinEvent(_cloakHook);
        _reader?.Dispose();
        PInvoke.CoUninitialize();
        s_instance = null;
    }

    // ---------------------------------------------------------------- shell process

    /// <summary>
    /// The shell host in this session, by process (its windows are immersive, and
    /// EnumWindows does not list those to desktop apps). 0 when it isn't running.
    /// </summary>
    private static uint FindShellPid()
    {
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ShellExperienceHost"))
            {
                using (p)
                    if (p.SessionId == session) return (uint)p.Id;
            }
        }
        catch (Exception)
        {
            // fall through: found later from events
        }
        return 0;
    }

    /// <summary>Scopes show/hide/move events to the shell process (they are frequent system-wide).</summary>
    private void AttachToShell(uint pid, string how)
    {
        if (pid == 0 || pid == _shellPid) return;
        foreach (var h in _shellHooks) PInvoke.UnhookWinEvent(h);
        _shellHooks.Clear();
        _shellPid = pid;
        _shellAttachedTick = Environment.TickCount64;
        const uint flags = PInvoke.WINEVENT_OUTOFCONTEXT;
        _shellHooks.Add(PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_SHOW, PInvoke.EVENT_OBJECT_HIDE, default(HMODULE), &OnWinEvent, pid, 0, flags));
        _shellHooks.Add(PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, default(HMODULE), &OnWinEvent, pid, 0, flags));
        Notice?.Invoke($"watching {ShellProcess} pid {pid} ({how})");
    }

    private bool IsShell(nint hwnd, out uint pid)
    {
        pid = Win32Util.ProcessIdOf(hwnd);
        return pid != 0 && (pid == _shellPid || Win32Util.ProcessName(pid) == ShellProcess);
    }

    // ---------------------------------------------------------------- events

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (s_instance is { } t)
            {
                t._eventTime = time;
                t.Handle(ev, hwnd, idObject, idChild);
            }
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
                if (IsShell(hwnd, out uint pid))
                {
                    AttachToShell(pid, "foreground");
                    Consider(hwnd, "foreground");
                }
                else if (_flyout != 0)
                {
                    // Our own windows are excluded from these events, so this is
                    // another app (or the taskbar): the calendar is dismissing.
                    if (!_closingRaised)
                    {
                        _closingRaised = true;
                        Log?.Invoke("closing: foreground moved");
                        Closing?.Invoke("foreground moved");
                    }
                    Poll("foreground moved");
                }
                break;
            }
            case PInvoke.EVENT_OBJECT_SHOW:
                Consider(hwnd, "show");
                break;
            case PInvoke.EVENT_OBJECT_UNCLOAKED:
                // system-wide: only the shell host's windows matter
                if (IsShell(hwnd, out uint shellPid))
                {
                    AttachToShell(shellPid, "uncloak");
                    Consider(hwnd, "uncloak");
                }
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
        long openTick = EventTick(_eventTime);
        if (!LooksOpen(hwnd, out var rect))
        {
            // A shell host that has just started brings its window to the front
            // before it is sized, shown and uncloaked, and those later changes
            // raise no event we act on: keep checking it for a moment.
            StartScan(hwnd, reason, "not on screen yet");
            return;
        }

        bool known = hwnd == _lastCalendarHwnd && _lastOpened is not null;
        if (known)
        {
            // The calendar window we already know is coming back and is sliding in
            // right now: announce it before the (slower) UI Automation read, with
            // the layout it will most likely have.
            BeginOpen(hwnd, openTick);
            _announced = true;
            var basis = _reopenLikeSettled ? _lastSettled ?? _lastOpened! : _lastOpened!;
            var predicted = basis with { Hwnd = hwnd, Window = rect, Source = "predicted", SlideOffset = 0 };
            Log?.Invoke($"opening (predicted, {(_reopenLikeSettled ? "as last left" : "as last opened")}): {predicted}");
            Opening?.Invoke(predicted);
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = ReadState(hwnd, fresh: true);
        var readMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (state is null && !known)
        {
            // Another shell surface, or the calendar of a shell host that is still
            // starting (its card isn't laid out yet): look again for a moment.
            StartScan(hwnd, reason, "no calendar card yet");
            return;
        }
        StopScan();
        if (!known) BeginOpen(hwnd, openTick);
        Log?.Invoke($"flyout window {hwnd:X} ({reason}, read {readMs:0} ms, event {Environment.TickCount64 - openTick} ms ago): " +
                    $"class={Win32Util.ClassName(hwnd)} {state}");
        if (state is not null) TryCompleteOpen(state);
    }

    private long _shellAttachedTick;
    private int _scanSkip;

    private static bool LooksOpen(nint hwnd, out PixelRect rect)
    {
        rect = default;
        if (!Win32Util.IsTopLevel(hwnd) || !Win32Util.IsVisible(hwnd) || Win32Util.IsCloaked(hwnd)) return false;
        rect = Win32Util.WindowRect(hwnd);
        return rect.Width >= 200 && rect.Height >= 60;
    }

    /// <summary>
    /// Look through the shell host's windows for a few seconds. Only while it is
    /// new to us (just started, or no calendar seen yet this session), so other
    /// shell surfaces (toasts, quick settings) cost nothing later on.
    /// </summary>
    private void StartScan(nint hwnd, string reason, string why)
    {
        bool coldShell = !_firstOpenNoticed || Environment.TickCount64 - _shellAttachedTick < 15_000;
        if (!coldShell || _shellPid == 0) return;
        bool already = _scanUntil != 0;
        _scanUntil = Environment.TickCount64 + ScanMs;
        if (already) return;
        _scanSkip = 0;
        Log?.Invoke($"shell window {hwnd:X} ({reason}): {why}; looking through the shell's windows for {ScanMs} ms");
        StartTimer();
    }

    private void StopScan()
    {
        if (_scanUntil == 0) return;
        _scanUntil = 0;
        if (_flyout == 0) StopTimer();
    }

    private void Scan()
    {
        if (_scanUntil == 0) { StopTimer(); return; }
        if (Environment.TickCount64 > _scanUntil)
        {
            Log?.Invoke("shell scan: no calendar appeared");
            StopScan();
            return;
        }
        if (++_scanSkip % 3 != 0) return; // every ~100 ms is plenty here
        HWND w = default;
        for (int i = 0; i < 64; i++)
        {
            fixed (char* cls = "Windows.UI.Core.CoreWindow")
                w = PInvoke.FindWindowEx(default, w, cls, null);
            if (w.IsNull) break;
            nint h = (nint)w.Value;
            if (Win32Util.ProcessIdOf(h) != _shellPid || !LooksOpen(h, out _)) continue;
            var state = ReadState(h, fresh: true);
            if (state is null) continue;
            StopScan();
            // It came on screen within the last ~100 ms: its slide-in starts about now.
            BeginOpen(h, Environment.TickCount64);
            Log?.Invoke($"flyout window {h:X} (found by scanning the shell host): {state}");
            TryCompleteOpen(state);
            return;
        }
    }

    private void BeginOpen(nint hwnd, long openTick)
    {
        if (_flyout != 0) CloseFlyout("replaced");
        Volatile.Write(ref _openTick, openTick);
        Volatile.Write(ref _flyout, hwnd);
        _misses = 0;
        _closingRaised = false;
        _opening = true;
        _announced = false;
        StartTimer();
    }

    /// <summary>
    /// Whether the calendar reopens the way it was last left (expanded or
    /// collapsed) rather than the way it last opened. Builds differ; learned
    /// from what actually happens.
    /// </summary>
    private bool _reopenLikeSettled;

    /// <summary>Reports Opened once the card has a usable size (it grows in during the open animation).</summary>
    private void TryCompleteOpen(CalendarState state)
    {
        if (state.Card.Height < 60 * state.Monitor.Scale) return;
        if (_lastOpened is { } lo && _lastSettled is { } ls && lo.Expanded != ls.Expanded)
            _reopenLikeSettled = state.Expanded == ls.Expanded;
        _opening = false;
        _lastRawCard = state.Card;
        _lastSlide = state.SlideOffset;
        _lastHeightChange = 0;
        _lastCalendarHwnd = state.Hwnd;
        _lastOpened = state;
        Remember(state);
        Volatile.Write(ref _state, state);
        Log?.Invoke($"opened: {state}");
        if (!_firstOpenNoticed)
        {
            _firstOpenNoticed = true;
            Notice?.Invoke($"calendar found: {state}");
        }
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
            // while opening the card may take a few reads to appear (~0.5 s max)
            if (++_misses >= (_opening ? 15 : 3)) CloseFlyout("calendar no longer found");
            return;
        }
        _misses = 0;
        if (_opening) { TryCompleteOpen(state); return; }

        // Esc or a click elsewhere: the card starts sliding back out to the right
        // before the foreground moves (and ~250 ms before the window is cloaked).
        int lastSlide = _lastSlide;
        _lastSlide = state.SlideOffset;
        if (!_closingRaised && state.SlideOffset > lastSlide && state.SlideOffset > 6 * state.Monitor.Scale)
        {
            _closingRaised = true;
            Log?.Invoke($"closing: card sliding out ({lastSlide} -> {state.SlideOffset} px)");
            Closing?.Invoke("card sliding out");
        }

        // Expand/collapse animates the card's height for ~300 ms. As soon as it
        // starts, jump to the final height (known from earlier in the session, else
        // the usual Windows 11 size) so the panel resizes once, alongside; report
        // the real size only after it has stopped changing.
        var last = _lastRawCard;
        _lastRawCard = state.Card;
        long now = Environment.TickCount64;
        if (last is { } l && l.Height != state.Card.Height)
        {
            _lastHeightChange = now;
            bool expanding = state.Card.Height > l.Height;
            if (_predictedExpanded != expanding)
            {
                _predictedExpanded = expanding;
                double s = state.Monitor.Scale;
                int h = (expanding ? _settledExpandedHeight : _settledCollapsedHeight)
                        ?? (int)Math.Round((expanding ? 455 : 100) * s);
                var c = state.Card;
                var predicted = state with
                {
                    Card = new PixelRect(c.Left, c.Bottom - h, c.Right, c.Bottom),
                    Expanded = expanding,
                    Source = state.Source + "+predicted",
                };
                Report(predicted, reason);
            }
            return;
        }
        if (now - _lastHeightChange < SettleMs) return; // may still be moving

        // settled
        _predictedExpanded = null;
        Remember(state);
        var prev = _state;
        if (prev is not null && Same(prev, state)) return;
        Report(state, reason);
    }

    private void Remember(CalendarState state)
    {
        if (state.Expanded) _settledExpandedHeight = state.Card.Height;
        else _settledCollapsedHeight = state.Card.Height;
        _lastSettled = state;
        _lastCalendarHwnd = state.Hwnd;
    }

    private void Report(CalendarState state, string reason)
    {
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

        if (!fresh && _reader.ReadFast(hwnd) is { } fast)
        {
            var quick = CalendarGeometry.Resolve(hwnd, window, fast, _lastAncestors, monitor);
            if (quick is not null) return quick;
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
        _lastRawCard = null;
        _predictedExpanded = null;
        bool wasOpening = _opening, announced = _announced;
        _opening = false;
        _announced = false;
        _closingRaised = false;
        _lastAncestors = [];
        _lastKeySignature = "";
        Log?.Invoke($"closed: {reason}");
        if (!wasOpening || announced) Closed?.Invoke(reason);
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
