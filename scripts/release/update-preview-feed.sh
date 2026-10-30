#!/usr/bin/env bash
# Point the Mac preview feed at one published build: copy that release's appcast.xml onto the
# permanent preview-feed prerelease, which Mac preview builds read as SUFeedURL
# (releases/download/preview-feed/appcast.xml; build-app.sh gives every X.Y.Z-preview.N that
# URL). preview.yml calls this after publishing preview-<n>, release.yml after publishing vX.Y.Z,
# so a preview install is offered every later preview and then the final release.
#
#   update-preview-feed.sh <source-tag>
#
#   <source-tag>  the release just published (preview-<n> or vX.Y.Z). Its appcast.xml asset is
#                 the one copied, not the run's build artifact: on a re-run, ensure-release.sh
#                 keeps the DMG and appcast first published under the tag and the run's rebuilt
#                 pair is discarded, so only the published appcast is guaranteed to carry the
#                 EdDSA signature and length of the DMG testers actually download.
#
# The feed only ever moves forward, judged against the published releases, not against the feed
# itself. Builds are sparkle:version (CFBundleVersion, as build-app.sh derives it: 10499.10 for
# 1.5.0-preview.10, 10500 for 1.5.0), compared numerically component by component. The newest
# published build is the highest sparkle:version among the appcast.xml assets of every non-draft
# preview-<n> and vX.Y.Z release, read through the API. A source older than that is left out with
# a notice - a re-run of an old preview, or a stable release of an older line, must never move
# preview installs backwards. A served feed announcing a build newer than every published release
# did not come from this pipeline (a hand upload, or an appcast planted to wedge preview delivery
# behind a build that never comes): that fails the job rather than passing with a notice. An equal
# build is replaced: the source is the published appcast of that same build, so this refreshes it
# idempotently. The compare and the replace are not atomic; both callers run this in the job-level
# concurrency group "preview-feed", which serialises a preview and a release that publish at the
# same moment.
#
# The feed's EdDSA signature is checked for presence only, not verified against SUPublicEDKey:
# both callers run this on ubuntu-24.04, and Sparkle's sign_update, the one tool that verifies its
# signatures, ships only as a macOS binary (make-appcast.sh fetches it on the macOS build runner).
# Every installed preview verifies the whole feed against its own key (SURequireSignedFeed) and
# refuses one that does not match, so a forged feed can only stop updates, which the ordering check
# above catches when it claims a newer build.
#
# preview-feed itself. Created here, on the first run that finds it missing, as a PRERELEASE
# (never releases/latest, so the stable feed at releases/latest/download/appcast.xml cannot
# resolve to it), with appcast.xml attached at creation so it is never live without its feed.
# Its tag is created on master as it stands then and never moves: the commit means nothing (the
# release exists for its one asset), and a commit on the default branch, unlike a preview's
# commit on some branch, is one the tag never keeps alive on its own. An existing preview-feed
# that is not a prerelease is a fault this refuses to write to - and, since a full release could
# have become releases/latest and served this feed to every stable install, it is turned back
# into a prerelease at once before failing. Deleting the release only makes preview installs'
# checks fail until the next preview or release recreates it.
#
# Operational rules this relies on:
#   - Never delete the newest published preview release (or the stable release the feed was last
#     copied from): the feed's enclosure points at that release's DMG, so preview installs would
#     find an update they cannot download until the next preview or release replaces the feed.
#   - Immutable releases must stay off for this repository: the feed is replaced in place with
#     `gh release upload --clobber`, which an immutable preview-feed would refuse.
#   - The "preview-feed" concurrency group holds one running and one pending job. A third
#     contender cancels the pending one; re-running the cancelled job brings the feed up to date
#     (the ordering check makes any re-run safe).
#
# After writing, the feed is fetched the way Sparkle fetches it (curl -L of the preview URL) and
# must be byte-for-byte the appcast written, announcing this version and build, enclosing the
# source release's DMG and carrying an EdDSA signature on the item and the feed's own
# sparkle-signatures block (SURequireSignedFeed refuses a feed without it); releases/latest must
# still be a stable
# v* release. Writes a step summary.
#
# Needs GH_TOKEN and GITHUB_REPOSITORY in the environment and gh, jq, curl and cmp on PATH.
# scripts/tests/update-preview-feed-order.sh runs it against a fake gh and curl.
set -euo pipefail

