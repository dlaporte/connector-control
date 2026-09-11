#!/usr/bin/env bash
# Print the CHANGELOG.md section for one version heading: everything between "## vX.Y.Z" and
# the next "## " heading (any "###" sub-headings inside are included, since they don't match
# that boundary). This is the single source for the GitHub release notes and the text both apps
# show in their update dialogs, so a missing section fails here before any build minutes are
# spent.
#
#   changelog-section.sh <vX.Y.Z> [CHANGELOG.md]
set -euo pipefail

VER=${1:?usage: changelog-section.sh <vX.Y.Z> [CHANGELOG.md]}
FILE=${2:-CHANGELOG.md}
[ -f "$FILE" ] || { echo "::error::$FILE not found"; exit 1; }

SECTION=$(awk -v ver="## $VER" '$0 == ver {found=1; next} /^## / && found {exit} found {print}' "$FILE")
if [ -z "$(printf '%s' "$SECTION" | tr -d '[:space:]')" ]; then
  echo "::error::$FILE has no '## $VER' section — write one before tagging." >&2
  exit 1
fi
printf '%s\n' "$SECTION"
