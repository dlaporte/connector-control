#!/usr/bin/env bash
# Build the Sparkle appcast for one Mac release: download the pinned Sparkle tools, verify them
# by checksum, render the release notes to HTML, and sign the appcast entry with the EdDSA key.
#
#   make-appcast.sh <version> <tag> <notes.md>
#
# Must run from the directory holding ConnectorControl_<version>.dmg (the release job's working
# directory), AFTER the DMG is signed, notarized and stapled — the EdDSA signature covers the
# final DMG bytes. Writes appcast.xml there too.
#
# Needs SPARKLE_ED_PRIVATE_KEY, GH_TOKEN and GITHUB_REPOSITORY in the environment; gh, curl,
# shasum and tar on PATH.
set -euo pipefail

VERSION=${1:?usage: make-appcast.sh <version> <tag> <notes.md>}
TAG=${2:?usage: make-appcast.sh <version> <tag> <notes.md>}
NOTES=${3:?usage: make-appcast.sh <version> <tag> <notes.md>}
: "${SPARKLE_ED_PRIVATE_KEY:?SPARKLE_ED_PRIVATE_KEY must be set}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"
[ -f "$NOTES" ] || { echo "::error::$NOTES not found"; exit 1; }

REPO_URL="${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY"
DMG="ConnectorControl_${VERSION}.dmg"
[ -f "$DMG" ] || { echo "::error::$DMG not found in $(pwd) — run this after the DMG is built, signed, notarized and stapled."; exit 1; }

# Tools version pinned to match Package.resolved's Sparkle dependency: generate_appcast ships
# alongside the Sparkle.framework the app links, and a drifted pin could sign appcast entries
# with a tool version the shipped app's framework disagrees with.
SPARKLE_TOOLS_VERSION=2.9.6
SPARKLE_SHA256=52bf9e88cdd972fc0c81501377a880e90d47031bd8ca5462488f843e2609e192

RESOLVED_VERSION=$(grep -A3 '"identity" *: *"sparkle"' Package.resolved | grep -o '"version" *: *"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/')
if [ -z "$RESOLVED_VERSION" ]; then
  echo "::error::could not find the Sparkle package version in Package.resolved"
  exit 1
fi
if [ "$RESOLVED_VERSION" != "$SPARKLE_TOOLS_VERSION" ]; then
  echo "::error::Sparkle tools are pinned to $SPARKLE_TOOLS_VERSION but Package.resolved has $RESOLVED_VERSION — bump the pin (and its SHA-256) in the same commit."
  exit 1
fi

WORK="${RUNNER_TEMP:-$(mktemp -d)}"
SPARKLE_TOOLS="$WORK/sparkle-tools"
ARCHIVE="$WORK/sparkle-tools.tar.xz"
rm -rf "$SPARKLE_TOOLS"
mkdir -p "$SPARKLE_TOOLS"
# -f: an HTTP error page must fail here, not reach tar as a bogus archive with a misleading
# "Unrecognized archive format" error.
curl -sfL --retry 3 --retry-delay 5 -o "$ARCHIVE" \
  "https://github.com/sparkle-project/Sparkle/releases/download/${SPARKLE_TOOLS_VERSION}/Sparkle-${SPARKLE_TOOLS_VERSION}.tar.xz"
# The private key is piped into a binary from this archive, so the archive is pinned by content,
# not just by URL: a GitHub release asset can be deleted and re-uploaded under the same name.
echo "$SPARKLE_SHA256  $ARCHIVE" | shasum -a 256 -c - \
  || { echo "::error::Sparkle-${SPARKLE_TOOLS_VERSION}.tar.xz does not match the pinned SHA-256; refusing to sign with its tools."; exit 1; }
tar -xf "$ARCHIVE" -C "$SPARKLE_TOOLS"

APPCAST_WORK="$WORK/appcast-work"
rm -rf "$APPCAST_WORK"
mkdir -p "$APPCAST_WORK"
cp "$DMG" "$APPCAST_WORK/"
# Render the changelog to HTML (GitHub's markdown renderer) and drop it next to the DMG;
# --embed-release-notes inlines it into the appcast item so the update dialog shows it. Emit an
# HTML FRAGMENT (no DOCTYPE/body) — that's what generate_appcast embeds into the item's
# description CDATA.
NOTES_HTML="$WORK/notes-body.html"
gh api -X POST /markdown -f mode=markdown -f text="$(cat "$NOTES")" > "$NOTES_HTML"
{
  echo '<style>body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; font-size: 13px; margin: 12px; }</style>'
  cat "$NOTES_HTML"
} > "$APPCAST_WORK/${DMG%.dmg}.html"

printf '%s' "$SPARKLE_ED_PRIVATE_KEY" | "$SPARKLE_TOOLS/bin/generate_appcast" \
  --ed-key-file - \
  --embed-release-notes \
  --download-url-prefix "$REPO_URL/releases/download/${TAG}/" \
  --link "$REPO_URL/releases" \
  -o appcast.xml "$APPCAST_WORK"
cat appcast.xml
