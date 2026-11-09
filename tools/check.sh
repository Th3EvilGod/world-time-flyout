#!/bin/sh
# Local compile check for the WinUI app on a non-Windows machine. Skips the
# Windows-only resource (PRI) steps; the real build runs in GitHub Actions.
cd "$(dirname "$0")/../src/WorldTimeFlyout" && dotnet build -c Release -v:q -nologo \
  -p:EnableMsixTooling=false -p:AppxGeneratePriEnabled=false -p:EnablePriGenTooling=false "$@"
