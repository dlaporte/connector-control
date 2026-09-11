#!/usr/bin/env bash
# Print the CHANGELOG.md section for one version heading: everything between "## vX.Y.Z" and
# the next "## " heading (any "###" sub-headings inside are included, since they don't match
# that boundary). This is the single source for the GitHub release notes and the text both apps
# show in their update dialogs, so a missing section exits 1; callers that don't tolerate that
# let it fail the run, and callers that do (e.g. a preview cut before the section exists) pass
# --quiet to suppress the ::error:: annotation.
#
#   changelog-section.sh [-q|--quiet] <vX.Y.Z> [CHANGELOG.md]
set -euo pipefail

QUIET=false
if [ "${1:-}" = "-q" ] || [ "${1:-}" = "--quiet" ]; then
  QUIET=true
  shift
fi

VER=${1:?usage: changelog-section.sh [-q|--quiet] <vX.Y.Z> [CHANGELOG.md]}
FILE=${2:-CHANGELOG.md}
if [ ! -f "$FILE" ]; then
  $QUIET || echo "::error::$FILE not found" >&2
  exit 1
fi

SECTION=$(awk -v ver="## $VER" '$0 == ver {found=1; next} /^## / && found {exit} found {print}' "$FILE")
if [ -z "$(printf '%s' "$SECTION" | tr -d '[:space:]')" ]; then
  $QUIET || echo "::error::$FILE has no '## $VER' section — write one before tagging." >&2
  exit 1
fi
printf '%s\n' "$SECTION"
