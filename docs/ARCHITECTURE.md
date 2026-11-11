# Architecture

## Goals, in priority order

1. Open beside the native Windows 11 calendar when the taskbar clock is clicked.
2. Feel like part of Windows: Fluent controls, system theme, accent, acrylic, DPI.
3. Fast multi-city comparison on one shared time cursor.
4. Near-zero cost when idle; small footprint; no admin, no injection, no network.

## Technology

| Choice | Why |
|---|---|
| C# on .NET 10, compiled with **NativeAOT** | Native startup and no .NET runtime to install, while keeping a productive, memory-safe language. |
| **WinUI 3** (Windows App SDK 2.6), self-contained | The real Windows 11 controls: menus, text box, pips pager, info bar, calendar and time pickers, focus visuals, contrast themes, screen-reader support. Bundled so nothing else needs installing. |
| UI built in C#, no XAML markup | Fully AOT-safe (no reflection binding), compile-checkable anywhere, and every custom visual is repainted explicitly on theme/accent change. |
| **Noda Time** + bundled tzdb | Correct historical and future offsets, independent of Windows registry time-zone data; IANA ids everywhere. |
| **CsWin32** | Generated, blittable P/Invoke and COM (UI Automation) definitions that work under NativeAOT. |

Rejected: Rust + Direct2D (smallest, but every control hand-drawn, which is what made the
first prototype look cheap); Electron/WebView (heavy, not native); C++/WinRT WinUI (same
look as C# but much slower to build and maintain).

## Projects

```
src/WorldTime.Core      platform-neutral: time math, formatting, city catalog + search, settings (unit tested)
src/WorldTime.Shell     Windows integration: calendar flyout tracker, UI Automation reader, geometry,
                        tray/message window, clipboard, startup registration, screen capture
src/WorldTimeFlyout     WinUI app: controller (lifecycle), model, panel window, views
tests/                  xUnit tests for Core
tools/                  catalog generator, icon generator, measurement script, flyout probe (Rust)
```

## Calendar integration

The Windows 11 notification center and calendar live in one shell window owned by
`ShellExperienceHost.exe`.

1. **Events, not polling.** `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` (the documented
   accessibility mechanism; nothing is loaded into another process): one global hook for
   foreground changes, plus show/hide, cloak/uncloak and move events scoped to the shell
   process id. The shell process is re-found if Explorer restarts.
2. **Identify and measure with UI Automation.** When a shell window appears, one cached UIA
   query looks for the calendar's `ExpandCollapseButton` and `CalendarView`. If neither
   exists it is some other shell surface and is ignored. The card's bounds come from the
   button's container chain (`CalendarGeometry.Resolve`); expanded means the month grid is
   on screen.
3. **Follow it while open.** While the calendar is visible its layout is re-read every
   33 ms (one cached UIA element refresh) to catch expand/collapse; when it closes,
   everything stops.
4. **Place the panel** immediately left of the card, same top/bottom/width, on the card's
   monitor (`CalendarGeometry.PanelRect`). Compact mode never gets shorter than the tiles
   and the strip under them.

#### Finding the shell host

ShellExperienceHost is often not running when the app starts at sign-in; it starts on
the first click on the clock and Windows may restart it later. Its windows are immersive,
so `EnumWindows` never lists them; the tracker finds it by process name, and otherwise
from system-wide foreground and cloak events (both rare). While the shell host is new,
its window comes to the front before it is sized, shown and uncloaked, without further
events we can rely on, so for 4 s after any sign of it the tracker looks through its
visible CoreWindows every ~100 ms for the calendar card. Integration ends the shell host
before the app starts and again mid-session to cover both cases.

## Motion

The panel window itself moves, one `SetWindowPos` per rendered frame (`PanelWindow`,
`Motion`), using the values from Microsoft's "Motion in Windows 11" guidance:

| What | Curve | Duration |
|---|---|---|
| Slide in from the right (with the calendar) | cubic-bezier(0, 0, 0, 1) | 333 ms |
| Expand / collapse, bottom edge fixed | cubic-bezier(0.55, 0.55, 0, 1) | 250 ms |
| Slide out to the right, with a fade | cubic-bezier(1, 0, 1, 1) | 167 ms |

The calendar starts its slide when the shell uncloaks its window, so the tracker passes
that event's timestamp along and the panel's slide is started "in the past" to stay in
step. When the calendar's window is already known, the panel starts moving at the uncloak
with the layout the calendar had last time, instead of waiting for the card to be laid
out. The exit starts as soon as the calendar loses the foreground (it is cloaked only
after its own exit animation). During expand/collapse both layouts stay laid out at their
own final heights, pinned to the bottom edge, and cross-fade.

The panel window is `WS_EX_NOACTIVATE` and answers `WM_MOUSEACTIVATE` with `MA_NOACTIVATE`;
buttons don't take focus on click. So clicking times, paging and dragging the timeline do
not deactivate the calendar. Typing (search) and menus need focus; the panel then becomes
the active window, the calendar closes, and the panel stays until it loses focus or Esc.

If UI Automation is unavailable the tracker falls back to the shell window's bounds. The
tray icon (and launching the exe again) always opens the panel on its own.

## Time model

One absolute `Instant` is selected (`TimeSelection`). Every row renders that instant in its
own zone. "Live" follows the clock and ticks once per minute only while the panel is
visible. Step boundaries (15/30/60 min) are defined in the reference city's local time,
and are computed per zone interval so they behave across DST gaps and overlaps
(`TimeMath`, covered by tests for New York, Lord Howe, Kathmandu, Kolkata, Adelaide and the
date line).

Stepping away from "now" starts at the current time rounded to the nearest step (the
anchor), so steps land on clean times and the shift is shown in whole steps relative to
the anchor (`TimeSelection.Anchor`, `OffsetFromAnchor`). Day steps and the date picker keep
the time of day in the reference city.

## Resource use

- Hidden: no timers, no polling. The only activity is the shell's own window events.
- Visible: one timer per minute boundary; UIA refreshes every 33 ms only while the calendar is open.
- The city catalog (750 KB compressed) is loaded only when search opens.
- Measured numbers for each build are in the CI summary and in `metrics.md` of each release.

## Settings

`%LOCALAPPDATA%\WorldTimeFlyout\settings.json` (atomic writes; repaired or reset if
hand-edited badly). `log.txt` in the same folder records errors; create `diag.on` there (or
start with `--diag`) to also log calendar tracking details.

## Installer and updates

The installer is Inno Setup (`installer/WorldTimeFlyout.iss`). It asks "for me only"
(no admin, `%LOCALAPPDATA%\Programs\World Time Flyout`, HKCU entries) or "for all users"
(`Program Files`, HKLM entries, Inno elevates itself); `/CURRENTUSER` and `/ALLUSERS`
choose on the command line. It adds a Start menu entry and an Installed apps entry. Before copying files it asks a running copy to exit (`WM_APP+3` to the
`WorldTimeFlyout.MessageWindow` window; `--exit` does the same) and then makes sure with
`taskkill`, since older versions don't know the request. A silent install starts the app in
the background; uninstall removes the "Start with Windows" entry if it points at the
installed copy and, when not silent, offers to delete the settings.

`Updater` reads `releases/latest` from the GitHub REST API once a day (90 s after start at
the earliest; 6 h after a failure). `UpdateFeed.Choose` (pure, unit-tested) accepts only a
newer, non-draft, non-pre-release version that has both
`WorldTimeFlyout-Setup-<version>-win-x64.exe` and its `.sha256` over HTTPS. Install
downloads both, hashes the installer while downloading, refuses a mismatch, then runs the
installer with `/VERYSILENT` plus `/ALLUSERS` or `/CURRENTUSER` (from which Installed apps
entry points at the running copy) and waits: the installer closes the app when it is ready
to replace the files and restarts it as the original, non-elevated user
(`runasoriginaluser`). If the installer ends first (Windows prompt declined, error), the app
reports it and keeps running. A "just me" copy in a folder the user can't write is not
updated in place; the panel explains how to reinstall it for all users. Test hooks:
`--update-feed URL` (loopback only), `--pretend-version X`, `--update-now`; CI uses them
to run a real over-the-air update against a local copy of a release, plus a tampered one.
