#!/usr/bin/env bash
# Import the Developer ID Application certificate into a throwaway CI keychain and make it the
# default, so `codesign` finds it without any further setup.
#
# Needs APPLE_CERTIFICATE (base64-encoded .p12), APPLE_CERTIFICATE_PASSWORD, KEYCHAIN_PASSWORD
# and RUNNER_TEMP in the environment; security on PATH (macOS only).
set -euo pipefail

: "${APPLE_CERTIFICATE:?APPLE_CERTIFICATE must be set}"
: "${APPLE_CERTIFICATE_PASSWORD:?APPLE_CERTIFICATE_PASSWORD must be set}"
: "${KEYCHAIN_PASSWORD:?KEYCHAIN_PASSWORD must be set}"
: "${RUNNER_TEMP:?RUNNER_TEMP must be set}"

# The decoded .p12 lives under RUNNER_TEMP, never in the checkout, and is removed by a trap so an
# import failure cannot leave it on disk; the throwaway keychain itself is deleted by the
# workflow's own always() cleanup step.
KEYCHAIN_PATH="$RUNNER_TEMP/build.keychain-db"
P12="$RUNNER_TEMP/certificate.p12"
trap 'rm -f "$P12"' EXIT
echo "$APPLE_CERTIFICATE" | base64 --decode > "$P12"
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security set-keychain-settings -lut 21600 "$KEYCHAIN_PATH"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security import "$P12" -k "$KEYCHAIN_PATH" -P "$APPLE_CERTIFICATE_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security -A
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
security list-keychains -d user -s "$KEYCHAIN_PATH"
security default-keychain -s "$KEYCHAIN_PATH"
security find-identity -v -p codesigning "$KEYCHAIN_PATH"
