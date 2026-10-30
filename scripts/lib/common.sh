#!/usr/bin/env bash
# Helpers more than one script needs, sourced, never run:
#
#   . "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
#
# It sets no shell options: each caller's own `set -euo pipefail` applies. Written for bash 3.2 too
# (no namerefs, no mapfile), since the macOS runner's /usr/bin/env bash may be the system one.

# SIGTERM first, as a quit; SIGKILL if the process is still there after about 3 s.
stop_process() { # <pid>
    kill -TERM "$1" 2>/dev/null || return 0
    for _ in $(seq 1 30); do
        kill -0 "$1" 2>/dev/null || return 0
        sleep 0.1
    done
    kill -KILL "$1" 2>/dev/null || true
}

# The names a published release carries. Every script that uploads or verifies them builds the list
# here, so a renamed installer or package, or another runtime, is one edit.
release_rids() { echo win-x64 win-arm64; }
# The Mac pair, attached when the release is created: the DMG and the Sparkle appcast announcing it.
mac_release_assets() { # <version>
    printf '%s\n' "ConnectorControl_$1.dmg" appcast.xml
}
# One runtime's Velopack set: the installer, the full package, and the channel index GithubSource reads.
# RELEASES-<rid> and assets.<rid>.json are pack-time by-products and are never published.
windows_release_assets() { # <version> <rid>
    printf '%s\n' "ConnectorControl-$2-Setup.exe" "ConnectorControl-$1-$2-full.nupkg" "releases.$2.json"
}

# Splits the files' names into the globals `present` (already among <published>, one name per line)
# and `missing`. A set is uploaded all or nothing: when only some are published this prints the
# delete-asset commands to run first and returns 1, since a published asset is never replaced (a
# re-run has rebuilt its file, and a different binary under the same name and version is exactly
# what an update system must never see). <what> names the set in that message ("given", "win-x64").
partition_assets() { # <tag> <what> <published> <file>...
    local tag=$1 what=$2 published=$3 f
    shift 3
    # shellcheck disable=SC2034 # read by the caller
    present=(); missing=()
    for f in "$@"; do
        if grep -qxF "$(basename "$f")" <<< "$published"; then present+=("$(basename "$f")"); else missing+=("$(basename "$f")"); fi
    done
    if [ "${#present[@]}" -gt 0 ] && [ "${#missing[@]}" -gt 0 ]; then
        echo "::error::$tag already carries some $what assets (${present[*]}) but not others (${missing[*]}). Published assets are never replaced: remove the partial set with 'gh release delete-asset $tag <name> --yes' for each of ${present[*]}, then re-run."
        return 1
    fi
}

# The one <sparkle:version> (CFBundleVersion) in an appcast; generate_appcast is given one DMG, so
# writes one item. Empty, and status 1, when there is not exactly one.
build_of() { # <appcast>
    local found
    found=$(sed -n 's:.*<sparkle\:version>\([^<]*\)</sparkle\:version>.*:\1:p' "$1")
    [ "$(grep -c . <<< "$found")" = 1 ] && printf '%s' "$found"
}

# A build number as build-app.sh writes one: dot-separated digits (10500, or 10499.10 for a preview).
is_build() { [[ "$1" =~ ^[0-9]+(\.[0-9]+)*$ ]]; }
