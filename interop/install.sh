#!/usr/bin/env bash
# Installs the interop oracles at their pinned versions under $INTEROP_CACHE (a directory outside the repository).
#   npm packages  -> $INTEROP_CACHE/npm   (package.json in this directory is the pin list)
#   Python        -> $INTEROP_CACHE/py    (a uv virtual environment)
#   Node          -> $INTEROP_CACHE/node  (only when `node` is not on PATH: the official Linux tarball, user-local)
# The oracles are run, never read: nothing here opens a file inside an installed package.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${INTEROP_CACHE:?set INTEROP_CACHE to a directory outside the repository}"
NODE_VERSION="22.22.1"
MCP_PY="2.3.0"
PY_SERVERS=("mcp-server-time==2026.11.4")

mkdir -p "$INTEROP_CACHE"

if ! command -v node > /dev/null 2>&1; then
  if [ ! -x "$INTEROP_CACHE/node/bin/node" ]; then
    case "$(uname -m)" in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo "unsupported CPU $(uname -m)" >&2; exit 1 ;; esac
    os=linux; [ "$(uname -s)" = Darwin ] && os=darwin
    tarball="node-v$NODE_VERSION-$os-$arch.tar.xz"; [ "$os" = darwin ] && tarball="node-v$NODE_VERSION-$os-$arch.tar.gz"
    mkdir -p "$INTEROP_CACHE/node"
    curl -fsSL "https://nodejs.org/dist/v$NODE_VERSION/$tarball" | tar -xf - -C "$INTEROP_CACHE/node" --strip-components=1
  fi
  export PATH="$INTEROP_CACHE/node/bin:$PATH"
fi

mkdir -p "$INTEROP_CACHE/npm"
cp "$HERE/package.json" "$INTEROP_CACHE/npm/package.json"
(cd "$INTEROP_CACHE/npm" && npm install --silent --no-audit --no-fund --no-package-lock)

if [ ! -x "$INTEROP_CACHE/py/bin/python" ]; then
  uv venv --quiet --python 3.13 "$INTEROP_CACHE/py"
fi
uv pip install --quiet --python "$INTEROP_CACHE/py/bin/python" "mcp==$MCP_PY" "${PY_SERVERS[@]}"

echo "node $(node --version); npm packages in $INTEROP_CACHE/npm; python $("$INTEROP_CACHE/py/bin/python" --version) in $INTEROP_CACHE/py"
