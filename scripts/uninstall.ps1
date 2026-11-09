$startup = Join-Path ([Environment]::GetFolderPath("Startup")) "World Time Flyout.lnk"
Remove-Item $startup -ErrorAction SilentlyContinue
Get-Process WorldTimeFlyout -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:LOCALAPPDATA "WorldTimeFlyout") -Recurse -Force -ErrorAction SilentlyContinue
