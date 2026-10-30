#!/usr/bin/env bash
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
# On a hosted runner hdiutil now and then fails "Resource busy" while the system still holds a
# volume it has just used, and the same command succeeds moments later. Only that failure is
# retried, four tries in all, each wait longer than the last; any other failure is final.
# MAKE_DMG_RETRY_SECONDS shortens the waits for scripts/tests/make-dmg-retry.sh.
delay="${MAKE_DMG_RETRY_SECONDS:-5}"
attempt=1
until output=$(hdiutil create -volname "Connector Control" -srcfolder "$STAGE" \
        -ov -format UDZO "$OUT" 2>&1); do
    if [ "$attempt" -ge 4 ] || ! grep -q "Resource busy" <<< "$output"; then
        printf '%s\n' "$output" >&2
        exit 1
    fi
    echo "hdiutil create: Resource busy (try $attempt of 4); trying again in $((attempt * delay)) s" >&2
    sleep "$((attempt * delay))"
    rm -f "$OUT"
    attempt=$((attempt + 1))
done
echo "Created: $OUT"
