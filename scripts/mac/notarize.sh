#!/usr/bin/env bash
# Submit one Mac binary or app bundle for Apple notarization and staple the ticket, waiting for
# the result. Used for both the app bundle (zipped for submission; the .app itself is stapled)
# and the signed DMG (submitted and stapled directly).
#
#   notarize.sh <path>
#
# Needs APPLE_ID, APPLE_PASSWORD, APPLE_TEAM_ID in the environment, xcrun (Xcode command line
# tools) and jq on PATH. scripts/tests/notarize-retry.sh runs it against a fake xcrun.
#
# Three bounded phases, so a stalled network call costs minutes, not the job:
#   upload  `notarytool submit` without --wait, which returns once Apple has the file and has
#           given it a submission ID. On 2026-10-30 one upload never got an ID and hung for 57
#           minutes, until the job's timeout. Each try is stopped after UPLOAD_SECONDS and the
#           upload retried, three tries in all.
#   wait    `notarytool wait` on that ID, with notarytool's own --timeout (WAIT_SECONDS) and the
#           same stop two minutes later in case it hangs too. Not retried: Apple keeps processing
#           the submission whatever happens here, and a rejection is final.
#   staple  retried five times, 30 seconds apart (below).
# mac-build.yml's job has 60 minutes and calls this twice (the app, then the DMG). A stalled upload
# now costs UPLOAD_SECONDS plus RETRY_SECONDS (5.5 minutes) instead of the job. Every upload of one
# call stalling fails it after 3 x 5 + 2 x 0.5 = 16 minutes, leaving the job time to say so. The
# 20-minute wait is for Apple's queue, which normally answers in a few minutes; a run where both
# calls use all of it would reach the job's timeout, as any run would before this.
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
. "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh" # stop_process

TARGET=${1:?usage: notarize.sh <path>}
: "${APPLE_ID:?APPLE_ID must be set}"
: "${APPLE_PASSWORD:?APPLE_PASSWORD must be set}"
: "${APPLE_TEAM_ID:?APPLE_TEAM_ID must be set}"
[ -e "$TARGET" ] || { echo "::error::$TARGET not found"; exit 1; }

# The test shortens these; nothing else sets them.
UPLOAD_SECONDS=${NOTARIZE_UPLOAD_SECONDS:-300}
WAIT_SECONDS=${NOTARIZE_WAIT_SECONDS:-1200}
RETRY_SECONDS=${NOTARIZE_RETRY_SECONDS:-30}

WORK=$(mktemp -d "${TMPDIR:-/tmp}/notarize-XXXXXX")
trap 'rm -rf "$WORK"' EXIT

if [ -d "$TARGET" ]; then
  # notarytool only accepts a zip/dmg/pkg; an app bundle is zipped for submission and the
  # original directory is what gets stapled (ditto -c -k --keepParent preserves the bundle
  # structure notarization needs to see).
  SUBMIT_FILE="$WORK/$(basename "$TARGET").zip"
  ditto -c -k --keepParent "$TARGET" "$SUBMIT_FILE"
else
  SUBMIT_FILE="$TARGET"
fi

# Run directly rather than through xcrun, so stopping it stops notarytool itself.
NOTARYTOOL=$(xcrun --find notarytool)
AUTH=(--apple-id "$APPLE_ID" --password "$APPLE_PASSWORD" --team-id "$APPLE_TEAM_ID")

# bounded <seconds> <stdout file> <command>...: runs the command with its standard output in the
# file, and its status as this function's. One still running after <seconds> is stopped
# (stop_process) and the status is 124, as timeout(1) reports it.
bounded() {
  local limit=$1 out=$2 pid waited=0
  shift 2
  "$@" > "$out" &
  pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$waited" -ge "$limit" ]; then
      stop_process "$pid"
      wait "$pid" 2>/dev/null || true
      return 124
    fi
    sleep 1
    waited=$((waited + 1))
  done
  wait "$pid"
}

SUBMISSION=""
for attempt in 1 2 3; do
  status=0
  bounded "$UPLOAD_SECONDS" "$WORK/submit.json" \
    "$NOTARYTOOL" submit "$SUBMIT_FILE" "${AUTH[@]}" --output-format json || status=$?
  if [ "$status" = 0 ]; then
    SUBMISSION=$(jq -r '.id // empty' "$WORK/submit.json" 2>/dev/null || true)
    [ -z "$SUBMISSION" ] || break
    echo "notarytool submit gave no submission ID: $(cat "$WORK/submit.json")"
  elif [ "$status" = 124 ]; then
    echo "notarytool submit had no submission ID after $UPLOAD_SECONDS seconds; stopped it."
  else
    echo "notarytool submit failed (exit $status): $(cat "$WORK/submit.json")"
  fi
  [ "$attempt" -lt 3 ] || break
  echo "Uploading again (attempt $((attempt + 1)) of 3) in $RETRY_SECONDS seconds."
  sleep "$RETRY_SECONDS"
done
[ -n "$SUBMISSION" ] || { echo "::error::Could not upload $TARGET for notarization after 3 attempts"; exit 1; }
echo "Submitted $TARGET for notarization as $SUBMISSION."

bounded "$((WAIT_SECONDS + 120))" "$WORK/wait.json" \
  "$NOTARYTOOL" wait "$SUBMISSION" "${AUTH[@]}" --timeout "$WAIT_SECONDS" --output-format json || true
# The verdict comes from `info`, read until it answers (three tries), so a dropped connection here
# is not taken for a rejection.
RESULT=""
for attempt in 1 2 3; do
  bounded 120 "$WORK/info.json" "$NOTARYTOOL" info "$SUBMISSION" "${AUTH[@]}" --output-format json || true
  RESULT=$(jq -r '.status // empty' "$WORK/info.json" 2>/dev/null || true)
  [ -z "$RESULT" ] || break
  if [ "$attempt" -lt 3 ]; then sleep "$RETRY_SECONDS"; fi
done
if [ "$RESULT" != Accepted ]; then
  echo "::error::Notarization of $TARGET is '${RESULT:-unknown}', not Accepted (submission $SUBMISSION)"
  bounded 120 "$WORK/log.json" "$NOTARYTOOL" log "$SUBMISSION" "${AUTH[@]}" || true
  cat "$WORK/log.json"
  exit 1
fi
echo "Notarization of $TARGET: Accepted."

# Stapling fetches the ticket from Apple's CDN, which can lag the Accepted status or drop the
# connection (error 68: "CloudKit's response is inconsistent", "network connection was lost"),
# so it is retried before the step fails.
for attempt in 1 2 3 4 5; do
  xcrun stapler staple "$TARGET" && exit 0
  [ "$attempt" -lt 5 ] || break
  echo "Stapling failed (attempt $attempt of 5); retrying in 30 seconds."
  sleep 30
done
echo "::error::Could not staple $TARGET after 5 attempts"
exit 1
