#!/bin/sh
# Correctness check for one edit kind. Builds the edit with blaze-cs, keeps every project's compiled
# assemblies, rebuilds everything with `dotnet build --no-incremental`, then compares each pair:
#   identical            byte for byte what MSBuild produced
#   same code            differs only in debug identity (scripts/pe-equal.cs): a dependent blaze-cs
#                        found unaffected keeps its DLL, whose PDB records the reference's old MVID
#   anything else fails
# usage: verify.sh body|signature|file
set -e
root="$(cd "$(dirname "$0")/.." && pwd)"
work="${TMPDIR:-/tmp}/blaze-cs-verify"
cd "$root/bench/jellyfin"
dotnet run "$root/scripts/edit.cs" -- "$1" .
"$root/blaze-cs" build | grep -E '^blaze-cs: (rebuilt|up to date|build failed)' | cut -c1-220

rm -rf "$work"; mkdir -p "$work"
find . -path ./.blaze-cs -prune -o -path '*/obj/*' -name '*.dll' -print | grep -v '/refint/' > "$work/list"
tar -cf "$work/blaze.tar" -T "$work/list"
dotnet build Jellyfin.slnx -nologo -v:q -clp:ErrorsOnly --no-incremental >/dev/null
mkdir "$work/blaze"; tar -xf "$work/blaze.tar" -C "$work/blaze"

identical=0; : > "$work/pairs"
while read -r f; do
  if cmp -s "$work/blaze/$f" "$f"; then identical=$((identical + 1)); else echo "$work/blaze/$f $f" >> "$work/pairs"; fi
done < "$work/list"
echo "$1: $identical of $(wc -l < "$work/list") assemblies identical to dotnet build --no-incremental"
if [ -s "$work/pairs" ]; then
  dotnet run "$root/scripts/pe-equal.cs" -- "$work/pairs"
  echo "$1: $(wc -l < "$work/pairs") differ only in debug identity (same code)"
fi
