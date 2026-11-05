#!/bin/sh
# Times each edit kind three ways on jellyfin; markdown tables land in bench/results/<kind>.md.
# usage: bench.sh [runs]
set -e
runs=${1:-6}
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root/bench/jellyfin"
mkdir -p ../results
rm -f MediaBrowser.Controller/Entities/BlazeProbe.cs
../../blaze-cs stop >/dev/null; ../../blaze-cs build >/dev/null
for kind in body signature file; do
  hyperfine -N --runs "$runs" --warmup 2 --prepare "dotnet run $root/scripts/edit.cs -- $kind ." \
    -n 'dotnet build' 'dotnet build Jellyfin.slnx -nologo -v:q -clp:ErrorsOnly' \
    -n 'dotnet build -p:RunAnalyzers=false' 'dotnet build Jellyfin.slnx -nologo -v:q -clp:ErrorsOnly -p:RunAnalyzers=false' \
    -n 'blaze-cs build' "$root/blaze-cs build" \
    --export-markdown "../results/$kind.md" >/dev/null
  echo "## $kind"; cat "../results/$kind.md"
done
