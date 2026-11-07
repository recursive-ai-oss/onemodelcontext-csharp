#!/usr/bin/env bash
# Builds the harness, instruments the library with SharpFuzz and runs a target under libFuzzer: the HTTP endpoint (default) or, with
# FUZZ_TARGET=sse, the event-stream reader of the HTTP client, or with FUZZ_TARGET=oauth the parsers of what an OAuth client reads from a server.
#
#   fuzz/run.sh <seconds> [extra libFuzzer flags]
#   fuzz/run.sh replay <file>...          run saved inputs once, without libFuzzer (reproduce a finding)
#
# Environment (nothing is baked in):
#   ONEMODELCONTEXT_FUZZ_TOOLS   directory holding `libfuzzer-dotnet` (the libFuzzer host) and `sharpfuzz/sharpfuzz` (the instrumenter)
#   ONEMODELCONTEXT_FUZZ_WORK    where builds, the corpus and findings go (default ./fuzz-work; keep it outside the checkout)
#   FUZZ_TIMEOUT                 per-input timeout in seconds (default 10)
#   FUZZ_RSS_MB                  memory limit in MB (default 2048)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="${ONEMODELCONTEXT_FUZZ_WORK:-$PWD/fuzz-work}"
tools="${ONEMODELCONTEXT_FUZZ_TOOLS:-}"
host="$tools/libfuzzer-dotnet"
instrument="$tools/sharpfuzz/sharpfuzz"
bin="$work/bin"
mkdir -p "$bin"

dotnet build "$root/fuzz/RecursiveAI.OneModelContext.Fuzz/RecursiveAI.OneModelContext.Fuzz.csproj" -c Release -v quiet -clp:NoSummary -o "$bin"

if [[ "${1:-}" == replay ]]; then
  shift
  exec dotnet "$bin/RecursiveAI.OneModelContext.Fuzz.dll" replay "$@"
fi

seconds="${1:?usage: run.sh <seconds> | run.sh replay <file>...}"
shift
[[ -x "$host" ]] || { echo "libFuzzer host not found at '$host' (set ONEMODELCONTEXT_FUZZ_TOOLS)" >&2; exit 2; }
[[ -x "$instrument" ]] || { echo "sharpfuzz not found at '$instrument' (set ONEMODELCONTEXT_FUZZ_TOOLS)" >&2; exit 2; }
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"

"$instrument" "$bin/RecursiveAI.OneModelContext.dll" > /dev/null

target="${FUZZ_TARGET:-http}"
corpus="$work/corpus-$target"
findings="$work/findings-$target"
mkdir -p "$corpus" "$findings"

seed() { [[ -e "$corpus/$1" ]] || printf "$2" > "$corpus/$1"; }
if [[ "$target" == sse ]]; then
  # Seeds: byte 0 is the read size, then an event stream.
  seed message '\x07id: 1\nretry: 100\ndata: {"jsonrpc":"2.0","id":1,"result":{}}\n\n'
  seed crlf '\x03event: endpoint\r\ndata: /messages?x=1\r\n\r\n: keep-alive\r\ndata: a\r\ndata: b\r\n\r\n'
  seed bom '\x01\xef\xbb\xbfid: 9\n\ndata:\n\ndata: x\r\r'
elif [[ "$target" == oauth ]]; then
  # Byte 0 picks the parser: 0 challenge header, 1 resource metadata, 2 server metadata, 3 redirect query.
  seed challenge '\x00Bearer error="insufficient_scope", scope="a b", resource_metadata="https://r.example/.well-known/x"'
  seed challenge-two '\x00Basic realm="x", Bearer realm=r, error_description="say \"hi\""'
  seed resource '\x01{"resource":"https://r.example/mcp","authorization_servers":["https://a.example"],"scopes_supported":["x"]}'
  seed server '\x02{"issuer":"https://a.example","token_endpoint":"https://a.example/t","code_challenge_methods_supported":["S256"],"authorization_response_iss_parameter_supported":true}'
  seed redirect '\x03code=abc&state=s&iss=https%3A%2F%2Fi.example'
  seed redirect-error '\x03error=access_denied&state=s&iss=https%3A%2F%2Fi.example&error_description=no'
  max_len=4096
elif [[ "$target" == listener ]]; then
  # The built-in listener's request head, chunked body and length body parsers.
  seed head-post '\x00POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 12'
  seed head-chunked '\x00POST /mcp HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked'
  seed head-smuggle '\x00POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 4\r\nTransfer-Encoding: chunked'
  seed head-fold '\x00GET / HTTP/1.1\r\nHost: h\r\nX-A: 1\r\n  2'
  seed chunked '\x015;ext=1\r\nhello\r\n0\r\nX-T: 1\r\n\r\n'
  seed chunked-two '\x011\r\na\r\n10\r\n0123456789abcdef\r\n0\r\n\r\n'
  seed length '\x02\x05hello'
  max_len=8192
else
  hdr='Content-Type: application/json\nAccept: application/json, text/event-stream\nMCP-Protocol-Version: 2026-07-28\nMcp-Method: tools/call\nMcp-Name: echo'
  meta='"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}'
  seed modern-call "\x00$hdr\n\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{},$meta}}}"
  seed modern-progress "\x00$hdr\n\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"progress\",\"arguments\":{},$meta,\"progressToken\":\"p\"}}}"
  seed legacy-init '\x00Content-Type: application/json\nAccept: application/json, text/event-stream\n\n{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"f","version":"1"}}}'
  seed legacy-call '\x00Content-Type: application/json\nAccept: application/json, text/event-stream\nMcp-Session-Id: $SESSION\nMCP-Protocol-Version: 2025-11-25\n\n{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  seed legacy-batch '\x00Content-Type: application/json\nAccept: application/json, text/event-stream\nMcp-Session-Id: $SESSION\n\n[{"jsonrpc":"2.0","id":3,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"}]'
  seed get-stream '\x03Accept: text/event-stream\nMcp-Session-Id: $SESSION\nLast-Event-ID: 0\n\n'
  seed delete '\x04Mcp-Session-Id: $SESSION\n\n'
fi

cd "$findings"
exec "$host" --target_path="$(command -v dotnet)" --target_arg="$bin/RecursiveAI.OneModelContext.Fuzz.dll" \
  -max_total_time="$seconds" -timeout="${FUZZ_TIMEOUT:-10}" -rss_limit_mb="${FUZZ_RSS_MB:-2048}" \
  -max_len="${max_len:-65536}" -artifact_prefix="$findings/" "$@" "$corpus"
