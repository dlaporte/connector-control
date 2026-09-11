#!/usr/bin/env bash
# Print the release notes for a Windows preview build: the install steps (linked to the
# README's full instructions), the CHANGELOG section for the version this preview is cut from,
# and the commits since the last real preview or release, whichever is more recent.
#
#   preview-notes.sh <tag> <version> <next> <number>
#
#   <tag>     the preview's release tag (windows-preview-<n> or windows-preview-dry-<n>)
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
# windows-preview-dry-<n> tag is never a comparison point) and the newest v* release reachable
# from here — previews are cut from master after a release, so "since the last preview" alone
# would re-list a whole release. With neither (a first preview on a fresh branch), since the
# branch left master.
PREV=$(git tag -l 'windows-preview-[0-9]*' \
  | sed -n 's/^windows-preview-\([0-9][0-9]*\)$/\1/p' \
  | sort -n | awk -v n="$NUMBER" '$1 + 0 < n + 0' | tail -1)
LAST=$(git describe --tags --abbrev=0 --match 'v[0-9]*' HEAD 2>/dev/null || true)
BASE=""
for candidate in ${PREV:+windows-preview-$PREV} $LAST; do
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

echo "Windows **preview** build \`$VERSION\` of Connector Control, from commit ${GITHUB_SHA:0:7} (tag \`$TAG\`)."
echo
echo "> Preview builds are GitHub prereleases for Windows testers. They never touch the Mac app's update feed, and a preview install only updates to later previews (and to the final release). This build is code-signed (Azure Artifact Signing)."
echo
echo "### Install"
echo
echo "1. Download \`ConnectorControl-win-x64-Setup.exe\` (Intel/AMD PCs) or \`ConnectorControl-win-arm64-Setup.exe\` (ARM PCs) from the assets below."
echo "2. Run it — the same install steps as a full release; see [Installation ▸ Windows]($REPO_URL/blob/master/README.md#windows) in the README."
echo "3. Uninstall from Settings ▸ Apps ▸ Installed apps; app data in \`%LOCALAPPDATA%\\Connector Control\` is left in place."
echo
echo "### Changes planned for v$NEXT (CHANGELOG.md)"
echo
# A preview can be cut before CHANGELOG.md grows its "## v$NEXT" section (that section is
# written when the release itself is prepared), so tolerate changelog-section.sh's exit 1 for a
# section that doesn't exist yet rather than failing the preview over it; --quiet keeps that
# from rendering as a GitHub error annotation.
scripts/release/changelog-section.sh --quiet "v$NEXT" || true
echo
echo "### Commits since $BASE_LABEL"
echo
git log --format='- %s (%h)' "$BASE..HEAD"
