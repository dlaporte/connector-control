#!/usr/bin/env bash
# Print the release notes for a preview build of both apps: who it is for, how to install each
# app and how to go back to the stable release, the CHANGELOG section for the version the
# preview is cut from, and the commits since the last real preview or release, whichever is
# more recent.
#
#   preview-notes.sh <tag> <version> <next> <number>
#
#   <tag>     the preview's release tag (preview-<n> or preview-dry-<n>)
#   <version> the full preview version, <next>-preview.<number>
#   <next>    the version CHANGELOG.md's top "## vX.Y.Z" heading names
#   <number>  the preview number <n>
#
# Needs GITHUB_SHA and GITHUB_REPOSITORY in the environment (Actions sets both), a checkout with
# `git fetch --no-tags origin master:refs/remotes/origin/master` already run, and CHANGELOG.md in
# the current directory.
set -euo pipefail

TAG=${1:?usage: preview-notes.sh <tag> <version> <next> <number>}
VERSION=${2:?usage: preview-notes.sh <tag> <version> <next> <number>}
NEXT=${3:?usage: preview-notes.sh <tag> <version> <next> <number>}
NUMBER=${4:?usage: preview-notes.sh <tag> <version> <next> <number>}
: "${GITHUB_SHA:?GITHUB_SHA must be set}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"
[ -f CHANGELOG.md ] || { echo "::error::CHANGELOG.md not found in $(pwd)"; exit 1; }

REPO_URL="${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY"

# Commits since the more recent of two points: the highest REAL preview below this one (a
# preview-dry-<n> tag is never a comparison point) and the newest v* release reachable from
# here — a preview cut right after a release would otherwise re-list that whole release. With
# neither (a first preview on a fresh branch), since the branch left master.
PREV=$(git tag -l 'preview-[0-9]*' \
  | sed -n 's/^preview-\([0-9][0-9]*\)$/\1/p' \
  | sort -n | awk -v n="$NUMBER" '$1 + 0 < n + 0' | tail -1)
LAST=$(git describe --tags --abbrev=0 --match 'v[0-9]*' HEAD 2>/dev/null || true)
BASE=""
for candidate in ${PREV:+preview-$PREV} $LAST; do
  if [ -z "$BASE" ] || git merge-base --is-ancestor "$BASE" "$candidate"; then
    BASE="$candidate"
  fi
done
if [ -n "$BASE" ]; then
  BASE_LABEL="$BASE"
else
  BASE=$(git merge-base origin/master HEAD)
  BASE_LABEL="master (${BASE:0:7})"
fi

echo "**Preview** build \`$VERSION\` of Connector Control for macOS and Windows, from commit ${GITHUB_SHA:0:7} (tag \`$TAG\`)."
echo
echo "> A preview for invited testers. It is a GitHub prerelease: the stable apps never see it and their update feeds are untouched. Both apps are code-signed (Developer ID and notarized on the Mac, Azure Artifact Signing on Windows). A Windows preview install updates itself to later previews and to the final release; a Mac preview does not, so download each new preview."
echo
echo "### Install"
echo
echo "- **Mac:** download \`ConnectorControl_$VERSION.dmg\`, open it and drag Connector Control to Applications over the stable copy. Your connectors, settings and backups are kept."
echo "- **Windows:** download \`ConnectorControl-win-x64-Setup.exe\` (Intel/AMD) or \`ConnectorControl-win-arm64-Setup.exe\` (Arm) and run it; see [Installation ▸ Windows]($REPO_URL/blob/master/README.md#windows)."
echo
echo "### Back to the stable release"
echo
echo "- **Mac:** download the DMG from the [latest release]($REPO_URL/releases/latest) and drag it over the preview."
echo "- **Windows:** uninstall from Settings ▸ Apps ▸ Installed apps, then run the stable Setup.exe from the latest release."
echo "- App data is left in place either way. Anything a preview feature wrote that the stable app does not understand is ignored, not deleted."
echo
echo "### Changes planned for v$NEXT (CHANGELOG.md)"
echo
# preview.yml's derive step has already checked that "## v$NEXT" exists, so any failure here
# (a missing section, a missing script, an awk error) fails the preview under pipefail rather
# than publishing notes without their changes.
#
# A sub-heading with no bullets yet stays in CHANGELOG.md until the version ships, but a
# tester reading these notes should not meet an empty heading, so drop those here.
scripts/release/changelog-section.sh "v$NEXT" \
  | awk '
      /^### / { if (heading != "" && body != "") printf "%s", held; heading = $0; held = $0 ORS; body = ""; next }
      heading != "" { held = held $0 ORS; if ($0 ~ /[^[:space:]]/) body = body $0; next }
      { print }
      END { if (heading != "" && body != "") printf "%s", held }
  '
echo
echo "### Commits since $BASE_LABEL"
echo
git log --format='- %s (%h)' "$BASE..HEAD"
