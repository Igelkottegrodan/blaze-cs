#!/bin/sh
# Runs the fixture check on native Windows from WSL (task check:windows), for the pinned SDK and the
# latest .NET 11 preview, as CI's windows-latest jobs do. Through WSL interop:
#   1. installs both SDKs into %LOCALAPPDATA%\blaze-cs\dotnet with dotnet-install.ps1 (once; the
#      system-wide SDK is left alone)
#   2. copies the working tree (tracked and new files, not ignored ones) to %LOCALAPPDATA%\blaze-cs\check (fresh each run: MSBuild and file
#      timestamps on \\wsl.localhost paths are unreliable)
#   3. runs scripts/check-windows.ps1 there: builds blaze-cs with that SDK, then the check per SDK
set -eu

if ! grep -qi microsoft /proc/version 2>/dev/null || ! command -v powershell.exe >/dev/null; then
    echo "check:windows runs from WSL with Windows interop; on Linux, Windows is checked in CI" >&2
    exit 2
fi

root=$(git rev-parse --show-toplevel)
sdk=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$root/global.json")
appdata=$(wslpath "$(powershell.exe -NoProfile -Command '$env:LOCALAPPDATA' | tr -d '\r')")
dotnet="$appdata/blaze-cs/dotnet"
work="$appdata/blaze-cs/check"

install() { # dotnet-install.ps1 arguments
    if [ ! -f "$dotnet/dotnet-install.ps1" ]; then
        mkdir -p "$dotnet"
        curl -fsSL https://dot.net/v1/dotnet-install.ps1 -o "$dotnet/dotnet-install.ps1"
    fi
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$dotnet/dotnet-install.ps1")" \
        -InstallDir "$(wslpath -w "$dotnet")" -NoPath "$@"
}
[ -d "$dotnet/sdk/$sdk" ] || install -Version "$sdk"
ls "$dotnet/sdk" 2>/dev/null | grep -q '^11\.' || install -Channel 11.0 -Quality preview
sdk11=$(ls "$dotnet/sdk" | grep '^11\.' | sort -V | tail -1)

rm -rf "$work"
mkdir -p "$work"
git -C "$root" ls-files -z --cached --others --exclude-standard | (cd "$root" && tar --null -T - -cf -) | tar -xf - -C "$work"

cd "$work"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$work/scripts/check-windows.ps1")" \
    "$(wslpath -w "$dotnet")" "$sdk" "$sdk11"