SOURCE=${1:?usage: update-preview-feed.sh <source-tag>}
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

FEED_TAG=preview-feed
REPO_URL="${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY"
FEED_URL="$REPO_URL/releases/download/$FEED_TAG/appcast.xml"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# The one <sparkle:version> in an appcast (generate_appcast is given one DMG, so writes one item).
# Empty when there is not exactly one.
build_of() {
  local found
  found=$(sed -n 's:.*<sparkle\:version>\([^<]*\)</sparkle\:version>.*:\1:p' "$1")
  [ "$(grep -c . <<< "$found")" = 1 ] && printf '%s' "$found"
}

# -1, 0 or 1 as build $1 is older than, equal to or newer than build $2: dot-separated numbers
# compared numerically, a missing component counting as 0 - a string compare would put 10499.10
# below 10499.9.
compare_builds() {
  local IFS=. i x y
  local -a a b
  read -r -a a <<< "$1"
  read -r -a b <<< "$2"
  for (( i = 0; i < ${#a[@]} || i < ${#b[@]}; i++ )); do
    x=$(( 10#${a[i]:-0} )); y=$(( 10#${b[i]:-0} ))
    if (( x < y )); then echo -1; return; fi
    if (( x > y )); then echo 1; return; fi
  done
  echo 0
}

is_build() { [[ "$1" =~ ^[0-9]+(\.[0-9]+)*$ ]]; }

# Downloads release $1's appcast.xml into folder $2, trying three times, PREVIEW_FEED_RETRY_SECONDS
# apart (5 unless set; the ordering test sets 0), as the served feed's curl retries: one transient
# failure must not fail the run.
download_appcast() {
  local try
  for try in 1 2 3; do
    gh release download "$1" --pattern appcast.xml --dir "$2" --clobber >&2 && return 0
    if [ "$try" != 3 ]; then
      echo "downloading $1's appcast.xml failed; retrying ($try/3)" >&2
      sleep "${PREVIEW_FEED_RETRY_SECONDS:-5}"
    fi
  done
  return 1
}

# The newest build any published release announces: the highest sparkle:version among the
# appcast.xml assets of the non-draft preview-<n> and vX.Y.Z releases. Empty when there is none.
# A release that lists an appcast it cannot then give fails the run, so a flaky download can never
# make the newest release look older than it is. Every failure returns explicitly: errexit does not
# reach into the command substitution this runs in.
newest_published_build() {
  local tag build newest=""
  gh api --paginate "repos/$GITHUB_REPOSITORY/releases?per_page=100" > "$WORK/releases.json" \
    || { echo "::error::could not list the releases" >&2; return 1; }
  jq -r '.[] | select(.draft | not) | select(any(.assets[]; .name == "appcast.xml")) | .tag_name' \
    "$WORK/releases.json" > "$WORK/listed-tags" || { echo "::error::could not read the release list" >&2; return 1; }
  grep -E '^(preview-[0-9]+|v[0-9]+\.[0-9]+\.[0-9]+)$' "$WORK/listed-tags" > "$WORK/published-tags" || true
  while IFS= read -r tag; do
    rm -rf "$WORK/published"
    mkdir -p "$WORK/published"
    download_appcast "$tag" "$WORK/published" \
      || { echo "::error::could not download $tag's appcast.xml" >&2; return 1; }
    build=$(build_of "$WORK/published/appcast.xml") || true
    if ! is_build "$build"; then
      echo "::warning::$tag's appcast.xml has no single numeric sparkle:version ('$build'); not counted." >&2
      continue
    fi
    if [ -z "$newest" ] || [ "$(compare_builds "$build" "$newest")" = 1 ]; then newest=$build; fi
  done < "$WORK/published-tags"
  printf '%s' "$newest"
}

# The source: its published appcast, its version and its build.
mkdir -p "$WORK/source"
download_appcast "$SOURCE" "$WORK/source" || { echo "::error::could not download $SOURCE's appcast.xml"; exit 1; }
NEW="$WORK/source/appcast.xml"
NEW_BUILD=$(build_of "$NEW") || true
is_build "$NEW_BUILD" || { echo "::error::$SOURCE's appcast.xml does not carry exactly one numeric sparkle:version ('$NEW_BUILD')"; exit 1; }
NEW_VERSION=$(sed -n 's:.*<sparkle\:shortVersionString>\([^<]*\)</sparkle\:shortVersionString>.*:\1:p' "$NEW" | head -1)
[ -n "$NEW_VERSION" ] || { echo "::error::$SOURCE's appcast.xml carries no sparkle:shortVersionString"; exit 1; }
grep -q "url=\"$REPO_URL/releases/download/$SOURCE/ConnectorControl_$NEW_VERSION.dmg\"" "$NEW" \
  || { echo "::error::$SOURCE's appcast.xml does not enclose $SOURCE's own DMG"; exit 1; }
echo "$SOURCE announces $NEW_VERSION (build $NEW_BUILD)"

# The feed as preview installs see it now. 404 (no release yet, or no asset) is an empty feed;
# any other failure stops here rather than guessing.
STATUS=$(curl -sL --retry 3 --retry-delay 5 -o "$WORK/served.xml" -w '%{http_code}' "$FEED_URL" || true)
CURRENT_BUILD=""
case "$STATUS" in
  200)
    CURRENT_BUILD=$(build_of "$WORK/served.xml") || true
    if ! is_build "$CURRENT_BUILD"; then
      echo "::warning::the served preview feed has no single numeric sparkle:version ('$CURRENT_BUILD'); replacing it."
      CURRENT_BUILD=""
    fi ;;
  404) echo "The preview feed is not served yet ($FEED_URL: 404)." ;;
  *) echo "::error::fetching $FEED_URL answered HTTP '$STATUS'"; exit 1 ;;
esac

PUBLISHED_BUILD=$(newest_published_build) || exit 1
# The source was just published, so it is among them: none at all means the listing failed.
is_build "$PUBLISHED_BUILD" || { echo "::error::no published preview-<n> or vX.Y.Z release carries an appcast.xml with a numeric sparkle:version, not even $SOURCE"; exit 1; }
echo "The newest published release announces build $PUBLISHED_BUILD"

if [ -n "$CURRENT_BUILD" ] && [ "$(compare_builds "$CURRENT_BUILD" "$PUBLISHED_BUILD")" = 1 ]; then
  echo "::error::The preview feed announces build $CURRENT_BUILD, newer than every published release (newest: $PUBLISHED_BUILD), so this pipeline did not write it. Preview installs are offered nothing until it is replaced. Find out who uploaded it, delete it ('gh release delete-asset $FEED_TAG appcast.xml --yes'), then re-run the newest release's preview-feed job."
  exit 1
fi

if [ "$(compare_builds "$NEW_BUILD" "$PUBLISHED_BUILD")" = -1 ]; then
  echo "::notice::A newer release announcing build $PUBLISHED_BUILD is published; $SOURCE's $NEW_BUILD leaves the preview feed unchanged."
  if [ "$CURRENT_BUILD" != "$PUBLISHED_BUILD" ]; then
    echo "::warning::The preview feed announces build ${CURRENT_BUILD:-none}, not the newest published $PUBLISHED_BUILD: re-run the preview-feed job of the release that published it."
  fi
  {
    echo "## Preview feed"
    echo
    echo "- Left unchanged (build ${CURRENT_BUILD:-none}): a release announcing build $PUBLISHED_BUILD, newer than $SOURCE ($NEW_VERSION, build $NEW_BUILD), is published."
  } >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  exit 0
fi

# Unreachable while the source is the newest published build and the feed is no newer than that;
# kept so a feed newer than the source is never replaced, whatever the listing said.
if [ -n "$CURRENT_BUILD" ] && [ "$(compare_builds "$CURRENT_BUILD" "$NEW_BUILD")" = 1 ]; then
  echo "::notice::The preview feed already announces build $CURRENT_BUILD, newer than $SOURCE's $NEW_BUILD; left unchanged."
  {
    echo "## Preview feed"
    echo
    echo "- Left unchanged: it already announces build $CURRENT_BUILD, newer than $SOURCE ($NEW_VERSION, build $NEW_BUILD)."
  } >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  exit 0
fi

if STATE=$(gh release view "$FEED_TAG" --json isPrerelease,isDraft --jq '"\(.isPrerelease) \(.isDraft)"' 2>/dev/null); then
  case "$STATE" in
    "true false") ;;
    "false "*)
      gh release edit "$FEED_TAG" --prerelease
      echo "::error::$FEED_TAG was not a prerelease (so it could have been releases/latest); it has been made a prerelease again. Check releases/latest and the stable feed, then re-run."
      exit 1 ;;
    *) echo "::error::$FEED_TAG is a draft; delete it ('gh release delete $FEED_TAG --yes') and re-run to recreate it."; exit 1 ;;
  esac
  if [ "$STATUS" = 404 ]; then
    echo "::warning::$FEED_TAG exists but serves no appcast.xml: the feed was missing, perhaps because an earlier upload failed after --clobber deleted the old asset. Restoring it now."
  fi
  gh release upload "$FEED_TAG" "$NEW" --clobber
  echo "Replaced $FEED_TAG's appcast.xml (was build ${CURRENT_BUILD:-none}, now $NEW_BUILD)"
