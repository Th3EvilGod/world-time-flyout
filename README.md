# World Time Flyout

A world clock and time-zone planner that opens next to the Windows 11 taskbar calendar.
Click the clock in the taskbar and your cities appear beside the calendar, in the same
Fluent style, theme and accent color.

**Status: preview.** Built and unit-tested on GitHub's Windows runner. The calendar
integration still needs confirming on real Windows 11 desktops (see "Known limits").

## What it does

- Opens automatically beside the native calendar when you click the taskbar clock
  (or press Win+N). Closes with it.
- Follows the calendar: full view when the calendar is expanded, a three-city compact
  view when it is collapsed.
- One shared time cursor for every city. Drag the timeline, click an hour, use the
  arrows or the mouse wheel. "Now" jumps back.
- Click any time to copy it ("New York: 9:00 AM"). Right-click for copy with date,
  copy all, reorder, set the reference city, remove.
- Offline search across about 34,000 cities, plus countries, IANA zones
  (America/New_York), abbreviations (EST, IST) and offsets (UTC+5:30). Typos are tolerated.
- Correct daylight saving, 30 and 45 minute offsets and date changes, using the IANA
  time-zone database bundled with the app.
- Follows the Windows light/dark mode, accent color, contrast themes, text size and
  animation settings, live.
- No network access, no telemetry, no admin rights, nothing injected into Explorer.

## Install

1. Download the latest `WorldTimeFlyout-<version>-win-x64.zip` from Releases
   (or from the Actions run artifacts).
2. Check it: `Get-FileHash .\WorldTimeFlyout-*.zip -Algorithm SHA256` must match the
   `.sha256` file.
3. Extract anywhere you keep apps, for example `%LOCALAPPDATA%\Programs\WorldTimeFlyout`,
   and run `WorldTimeFlyout.exe`. The executable is not code-signed, so Windows may show
   a SmartScreen prompt the first time.

It starts with Windows by default (per-user Run key, no admin). Turn that off in the
panel's "More" menu or the tray icon menu.

## Uninstall

1. Open the panel's "More" menu (or right-click the tray icon) and turn off
   "Start with Windows", then choose Exit.
2. Delete the app folder.
3. Optionally delete `%LOCALAPPDATA%\WorldTimeFlyout` (settings and log).

## How it works

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). In short: the app listens for the
shell's own window events (the documented accessibility mechanism, scoped to the
ShellExperienceHost process) and reads the calendar card's position through UI
Automation. Its panel is a non-activating window, so clicking it does not steal focus
from the calendar.

## Build

Builds run on GitHub Actions (`.github/workflows/ci.yml`): unit tests, a NativeAOT
publish, rendered screenshots, measured size/memory/CPU, a zip with SHA-256, and a
pre-release for `v*` tags. Locally on Windows with the .NET 10 SDK:

    dotnet test --project tests/WorldTime.Core.Tests
    dotnet publish src/WorldTimeFlyout -c Release -r win-x64 -o publish

Command-line options: `--background` (start hidden), `--show`, `--diag` (verbose log
in `%LOCALAPPDATA%\WorldTimeFlyout\log.txt`), `--settings FILE`.

## Known limits

- Detecting the calendar relies on the shell's UI Automation tree. If a Windows update
  changes it, the panel falls back to the flyout window bounds, and the tray icon always
  opens the panel on its own.
- Typing in search or opening a menu needs keyboard focus, which closes the native
  calendar. The panel stays open until you click elsewhere or press Esc.
- Windows 11 only (the calendar flyout it attaches to does not exist on Windows 10).
