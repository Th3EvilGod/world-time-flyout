# Measures the published app on a Windows runner: size on disk, memory and CPU
# while idle in the background, and memory with the panel open.
param(
  [Parameter(Mandatory)] [string] $AppDir,
  [Parameter(Mandatory)] [string] $OutFile
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $AppDir 'WorldTimeFlyout.exe'
$work = Join-Path $env:RUNNER_TEMP 'wtf-measure'
New-Item -ItemType Directory -Force $work | Out-Null
$settings = Join-Path $work 'settings.json'

$exeMB = (Get-Item $exe).Length / 1MB
$dirMB = ((Get-ChildItem $AppDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB
$files = (Get-ChildItem $AppDir -Recurse -File).Count

$p = Start-Process $exe -ArgumentList '--background', '--settings', $settings -PassThru
Start-Sleep -Seconds 20
$p.Refresh()
$cpu1 = $p.TotalProcessorTime.TotalMilliseconds
$ws1 = $p.WorkingSet64 / 1MB; $pm1 = $p.PrivateMemorySize64 / 1MB
Start-Sleep -Seconds 60
$p.Refresh()
$cpu2 = $p.TotalProcessorTime.TotalMilliseconds
$ws2 = $p.WorkingSet64 / 1MB; $pm2 = $p.PrivateMemorySize64 / 1MB
$idleCpuMs = $cpu2 - $cpu1

# open the panel through a second launch (single-instance handoff)
Start-Process $exe -ArgumentList '--settings', $settings | Wait-Process -Timeout 30
Start-Sleep -Seconds 6
$p.Refresh()
$ws3 = $p.WorkingSet64 / 1MB; $pm3 = $p.PrivateMemorySize64 / 1MB
$alive = -not $p.HasExited
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

$report = @"
| Measure | Value |
|---|---|
| WorldTimeFlyout.exe | $('{0:N1}' -f $exeMB) MB |
| App folder (all files) | $('{0:N1}' -f $dirMB) MB in $files files |
| Idle in background, working set | $('{0:N1}' -f $ws2) MB (private $('{0:N1}' -f $pm2) MB) |
| Idle CPU over 60 s | $('{0:N0}' -f $idleCpuMs) ms total ($('{0:N3}' -f ($idleCpuMs / 600)) % of one core) |
| Panel open, working set | $('{0:N1}' -f $ws3) MB (private $('{0:N1}' -f $pm3) MB) |
| Still running after handoff | $alive |
"@
$report | Set-Content $OutFile
$report