else
  cat > "$WORK/body.md" <<BODY
**Not a download.** This prerelease holds \`appcast.xml\`, the update feed that Connector Control's Mac preview builds check for new versions. Every preview and every stable release replaces it. For a preview, see the numbered \`preview-<n>\` releases; for the app itself, the [latest release]($REPO_URL/releases/latest). The source archives GitHub lists here are not a build of anything.
BODY
  # --prerelease is the whole point (see the header); --latest is never passed, in any form.
  gh release create "$FEED_TAG" --prerelease --title "Preview update feed" \
    --notes-file "$WORK/body.md" --target master "$NEW"
  echo "Created $FEED_TAG with build $NEW_BUILD"
fi

# Verify what Sparkle will read. The download redirect can lag the upload by a few seconds, hence
# the retry; `|| true` keeps `bash -e` from ending the loop on the first failed probe.
for i in $(seq 1 12); do
  curl -sfL "$FEED_URL" -o "$WORK/served.xml" || true
  cmp -s "$NEW" "$WORK/served.xml" && break
  echo "waiting for $FEED_URL to serve build $NEW_BUILD ($i/12)"
  sleep 10
done
cmp -s "$NEW" "$WORK/served.xml" || { echo "::error::$FEED_URL does not serve the appcast just written"; exit 1; }
SERVED="$WORK/served.xml"
grep -q "<sparkle:shortVersionString>$NEW_VERSION</sparkle:shortVersionString>" "$SERVED" || { echo "::error::the served preview feed does not announce $NEW_VERSION"; exit 1; }
grep -q "<sparkle:version>$NEW_BUILD</sparkle:version>" "$SERVED" || { echo "::error::the served preview feed's build is not $NEW_BUILD"; exit 1; }
grep -q 'sparkle:edSignature="[^"][^"]*"' "$SERVED" || { echo "::error::the served preview feed's item carries no EdDSA signature"; exit 1; }
# The feed's own signature (SURequireSignedFeed): without it Sparkle rejects the whole feed.
grep -q 'sparkle-signatures:' "$SERVED" || { echo "::error::the served preview feed carries no sparkle-signatures block"; exit 1; }

[ "$(gh release view "$FEED_TAG" --json isPrerelease --jq .isPrerelease)" = true ] \
  || { echo "::error::$FEED_TAG is not a prerelease"; exit 1; }
LATEST=$(gh api "repos/$GITHUB_REPOSITORY/releases/latest" --jq .tag_name)
case "$LATEST" in
  v[0-9]*) echo "releases/latest is still $LATEST (a stable release)" ;;
  *) echo "::error::releases/latest is '$LATEST', which is not a stable v* release"; exit 1 ;;
esac

{
  echo "## Preview feed"
  echo
  echo "- $FEED_URL now announces $NEW_VERSION (build $NEW_BUILD, from $SOURCE; was ${CURRENT_BUILD:-empty})"
  echo "- releases/latest unchanged: $LATEST"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
