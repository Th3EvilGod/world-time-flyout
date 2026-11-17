Fixes the panel not appearing beside the calendar after Windows restarted the part that
shows the calendar.

## Fixed

- **Panel missing while you work in another app.** Windows ends and restarts the part
  that shows the calendar now and then, or starts it for a notification (for example
  from Claude). After that, the app could skip its search for the new calendar window: a
  leftover from an earlier open said a search was already running when none was. The
  panel then stayed hidden on every open until one happened to be fast enough, which
  looked like clicking the tray icon fixed it. The search now always runs.
- The app also looks for the calendar when Windows shows it in a new window, and a
  window that never finishes opening no longer keeps it from noticing the real one.
- The panel's slide in and out always finishes, even if the system stops sending it
  frames for a moment.
- The log (tray menu, Open log folder) now notes when the calendar shows up in a new
  window, and when a search found nothing.

`metrics.md` below has the size, memory and idle CPU measured on the build machine.
