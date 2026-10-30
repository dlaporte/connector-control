#!/usr/bin/env bash
# notarize.sh stops an upload that gets no submission ID in time and tries again, three tries in
# all; it fails on a verdict other than Accepted (with Apple's log, unless Apple is still
# processing), and staples only an accepted submission. It runs notarize.sh with a fake xcrun and
# notarytool first on PATH, so nothing reaches Apple and nothing is signed or stapled.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"
command -v jq >/dev/null || { echo "jq is needed"; exit 1; }

cat > "$work/bin/xcrun" <<'FAKE'
#!/usr/bin/env bash
case "$1" in
  --find) echo "$FAKE_BIN/notarytool" ;;
  stapler) echo staple >> "$CALLS" ;;
  *) echo "fake xcrun: unexpected $*" >&2; exit 1 ;;
esac
FAKE

cat > "$work/bin/notarytool" <<'FAKE'
#!/usr/bin/env bash
# submit: the first HANG_TIMES uploads never return, the next FAIL_TIMES exit 1, then one is given an
# ID. info reports VERDICT. NOID_TIMES uploads exit 0 without an ID, AUTH_FAIL=1 makes every upload
# an authentication error, INFO_FAIL=1 makes info fail.
case "$1" in
  submit)
    echo submit >> "$CALLS"
    n=$(grep -c submit "$CALLS")
    if [ "${AUTH_FAIL:-0}" = 1 ]; then echo '{"message":"Error: HTTP status code: 401. Invalid credentials."}'; exit 1; fi
    if [ "$n" -le "${NOID_TIMES:-0}" ]; then echo '{"message":"uploaded"}'; exit 0; fi
    if [ "$n" -le "$HANG_TIMES" ]; then exec sleep 60; fi
    if [ "$n" -le $(( HANG_TIMES + FAIL_TIMES )) ]; then echo '{"message":"HTTP 500"}'; exit 1; fi
    echo '{"id":"0f1e2d3c-0000-0000-0000-000000000000","message":"Successfully uploaded file"}' ;;
  wait) echo '{"status":"'"$VERDICT"'"}' ;;
  info) [ "${INFO_FAIL:-0}" = 1 ] && exit 1; echo '{"status":"'"$VERDICT"'"}' ;;
  log) echo log >> "$CALLS"; echo '{"issues":[]}' ;;
  *) echo "fake notarytool: unexpected $*" >&2; exit 1 ;;
esac
FAKE
chmod +x "$work/bin/xcrun" "$work/bin/notarytool"
: > "$work/ConnectorControl.dmg"

# run <hang times> <fail times> <verdict>: prints "<exit code> <submits> <logs> <staples>".
run() {
  : > "$work/calls"
  set +e
  CALLS="$work/calls" FAKE_BIN="$work/bin" HANG_TIMES="$1" FAIL_TIMES="$2" VERDICT="$3" \
    NOTARIZE_UPLOAD_SECONDS=1 NOTARIZE_RETRY_SECONDS=0 APPLE_ID=a APPLE_PASSWORD=p APPLE_TEAM_ID=t \
    PATH="$work/bin:$PATH" "$here/mac/notarize.sh" "$work/ConnectorControl.dmg" > "$work/log" 2>&1
  local code=$?
  set -e
  echo "$code $(grep -c submit "$work/calls") $(grep -c log "$work/calls") $(grep -c staple "$work/calls")"
}
check() {
  [ "$2" = "$3" ] || { echo "FAIL: $1: got '$2', want '$3'"; cat "$work/log"; exit 1; }
  echo "ok: $1"
}
check "accepted at once: one upload, stapled" "$(run 0 0 Accepted)" "0 1 0 1"
check "two stalled uploads are stopped and retried, then stapled" "$(run 2 0 Accepted)" "0 3 0 1"
check "a failed upload is retried" "$(run 0 1 Accepted)" "0 2 0 1"
check "every upload stalls: three tries, then it fails" "$(run 3 0 Accepted)" "1 3 0 0"
check "rejected: no retry, Apple's log printed, nothing stapled" "$(run 0 0 Invalid)" "1 1 1 0"
check "still processing after the wait: fails without the log, nothing stapled" "$(run 0 0 'In Progress')" "1 1 0 0"
check "submit exits 0 with no ID: retried, then stapled" "$(NOID_TIMES=1 run 0 0 Accepted)" "0 2 0 1"
check "info always fails: the wait verdict (Accepted) is used" "$(INFO_FAIL=1 run 0 0 Accepted)" "0 1 0 1"
check "credentials refused: one submit, no retry" "$(AUTH_FAIL=1 run 0 0 Accepted)" "1 1 0 0"
