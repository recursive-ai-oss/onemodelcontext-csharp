#!/usr/bin/env bash
# Runs the conformance suite's authorization-server scenarios against the authorization server in conformance/AuthorizationServer.
#
#   conformance/authorization-server.sh --out DIR [--spec-version REVISION]
#
# The suite plays the user's browser by printing an authorization URL and waiting for the redirect to reach its callback server, so this
# script plays the browser too: it opens the URL with curl and delivers the redirect to the callback. Set ONEMODELCONTEXT_AS_PORT and
# ONEMODELCONTEXT_CALLBACK_PORT to free loopback ports, and ONEMODELCONTEXT_CONFORMANCE_CACHE to the directory the suite is installed
# in (see run.sh install).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${ONEMODELCONTEXT_CONFORMANCE_CACHE:?set ONEMODELCONTEXT_CONFORMANCE_CACHE to the directory the suite is installed in}"
: "${ONEMODELCONTEXT_AS_PORT:?set ONEMODELCONTEXT_AS_PORT to a free loopback port}"
: "${ONEMODELCONTEXT_CALLBACK_PORT:?set ONEMODELCONTEXT_CALLBACK_PORT to a free loopback port}"
OUT=""
SPEC=()
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --spec-version) SPEC=(--spec-version "$2"); shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
[ -n "$OUT" ] || { echo "--out is required" >&2; exit 2; }
mkdir -p "$OUT"

RESOURCE="http://127.0.0.1:$ONEMODELCONTEXT_AS_PORT/mcp"
dotnet run --project "$HERE/AuthorizationServer" -- --port "$ONEMODELCONTEXT_AS_PORT" --client-id test-client \
  --resource "$RESOURCE" --callback-port "$ONEMODELCONTEXT_CALLBACK_PORT" > "$OUT/server.log" 2>&1 &
SERVER=$!
SUITE=""
trap 'kill $SERVER $SUITE 2>/dev/null || true' EXIT
for _ in $(seq 1 90); do grep -q '^listening' "$OUT/server.log" && break; sleep 1; done

PREFIX="$ONEMODELCONTEXT_CONFORMANCE_CACHE/conformance-0.2.0-alpha.12"
npm exec --prefix "$PREFIX" --no --silent -- conformance authorization --url "http://127.0.0.1:$ONEMODELCONTEXT_AS_PORT" \
  --client-id test-client --resource "$RESOURCE" --port "$ONEMODELCONTEXT_CALLBACK_PORT" -o "$OUT" "${SPEC[@]}" > "$OUT/suite.log" 2>&1 &
SUITE=$!

URL=""
for _ in $(seq 1 60); do
  URL=$(grep -o "http://127.0.0.1:$ONEMODELCONTEXT_AS_PORT/authorize?[^ ]*" "$OUT/suite.log" | head -1 || true)
  [ -n "$URL" ] && break
  kill -0 "$SUITE" 2>/dev/null || break
  sleep 1
done
if [ -n "$URL" ]; then
  LOCATION=$(curl -s -D - -o /dev/null "$URL" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
  curl -s -o /dev/null "$LOCATION"
fi
wait "$SUITE" && STATUS=0 || STATUS=$?
grep -E 'passed|Total' "$OUT/suite.log" || true
exit "$STATUS"
