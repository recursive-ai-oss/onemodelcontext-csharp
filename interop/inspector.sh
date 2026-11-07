#!/usr/bin/env bash
# Runs one Inspector CLI probe set against a server in one era mode and prints one JSON line per check.
#   inspector.sh ERA -- TARGET...      ERA is modern or legacy; TARGET is a URL or a command line
# Needs INTEROP_CACHE (the Inspector is installed there by install.sh). The Inspector is run, never read.
set -uo pipefail
era="$1"; shift; [ "${1:-}" = "--" ] && shift
: "${INTEROP_CACHE:?}"
NM="$INTEROP_CACHE/npm/node_modules"
I="$NM/.bin/inspector"
export MCP_CATALOG_PATH="${MCP_CATALOG_PATH:-${TMPDIR:-/tmp}/inspector-catalog-$$.json}"
transport=()
case "$1" in http://*|https://*) transport=(--transport http) ;; esac

json() { python3 -c 'import json,sys; print(json.dumps({"check":sys.argv[1],"ok":sys.argv[2]=="1","detail":sys.argv[3][:300]}))' "$@"; }

# probe CHECK EXPECTED-SUBSTRING -- inspector method arguments...
probe() {
  local check="$1" expect="$2"; shift 3
  local out rc
  out=$(timeout 90 "$I" --cli --protocol-era "$era" "${transport[@]}" "$@" -- "${TARGET[@]}" 2>&1); rc=$?
  local one; one=$(printf '%s' "$out" | tr '\n' ' ' | tr -s ' ')
  if [ "$expect" = "!error" ]; then
    if [ $rc -ne 0 ] || printf '%s' "$out" | grep -q -e '"isError": true' -e '"error"'; then json "$check" 1 "error reported (exit $rc): $one"; else json "$check" 0 "no error: $one"; fi
  elif [ $rc -eq 0 ] && printf '%s' "$out" | grep -q -F -e "$expect"; then json "$check" 1 "$one"
  else json "$check" 0 "exit $rc: $one"; fi
}

TARGET=("$@")
probe "tools/list" "echo" -- --method tools/list
probe "tools/call text" '"text": "hi"' -- --method tools/call --tool-name echo --tool-arg message=hi
probe "tools/call image" "image/png" -- --method tools/call --tool-name image
probe "resources/list" "mem://hello" -- --method resources/list
probe "resources/read" "hello from interop" -- --method resources/read --uri mem://hello
probe "prompts/list" "greet" -- --method prompts/list
probe "prompts/get" "Say hello to Ann" -- --method prompts/get --prompt-name greet --prompt-args name=Ann
probe "error: unknown tool" "!error" -- --method tools/call --tool-name nope
