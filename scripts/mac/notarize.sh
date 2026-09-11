#!/usr/bin/env bash
# Submit one Mac binary or app bundle for Apple notarization and staple the ticket, waiting for
# the result. Used for both the app bundle (zipped for submission; the .app itself is stapled)
# and the signed DMG (submitted and stapled directly).
#
#   notarize.sh <path>
#
# Needs APPLE_ID, APPLE_PASSWORD, APPLE_TEAM_ID in the environment and xcrun (Xcode command line
# tools) on PATH.
set -euo pipefail

TARGET=${1:?usage: notarize.sh <path>}
: "${APPLE_ID:?APPLE_ID must be set}"
: "${APPLE_PASSWORD:?APPLE_PASSWORD must be set}"
: "${APPLE_TEAM_ID:?APPLE_TEAM_ID must be set}"
[ -e "$TARGET" ] || { echo "::error::$TARGET not found"; exit 1; }

if [ -d "$TARGET" ]; then
  # notarytool only accepts a zip/dmg/pkg; an app bundle is zipped for submission and the
  # original directory is what gets stapled (ditto -c -k --keepParent preserves the bundle
  # structure notarization needs to see).
  SUBMIT_FILE=$(mktemp "${TMPDIR:-/tmp}/notarize-XXXXXX.zip")
  trap 'rm -f "$SUBMIT_FILE"' EXIT
  ditto -c -k --keepParent "$TARGET" "$SUBMIT_FILE"
else
  SUBMIT_FILE="$TARGET"
fi

xcrun notarytool submit "$SUBMIT_FILE" \
  --apple-id "$APPLE_ID" --password "$APPLE_PASSWORD" \
  --team-id "$APPLE_TEAM_ID" --wait

xcrun stapler staple "$TARGET"
