#!/usr/bin/env bash
# Fail unless the Sparkle tools pin in sparkle-tools.env matches the Sparkle version in
# Package.resolved: generate_appcast ships alongside the Sparkle.framework the app links, and a
# drifted pin could sign appcast entries with a tool version the shipped framework disagrees with.
# Needs only jq. Run from the repository root (or with PACKAGE_RESOLVED set to another file).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/mac/sparkle-tools.env
. "$HERE/sparkle-tools.env"

# jq, not grep: the "version" field lives under "state", several lines below "identity", so a
# line-proximity grep never matches it.
RESOLVED_VERSION=$(jq -r '.pins[] | select(.identity == "sparkle") | .state.version' "${PACKAGE_RESOLVED:-Package.resolved}")
if [ -z "$RESOLVED_VERSION" ]; then
  echo "::error::could not find the Sparkle package version in Package.resolved"
  exit 1
fi
if [ "$RESOLVED_VERSION" != "$SPARKLE_TOOLS_VERSION" ]; then
  echo "::error::Sparkle tools are pinned to $SPARKLE_TOOLS_VERSION (scripts/mac/sparkle-tools.env) but Package.resolved has $RESOLVED_VERSION - bump the pin (and its SHA-256) in the same commit."
  exit 1
fi
echo "Sparkle tools pin $SPARKLE_TOOLS_VERSION matches Package.resolved"
