#!/usr/bin/env bash
# The interop matrix: black-box pairings of OneModelContext with the reference SDKs, the Inspector CLI and published servers.
#
#   interop/run.sh --out DIR [--matrix reduced|full] [--only SUBSTRING]
#
# Environment (all required unless a default is shown):
#   INTEROP_CACHE      directory outside the repository where install.sh put the oracles
#   INTEROP_WORK       scratch directory for build output and logs (default: $INTEROP_CACHE/work)
#   INTEROP_PORT_BASE  first port of the range this run may use, loopback only (default 49300)
#   DOTNET_ROOT        .NET 10 SDK location when `dotnet` is not on PATH
# Each case writes cases/ID.jsonl (one JSON line per check), ID.err and ID.exit under DIR; report.mjs turns them into results.md/json.
# ID is client__server__transport__era. Every server this script starts is stopped on exit; only recorded PIDs are signalled.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
OUT=""; MATRIX=reduced; ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --matrix) MATRIX="$2"; shift 2 ;;
    --only) ONLY="$2"; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
[ -n "$OUT" ] || { echo "usage: run.sh --out DIR [--matrix reduced|full] [--only SUBSTRING]" >&2; exit 2; }
: "${INTEROP_CACHE:?set INTEROP_CACHE (see install.sh)}"
WORK="${INTEROP_WORK:-$INTEROP_CACHE/work}"
PORT="${INTEROP_PORT_BASE:-49300}"
[ -x "$INTEROP_CACHE/node/bin/node" ] && export PATH="$INTEROP_CACHE/node/bin:$PATH"
NPM="$INTEROP_CACHE/npm"; NM="$NPM/node_modules"; PY="$INTEROP_CACHE/py/bin"
mkdir -p "$OUT/cases" "$WORK/tmp"
export TMPDIR="$WORK/tmp" MCP_CATALOG_PATH="$WORK/tmp/inspector-catalog.json"
full() { [ "$MATRIX" = full ]; }

# ---- build the .NET drivers (Release, output outside the repository)
export ArtifactsPath="${ArtifactsPath:-$WORK/artifacts}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
for p in ours/Client/RecursiveAI.OneModelContext.InteropClient ours/Server/RecursiveAI.OneModelContext.InteropServer cs/RecursiveAI.OneModelContext.InteropCsSdk; do
  dotnet build "$HERE/$p.csproj" -c Release -v quiet -clp:NoSummary > "$WORK/build-$(basename "$p").log" 2>&1 || { echo "build failed: $p (see $WORK)" >&2; tail -5 "$WORK/build-$(basename "$p").log" >&2; exit 1; }
