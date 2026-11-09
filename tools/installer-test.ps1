# End-to-end test of the installer and the in-app updater on a Windows runner:
#   1. silent install (files, Start menu, Installed apps entry, app started)
#   2. install again over the running app (it is closed and restarted)
#   3. over-the-air update: the installed app, pretending to be 0.0.1, reads a
#      local copy of a GitHub release, downloads the installer, checks its
#      SHA-256, runs it and exits; the installer starts the new app
#   4. silent uninstall (files, shortcut, entry, startup entry removed)
# Runs in Windows PowerShell 5.1.
param(
  [Parameter(Mandatory)] [string] $Setup,
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$Setup = (Resolve-Path $Setup).Path
$results = New-Object System.Collections.Generic.List[string]
function Check($name, $ok, $detail) { $line = "{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail; $results.Add($line); $line }

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\World Time Flyout'
$exe = Join-Path $appDir 'WorldTimeFlyout.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F4DB3BC9-6641-4980-8AAD-3BBDC55DBF72}_is1'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\World Time Flyout.lnk'
$log = Join-Path $env:LOCALAPPDATA 'WorldTimeFlyout\log.txt'
function Installed { @(Get-Process WorldTimeFlyout -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) }
function WaitFor([scriptblock] $condition, [int] $seconds) {
  for ($i = 0; $i -lt $seconds * 4; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
  return $false
}
function RunSetup($label) {
  # (not -Wait: in Windows PowerShell that also waits for the app the installer starts)
  $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$OutDir\$label.log" -PassThru
  $done = $p.WaitForExit(180000)
  Check "$label finished" $done $(if ($done) { "exit code $($p.ExitCode)" } else { 'timed out' })
  if ($done) { Check "$label exit code" ($p.ExitCode -eq 0) $p.ExitCode }
}

# make sure no other copy (e.g. from the integration test) is running
Get-Process WorldTimeFlyout -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $log -ErrorAction SilentlyContinue

# 1. fresh install
RunSetup 'install'
Check 'app files installed' (Test-Path $exe) $appDir
Check 'Start menu shortcut' (Test-Path $shortcut) ''
$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check 'listed in Installed apps' ($entry -ne $null) ("{0} {1}" -f $entry.DisplayName, $entry.DisplayVersion)
Check 'installed version' ($entry.DisplayVersion -eq $Version) $entry.DisplayVersion
Check 'exe version' ((Get-Item $exe).VersionInfo.ProductVersion -like "$Version*") (Get-Item $exe).VersionInfo.ProductVersion
Check 'app started after install' (WaitFor { (Installed).Count -eq 1 } 15) ''
Check 'starts with Windows' (WaitFor { ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).WorldTimeFlyout -like "*$exe*") } 10) ''

# 2. install again while it runs (the upgrade path)
$before = (Installed | Select-Object -First 1).Id
RunSetup 'reinstall'
Check 'reinstall restarts the app' (WaitFor { $p = Installed; $p.Count -eq 1 -and $p[0].Id -ne $before } 20) ''

# 3. over-the-air update from a local copy of a GitHub release
& $exe --exit
Check '--exit closes the app' (WaitFor { (Installed).Count -eq 0 } 15) ''
$feed = Join-Path $OutDir 'feed'
New-Item -ItemType Directory -Force $feed | Out-Null
$name = Split-Path $Setup -Leaf
Copy-Item $Setup, "$Setup.sha256" $feed
$release = [ordered]@{
  tag_name = "v$Version"; html_url = "https://github.com/Th3EvilGod/world-time-flyout/releases/tag/v$Version"
  draft = $false; prerelease = $false
  assets = @(
    [ordered]@{ name = $name; browser_download_url = "http://127.0.0.1:8765/$name"; size = (Get-Item $Setup).Length },
    [ordered]@{ name = "$name.sha256"; browser_download_url = "http://127.0.0.1:8765/$name.sha256"; size = (Get-Item "$Setup.sha256").Length }
  )
}
$release | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $feed 'latest.json') -Encoding ascii
$server = Start-Process python -ArgumentList '-m', 'http.server', '8765', '--bind', '127.0.0.1', '--directory', $feed -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
Remove-Item $log -ErrorAction SilentlyContinue
$old = Start-Process $exe -ArgumentList '--background', '--diag', '--update-feed', 'http://127.0.0.1:8765/latest.json', '--pretend-version', '0.0.1', '--update-now' -PassThru
Check 'app exits for the installer' ($old.WaitForExit(180000)) ''
Check 'updated app is running again' (WaitFor { $p = Installed; $p.Count -eq 1 -and $p[0].Id -ne $old.Id } 90) ''
$text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
Check 'update found' ($text -match "0\.0\.1 -> $([regex]::Escape($Version)) available") ''
Check 'download verified (SHA-256)' ($text -match 'downloaded and verified') ''
Check 'installer started by the app' ($text -match 'installer started') ''
Copy-Item $log (Join-Path $OutDir 'update-log.txt') -ErrorAction SilentlyContinue
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue

# a tampered download must be refused
& $exe --exit
WaitFor { (Installed).Count -eq 0 } 15 | Out-Null
'0000000000000000000000000000000000000000000000000000000000000000  ' + $name | Set-Content (Join-Path $feed "$name.sha256") -Encoding ascii
$server = Start-Process python -ArgumentList '-m', 'http.server', '8765', '--bind', '127.0.0.1', '--directory', $feed -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
Remove-Item $log -ErrorAction SilentlyContinue
$bad = Start-Process $exe -ArgumentList '--background', '--diag', '--update-feed', 'http://127.0.0.1:8765/latest.json', '--pretend-version', '0.0.1', '--update-now' -PassThru
Start-Sleep -Seconds 25
$text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
Check 'bad checksum is refused' ($text -match 'did not match' -and -not $bad.HasExited) ''
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
& $exe --exit
WaitFor { (Installed).Count -eq 0 } 15 | Out-Null
& $exe --background
WaitFor { (Installed).Count -eq 1 } 15 | Out-Null

# 4. uninstall (the uninstaller hands off to a temp copy, so wait for the result)
$uninstaller = Join-Path $appDir 'unins000.exe'
$u = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
$u.WaitForExit(60000) | Out-Null
Check 'uninstall removes the app' (WaitFor { -not (Test-Path $exe) } 60) ''
Check 'uninstall stops the app' ((Installed).Count -eq 0) ''
Check 'uninstall removes the shortcut' (-not (Test-Path $shortcut)) ''
Check 'uninstall removes the Installed apps entry' (-not (Test-Path $uninstallKey)) ''
Check 'uninstall removes the startup entry' (-not ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).WorldTimeFlyout)) ''
Check 'settings are kept' (Test-Path (Join-Path $env:LOCALAPPDATA 'WorldTimeFlyout\settings.json')) ''

$results | Set-Content (Join-Path $OutDir 'results.txt')
'--- results'
$results
