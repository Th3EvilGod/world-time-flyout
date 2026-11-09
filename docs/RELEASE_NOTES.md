First release of World Time Flyout: your cities next to the Windows 11 taskbar calendar.

Click the clock on the taskbar (or press Win+N) and the panel slides in beside the
calendar, matches its theme and accent color, follows it when you expand or collapse
it, and slides out with it.

## What's in it

- **Full view** next to the expanded calendar: every city with its time, day and
  difference from your main city, a timeline to drag, arrows to move a whole day, and
  a month view when you click the date.
- **Compact view** next to the collapsed calendar: three city tiles, ‹ › to cycle
  through the rest, and −/+ to move the time by your step (30 minutes by default).
- **One time for every city.** Drag the timeline, click an hour, use the mouse wheel
  (up = later) or the step buttons. Steps land on clean times: at 10:27, back is
  10:00 (−30m) and forward is 11:00 (+30m). "Now" jumps back.
- **Copy** a time with a click ("New York: 9:00 AM"). Right-click for copy with date,
  copy all, reorder, change the main city or remove.
- **Offline city search**: about 34,000 cities, plus countries, time zone names
  (America/New_York), abbreviations (EST, IST) and offsets (UTC+5:30), typos tolerated.
- **Correct time math**: daylight saving, 30 and 45 minute offsets and date changes,
  using the IANA time zone database 2026f bundled with the app.
- **Looks and moves like Windows 11**: light/dark, accent color, contrast themes, text
  size and the animation setting are followed live. Motion uses Microsoft's Windows 11
  values (333 ms slide in, 250 ms resize, 167 ms slide out).
- **Light**: no network access, no telemetry, no admin rights, nothing injected into
  Explorer. About 1 MB of RAM while hidden and no CPU while idle.

## Install

1. Download `WorldTimeFlyout-0.1.0-win-x64.zip` below.
2. Optional: check it with `Get-FileHash .\WorldTimeFlyout-0.1.0-win-x64.zip -Algorithm SHA256`
   and compare with the hash at the bottom of these notes.
3. Extract it anywhere, for example `%LOCALAPPDATA%\Programs\WorldTimeFlyout`, and run
   `WorldTimeFlyout.exe`. The exe is not code-signed, so SmartScreen may ask once
   ("More info", then "Run anyway").

It starts with Windows (per user, no admin). Turn that off in the panel's ⋯ menu or
the tray icon menu. To update later: Exit from the tray icon, extract the new version
over the old folder, start it again. Settings are kept.

## Requirements

- Windows 11, x64. Tested on 24H2 and 26H2.
- Nothing else to install: the .NET runtime and Windows App SDK are included.

## Known limits

- It finds the calendar through Windows' accessibility (UI Automation) tree. If a
  Windows update changes that, it falls back to the flyout window's bounds, and the
  tray icon always opens the panel on its own.
- Typing in search or opening a menu needs keyboard focus, which closes the calendar;
  the panel then stays until you click elsewhere or press Esc.
- Tested with the taskbar at the bottom of the screen, where Windows 11 puts it.

`metrics.md` below has the size, memory and idle CPU measured on the build machine.
