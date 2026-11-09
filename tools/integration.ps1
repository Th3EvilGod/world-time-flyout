# End-to-end check on a Windows desktop: opens the real taskbar calendar
# (Win+N and a click on the clock), collapses/expands it, clicks inside the
# companion panel, and saves screenshots plus the app's diagnostic log.
# Runs in Windows PowerShell 5.1 (UI Automation client is built in there).
param(
  [Parameter(Mandatory)] [string] $AppDir,
  [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Input {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  public static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero); }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
"@
function Shot($name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  "saved $name.png"
}
function WinN { [Input]::Key(0x5B, $false); [Input]::Key(0x4E, $false); [Input]::Key(0x4E, $true); [Input]::Key(0x5B, $true) }
function Esc { [Input]::Key(0x1B, $false); [Input]::Key(0x1B, $true) }
$AE = [System.Windows.Automation.AutomationElement]
function FindIn($root, $prop, $value) {
  $c = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

$settings = Join-Path $OutDir 'settings.json'
$exe = Join-Path $AppDir 'WorldTimeFlyout.exe'
$log = Join-Path $env:LOCALAPPDATA 'WorldTimeFlyout\log.txt'
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList '--background', '--diag', '--settings', $settings -PassThru
Start-Sleep -Seconds 8
"winver: " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion + " " + [Environment]::OSVersion.Version

"--- Win+N"
WinN; Start-Sleep -Milliseconds 2500; Shot '1-win-n'

$shellWindow = $null
$calendar = $null
Get-Process ShellExperienceHost -ErrorAction SilentlyContinue | ForEach-Object { "ShellExperienceHost pid $($_.Id)" }
$root = $AE::RootElement
$nc = FindIn $root $AE::NameProperty 'Notification Center'
if ($nc) {
  "notification center window: " + $nc.Current.BoundingRectangle
  $card = FindIn $nc $AE::AutomationIdProperty 'CalendarCenterGrid'
  if ($card) { "calendar card: " + $card.Current.BoundingRectangle } else { "no CalendarCenterGrid" }
  $btn = FindIn $nc $AE::AutomationIdProperty 'ExpandCollapseButton'
  if ($btn) {
    "--- collapse via button: " + $btn.Current.Name
    $r = $btn.Current.BoundingRectangle
    [Input]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 1500; Shot '2-collapsed'
    $r = $btn.Current.BoundingRectangle
    "--- expand via button: " + $btn.Current.Name
    [Input]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 1500; Shot '3-expanded'
  } else { "no ExpandCollapseButton" }
} else { "no notification center window found" }

"--- click inside the companion panel (timeline area)"
$panel = $null
$c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$panel = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
if ($panel) {
  $r = $panel.Current.BoundingRectangle
  "panel: $r"
  if ($r.Width -gt 0) {
    [Input]::Click([int]($r.X + $r.Width / 2 + 60), [int]($r.Y + $r.Height - 25))
    Start-Sleep -Milliseconds 1500; Shot '4-after-panel-click'
    $still = FindIn $root $AE::NameProperty 'Notification Center'
    "calendar still open after panel click: " + [bool]($still -and -not $still.Current.IsOffscreen)
  }
} else { "panel window not found (not shown?)" }

"--- Esc"
Esc; Start-Sleep -Milliseconds 1500; Shot '5-closed'

"--- click the taskbar clock"
$tray = FindIn $root $AE::ClassNameProperty 'Shell_TrayWnd'
$clock = $null
if ($tray) {
  $all = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) { if ($e.Current.ClassName -like 'SystemTray.Omni*' -and $e.Current.Name -like 'Clock*') { $clock = $e } }
}
if ($clock) {
  $r = $clock.Current.BoundingRectangle
  "clock button: $r"
  [Input]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 2500; Shot '6-clock-click'
  [Input]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 1500; Shot '7-clock-click-again'
} else { "clock button not found" }

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Test-Path $log) { Copy-Item $log (Join-Path $OutDir 'app-log.txt'); '--- app log'; Get-Content $log | Select-Object -Last 80 }
