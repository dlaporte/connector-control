#!/usr/bin/env bash
# changelog-section.sh --release drops empty sub-headings and fails on a section that is only
# sub-headings or is missing; --preview drops them too but never fails on an empty section.
# Runs the script in a scratch directory with its own CHANGELOG.md.
set -euo pipefail
script="$(cd "$(dirname "$0")/.." && pwd)/release/changelog-section.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cd "$work"
cat > CHANGELOG.md <<'MD'
# Changelog

## v2.0.0

### macOS

### Windows

### Both platforms

### Release pipeline

## v1.9.0

### macOS

### Windows

- Fixed a thing.

### Both platforms

## v1.8.0

Just a paragraph.
MD

fail=0
check() { # check <name> <condition result>
  if [ "$2" = ok ]; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}

# headings only -> release fails with an ::error::, nothing on stdout
set +e
out=$("$script" --release v2.0.0 2>err); code=$?
set -e
{ [ "$code" -ne 0 ] && [ -z "$out" ] && grep -q '^::error::' err; } && r=ok || r=bad
check "release: headings-only section fails" "$r"

# one bullet under one heading -> only that heading and bullet survive
out=$("$script" --release v1.9.0 | sed "/./,\$!d")
[ "$out" = "$(printf '### Windows\n\n- Fixed a thing.\n')" ] && r=ok || r=bad
check "release: empty headings dropped, filled one kept" "$r"

# prose with no sub-headings is content
"$script" --release v1.8.0 >/dev/null && r=ok || r=bad
check "release: prose-only section passes" "$r"

# missing section
set +e
"$script" --release v9.9.9 >/dev/null 2>err; code=$?
set -e
{ [ "$code" -ne 0 ] && grep -q '^::error::' err; } && r=ok || r=bad
check "release: missing section fails" "$r"

# preview: empty section is fine, kept headings demoted
out=$("$script" --preview v2.0.0) && r=ok || r=bad
check "preview: headings-only section does not fail" "$r"
out=$("$script" --preview v1.9.0 | sed "/./,\$!d")
[ "$out" = "$(printf '#### Windows\n\n- Fixed a thing.\n')" ] && r=ok || r=bad
check "preview: kept heading demoted" "$r"

exit "$fail"
