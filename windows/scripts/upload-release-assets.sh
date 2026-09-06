#!/usr/bin/env bash
# Upload one build's Velopack assets to an existing GitHub release, for both runtimes.
#
#   upload-release-assets.sh <tag> <version> <artifacts-dir>
#
# <artifacts-dir> holds one directory per RID (<artifacts-dir>/win-x64, .../win-arm64), exactly
# as the release and preview workflows download the velopack-<rid> artifacts. Needs GH_TOKEN in
# the environment (both callers export it) and gh + jq on PATH.
#
# The assets go up with gh, not `vpk upload github`. vpk looks the release up by tag and, when
# that lookup misses (it did for windows-preview-2, moments after gh had created the release),
# creates its OWN draft with the same tag and then cannot publish it:
#   Validation Failed: {"resource":"Release","code":"already_exists","field":"tag_name"}
# gh targets the tag directly and --clobber makes a re-run idempotent, so a partial upload
# recovers by re-running the failed job (`gh run rerun <run-id> --failed`); if the release itself
# is wrong, `gh release delete <tag> --yes --cleanup-tag`, `git tag -d <tag>`, re-tag and re-push
# for a fresh run. The file set is the one vpk uploaded for windows-preview-1: the installer, the
# full package, the delta package when a previous release existed, and the channel index
# GithubSource reads. RELEASES-<rid> and assets.<rid>.json are pack-time by-products and stay
# out. One difference from vpk is corrected below: the pack-time releases.<rid>.json also lists
# the PREVIOUS release's package (downloaded for the delta, then pruned), and vpk rebuilt the
# index from this build's Full and Delta only; the same filter is applied here so no entry points
# at a file this release does not carry.
set -euo pipefail

TAG=${1:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}
VERSION=${2:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}
ARTIFACTS=${3:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}

for rid in win-x64 win-arm64; do
  dir="$ARTIFACTS/$rid"
  index="$dir/releases.$rid.json"
  [ -f "$index" ] || { echo "::error::expected asset $index is missing"; exit 1; }
  jq --arg v "$VERSION" '.Assets |= map(select(.Version == $v))' "$index" > "$index.filtered"
  mv "$index.filtered" "$index"
  echo "releases.$rid.json now lists: $(jq -r '.Assets[] | "\(.Type) \(.Version)"' "$index" | paste -sd, -)"
  files=("$dir/ConnectorControl-$rid-Setup.exe" "$dir/ConnectorControl-$VERSION-$rid-full.nupkg" "$index")
  for f in "${files[@]}"; do [ -f "$f" ] || { echo "::error::expected asset $f is missing"; exit 1; }; done
  # The delta package only exists when a previous release of this channel was there to diff
  # against (the first release of a channel has none), so it is optional.
  delta="$dir/ConnectorControl-$VERSION-$rid-delta.nupkg"
  if [ -f "$delta" ]; then files+=("$delta"); fi
  gh release upload "$TAG" "${files[@]}" --clobber
done
