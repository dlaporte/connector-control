#!/usr/bin/env bash
# update-preview-feed.sh moves the preview feed only forward, judged against the published releases:
# a newer source replaces the feed, an older one leaves it, a draft never counts, and a served feed
# newer than every published release fails the run instead of passing. It runs the script with a
# fake gh and curl first on PATH that serve releases and the feed from a scratch folder, so nothing
# is fetched from or written to GitHub.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"
command -v jq >/dev/null || { echo "jq is needed"; exit 1; }

cat > "$work/bin/gh" <<'FAKE'
#!/usr/bin/env bash
# Releases live in $FAKE_STATE/releases/<tag>/ (appcast.xml; a file named draft marks a draft, one named
# broken an appcast whose download always fails, one named flaky how many downloads fail first);
# the served feed is $FAKE_STATE/served.xml.
set -euo pipefail
case "$1 $2" in
  "api --paginate")
    first=1
    printf '['
    for dir in "$FAKE_STATE"/releases/*/; do
      tag=$(basename "$dir")
      draft=false; [ -e "$dir/draft" ] && draft=true
      assets='[]'; [ -e "$dir/appcast.xml" ] && assets='[{"name":"appcast.xml"}]'
      [ "$first" = 1 ] || printf ','
      first=0
      printf '{"tag_name":"%s","draft":%s,"assets":%s}' "$tag" "$draft" "$assets"
    done
    printf ']\n' ;;
  "api repos/"*) echo "v1.4.0" ;;
  "release download")
    src="$FAKE_STATE/releases/$3/appcast.xml"
    [ -e "$src" ] && [ ! -e "$FAKE_STATE/releases/$3/broken" ] || { echo "could not download appcast.xml from $3" >&2; exit 1; }
    # flaky holds how many more downloads fail before one succeeds.
    flaky="$FAKE_STATE/releases/$3/flaky"
    if [ -e "$flaky" ] && [ "$(cat "$flaky")" -gt 0 ]; then
      echo $(( $(cat "$flaky") - 1 )) > "$flaky"
      echo "transient failure downloading appcast.xml from $3" >&2
      exit 1
    fi
    cp -f "$src" "$7/appcast.xml" ;;
  "release view")
    case "$*" in
      *isDraft*) echo "true false" ;;
      *) echo "true" ;;
    esac ;;
  "release upload") cp -f "$4" "$FAKE_STATE/served.xml" ;;
  *) echo "fake gh: unexpected $*" >&2; exit 1 ;;
esac
FAKE

cat > "$work/bin/curl" <<'FAKE'
#!/usr/bin/env bash
# Serves $FAKE_STATE/served.xml as the feed: HTTP 200, or 404 (and a -f failure) when there is none.
out="" fail=0 code=0
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out=$2; shift ;;
    -w) code=1; shift ;;
    -sfL) fail=1 ;;
    --retry|--retry-delay) shift ;;
  esac
  shift
done
if [ -e "$FAKE_STATE/served.xml" ]; then
  cp -f "$FAKE_STATE/served.xml" "$out"
  [ "$code" = 1 ] && printf 200
  exit 0
fi
[ "$code" = 1 ] && printf 404
[ "$fail" = 1 ] && exit 22
exit 0
FAKE
chmod +x "$work/bin/gh" "$work/bin/curl"

# appcast <tag> <version> <build>: an appcast as make-appcast.sh writes it, as far as the script reads one.
appcast() {
  cat <<XML
<?xml version="1.0" standalone="yes"?>
<rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle" version="2.0"><channel>
<item><title>$2</title><sparkle:version>$3</sparkle:version><sparkle:shortVersionString>$2</sparkle:shortVersionString>
<enclosure url="https://github.com/o/r/releases/download/$1/ConnectorControl_$2.dmg" length="1" type="application/octet-stream" sparkle:edSignature="c2ln"/></item>
</channel></rss>
<!-- sparkle-signatures:
edSignature: c2ln
length: 1
-->
XML
}

# release <tag> <version> <build> [draft]
release() {
  mkdir -p "$state/releases/$1"
  appcast "$1" "$2" "$3" > "$state/releases/$1/appcast.xml"
  [ "${4:-}" = draft ] && : > "$state/releases/$1/draft"
  return 0
}

# run <source tag>: prints the script's exit code.
run() {
  set +e
  FAKE_STATE="$state" PREVIEW_FEED_RETRY_SECONDS=0 GITHUB_REPOSITORY=o/r GH_TOKEN=x GITHUB_STEP_SUMMARY=/dev/null PATH="$work/bin:$PATH" \
    "$here/release/update-preview-feed.sh" "$1" > "$state/log" 2>&1
  local code=$?
  set -e
  echo "$code"
}

served_build() { sed -n 's:.*<sparkle\:version>\([^<]*\)</sparkle\:version>.*:\1:p' "$state/served.xml"; }

failures=0
check() {
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected '$3', got '$2'"; cat "$state/log"; failures=$((failures + 1)); fi
}

# A new preview replaces the feed that announces the one before it.
state="$work/forward"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
cp "$state/releases/preview-1/appcast.xml" "$state/served.xml"
check "a newer source replaces the feed" "$(run preview-2) $(served_build)" "0 10499.2"

# A re-run of an older preview leaves a feed that announces the newest published build.
state="$work/rerun"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
cp "$state/releases/preview-2/appcast.xml" "$state/served.xml"
check "an older source leaves the feed" "$(run preview-1) $(served_build)" "0 10499.2"

# With the feed gone, an older source still does not move it backwards.
state="$work/gone"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
check "an older source writes no missing feed" "$(run preview-1) $([ -e "$state/served.xml" ] && echo served || echo none)" "0 none"

# A draft announcing a newer build is not published: the source is still the newest.
state="$work/draft"; mkdir -p "$state"
release preview-2 1.5.0-preview.2 10499.2
release preview-3 1.5.0-preview.3 10499.3 draft
check "a draft never counts" "$(run preview-2) $(served_build)" "0 10499.2"

# A served feed newer than every published release fails the run and is left for a person to look at.
state="$work/planted"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
appcast preview-2 99.0.0 99999 > "$state/served.xml"
check "a feed newer than every release fails" "$(run preview-2) $(served_build)" "1 99999"
grep -q 'newer than every published release' "$state/log" || { echo "FAIL the failure does not say why"; failures=$((failures + 1)); }

# A release that lists an appcast it cannot give fails the run rather than being skipped.
state="$work/flaky"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
cp "$state/releases/preview-1/appcast.xml" "$state/served.xml"
: > "$state/releases/preview-2/broken"
check "an appcast that cannot be downloaded fails the run" "$(run preview-1) $(served_build)" "1 10499.1"

# A download that fails twice and then succeeds is retried, the source's and a published release's alike.
state="$work/transient"; mkdir -p "$state"
release preview-1 1.5.0-preview.1 10499.1
release preview-2 1.5.0-preview.2 10499.2
cp "$state/releases/preview-1/appcast.xml" "$state/served.xml"
echo 2 > "$state/releases/preview-1/flaky"
echo 2 > "$state/releases/preview-2/flaky"
check "a transient download failure is retried" "$(run preview-2) $(served_build)" "0 10499.2"

[ "$failures" = 0 ] || { echo "$failures failed"; exit 1; }
echo "all passed"
