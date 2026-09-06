#!/bin/bash
# Runs the Swift suite the way CI gates it: every test must run, exactly one may
# skip (GoldenFileTests.testRegenerateGoldens rewrites the goldens and runs only
# with CONNECTOR_CONTROL_UPDATE_GOLDENS=1), and none may fail — the Swift
# counterpart of windows/ci.runsettings' FailSkips. Called by mac-ci.yml and
# release.yml; runs locally too (set DEVELOPER_DIR when the default toolchain
# cannot run tests).
set -euo pipefail
cd "$(dirname "$0")/.."

mkdir -p .build
LOG=.build/swift-test.log
swift test 2>&1 | tee "$LOG"
grep -Eq 'Executed [0-9]+ tests, with 1 test skipped and 0 failures' "$LOG" \
    || { echo "error: expected exactly one skipped test (testRegenerateGoldens) and no failures" >&2; exit 1; }
