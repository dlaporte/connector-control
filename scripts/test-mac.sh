#!/usr/bin/env bash
# Runs the Swift suite the way CI gates it: every test must run, none may skip
# and none may fail — the Swift counterpart of windows/ci.runsettings'
# FailSkips. The one legitimate local exception is the exFAT test, which skips
# if hdiutil cannot attach a disk image on this machine (it attaches
# successfully on CI). Called by mac-ci.yml and release.yml; runs locally too
# (set DEVELOPER_DIR when the default toolchain cannot run tests).
#
# How the two test targets report depends on the toolchain: one .xctest bundle
# per target with its own summary (Xcode-beta locally) or one combined
# ConnectorControlPackageTests bundle (the CI runner's Xcode). The checks
# therefore count per-test lines across the whole log and look only for the
# toolchain-independent suite lines.
set -euo pipefail
cd "$(dirname "$0")/.."

mkdir -p .build
LOG=.build/swift-test.log
swift test 2>&1 | tee "$LOG"

SKIPPED=$(grep -cE "^Test Case '[^']*' skipped" "$LOG" || true)
FAILED=$(grep -cE "^Test Case '[^']*' failed" "$LOG" || true)
FAILED_SUITES=$(grep -cE "^Test Suite '[^']*' failed" "$LOG" || true)
PASSED_ALL=$(grep -cE "^Test Suite 'All tests' passed" "$LOG" || true)
if [ "$FAILED" != "0" ] || [ "$FAILED_SUITES" != "0" ]; then
    echo "error: $FAILED test(s) and $FAILED_SUITES suite(s) failed" >&2
    exit 1
fi
[ "$SKIPPED" = "0" ] \
    || { echo "error: expected no skipped tests, found $SKIPPED (the exFAT test skips locally if hdiutil cannot attach an image; it must not skip on CI)" >&2; exit 1; }
[ "$PASSED_ALL" -ge 1 ] \
    || { echo "error: no 'All tests' suite passed — did the run finish?" >&2; exit 1; }
