Install for all users (Program Files), and updates that work there too.

## New

- **Choose how to install.** The installer now asks:
  - **Install for me only** (as before): no admin rights, into
    `%LOCALAPPDATA%\Programs\World Time Flyout`, updates need no prompt.
  - **Install for all users**: into `Program Files`. Windows asks for admin rights once,
    and again when an update installs.

  No need to "Run as administrator" any more; the installer asks Windows itself when it
  needs to.
- **Safer updates.** The app now stays open until the installer is ready to replace it.
  If you say no to the Windows prompt, or the update can't finish, the panel says so and
  everything keeps working on the current version.
- After any update the app starts again as you, never with admin rights.

## If you installed 0.2.0 into Program Files with "Run as administrator"

That copy is registered as "just me" but sits in a folder only admins can write, so it
can't update itself (the panel says so). Move it once:

1. Settings → Apps → Installed apps → World Time Flyout → Uninstall. Answer **No** when
   it asks about deleting your cities and settings.
2. Run `WorldTimeFlyout-Setup-0.2.1-win-x64.exe` normally and choose **Install for all
   users**.

Your cities and settings carry over.

`metrics.md` below has the size, memory and idle CPU measured on the build machine.
