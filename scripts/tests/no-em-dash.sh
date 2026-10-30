#!/usr/bin/env bash
# The project writes hyphens, never em-dashes (U+2014): in code, comments, user-visible
# strings, docs and the CHANGELOG. Lists every tracked line that has one and fails if any do.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
if git grep -n $'—'; then
  echo "::error::em-dash (U+2014) found above; use a hyphen (\"-\") instead" >&2
  exit 1
fi
echo "no em-dashes"
