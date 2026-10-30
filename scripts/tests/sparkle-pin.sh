#!/usr/bin/env bash
# check-sparkle-pin.sh passes on the repository's own Package.resolved and fails, naming both
# versions, when Package.resolved's Sparkle differs from the pin.
set -euo pipefail
here="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
check="$here/scripts/mac/check-sparkle-pin.sh"

(cd "$here" && "$check") >/dev/null || { echo "FAIL repo pin does not match Package.resolved"; exit 1; }
jq '(.pins[] | select(.identity == "sparkle") | .state.version) = "0.0.1"' "$here/Package.resolved" > "$work/Package.resolved"
if out=$(PACKAGE_RESOLVED="$work/Package.resolved" "$check" 2>&1); then
  echo "FAIL a drifted Package.resolved passed"; exit 1
fi
case "$out" in *::error::*0.0.1*) echo "ok   pin check passes on the repo and fails on drift" ;; *) echo "FAIL no clear error: $out"; exit 1 ;; esac
