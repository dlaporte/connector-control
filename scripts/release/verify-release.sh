#!/usr/bin/env bash
# Verify a just-created (or reused) GitHub release: not a draft, carries the expected
# prerelease flag, and carries every asset a release of <version> publishes - the Mac DMG and
# appcast and each runtime's Velopack set, as scripts/lib/common.sh names them (the list
# upload-release-assets.sh uploads from). Writes a step summary.
#
#   verify-release.sh <tag> --prerelease|--full --version <version>
#
# Needs GH_TOKEN and GITHUB_REPOSITORY in the environment and gh + jq on PATH. Writes to
# GITHUB_STEP_SUMMARY when it is set (a no-op outside Actions).
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
. "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

usage() {
  echo "usage: verify-release.sh <tag> --prerelease|--full --version <version>" >&2
}

[ $# -eq 4 ] || { usage; exit 1; }
TAG=$1
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

case "$2" in
  --prerelease) WANT=true ;;
  --full) WANT=false ;;
  *) usage; exit 1 ;;
esac
if [ "$3" != --version ] || [ -z "$4" ]; then usage; exit 1; fi
VERSION=$4

ASSETS=()
while IFS= read -r name; do ASSETS+=("$name"); done < <(
  mac_release_assets "$VERSION"
  for rid in $(release_rids); do windows_release_assets "$VERSION" "$rid"; done
)

gh release view "$TAG" --json isPrerelease,isDraft,assets \
  --jq '{prerelease: .isPrerelease, draft: .isDraft, assets: [.assets[].name]}' | tee release.json

[ "$(jq -r .prerelease release.json)" = "$WANT" ] || { echo "::error::$TAG isPrerelease is $(jq -r .prerelease release.json), expected $WANT"; exit 1; }
[ "$(jq -r .draft release.json)" = "false" ] || { echo "::error::$TAG is a draft"; exit 1; }
for name in "${ASSETS[@]}"; do
  jq -e --arg a "$name" '.assets | index($a) != null' release.json >/dev/null \
    || { echo "::error::asset $name missing from $TAG"; exit 1; }
done

REPO_URL="${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY"
{
  echo "## Release $TAG"
  echo
  echo "- Release: $REPO_URL/releases/tag/$TAG"
  echo "- Prerelease: $WANT"
  echo "- Assets:"
  jq -r '.assets[] | "  - " + .' release.json
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
