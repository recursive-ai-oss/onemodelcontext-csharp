#!/bin/bash
# Packs the library and the template pack into a scratch feed, installs the templates into a scratch
# dotnet home, then creates each template, builds it, runs it, and publishes the stdio server with NativeAOT.
# Needs the .NET 10 SDK on PATH. Set ONEMODELCONTEXT_TEMPLATE_CHECK_DIR to a scratch directory outside the repository: projects
# created inside it would pick up the repository's central package management and fail to restore.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(dirname "$here")
: "${ONEMODELCONTEXT_TEMPLATE_CHECK_DIR:?set ONEMODELCONTEXT_TEMPLATE_CHECK_DIR to a directory outside the repository}"
case "$(cd "$(dirname "$ONEMODELCONTEXT_TEMPLATE_CHECK_DIR")" 2>/dev/null && pwd)/" in "$repo"/*) echo "ONEMODELCONTEXT_TEMPLATE_CHECK_DIR must be outside $repo" >&2; exit 2;; esac
work=$ONEMODELCONTEXT_TEMPLATE_CHECK_DIR/template-check
version=0.1.0-dev.$(date +%s)
rm -rf "$work"
mkdir -p "$work/feed" "$work/home"

dotnet pack "$repo/src/RecursiveAI.OneModelContext/RecursiveAI.OneModelContext.csproj" -c Release -p:Version="$version" -o "$work/feed" -v quiet -clp:NoSummary
dotnet pack "$here/RecursiveAI.OneModelContext.Templates.csproj" -c Release -p:Version="$version" -o "$work/feed" -v quiet -clp:NoSummary

# A private dotnet home keeps the installed templates away from the real one.
export DOTNET_CLI_HOME="$work/home"
dotnet new install "$work/feed/RecursiveAI.OneModelContext.Templates.$version.nupkg" >/dev/null
dotnet new list onemodelcontext

for variant in "server stdio" "server http" "client"; do
  set -- $variant
  if [ "${2:-}" = http ] && [ -n "${SKIP_HTTP:-}" ]; then continue; fi
  kind=$1
  transport=${2:-}
  name=Check${kind^}${transport^}
  dir="$work/$name"
  if [ -n "$transport" ]; then
    dotnet new onemodelcontext-$kind -n "$name" -o "$dir" --transport "$transport" --libraryVersion "$version" >/dev/null
  else
    dotnet new onemodelcontext-$kind -n "$name" -o "$dir" --libraryVersion "$version" >/dev/null
  fi
  printf '<configuration><packageSources><clear /><add key="local" value="%s" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>\n' "$work/feed" >"$dir/nuget.config"
  dotnet build "$dir" -c Release -o "$dir/out" -v quiet -clp:NoSummary -warnaserror
  echo "built $name"
  if [ "$kind$transport" = serverhttp ]; then
    dotnet "$dir/out/$name.dll" "${HTTP_PORT:-47810}" &
    pid=$!
    sleep 3
    code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
      -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' \
      "http://127.0.0.1:${HTTP_PORT:-47810}/mcp")
    kill "$pid"; wait "$pid" 2>/dev/null || true
    [ "$code" = 200 ] || { echo "http variant answered $code"; exit 1; }
    echo "http variant answered initialize"
    dotnet publish "$dir" -c Release -o "$dir/aot" -v quiet -clp:NoSummary -warnaserror
    echo "published $name with NativeAOT"
  fi
  if [ "$kind$transport" = serverstdio ]; then
    dotnet publish "$dir" -c Release -o "$dir/aot" -v quiet -clp:NoSummary -warnaserror
    echo "published $name with NativeAOT"
  fi
done

echo "template check ok: $work (feed version $version)"
