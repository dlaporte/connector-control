#!/usr/bin/env bash
# Check that an appcast is one Sparkle will take: it announces <version>, carries exactly one
# numeric build (sparkle:version; equal to <build> when given), encloses <dmg-url>, signs its item
# (sparkle:edSignature) and signs itself (the sparkle-signatures block). Prints the build on success,
# and nothing else on standard output. The one copy of these rules: release.yml checks the stable
# feed with it, update-preview-feed.sh the preview feed.
#
#   check-appcast.sh [--name <text>] <appcast> <version> <dmg-url> [<build>]
#
#   --name <text>  what the error messages call the appcast (default: "the served appcast").
#
# Only presence is checked, not the EdDSA signatures themselves: Sparkle's sign_update, the one tool
# that verifies them, ships only for macOS, and both callers run on ubuntu. Every installed app
# verifies the feed against its own SUPublicEDKey (SURequireSignedFeed) and refuses one that does
# not match.
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
. "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

usage() {
  echo "usage: check-appcast.sh [--name <text>] <appcast> <version> <dmg-url> [<build>]" >&2
}

NAME="the served appcast"
if [ "${1:-}" = --name ]; then
  NAME=${2:?--name needs a value}
  shift 2
fi
[ $# -ge 3 ] && [ $# -le 4 ] || { usage; exit 1; }
FILE=$1 VERSION=$2 DMG_URL=$3 WANT_BUILD=${4:-}

fail() { echo "::error::$NAME $1" >&2; exit 1; }

[ -f "$FILE" ] || fail "was not fetched ($FILE is missing)"
grep -q "<sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>" "$FILE" || fail "does not announce $VERSION"
BUILD=$(build_of "$FILE") || true
is_build "$BUILD" || fail "does not carry exactly one numeric sparkle:version ('$BUILD')"
[ -z "$WANT_BUILD" ] || [ "$BUILD" = "$WANT_BUILD" ] || fail "announces build $BUILD, not $WANT_BUILD"
grep -qF "url=\"$DMG_URL\"" "$FILE" || fail "does not enclose $DMG_URL"
# An unsigned item is refused by Sparkle at update time and looks perfectly fine on the release
# page - the failure mode is "the release is live, nobody's Mac updates".
grep -q 'sparkle:edSignature="[^"][^"]*"' "$FILE" || fail "item carries no EdDSA signature"
# The feed's own signature: SURequireSignedFeed makes Sparkle reject an appcast without its
# sparkle-signatures block, however well its item is signed.
grep -q 'sparkle-signatures:' "$FILE" || fail "carries no sparkle-signatures block"
printf '%s\n' "$BUILD"
