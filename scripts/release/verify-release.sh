#!/usr/bin/env bash
# Verify a just-created (or reused) GitHub release: not a draft, carries the expected
# prerelease flag, and every named asset is present. Writes a step summary.
#
#   verify-release.sh <tag> --prerelease|--full <asset>...
#
# Needs GH_TOKEN and GITHUB_REPOSITORY in the environment and gh + jq on PATH. Writes to
# GITHUB_STEP_SUMMARY when it is set (a no-op outside Actions).
set -euo pipefail

usage() {
  echo "usage: verify-release.sh <tag> --prerelease|--full <asset>..." >&2
}

[ $# -ge 2 ] || { usage; exit 1; }
TAG=$1
shift
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

case "$1" in
  --prerelease) WANT=true ;;
  --full) WANT=false ;;
  *) usage; exit 1 ;;
esac
shift
ASSETS=("$@")
[ "${#ASSETS[@]}" -ge 1 ] || { echo "::error::at least one asset is required"; usage; exit 1; }

gh release view "$TAG" --json isPrerelease,isDraft,assets \
  --jq '{prerelease: .isPrerelease, draft: .isDraft, assets: [.assets[].name]}' | tee release.json

[ "$(jq -r .prerelease release.json)" = "$WANT" ] || { echo "::error::$TAG isPrerelease is $(jq -r .prerelease release.json), expected $WANT"; exit 1; }
[ "$(jq -r .draft release.json)" = "false" ] || { echo "::error::$TAG is a draft"; exit 1; }
for a in "${ASSETS[@]}"; do
  name=$(basename "$a")
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
