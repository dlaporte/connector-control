#!/usr/bin/env bash
# make-dmg.sh retries `hdiutil create` while it reports "Resource busy", four tries in all, and
# stops at once on any other failure. It runs make-dmg.sh from a scratch copy of the repository's
# layout with a fake hdiutil first on PATH, so no disk image is made and the checkout is untouched.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/repo/scripts" "$work/repo/build/Connector Control.app" "$work/bin"
cp "$here/make-dmg.sh" "$work/repo/scripts/"
cat > "$work/bin/hdiutil" <<'FAKE'
#!/usr/bin/env bash
# Fails "Resource busy" for the first BUSY_TIMES calls, then fails with THEN, or succeeds when THEN is empty.
n=$(( $(cat "$CALLS" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$CALLS"
if [ "$n" -le "$BUSY_TIMES" ]; then echo "hdiutil: create failed - Resource busy" >&2; exit 1; fi
if [ -n "${THEN:-}" ]; then echo "$THEN" >&2; exit 1; fi
: > "${*: -1}"
FAKE
chmod +x "$work/bin/hdiutil"

# run <busy times> <then>: prints "<exit code> <calls>".
run() {
  rm -f "$work/calls"
  set +e
  CALLS="$work/calls" BUSY_TIMES="$1" THEN="$2" MAKE_DMG_RETRY_SECONDS=0 PATH="$work/bin:$PATH" \
    "$work/repo/scripts/make-dmg.sh" "$work/out.dmg" >/dev/null 2>&1
  local code=$?
  set -e
  echo "$code $(cat "$work/calls")"
}
check() {
  [ "$2" = "$3" ] || { echo "FAIL: $1: got '$2', want '$3'"; exit 1; }
  echo "ok: $1"
}
check "busy twice, then made" "$(run 2 '')" "0 3"
check "busy every time: four tries, then it fails" "$(run 9 '')" "1 4"
check "any other failure is final" "$(run 0 'hdiutil: create failed - No space left on device')" "1 1"
