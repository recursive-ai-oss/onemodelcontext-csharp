#!/usr/bin/env bash
# Runs every listed client scenario at each advertised revision, without a baseline.
# Usage: gate-client.sh --command CMD --out DIR [--revision REV]
# Requires the pinned suite cache used by run.sh and an already built client.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out=""; command=""; only_rev=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --command) command="$2"; shift 2 ;;
    --revision) only_rev="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] && [ -n "$command" ] || { echo 'need --out DIR and --command CMD' >&2; exit 2; }
mkdir -p "$out"
"$HERE/run.sh" list > "$out/list.txt" || exit 1
mapfile -t rows < <(sed -n '/^Client scenarios/,/^$/p' "$out/list.txt" | sed -n 's/^  - \([^ ]*\) \[\(.*\)\]$/\1 \2/p')
[ "${#rows[@]}" -gt 0 ] || { echo 'no client scenarios listed' >&2; exit 1; }
printf 'scenario\trevision\texit\n' > "$out/results.tsv"
pass=0; fail=0
for row in "${rows[@]}"; do
  scenario="${row%% *}"; revs="${row#* }"
  IFS=',' read -ra list <<< "$revs"
  for rev in "${list[@]}"; do
    [ -z "$only_rev" ] || [ "$only_rev" = "$rev" ] || continue
    name="${scenario//\//_}@$rev"
    force=""; version="$rev"
    if [ "$rev" = extension ]; then force=1; version=""; fi
    CONFORMANCE_FORCE="$force" SPEC_VERSION="$version" timeout 180 "$HERE/run.sh" client --command "$command" --out "$out/$name" --scenario "$scenario" > "$out/$name.log" 2>&1
    rc=$?
    printf '%s\t%s\t%s\n' "$scenario" "$rev" "$rc" >> "$out/results.tsv"
    if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo "PASS $name"; else fail=$((fail + 1)); echo "FAIL($rc) $name"; fi
  done
done
echo "gate-client: $pass passed, $fail failed (inspect suite reports for skipped checks)"
[ "$fail" -eq 0 ]
