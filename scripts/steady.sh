#!/bin/sh
# Restarts the jellyfin daemon (with whatever DOTNET_* settings are in the environment), then times
# 15 method-body edits; prints the daemon's own build time for the last 10.
set -e
cd "$(dirname "$0")/../bench/jellyfin"
../../blaze-cs stop >/dev/null
../../blaze-cs build >/dev/null
for i in $(seq 1 15); do
  python3 ../../scripts/edit.py body .
  ../../blaze-cs build | tail -1
done | tail -10 | sed -E 's/.* in ([0-9]+) ms \(scan ([0-9]+) ms.*compile ([0-9]+) ms, write ([0-9]+) ms\)/\1 \2 \3 \4/' |
  sort -n | awk '{t[NR]=$1; s+=$2; c+=$3; w+=$4} END {printf "median %d ms (mean scan %d, compile %d, write %d)\n", t[int((NR+1)/2)], s/NR, c/NR, w/NR}'
