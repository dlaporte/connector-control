#!/usr/bin/env bash
# Rewrites Tests/Fixtures/golden/ from the real Core module's output for each
# fixture in Tests/Fixtures/golden/inputs/. Run this after a change to
# JSONValue's encoding, editorText(), or the golden inputs themselves, then
# review the diff — a change outside the format you meant to touch is a bug,
# not a golden to bless.
#
# Set DEVELOPER_DIR first if the default toolchain cannot run tests, e.g.:
#   DEVELOPER_DIR=/Applications/Xcode-beta.app/Contents/Developer scripts/regenerate-goldens.sh
set -euo pipefail
cd "$(dirname "$0")/.."

CONNECTOR_CONTROL_UPDATE_GOLDENS=1 swift test --filter GoldenFileTests
