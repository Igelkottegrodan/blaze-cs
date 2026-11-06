#!/bin/bash
# Runs the fixture check on SDK 11 with the runtime 10 test projects still available (task check:sdk11).
# devbox's two SDKs collide on bin/dotnet, so plain `dotnet` is SDK 10 and SDK 11 alone has no runtime 10.
# This assembles one dotnet root from both store outputs:
#   - symlinks to everything SDK 11 ships, except `sdk`, `shared` and `host/fxr`
#   - in those three, symlinks to each version of both SDKs
#   - a copy of SDK 11's `dotnet` muxer (it finds its root next to itself)
# then builds blaze-cs with SDK 10 (global.json) and runs the check against SDK 11 from that root.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"

# SDK 11 is not in devbox.json (both SDKs' bin/dotnet would collide when devbox installs the profile in a fresh
# checkout), so it is built from this pinned nixpkgs; moving to GA is a bump of this revision.
nixpkgs=13e53762c5afaf384d4614e94527a9ccfafe41d0
store11=$(nix --extra-experimental-features 'nix-command flakes' build --no-link --print-out-paths "github:NixOS/nixpkgs/$nixpkgs#dotnet-sdk_11^out")
root11=$(readlink -f "$store11/bin/dotnet" | xargs dirname)
root10=$(dotnet --list-sdks | sed -n 's/^10\..*\[\(.*\)\/sdk\]$/\1/p' | head -1)
for r in "$root10" "$root11"; do
    [ -x "$r/dotnet" ] || { echo "no dotnet at '$r': run through devbox (devbox run -- task check:sdk11)" >&2; exit 1; }
done

root="${XDG_CACHE_HOME:-$HOME/.cache}/blaze-cs/dotnet-sdk11"
rm -rf "$root"
mkdir -p "$root/sdk" "$root/shared" "$root/host/fxr"
for entry in "$root11"/*; do
    case $(basename "$entry") in sdk | shared | host | dotnet) ;; *) ln -s "$entry" "$root/" ;; esac
done
cp "$root11/dotnet" "$root/dotnet"
for r in "$root10" "$root11"; do
    for v in "$r"/sdk/* "$r"/host/fxr/*; do ln -sfn "$v" "$root/${v#"$r"/}"; done
    for fx in "$r"/shared/*; do
        mkdir -p "$root/shared/$(basename "$fx")"
        for v in "$fx"/*; do ln -sfn "$v" "$root/shared/$(basename "$fx")/$(basename "$v")"; done
    done
done

sdk=$(ls "$root11/sdk" | head -1)
export DOTNET_ROOT="$root" PATH="$root:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build BlazeCs.slnx -nologo -v:q
echo "== Linux, SDK $sdk"
BLAZE_CS_SDK="$sdk" dotnet run tests/fixture/check.cs
