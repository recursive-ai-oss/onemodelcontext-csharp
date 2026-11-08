#!/usr/bin/env bash
# Publishes the four NativeAOT artefacts the release bar names and runs each one:
#   core         tests/RecursiveAI.OneModelContext.AotSmoke   (JSON-RPC, codecs, OAuth/JOSE, extensions, authorization server)
#   stdio server samples/IndexedSearch                        (driven on both protocol eras by its check-native.sh)
#   HTTP server  samples/HttpServer                           (built-in listener; one request per era over HTTP)
#   CLI          src/RecursiveAI.OneModelContext.Cli          (inspects the native stdio server)
# Compiler, trim and AOT warnings are errors; native linker debug-info notices remain informational.
# Usage: tools/aot-check.sh [output directory]   (default: a temporary directory, removed on exit)
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-$(mktemp -d)}
[ -n "${1:-}" ] || trap 'rm -rf "$out"' EXIT
mkdir -p "$out"

publish() { # project, output subdirectory
  echo "== publish $1"
  dotnet publish "$root/$1" -c Release -p:SmokeMode=aot -o "$out/$2" -v quiet -clp:NoSummary 2>&1 | tee "$out/$2.log"
  if grep -Ei 'warning[[:space:]]+(IL[0-9]+|AOT)' "$out/$2.log"; then echo "FAIL: IL or AOT warnings in $1" >&2; exit 1; fi
}

publish tests/RecursiveAI.OneModelContext.AotSmoke core
publish samples/IndexedSearch stdio
publish samples/HttpServer http
publish src/RecursiveAI.OneModelContext.Cli cli

echo "== run core smoke"
"$out/core/RecursiveAI.OneModelContext.AotSmoke"

echo "== run stdio server on both eras"
"$root/samples/IndexedSearch/check-native.sh" "$out/stdio/indexed-search"

echo "== run HTTP server on both eras"
port=${AOT_CHECK_PORT:-0}
"$out/http/$(ls "$out/http" | grep -vE '\.(dbg|pdb|json)$' | head -1)" "$port" > "$out/http.out" 2>&1 &
pid=$!
trap 'kill $pid 2>/dev/null || true; [ -z "${1:-}" ] && rm -rf "$out"' EXIT
url=
for _ in $(seq 1 100); do
  url=$(sed -n 's/^Serving //p' "$out/http.out" | head -1)
  [ -n "$url" ] && break
  sleep 0.1
done
[ -n "$url" ] || { echo "FAIL: HTTP server did not start" >&2; cat "$out/http.out" >&2; exit 1; }
meta='"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"check","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}'
modern=$(curl -fsS "$url" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' -H 'Mcp-Method: tools/call' -H 'Mcp-Name: add' \
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"add\",\"arguments\":{\"a\":1,\"b\":2},$meta}}")
grep -q '"text":"3"' <<<"$modern" || { echo "FAIL: modern HTTP call: $modern" >&2; exit 1; }
legacy=$(curl -fsS "$url" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}')
grep -q '"protocolVersion":"2025-11-25"' <<<"$legacy" || { echo "FAIL: legacy HTTP initialize: $legacy" >&2; exit 1; }
kill $pid; wait $pid 2>/dev/null || true

echo "== run CLI against the native stdio server"
"$out/cli/onemodelcontext" inspect "$out/stdio/indexed-search" | grep -q 'search' || { echo "FAIL: cli inspect" >&2; exit 1; }
"$out/cli/onemodelcontext" inspect "$out/stdio/indexed-search" --era legacy | grep -q 'legacy era' || { echo "FAIL: cli inspect legacy" >&2; exit 1; }

echo "OK: four NativeAOT publishes with zero IL/AOT warnings, all run"
