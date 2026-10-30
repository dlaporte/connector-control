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
# gh targets the tag directly. A published asset is never overwritten: a re-run has rebuilt the
# packages, and a different Setup.exe or .nupkg under the same version is exactly what an update
# system must never see. Per RID the upload is all-or-nothing - releases.<rid>.json carries the
# packages' sizes and hashes, so completing a half-uploaded RID with this run's index would point
# Velopack at files it does not describe. A RID whose assets are all there is skipped (so a re-run
# of a job that failed AFTER uploading is idempotent); a RID with some but not all of them fails
# with the delete-asset commands to run first. If the release itself is wrong,
# `gh release delete <tag> --yes --cleanup-tag`, `git tag -d <tag>`, re-tag and re-push for a
# fresh run. The file set is the one vpk uploaded for windows-preview-1, named in
# scripts/lib/common.sh (windows_release_assets), which verify-release.sh reads too.
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
. "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

TAG=${1:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}
VERSION=${2:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}
ARTIFACTS=${3:?usage: upload-release-assets.sh <tag> <version> <artifacts-dir>}

published=$(gh release view "$TAG" --json assets --jq '.assets[].name')

for rid in $(release_rids); do
  dir="$ARTIFACTS/$rid"
  index="$dir/releases.$rid.json"
  [ -f "$index" ] || { echo "::error::expected asset $index is missing"; exit 1; }
  echo "releases.$rid.json lists: $(jq -r '.Assets[] | "\(.Type) \(.Version)"' "$index" | paste -sd, -)"
  files=()
  while IFS= read -r name; do files+=("$dir/$name"); done < <(windows_release_assets "$VERSION" "$rid")
  for f in "${files[@]}"; do [ -f "$f" ] || { echo "::error::expected asset $f is missing"; exit 1; }; done

  missing=() # set by partition_assets
  partition_assets "$TAG" "$rid" "$published" "${files[@]}" || exit 1
  if [ "${#missing[@]}" -eq 0 ]; then
    echo "$rid: every asset is already published; leaving them untouched."
    continue
  fi
  gh release upload "$TAG" "${files[@]}"
done
