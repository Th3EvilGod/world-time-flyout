# End-to-end check on a Windows desktop (the GitHub runner): opens the real
# taskbar calendar, checks the companion panel attaches beside it, survives a
# click, collapses/expands with it and closes with it. Saves screenshots, the
# app's diagnostic log and a PASS/FAIL summary.
# Runs in Windows PowerShell 5.1 (UI Automation client is built in there).
param(
  [Parameter(Mandatory)] [string] $AppDir,
  [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() { return string.Format("({0},{1} {2}x{3})", L, T, R - L, B - T); } }
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  public static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero); }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
  public static RECT Frame(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, 16); return r; }
  public static bool Shown(IntPtr h) { int c; DwmGetWindowAttribute(h, 14, out c, 4); return h != IntPtr.Zero && IsWindowVisible(h) && c == 0; }
  public static IntPtr VisibleWindowOf(uint pid, int minW, int minH) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && Shown(h)) { RECT r = Frame(h); if (r.R - r.L >= minW && r.B - r.T >= minH) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }
}
"@
$results = New-Object System.Collections.Generic.List[string]
function Check($name, $ok, $detail) { $line = "{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail; $results.Add($line); $line }
function Shot($name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
function WinN { [W]::Key(0x5B, $false); [W]::Key(0x4E, $false); [W]::Key(0x4E, $true); [W]::Key(0x5B, $true) }
function Esc { [W]::Key(0x1B, $false); [W]::Key(0x1B, $true) }
$AE = [System.Windows.Automation.AutomationElement]
function NcWindow { [W]::FindWindow('Windows.UI.Core.CoreWindow', 'Notification Center') }
function CalendarOpen { [W]::Shown((NcWindow)) }
function Card {
  $h = NcWindow; if (-not [W]::Shown($h)) { return $null }
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'CalendarCenterGrid')
  $e = $AE::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
  if ($e) { $e.Current.BoundingRectangle } else { $null }
}
function ExpandButton {
  $h = NcWindow; if (-not [W]::Shown($h)) { return $null }
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'ExpandCollapseButton')
  $AE::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Panel { [W]::VisibleWindowOf([uint32]$p.Id, 200, 60) }
function CheckAttached($label) {
  Start-Sleep -Milliseconds 700
  $card = Card; $panel = Panel
  if (-not $card -or $panel -eq [IntPtr]::Zero) { Check "$label attached" $false "card=$card panel=$panel"; return }
  $f = [W]::Frame($panel)
  $gap = [int]$card.X - $f.R
  $okGap = [Math]::Abs($gap - 12) -le 1
  $okBottom = [Math]::Abs(([int]($card.Y + $card.Height)) - $f.B) -le 1
  $okWidth = [Math]::Abs(([int]$card.Width) - ($f.R - $f.L)) -le 1
  $okHeight = ($f.B - $f.T) -ge ([int]$card.Height - 1)
  Check "$label attached" ($okGap -and $okBottom -and $okWidth -and $okHeight) "card=$card panel=$f gap=$gap"
}

$settings = Join-Path $OutDir 'settings.json'
$exe = Join-Path $AppDir 'WorldTimeFlyout.exe'
$log = Join-Path $env:LOCALAPPDATA 'WorldTimeFlyout\log.txt'
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--background', '--diag', '--settings', $settings -PassThru
Start-Sleep -Seconds 8
"winver: " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion + " " + [Environment]::OSVersion.Version

# 1. Win+N opens the calendar; the panel attaches
WinN; Start-Sleep -Milliseconds 1500
Check 'Win+N opens calendar' (CalendarOpen) ''
CheckAttached 'after Win+N'
Shot '1-open'

# 2. expand / collapse with the calendar's own button
$btn = ExpandButton
if ($btn) {
  $r = $btn.Current.BoundingRectangle
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 900
  CheckAttached 'after expand/collapse toggle'
  Shot '2-toggled'
  $btn = ExpandButton; $r = $btn.Current.BoundingRectangle
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 900
  CheckAttached 'after toggling back'
  Shot '3-toggled-back'
} else { Check 'expand button found' $false '' }

# 3. a click inside the panel must not close the calendar
$panel = Panel
if ($panel -ne [IntPtr]::Zero) {
  $f = [W]::Frame($panel)
  [W]::Click([int](($f.L + $f.R) / 2), [int]($f.T + 20))
  Start-Sleep -Milliseconds 1200
  Check 'calendar stays open after a click in the panel' (CalendarOpen) ''
  Check 'panel still visible after the click' ((Panel) -ne [IntPtr]::Zero) ''
  $fg = [W]::GetForegroundWindow()
  Check 'panel did not take the foreground' ($fg -ne $panel) ''
  Shot '4-after-panel-click'
}

# 4. Esc closes the calendar and the panel goes with it
Esc; Start-Sleep -Milliseconds 1200
Check 'Esc closes calendar' (-not (CalendarOpen)) ''
Check 'panel hides with the calendar' ((Panel) -eq [IntPtr]::Zero) ''
Shot '5-closed'

# 5. clicking the taskbar clock opens both; clicking again closes both
$root = $AE::RootElement
$tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Shell_TrayWnd')))
$clock = $null
if ($tray) {
  foreach ($e in $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.ClassName -like 'SystemTray.Omni*' -and $e.Current.Name -like 'Clock*') { $clock = $e }
  }
}
if ($clock) {
  $r = $clock.Current.BoundingRectangle
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 1500
  Check 'clock click opens calendar' (CalendarOpen) ''
  CheckAttached 'after clock click'
  Shot '6-clock'
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 1500
  Check 'second clock click closes calendar' (-not (CalendarOpen)) ''
  Check 'panel hides on second clock click' ((Panel) -eq [IntPtr]::Zero) ''
  Shot '7-clock-again'
} else { Check 'taskbar clock found' $false '' }

Check 'app still running' (-not $p.HasExited) ''
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Copy-Item $log (Join-Path $OutDir 'app-log.txt') -ErrorAction SilentlyContinue
$results | Set-Content (Join-Path $OutDir 'results.txt')
"--- results"
$results
"--- app log"
Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Last 60
