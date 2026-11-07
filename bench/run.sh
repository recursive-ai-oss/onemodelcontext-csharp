#!/usr/bin/env bash
# Publishes the bench apps and runs the speed rows. Usage: bench/run.sh [--rows all|r1,r2,...] [--out DIR] [--seconds N] [--seq N]
# Rows: r1 stdio round trip, r2 HTTP throughput (+ listener), r3 client calls/s, r4 large results, r5 tools/list 1,000, r6 AOT cold start.
# The results (results.md, results.json) are for the machine this runs on; load before, during and after is recorded in them.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROWS=all; OUT="$PWD/bench-out"; SECONDS_ARG=4; SEQ=2000
while [ $# -gt 0 ]; do
  case "$1" in
    --rows) ROWS="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --seconds) SECONDS_ARG="$2"; shift 2 ;;
    --seq) SEQ="$2"; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$OUT/bin"
REF_VERSION="$(sed -n 's/.*Include="ModelContextProtocol" Version="\([^"]*\)".*/\1/p' "$HERE/../Directory.Packages.props" | head -1)"
P="$HERE/Ours/RecursiveAI.OneModelContext.Bench.Ours.csproj"
R="$HERE/Reference/RecursiveAI.OneModelContext.Bench.Reference.csproj"
D="$HERE/Driver/RecursiveAI.OneModelContext.Bench.Driver.csproj"
dotnet publish "$D" -c Release -o "$OUT/bin/driver" -v quiet -clp:NoSummary
dotnet publish "$P" -c Release -o "$OUT/bin/ours" -v quiet -clp:NoSummary
dotnet publish "$R" -c Release -o "$OUT/bin/ref" -v quiet -clp:NoSummary
if [ "$ROWS" = all ] || [[ ",$ROWS," == *,r6,* ]]; then
  dotnet publish "$P" -c Release -p:BenchAot=true -o "$OUT/bin/ours-aot" -v quiet -clp:NoSummary
  # The reference may not publish with NativeAOT; the row then reports ours alone.
  dotnet publish "$R" -c Release -p:BenchAot=true -o "$OUT/bin/ref-aot" -v quiet -clp:NoSummary > "$OUT/ref-aot-publish.log" 2>&1 || { echo "reference NativeAOT publish failed (see ref-aot-publish.log)"; rm -rf "$OUT/bin/ref-aot"; }
fi
"$OUT/bin/driver/bench-driver" --bin "$OUT/bin" --out "$OUT" --rows "$ROWS" --seconds "$SECONDS_ARG" --seq "$SEQ" --ref-version "$REF_VERSION"
