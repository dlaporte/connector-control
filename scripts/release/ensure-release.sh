#!/usr/bin/env bash
# Create a GitHub release under one tag, or reuse it if a run already created it.
#
#   ensure-release.sh <tag> --prerelease|--full --notes <file> --title <text> [--target <ref>] [asset...]
#
#   --prerelease / --full   the release must be (or become) a prerelease, or a full release.
#   --notes <file>          markdown notes file (gh release create --notes-file).
#   --title <text>          release title.
#   --target <ref>          commit/branch to tag if <tag> does not exist yet (gh release create
#                            --target). Only used when the tag is created here; a tag pushed
#                            before this ran already names its own commit.
#   asset...                zero or more files. On CREATE, all are attached as positional
#                            arguments to `gh release create` so the release is never published
#                            without them (uploading them as a follow-up would leave a window
#                            where GitHub has already moved releases/latest, or published the
#                            prerelease, with assets still missing). On REUSE, each asset already
#                            published is left untouched; if none of the given assets are
#                            published yet, all are uploaded; if some are published and others are
#                            not, the run fails instead of guessing which half to trust — a
#                            published asset is never replaced, since a re-run has rebuilt its
#                            file, and a different binary under the same name and version is
#                            exactly what an update system must never see.
#
# A stale DRAFT release under <tag> (a leftover of an earlier failed run) is deleted first: it
# would otherwise make GitHub refuse to publish anything else under the tag, and it is invisible
# to users and carries nothing a fresh run does not rebuild.
#
# Needs GH_TOKEN and GITHUB_REPOSITORY in the environment (both callers export GH_TOKEN; Actions
# sets GITHUB_REPOSITORY) and gh + jq on PATH.
set -euo pipefail

usage() {
  echo "usage: ensure-release.sh <tag> --prerelease|--full --notes <file> --title <text> [--target <ref>] [asset...]" >&2
}

# Every DRAFT release carrying $1, paginated because the draft can be arbitrarily far down the
# release list.
delete_stale_drafts() {
  local tag=$1
  gh api --paginate "repos/$GITHUB_REPOSITORY/releases?per_page=100" \
    --jq ".[] | select(.draft and .tag_name == \"$tag\") | .id" | while read -r id; do
    echo "Deleting stale draft release $id for $tag"
    gh api -X DELETE "repos/$GITHUB_REPOSITORY/releases/$id"
  done
}

[ $# -ge 1 ] || { usage; exit 1; }
TAG=$1
shift
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

PRERELEASE=""
NOTES=""
TITLE=""
TARGET=""
ASSETS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --prerelease) PRERELEASE=true; shift ;;
    --full) PRERELEASE=false; shift ;;
    --notes) NOTES=${2:?--notes needs a file}; shift 2 ;;
    --title) TITLE=${2:?--title needs a value}; shift 2 ;;
    --target) TARGET=${2:?--target needs a ref}; shift 2 ;;
    --) shift; ASSETS+=("$@"); break ;;
    -*) echo "::error::unknown option $1"; usage; exit 1 ;;
    *) ASSETS+=("$1"); shift ;;
  esac
done
[ -n "$PRERELEASE" ] || { echo "::error::one of --prerelease or --full is required"; usage; exit 1; }
[ -n "$NOTES" ] || { echo "::error::--notes is required"; usage; exit 1; }
[ -n "$TITLE" ] || { echo "::error::--title is required"; usage; exit 1; }
[ -f "$NOTES" ] || { echo "::error::notes file $NOTES not found"; exit 1; }

delete_stale_drafts "$TAG"

if EXISTING=$(gh release view "$TAG" --json isPrerelease --jq .isPrerelease 2>/dev/null); then
  if [ "$EXISTING" != "$PRERELEASE" ]; then
    echo "::error::Release $TAG exists and isPrerelease=$EXISTING, but this run expected $PRERELEASE. Delete it and re-run."
    exit 1
  fi
  echo "Release $TAG already exists (re-run); reusing it."
  if [ "${#ASSETS[@]}" -gt 0 ]; then
    published=$(gh release view "$TAG" --json assets --jq '.assets[].name')
    present=(); missing=()
    for f in "${ASSETS[@]}"; do
      if grep -qxF "$(basename "$f")" <<< "$published"; then present+=("$(basename "$f")"); else missing+=("$(basename "$f")"); fi
    done
    if [ "${#missing[@]}" -eq 0 ]; then
      echo "Every given asset is already published; leaving them untouched."
    elif [ "${#present[@]}" -gt 0 ]; then
      echo "::error::$TAG already carries some given assets (${present[*]}) but not others (${missing[*]}). Published assets are never replaced: remove the partial set with 'gh release delete-asset $TAG <name> --yes' for each of ${present[*]}, then re-run."
      exit 1
    else
      gh release upload "$TAG" "${ASSETS[@]}"
    fi
  fi
else
  create_args=(--title "$TITLE" --notes-file "$NOTES")
  if [ "$PRERELEASE" = true ]; then
    create_args+=(--prerelease)
  fi
  # --target pins the tag to the commit being built: on a tag push the tag already exists and gh
  # ignores it; on a dispatch it stops gh from creating the tag on the default branch instead of
  # the ref that was dispatched.
  if [ -n "$TARGET" ]; then
    create_args+=(--target "$TARGET")
  fi
  # The assets are positional arguments to `gh release create`, NOT a follow-up upload: with
  # assets to attach, gh creates the release as a draft, uploads them, and only then publishes
  # it — so a release with an asset requirement (the Mac DMG + appcast) is never live without
  # them, even if this job dies mid-upload.
  # No --prerelease and no --latest for a full release: GitHub marks the newest full release
  # "latest" on its own, which is exactly what SUFeedURL relies on.
  gh release create "$TAG" "${create_args[@]}" "${ASSETS[@]}"
fi
