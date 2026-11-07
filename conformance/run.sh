#!/usr/bin/env bash
# Runs the protocol's conformance suite against a server or a client, black-box.
#
#   conformance/run.sh install                        install the pinned suite into $ONEMODELCONTEXT_CONFORMANCE_CACHE
#   conformance/run.sh list                           print the suite's scenarios
#   conformance/run.sh server --url URL --out DIR [--scenario NAME | --requirements REVISION]
#   conformance/run.sh client --command CMD --out DIR [--scenario NAME | --requirements REVISION]
#   conformance/run.sh regression --out DIR [--url URL] [--command CMD] [--only server|client]
#                                                      run every scenario named in conformance/scenarios.txt
#
# ONEMODELCONTEXT_CONFORMANCE_CACHE must name a directory outside the repository; the suite is installed there, never in the tree.
# Results (the suite's own output and a log per run) are written under the --out directory.
set -euo pipefail

VERSION="0.2.0-alpha.12"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${ONEMODELCONTEXT_CONFORMANCE_CACHE:?set ONEMODELCONTEXT_CONFORMANCE_CACHE to a directory outside the repository}"
CACHE="$ONEMODELCONTEXT_CONFORMANCE_CACHE"
PREFIX="$CACHE/conformance-$VERSION"

install_suite() {
  if ! suite --version > /dev/null 2>&1; then
    mkdir -p "$PREFIX"
    npm install --silent --no-audit --no-fund --prefix "$PREFIX" "@modelcontextprotocol/conformance@$VERSION"
  fi
  [ "$(suite --version)" = "$VERSION" ] || { echo "the conformance suite $VERSION did not install" >&2; exit 1; }
}

# Runs the installed suite as a separate process; npm finds its launcher.
suite() { npm exec --prefix "$PREFIX" --no --silent -- conformance "$@"; }

die() { echo "$*" >&2; exit 2; }

cmd="${1:-}"
[ -n "$cmd" ] || die "usage: run.sh install|list|server|client|regression ..."
shift || true
install_suite

case "$cmd" in
  install)
    echo "conformance suite $VERSION installed in $CACHE/conformance-$VERSION"
    ;;
  list)
    suite list
    ;;
  server|client)
    out=""; target=""; selector=()
    while [ $# -gt 0 ]; do
      case "$1" in
        --out) out="$2"; shift 2 ;;
        --url|--command) target="$2"; shift 2 ;;
        --scenario|--requirements) selector=("$1" "$2"); shift 2 ;;
        *) die "unknown option $1" ;;
      esac
    done
    [ -n "$out" ] && [ -n "$target" ] || die "need --out and --url (server) or --command (client)"
    mkdir -p "$out"
    cd "$out"
    flag=$([ "$cmd" = server ] && echo --url || echo --command)
    name="$cmd-${selector[1]:-all}"
    version=()
    [ -n "${SPEC_VERSION:-}" ] && version=(--spec-version "$SPEC_VERSION")
    suite "$cmd" "$flag" "$target" "${selector[@]}" "${version[@]}" 2>&1 | tee "$out/${name//\//_}.log"
    ;;
  regression)
    out=""; url=""; command=""; only=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --out) out="$2"; shift 2 ;;
        --only) only="$2"; shift 2 ;;
        --url) url="$2"; shift 2 ;;
        --command) command="$2"; shift 2 ;;
        *) die "unknown option $1" ;;
      esac
    done
    [ -n "$out" ] || die "need --out"
    mkdir -p "$out"
    failed=0; ran=0
    # Each line: side scenario revision. Blank lines and # comments are ignored.
    while read -r side scenario revision; do
      case "${side:-}" in ''|'#'*) continue ;; esac
      [ -z "$only" ] || [ "$only" = "$side" ] || continue
      ran=$((ran + 1))
      if [ "$side" = server ]; then
        # The revision column picks the scenario's variant for that revision (--spec-version).
        SPEC_VERSION="${revision:-}" "$HERE/run.sh" server --url "${url:?--url is needed for server scenarios}" --out "$out" --scenario "$scenario" || failed=$((failed + 1))
      else
        "$HERE/run.sh" client --command "${command:?--command is needed for client scenarios}" --out "$out" --scenario "$scenario" || failed=$((failed + 1))
      fi
    done < "$HERE/scenarios.txt"
    echo "regression: $ran scenario(s) run, $failed failed"
    [ "$failed" -eq 0 ]
    ;;
  *)
    die "unknown command $cmd"
    ;;
esac
