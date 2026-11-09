# World Time Flyout

Experimental lightweight native Windows 11 world clock companion, written in C++/Win32.

**Status:** v0.1 starter prototype, not a tested release. The code currently in this repository is a smaller first-stage implementation, not yet the full enhanced source archive shared earlier in chat.

## What works in the initial code

- Native Windows popup using GDI, Segoe UI Variable, system light/dark setting and accent color
- Three-zone compact pager and expanded scrollable list
- Time zone conversions using built-in Windows DST data
- Adjust selected time, horizontal drag in expanded mode, Now/reset, and copying times
- Right-click to add/remove from 12 predefined cities
- Open/close using Ctrl+Alt+T, a tray icon, or experimental detection of the primary taskbar clock area

## Known limitations

- Clock click detection is an approximate check for the rightmost 115 pixels of the primary Windows taskbar. It can trigger on adjacent controls or fail with different taskbar positions, layouts or multiple monitors.
- The panel does not yet detect the native calendar expanded/collapsed state or track its exact bounds. Compact/expanded switching is manual.
- City search and arbitrary city selection, persistent preferences, exact minute input and a synchronized hourly grid are not implemented yet.
- The visual match is an approximation, not a pixel-perfect Fluent UI.
- Windows build and runtime behavior have NOT been verified.

## Build on GitHub (no Git installation needed)

Open Actions, choose Build Windows x64, and download the artifact from a successful run. Extract the inner WorldTimeFlyout-win-x64.zip file.

Run WorldTimeFlyout.exe directly, or in PowerShell next to the EXE run the optional install script with:

    .\install.ps1 -StartAtLogon

The installer copies to %LOCALAPPDATA%\WorldTimeFlyout and creates a user Startup shortcut without elevation.

## Manual Windows build

    cmake -S . -B build -G "Visual Studio 17 2022" -A x64
    cmake --build build --config Release

Run build/Release/WorldTimeFlyout.exe.

## Privacy and security

No telemetry, network calls or administrator privileges. The first prototype uses a low-level mouse hook to detect clicks in a small taskbar area. It does not capture keyboard input or store mouse events. A more narrowly scoped Win32/UI Automation observer is preferred for a stable version.

## Roadmap

1. Verify successful Windows build and correct any compile issues.
2. Replace approximate clock detection with accurate native flyout tracking.
3. Automatically match expanded/collapsed Windows calendar position and size.
4. Searchable full city catalog and persistent settings.
5. Fine-grained time scrubbing, accurate timeline, better copying.
6. Accessibility and high-DPI/multi-monitor testing.
