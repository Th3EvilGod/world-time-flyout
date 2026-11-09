World Time Flyout now has a proper installer and updates itself.

## New

- **Installer.** `WorldTimeFlyout-Setup-0.2.0-win-x64.exe` installs for your user only
  (no admin), adds World Time Flyout to the Start menu and to Settings → Apps →
  Installed apps, where you can uninstall it.
- **Updates from inside the app.** Once a day it checks GitHub for a new version. When
  there is one, the panel shows "Update available" with an Install button (also in the
  ⋯ menu and the tray icon menu). Install downloads the new installer, checks it
  against its published SHA-256, installs it silently and starts the new version. You
  can also check by hand (⋯ → Check for updates) or turn the daily check off
  (⋯ → Check for updates daily).
- After an update the panel says "Updated to …" once, with a link to what's new.

## Upgrading from 0.1.0 (the zip)

1. Download and run `WorldTimeFlyout-Setup-0.2.0-win-x64.exe` below. It closes the old
   version for you.
2. Your cities and settings carry over. "Start with Windows" moves to the installed
   copy on its first start.
3. Delete the old unzipped folder.

From now on updates come through the app.

## Good to know

- The installer is not code-signed yet, so the first time Windows SmartScreen may say
  "Windows protected your PC": choose "More info", then "Run anyway". Updates
  installed from inside the app don't show this.
- The update check is the app's only network connection: one request a day to GitHub's
  public API, carrying nothing but the app's version.
- The portable zip is still attached. If a zip copy installs an update, it installs the
  regular version and switches to it.

`metrics.md` below has the size, memory and idle CPU measured on the build machine.
