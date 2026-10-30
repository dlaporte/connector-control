#!/usr/bin/env bash
# Print the CHANGELOG.md section for one version heading: everything between "## vX.Y.Z" and
# the next "## " heading (any "###" sub-headings inside are included, since they don't match
# that boundary). This is the single source for the GitHub release notes and the text both apps
# show in their update dialogs, so a missing section exits 1 and its caller lets that fail the
# run. A caller that tolerates a missing section can pass --quiet to suppress the ::error::
# annotation; release.yml and preview-notes.sh both fail instead.
#
#   changelog-section.sh [-q|--quiet] [--release | --preview] <vX.Y.Z>
#
#   --release  for a shipping version: drop every "###" sub-heading that has no bullets under
#              it, and fail when nothing but sub-headings was there (a section freshly opened
#              from the template, never filled in).
#   --preview  for a preview's notes: drop the empty sub-headings the same way and demote the
#              kept ones to "####", so they sit under the notes' own "###" heading. A section
#              with nothing in it yet is not an error for a preview.
set -euo pipefail

QUIET=false
MODE=""
while [ $# -gt 0 ]; do
  case "$1" in
    -q|--quiet) QUIET=true ;;
    --release) MODE=release ;;
    --preview) MODE=preview ;;
    *) break ;;
  esac
  shift
done

VER=${1:?usage: changelog-section.sh [-q|--quiet] [--release | --preview] <vX.Y.Z>}
FILE=CHANGELOG.md
if [ ! -f "$FILE" ]; then
  $QUIET || echo "::error::$FILE not found" >&2
  exit 1
fi

SECTION=$(awk -v ver="## $VER" '$0 == ver {found=1; next} /^## / && found {exit} found {print}' "$FILE")
if [ -z "$(printf '%s' "$SECTION" | tr -d '[:space:]')" ]; then
  $QUIET || echo "::error::$FILE has no '## $VER' section - write one before tagging." >&2
  exit 1
fi

if [ -n "$MODE" ]; then
  # A sub-heading with no bullets yet stays in CHANGELOG.md until the version ships, but
  # nobody reading the notes should meet an empty heading, so drop those here.
  SECTION=$(printf '%s\n' "$SECTION" | awk -v demote="$([ "$MODE" = preview ] && echo 1 || echo 0)" '
      /^### / { if (heading != "" && body != "") printf "%s", held; heading = $0; if (demote) sub(/^### /, "#### ", heading); held = heading ORS; body = ""; next }
      heading != "" { held = held $0 ORS; if ($0 ~ /[^[:space:]]/) body = body $0; next }
      { print }
      END { if (heading != "" && body != "") printf "%s", held }
  ')
  if [ "$MODE" = release ] && [ -z "$(printf '%s' "$SECTION" | tr -d '[:space:]')" ]; then
    $QUIET || echo "::error::the '## $VER' section of $FILE has only empty sub-headings - add the release notes before tagging." >&2
    exit 1
  fi
fi
printf '%s\n' "$SECTION"