done
dll() { echo "$ArtifactsPath/bin/$1/release/$1.dll"; }
OURS_CLIENT="dotnet $(dll RecursiveAI.OneModelContext.InteropClient)"
OURS_SERVER="dotnet $(dll RecursiveAI.OneModelContext.InteropServer)"
CS="dotnet $(dll RecursiveAI.OneModelContext.InteropCsSdk)"
# The TypeScript drivers resolve the SDK from a node_modules next to them, so run copies from the install directory.
cp "$HERE"/ts/*.mjs "$NPM/"
TS_CLIENT="node $NPM/client.mjs"; TS_SERVER="node $NPM/server.mjs"
PY_CLIENT="$PY/python $HERE/py/client.py"; PY_SERVER="$PY/python $HERE/py/server.py"

PIDS=()
cleanup() { for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2> /dev/null; done; for p in "${PIDS[@]:-}"; do [ -n "$p" ] && wait "$p" 2> /dev/null; done; }
trap cleanup EXIT

next_port() { PORT=$((PORT + 1)); echo "$PORT"; }

# start_server NAME PORT CMD... : starts a server in the background and waits until it listens.
#   PORT is the port the command was told to use, or - when the command picks one and prints "listening URL" (sets SERVER_URL).
start_server() {
  local name="$1" port="$2"; shift 2
  "$@" > "$OUT/cases/server-$name.log" 2>&1 &
  PIDS+=("$!"); LAST_PID=$!
  local i
  for i in $(seq 1 120); do
    kill -0 "$LAST_PID" 2> /dev/null || { echo "server $name exited early" >&2; return 1; }
    if [ "$port" = "-" ]; then
      if grep -q '^listening ' "$OUT/cases/server-$name.log"; then SERVER_URL=$(grep -m1 '^listening ' "$OUT/cases/server-$name.log" | cut -d' ' -f2); return 0; fi
    elif (exec 3<> "/dev/tcp/127.0.0.1/$port") 2> /dev/null; then return 0; fi
    sleep 0.5
  done
  echo "server $name did not start listening" >&2; return 1
}
stop_server() { kill "$LAST_PID" 2> /dev/null; wait "$LAST_PID" 2> /dev/null; }

# run_case ID CMD... : runs one client, capturing its JSON lines.
run_case() {
  local id="$1"; shift
  case "$id" in *"$ONLY"*) ;; *) return 0 ;; esac
  echo "case $id" >&2
  timeout 240 "$@" > "$OUT/cases/$id.jsonl" 2> "$OUT/cases/$id.err"
  echo $? > "$OUT/cases/$id.exit"
}
# The oracle SDK's http servers print "listening PORT" (ts) or take the port as an argument; normalise to a URL.
url_of() { echo "http://127.0.0.1:$1/mcp"; }

# ---- A: reference clients and the Inspector against our server
hosts=(aspnet); full && hosts+=(listener)
echo "== our server, stdio" >&2
OS="$OURS_SERVER --stdio"
run_case "ts__ours__stdio__native" $TS_CLIENT --stdio "$OS"
run_case "py__ours__stdio__auto" $PY_CLIENT --mode auto --stdio "$OS"
run_case "py__ours__stdio__legacy" $PY_CLIENT --mode legacy --stdio "$OS"
run_case "cs__ours__stdio__native" $CS client --stdio "$OS"
run_case "inspector__ours__stdio__modern" "$HERE/inspector.sh" modern -- $OS
run_case "inspector__ours__stdio__legacy" "$HERE/inspector.sh" legacy -- $OS
for h in "${hosts[@]}"; do
  flag=--http; [ "$h" = listener ] && flag=--listener
  echo "== our server, $h" >&2
  if start_server "ours-$h" - $OURS_SERVER $flag --port 0; then
    U="$SERVER_URL"
    run_case "ts__ours-${h}__http__native" $TS_CLIENT --url "$U"
    run_case "py__ours-${h}__http__auto" $PY_CLIENT --mode auto --url "$U"
    run_case "py__ours-${h}__http__legacy" $PY_CLIENT --mode legacy --url "$U"
    full && run_case "py__ours-${h}__http__modern" $PY_CLIENT --mode 2026-07-28 --url "$U"
    run_case "cs__ours-${h}__http__native" $CS client --url "$U"
    run_case "inspector__ours-${h}__http__modern" "$HERE/inspector.sh" modern -- "$U"
    run_case "inspector__ours-${h}__http__legacy" "$HERE/inspector.sh" legacy -- "$U"
    stop_server
  fi
done

# ---- B: our client against the reference servers (eras: auto, forced legacy, and forced modern in the full matrix)
eras=(auto legacy); full && eras+=(modern)
for era in "${eras[@]}"; do
  pymode=$era; [ "$era" = modern ] && pymode=2026-07-28
  run_case "ours__ts-server__stdio__$era" $OURS_CLIENT --era "$era" --stdio "$TS_SERVER stdio"
  run_case "ours__py-server__stdio__$era" $OURS_CLIENT --era "$era" --stdio "$PY_SERVER stdio"
  run_case "ours__cs-server__stdio__$era" $OURS_CLIENT --era "$era" --stdio "$CS server stdio"
done
for kind in ts py cs; do
  p=$(next_port); echo "== $kind server, http" >&2
  case $kind in
    ts) srv=($TS_SERVER http "$p") ;;
    py) srv=($PY_SERVER http "$p") ;;
    cs) srv=($CS server http "$p") ;;
  esac
  if start_server "$kind-http" "$p" "${srv[@]}"; then
    for era in "${eras[@]}"; do run_case "ours__${kind}-server__http__$era" $OURS_CLIENT --era "$era" --url "$(url_of "$p")"; done
  fi
  stop_server
done
# The deprecated HTTP+SSE transport (2024-11-05): a legacy-only server, reached through the client's fallback.
p=$(next_port); echo "== ts server, http+sse" >&2
if start_server "ts-sse" "$p" $TS_SERVER sse "$p"; then
  for era in auto legacy; do run_case "ours__ts-server-sse__sse__$era" $OURS_CLIENT --era "$era" --url "http://127.0.0.1:$p/sse"; done
  run_case "ts__ts-server-sse__sse__native" $TS_CLIENT --sse --url "http://127.0.0.1:$p/sse"
fi
stop_server

# ---- C: our client against published servers (generic probe: list tools, resources, prompts)
EVERY="$NM/.bin/mcp-server-everything"
for era in "${eras[@]}"; do
  [ -x "$EVERY" ] && run_case "ours__everything-2.2.1__stdio__$era" $OURS_CLIENT --era "$era" --profile generic --stdio "$EVERY stdio"
  [ -x "$PY/mcp-server-time" ] && run_case "ours__server-time-2026.11.4__stdio__$era" $OURS_CLIENT --era "$era" --profile generic --stdio "$PY/mcp-server-time"
done
if full; then
  mkdir -p "$WORK/tmp/fs"
  for era in "${eras[@]}"; do
    run_case "ours__filesystem-2.2.0__stdio__$era" $OURS_CLIENT --era "$era" --profile generic --stdio "$NM/.bin/mcp-server-filesystem $WORK/tmp/fs"
    run_case "ours__memory-2.1.0__stdio__$era" $OURS_CLIENT --era "$era" --profile generic --stdio "$NM/.bin/mcp-server-memory"
  done
fi

{
  echo "{"
  echo "  \"matrix\": \"$MATRIX\","
  echo "  \"host\": \"$(uname -srm)\","
  echo "  \"node\": \"$(node --version)\","
  echo "  \"dotnet\": \"$(dotnet --version)\","
  echo "  \"python\": \"$("$PY/python" --version)\","
  echo "  \"pins\": \"sdk(ts) 1.32.2; inspector $("$NM/.bin/inspector" --version 2> /dev/null | head -1); mcp(py) 2.3.0; ModelContextProtocol(cs) 2.4.0; server-everything 2.2.1; mcp-server-time 2026.11.4; server-filesystem 2.2.0; server-memory 2.1.0\""
  echo "}"
} > "$OUT/meta.json"
node "$HERE/report.mjs" "$OUT"
