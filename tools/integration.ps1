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
using System; using System.Collections.Generic; using System.Diagnostics; using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() { return string.Format("({0},{1} {2}x{3})", L, T, R - L, B - T); } }
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pv, uint winIni);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint uiParam, out int pv, uint winIni);
  public static int GetAnimations() { int v; SystemParametersInfo(0x1042, 0, out v, 0); return v; }
  public static void SetAnimations(bool on) { SystemParametersInfo(0x1043, 0, new IntPtr(on ? 1 : 0), 3); }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
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
  // The panel window (by title, so tooltips and menus of the same process don't count).
  public static IntPtr PanelOf(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid) { var sb = new StringBuilder(64); GetWindowText(h, sb, 64); if (sb.ToString() == "World Time") { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }
  // Records the panel's frame every few ms: "t:(L,T WxH)" for each change, "t:hidden" when it goes away.
  public static List<int[]> Track(uint pid, int ms) {
    var list = new List<int[]>(); var sw = Stopwatch.StartNew(); RECT last = new RECT(); bool shown = false;
    IntPtr panel = PanelOf(pid);
    while (sw.ElapsedMilliseconds < ms) {
      if (panel == IntPtr.Zero) panel = PanelOf(pid);
      IntPtr h = panel != IntPtr.Zero && Shown(panel) ? panel : IntPtr.Zero;
      int t = (int)sw.ElapsedMilliseconds;
      if (h != IntPtr.Zero) {
        RECT r = Frame(h);
        if (!shown || r.L != last.L || r.T != last.T || r.R != last.R || r.B != last.B) { list.Add(new[] { t, r.L, r.T, r.R - r.L, r.B - r.T }); last = r; shown = true; }
      } else if (shown) { list.Add(new[] { t, -1, -1, -1, -1 }); shown = false; }
      Thread.Sleep(3);
    }
    return list;
  }
  public static string Format(List<int[]> l) {
    var sb = new StringBuilder();
    foreach (var e in l) sb.Append(e[1] < 0 ? string.Format("{0}ms:hidden ", e[0]) : string.Format("{0}ms:({1},{2} {3}x{4}) ", e[0], e[1], e[2], e[3], e[4]));
    return sb.ToString();
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
function Panel { $h = [W]::PanelOf([uint32]$p.Id); if ($h -ne [IntPtr]::Zero -and [W]::Shown($h)) { $h } else { [IntPtr]::Zero } }
function PanelEl($id) {
  $h = Panel; if ($h -eq [IntPtr]::Zero) { return $null }
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  $AE::FromHandle($h).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function ClickEl($e) { $r = $e.Current.BoundingRectangle; [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function CheckLayout($label) {
  $card = Card
  if (-not $card) { Check "$label layout follows the calendar" $false 'no card'; return }
  $expanded = $card.Height -gt 200
  $ok = if ($expanded) { (PanelEl 'DateChip') -ne $null } else { (PanelEl 'Tile0') -ne $null }
  Check "$label layout follows the calendar" $ok ("calendar {0}" -f $(if ($expanded) { 'expanded' } else { 'collapsed' }))
}
function Motion($label, $track) {
  $line = "MOTION {0}: {1} steps: {2}" -f $label, $track.Count, [W]::Format($track)
  $results.Add($line); $line
}
$motionLog = New-Object System.Collections.Generic.List[string]
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

# Runners usually have "Animation effects" off; turn them on so the motion is exercised.
"animation effects before: " + [W]::GetAnimations()
[W]::SetAnimations($true)
"animation effects now: " + [W]::GetAnimations()

$settings = Join-Path $OutDir 'settings.json'
$exe = Join-Path $AppDir 'WorldTimeFlyout.exe'
$log = Join-Path $env:LOCALAPPDATA 'WorldTimeFlyout\log.txt'
Remove-Item $log -ErrorAction SilentlyContinue
# Warm up: the shell host may only start on the first Win+N after boot.
WinN
for ($i = 0; $i -lt 30 -and -not (CalendarOpen); $i++) { Start-Sleep -Milliseconds 200 }
"warm-up: calendar open = " + (CalendarOpen)
Esc; Start-Sleep -Milliseconds 1500
if (CalendarOpen) { WinN; Start-Sleep -Milliseconds 1500 }

$p = Start-Process $exe -ArgumentList '--background', '--diag', '--settings', $settings -PassThru
Start-Sleep -Seconds 8
"winver: " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion + " " + [Environment]::OSVersion.Version

# 1. Win+N opens the calendar; the panel attaches
WinN
$track = [W]::Track([uint32]$p.Id, 900)
Motion 'open (first)' $track
Start-Sleep -Milliseconds 600
Check 'Win+N opens calendar' (CalendarOpen) ''
CheckAttached 'after Win+N'
CheckLayout 'after Win+N'
Shot '1-open'

# 2. expand / collapse with the calendar's own button
$btn = ExpandButton
if ($btn) {
  $r = $btn.Current.BoundingRectangle
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  $track = [W]::Track([uint32]$p.Id, 800)
  Motion 'expand/collapse' $track
  $heights = @($track | ForEach-Object { $_[4] } | Sort-Object -Unique)
  Check 'expand/collapse resizes in steps (animated)' ($heights.Count -ge 4) ("{0} distinct heights" -f $heights.Count)
  Start-Sleep -Milliseconds 300
  CheckAttached 'after expand/collapse toggle'
  CheckLayout 'after expand/collapse toggle'
  Shot '2-toggled'
  $btn = ExpandButton; $r = $btn.Current.BoundingRectangle
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  $track = [W]::Track([uint32]$p.Id, 800)
  Motion 'toggle back' $track
  Start-Sleep -Milliseconds 300
  CheckAttached 'after toggling back'
  CheckLayout 'after toggling back'
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

# 3b. controls in both layouts (the calendar must stay open throughout)
function SetCalendarExpanded($want) {
  $isExpanded = (PanelEl 'DateChip') -ne $null
  if ($isExpanded -ne $want) {
    $b = ExpandButton; if ($b) { ClickEl $b; Start-Sleep -Milliseconds 900 }
  }
  return ((PanelEl 'DateChip') -ne $null) -eq $want
}
if (SetCalendarExpanded $true) {
  $chip = PanelEl 'DateChip'
  $name0 = $chip.Current.Name
  "chip: $name0"
  ClickEl $chip; Start-Sleep -Milliseconds 800
  $picker = PanelEl 'DatePicker'
  Check 'date chip opens the date picker' ($picker -ne $null) ''
  Check 'calendar stays open with the date picker' (CalendarOpen) ''
  Shot '4a-date-picker'
  if ($picker) {
    $days = @($picker.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'CalendarViewDayItem'))))
    "day items: $($days.Count)"
    if ($days.Count -gt 25) {
      ClickEl $days[24]; Start-Sleep -Milliseconds 800
      $chip = PanelEl 'DateChip'
      $name1 = if ($chip) { $chip.Current.Name } else { '' }
      "chip after picking: $name1"
      Check 'picking a day sets the date' ($chip -ne $null -and $name1 -ne $name0 -and $name1 -like '*from now*') $name1
      Shot '4b-date-picked'
    } else { Check 'date picker shows days' $false "$($days.Count) items" }
  }
  $now = PanelEl 'NowButton'; if ($now) { ClickEl $now; Start-Sleep -Milliseconds 500 }
  $next = PanelEl 'NextDay'
  if ($next) {
    ClickEl $next; Start-Sleep -Milliseconds 500
    $n1 = (PanelEl 'DateChip').Current.Name
    ClickEl (PanelEl 'NextDay'); Start-Sleep -Milliseconds 500
    $n2 = (PanelEl 'DateChip').Current.Name
    ClickEl (PanelEl 'PreviousDay'); Start-Sleep -Milliseconds 500
    $n3 = (PanelEl 'DateChip').Current.Name
    Check 'next day steps whole days' ($n1 -like '*+1d*' -and $n2 -like '*+2d*' -and $n3 -like '*+1d*') "$n1 | $n2 | $n3"
    Shot '4c-next-day'
    $now = PanelEl 'NowButton'; if ($now) { ClickEl $now; Start-Sleep -Milliseconds 500 }
  }
  Check 'calendar still open after the expanded controls' (CalendarOpen) ''
} else { Check 'calendar expanded for the control tests' $false '' }

if (SetCalendarExpanded $false) {
  $tile = PanelEl 'Tile0'
  Check 'compact view shows tiles' ($tile -ne $null) ''
  Shot '4d-compact'
  if ($tile) {
    $t0 = $tile.Current.Name
    $nextPage = PanelEl 'NextPage'
    if ($nextPage) {
      ClickEl $nextPage; Start-Sleep -Milliseconds 600
      $t1 = (PanelEl 'Tile0').Current.Name
      Check 'next page shows other cities' ($t1 -ne $t0) "$t0 -> $t1"
      Shot '4e-compact-next-page'
      ClickEl (PanelEl 'NextPage'); Start-Sleep -Milliseconds 600
      $t2 = (PanelEl 'Tile0').Current.Name
      Check 'paging wraps around to the first cities' ($t2 -eq $t0) "$t2"
    }
    $later = PanelEl 'Later'
    if ($later) {
      ClickEl $later; Start-Sleep -Milliseconds 500
      $shift = PanelEl 'ShiftChip'
      $s1 = if ($shift) { $shift.Current.Name } else { '' }
      ClickEl (PanelEl 'Earlier'); Start-Sleep -Milliseconds 300
      ClickEl (PanelEl 'Earlier'); Start-Sleep -Milliseconds 500
      Shot '4f-compact-earlier'
      $tile = PanelEl 'Tile0'
      "tile after steps: $($tile.Current.Name)"
      Check 'compact steps show the shift' ($shift -ne $null) $s1
      $shift = PanelEl 'ShiftChip'; if ($shift) { ClickEl $shift; Start-Sleep -Milliseconds 400 }
      Check 'shift chip returns to now' ((PanelEl 'ShiftChip') -eq $null) ''
    }
  }
  Check 'calendar still open after the compact controls' (CalendarOpen) ''
  SetCalendarExpanded $true | Out-Null
} else { Check 'calendar collapsed for the control tests' $false '' }

# 4. Esc closes the calendar and the panel goes with it
Esc
$track = [W]::Track([uint32]$p.Id, 700)
Motion 'close (Esc)' $track
Start-Sleep -Milliseconds 500
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
  $track = [W]::Track([uint32]$p.Id, 900)
  Motion 'open (clock, known layout)' $track
  $xs = @($track | Where-Object { $_[1] -ge 0 } | ForEach-Object { $_[1] })
  Check 'panel slides in from the right' ($xs.Count -ge 5 -and ($xs[0] - $xs[-1]) -ge 100) ("{0} positions, x {1} -> {2}" -f $xs.Count, $(if ($xs.Count) { $xs[0] } else { '-' }), $(if ($xs.Count) { $xs[-1] } else { '-' }))
  Start-Sleep -Milliseconds 600
  Check 'clock click opens calendar' (CalendarOpen) ''
  CheckAttached 'after clock click'
  Shot '6-clock'
  [W]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  $track = [W]::Track([uint32]$p.Id, 700)
  Motion 'close (clock)' $track
  $xs = @($track | Where-Object { $_[1] -ge 0 } | ForEach-Object { $_[1] })
  Check 'panel slides out to the right' ($xs.Count -ge 3 -and ($xs[-1] - $xs[0]) -ge 50) ("{0} positions" -f $xs.Count)
  Start-Sleep -Milliseconds 800
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
