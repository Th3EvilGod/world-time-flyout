Fixes the panel not appearing beside the calendar after the PC starts.

## Fixed

- **Panel missing after a restart.** When World Time Flyout started with Windows, the
  part of Windows that shows the calendar often wasn't running yet. It starts on the
  first click on the clock, and while it starts its window comes on screen without the
  signals the app was waiting for, so the panel stayed hidden until the panel had been
  opened once from the tray icon. The app now finds the calendar while that part of
  Windows is starting, and again whenever Windows restarts it.
- Startup no longer waits for Windows' accessibility service, and the app doesn't hold
  up its own start for it.
- The log (`%LOCALAPPDATA%\WorldTimeFlyout\log.txt`, tray menu → Open log folder) now
  always notes when the calendar is found, so a problem like this is easy to see.

`metrics.md` below has the size, memory and idle CPU measured on the build machine.
