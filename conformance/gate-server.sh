#!/usr/bin/env bash
# Runs every server scenario the suite lists, at every revision it lists the scenario for, against one HTTP host of the conformance
# server. Extension scenarios run with --force. Unlike the regression run it takes the scenario list from the suite, not from
# scenarios.txt, and prints one line per (scenario, revision) plus totals.
#
#   conformance/gate-server.sh aspnetcore|listener --out DIR [--revision REV]
#
# Needs ONEMODELCONTEXT_CONFORMANCE_CACHE (see run.sh) and a built conformance server.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
host="${1:?usage: gate-server.sh aspnetcore|listener --out DIR [--revision REV]}"
shift
out=""; only_rev=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --revision) only_rev="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] || { echo "need --out DIR" >&2; exit 2; }
case "$host" in
  aspnetcore) flag=--http ;;
  listener) flag=--listener ;;
  *) echo "unknown host $host" >&2; exit 2 ;;
esac
mkdir -p "$out/$host"

log="$out/$host/server.log"
dotnet run --project "$HERE/Server" -- "$flag" --port 0 > "$log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
url=""
for _ in $(seq 1 240); do
  url="$(sed -n 's/^listening //p' "$log" | head -n 1)"
  [ -n "$url" ] && break
  kill -0 "$pid" 2>/dev/null || { cat "$log" >&2; exit 1; }
  sleep 0.5
done
[ -n "$url" ] || { echo "the server did not start" >&2; exit 1; }

# Server section of `list`: "  - name [rev,rev]".
mapfile -t rows < <("$HERE/run.sh" list | sed -n '/^Server scenarios/,/^$/p' | sed -n 's/^  - \([^ ]*\) \[\(.*\)\]$/\1 \2/p')

pass=0; fail=0
for row in "${rows[@]}"; do
  scenario="${row%% *}"; revs="${row#* }"
  IFS=',' read -ra list <<< "$revs"
  for rev in "${list[@]}"; do
    [ -z "$only_rev" ] || [ "$only_rev" = "$rev" ] || continue
    name="${scenario}@${rev}"
    if [ "$rev" = extension ]; then
      CONFORMANCE_FORCE=1 SPEC_VERSION="" timeout 120 "$HERE/run.sh" server --url "$url" --out "$out/$host/$name" --scenario "$scenario" > "$out/$host/$name.log" 2>&1
    else
      CONFORMANCE_FORCE="" SPEC_VERSION="$rev" timeout 120 "$HERE/run.sh" server --url "$url" --out "$out/$host/$name" --scenario "$scenario" > "$out/$host/$name.log" 2>&1
    fi
    rc=$?
    if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo "PASS $name"; else fail=$((fail + 1)); echo "FAIL($rc) $name"; fi
  done
done
echo "gate-server $host: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
