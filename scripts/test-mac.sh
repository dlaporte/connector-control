#!/bin/bash
# Runs the Swift suite the way CI gates it: every test must run, exactly one may
# skip (GoldenFileTests.testRegenerateGoldens rewrites the goldens and runs only
# with CONNECTOR_CONTROL_UPDATE_GOLDENS=1), and none may fail — the Swift
# counterpart of windows/ci.runsettings' FailSkips. Called by mac-ci.yml and
# release.yml; runs locally too (set DEVELOPER_DIR when the default toolchain
# cannot run tests).
#
# Two test bundles report separately (ConnectorControlStateTests, then
# ConnectorControlCoreTests), each with its own summary line, so the checks
# count per-test lines across the whole log rather than matching one summary.
set -euo pipefail
cd "$(dirname "$0")/.."

mkdir -p .build
LOG=.build/swift-test.log
swift test 2>&1 | tee "$LOG"

SKIPPED=$(grep -cE "^Test Case '[^']*' skipped" "$LOG" || true)
FAILED=$(grep -cE "^Test Case '[^']*' failed" "$LOG" || true)
BUNDLES=$(grep -cE "^Test Suite '[^']*\.xctest' passed" "$LOG" || true)
[ "$FAILED" = "0" ] \
    || { echo "error: $FAILED test(s) failed" >&2; exit 1; }
[ "$SKIPPED" = "1" ] \
    || { echo "error: expected exactly one skipped test (testRegenerateGoldens), found $SKIPPED" >&2; exit 1; }
[ "$BUNDLES" = "2" ] \
    || { echo "error: expected both test bundles (State, Core) to pass; $BUNDLES did" >&2; exit 1; }
