#!/bin/sh
# Restarts the jellyfin daemon (with whatever DOTNET_* settings are in the environment), then times
# 15 edits of one kind (see edit.cs; body by default); prints the daemon's own build time for the last 10.
# usage: steady.sh [body|signature|file]
set -e
kind=${1:-body}
cd "$(dirname "$0")/../bench/jellyfin"
rm -f MediaBrowser.Controller/Entities/BlazeProbe.cs
../../blaze-cs stop >/dev/null
../../blaze-cs build >/dev/null
for i in $(seq 1 15); do
  dotnet run ../../scripts/edit.cs -- "$kind" .
  ../../blaze-cs build | grep '^blaze-cs: rebuilt'
done | tail -10 | sed -E 's/.* in ([0-9]+) ms \(scan ([0-9]+) ms.*compile ([0-9]+) ms, write ([0-9]+) ms.*/\1 \2 \3 \4/' |
  sort -n | awk '{t[NR]=$1; s+=$2; c+=$3; w+=$4} END {printf "median %d ms (mean scan %d, compile %d, write %d)\n", t[int((NR+1)/2)], s/NR, c/NR, w/NR}'
