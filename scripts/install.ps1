param([switch]$StartAtLogon)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot "WorldTimeFlyout.exe"
if (-not (Test-Path $source)) { throw "WorldTimeFlyout.exe not found next to install.ps1" }
$target = Join-Path $env:LOCALAPPDATA "WorldTimeFlyout"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item $source (Join-Path $target "WorldTimeFlyout.exe") -Force
if ($StartAtLogon) {
  $startup = [Environment]::GetFolderPath("Startup")
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut((Join-Path $startup "World Time Flyout.lnk"))
  $shortcut.TargetPath = Join-Path $target "WorldTimeFlyout.exe"
  $shortcut.Save()
}
Start-Process (Join-Path $target "WorldTimeFlyout.exe")
