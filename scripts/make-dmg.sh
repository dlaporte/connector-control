#!/bin/bash
# Packages build/Connector Control.app into a drag-to-Applications DMG.
# Usage: scripts/make-dmg.sh <output.dmg>
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="${1:?usage: make-dmg.sh <output.dmg>}"
APP="build/Connector Control.app"
[ -d "$APP" ] || { echo "missing $APP — run scripts/build-app.sh first"; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

rm -f "$OUT"
hdiutil create -volname "Connector Control" -srcfolder "$STAGE" \
    -ov -format UDZO "$OUT" >/dev/null
echo "Created: $OUT"
