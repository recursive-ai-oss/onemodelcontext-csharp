#!/usr/bin/env bash
# Starts the conformance server on one HTTP host and runs the server scenarios of scenarios.txt against it.
#
#   conformance/regression-http.sh kestrel|listener --out DIR
#
# "kestrel" is ASP.NET Core hosting the endpoint; "listener" is the library's built-in HTTP/1.1 listener. The same scenarios must
# pass on both. Needs ONEMODELCONTEXT_CONFORMANCE_CACHE (see run.sh) and a built conformance server (dotnet build).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
host="${1:?usage: regression-http.sh kestrel|listener --out DIR}"
shift
[ "${1:-}" = --out ] || { echo "need --out DIR" >&2; exit 2; }
out="${2:?need --out DIR}"
mkdir -p "$out"
case "$host" in
  kestrel) flag=--http ;;
  listener) flag=--listener ;;
  *) echo "unknown host $host" >&2; exit 2 ;;
esac

log="$out/server-$host.log"
dotnet run --project "$HERE/Server" -- "$flag" --port 0 > "$log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT

url=""
for _ in $(seq 1 120); do
  url="$(sed -n 's/^listening //p' "$log" | head -n 1)"
  [ -n "$url" ] && break
  kill -0 "$pid" 2>/dev/null || { cat "$log" >&2; exit 1; }
  sleep 0.5
done
[ -n "$url" ] || { echo "the server did not start" >&2; exit 1; }

"$HERE/run.sh" regression --out "$out/$host" --url "$url" --only server
