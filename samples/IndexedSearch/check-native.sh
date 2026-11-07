#!/usr/bin/env bash
# Drives a published (NativeAOT) build of the sample over standard input and output on both protocol eras and checks that
# tools/list and tools/call answer. Usage: check-native.sh <path to the indexed-search binary>
#   dotnet publish samples/IndexedSearch -c Release -p:SmokeMode=aot -warnaserror -o out && samples/IndexedSearch/check-native.sh out/indexed-search
set -euo pipefail
bin=${1:?usage: check-native.sh <binary>}

meta='"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"check","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}'

# 2025-11-25 lifecycle: initialization must finish before tools requests. Then close input immediately after the requests to check EOF draining.
coproc legacy_server { timeout 20 "$bin"; }
legacy_pid=$legacy_server_PID
trap 'kill "$legacy_pid" 2>/dev/null || true; wait "$legacy_pid" 2>/dev/null || true' EXIT
input=${legacy_server[1]}; output=${legacy_server[0]}
# Duplicate the descriptors so they remain readable even if the child exits before the shell starts collecting its replies.
exec {legacy_input}>&"$input" {legacy_output}<&"$output"
exec {input}>&- {output}<&-
printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' >&"$legacy_input"
IFS= read -r -t 20 -u "$legacy_output" initialized
printf '%s\n' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search","arguments":{"query":"coffee water"}}}' >&"$legacy_input"
exec {legacy_input}>&-
legacy=$(printf '%s\n' "$initialized"; timeout 20 cat <&"$legacy_output")
exec {legacy_output}<&-
wait "$legacy_pid"
trap - EXIT

# Modern (2026-07-28): no handshake; every request carries its version and identity in _meta.
modern=$(printf '%s\n' \
  "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{$meta}}" \
  "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"coffee water\"},$meta}}" |
  timeout 20 "$bin")

fail() { echo "FAIL: $1" >&2; echo "--- legacy ---" >&2; echo "$legacy" >&2; echo "--- modern ---" >&2; echo "$modern" >&2; exit 1; }

grep -q '"protocolVersion":"2025-11-25"' <<<"$legacy" || fail "legacy initialize"
grep -q '"name":"search"' <<<"$legacy" || fail "legacy tools/list"
grep -q 'Brewing' <<<"$legacy" || fail "legacy tools/call"
grep -q '"name":"search"' <<<"$modern" || fail "modern tools/list"
grep -q 'Brewing' <<<"$modern" || fail "modern tools/call"
echo "ok: tools/list and tools/call answered on 2025-11-25 and 2026-07-28"
