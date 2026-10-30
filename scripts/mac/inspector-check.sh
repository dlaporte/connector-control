#!/usr/bin/env bash
# Drives `connector-control mcp` with the official MCP Inspector against the headless test host - the
# real control layer over a sandbox - so the MCP subset this repo writes for itself is checked by the
# reference client. Nothing outside a mktemp folder is touched: the test host builds every path from
# it, and CONNECTOR_CONTROL_STORE_DIR, passed to the relay with -e, both points the relay at the test
# host's socket and keeps it from ever starting the real app.
#
#   inspector-check.sh <connector-control> <connector-control-test-host>
#
# Needs Node 22.19 or later (npx) and jq. windows/scripts/inspector-check.ps1 is its Windows twin.
set -euo pipefail

TOOL=${1:?usage: inspector-check.sh <connector-control> <connector-control-test-host>}
HOST_BIN=${2:?usage: inspector-check.sh <connector-control> <connector-control-test-host>}
# Pinned: the Inspector's CLI flags and exit codes changed between major versions.
INSPECTOR="@modelcontextprotocol/inspector@2.12.0"

# Short: a socket path holds at most 103 bytes.
SANDBOX=$(mktemp -d /tmp/cc-inspect-XXXXXX)
HOST_PID=""
# SIGTERM first, as a quit; SIGKILL if the process is still there after about 3 s.
stop_process() { # <pid>
    kill -TERM "$1" 2>/dev/null || return 0
    for _ in $(seq 1 30); do
        kill -0 "$1" 2>/dev/null || return 0
        sleep 0.1
    done
    kill -KILL "$1" 2>/dev/null || true
}
cleanup() {
    local code=$?
    if [ "$code" != 0 ] && [ -s "$SANDBOX/host.err" ]; then
        echo "== the test host's stderr" >&2
        cat "$SANDBOX/host.err" >&2
    fi
    if [ -n "$HOST_PID" ]; then stop_process "$HOST_PID"; fi
    rm -rf "$SANDBOX"
}
trap cleanup EXIT

# The Inspector's own state stays in the sandbox, and its secrets in memory: no keychain prompt on a
# runner, nothing left in the home folder.
export MCP_INSPECTOR_SECRET_STORE=memory
export MCP_STORAGE_DIR="$SANDBOX/inspector"
export MCP_CATALOG_PATH="$SANDBOX/inspector/mcp.json"
export MCP_CLIENT_CONFIG_PATH="$SANDBOX/inspector/client.json"
mkdir -p "$MCP_STORAGE_DIR"

"$HOST_BIN" "$SANDBOX" > "$SANDBOX/host.out" 2> "$SANDBOX/host.err" &
HOST_PID=$!
for _ in $(seq 1 100); do
    if grep -q '^ready ' "$SANDBOX/host.out" 2>/dev/null; then break; fi
    if ! kill -0 "$HOST_PID" 2>/dev/null; then
        echo "::error::the test host exited before it was ready" >&2
        exit 1
    fi
    sleep 0.1
done
grep -q '^ready ' "$SANDBOX/host.out" || { echo "::error::the test host never said it was ready" >&2; exit 1; }

inspect() {
    npx --yes "$INSPECTOR" --cli --format json -q -e "CONNECTOR_CONTROL_STORE_DIR=$SANDBOX/store" "$@" -- "$TOOL" mcp
}

echo "== tools/list, with the Inspector's strict schema check"
inspect --method tools/list --strict > "$SANDBOX/tools.json"
jq -e '(.tools // .result.tools) | length == 24' "$SANDBOX/tools.json" > /dev/null \
    || { echo "::error::expected 24 tools" >&2; cat "$SANDBOX/tools.json" >&2; exit 1; }
jq -e '[(.tools // .result.tools)[] | select(.annotations.readOnlyHint == true)] | length == 8' "$SANDBOX/tools.json" > /dev/null \
    || { echo "::error::expected 8 read-only tools" >&2; exit 1; }

echo "== tools/call get_status"
inspect --method tools/call --tool-name get_status > "$SANDBOX/status.json"
jq -e '((.structuredContent // .result.structuredContent).version | type == "string") and ((.isError // .result.isError) == false)' \
    "$SANDBOX/status.json" > /dev/null || { echo "::error::get_status did not answer" >&2; cat "$SANDBOX/status.json" >&2; exit 1; }

echo "== tools/call list_connectors"
inspect --method tools/call --tool-name list_connectors > "$SANDBOX/connectors.json"
jq -e '[(.structuredContent // .result.structuredContent).connectors[].name] | sort == ["fetch","time"]' \
    "$SANDBOX/connectors.json" > /dev/null || { echo "::error::list_connectors did not list the seed" >&2; exit 1; }

echo "== restart_claude, which always asks and which the test host declines"
code=0
inspect --method tools/call --tool-name restart_claude > "$SANDBOX/declined.json" 2> "$SANDBOX/declined.err" || code=$?
[ "$code" = 5 ] || { echo "::error::a declined change should exit 5 (isError), got $code" >&2; cat "$SANDBOX/declined.err" >&2; exit 1; }
# Exit 5 also covers a tool the server does not have, which writes nothing: read the answer itself.
jq -e '(.isError // .result.isError) == true and (((.structuredContent // .result.structuredContent).error // "") | test("You declined this change"))' \
    "$SANDBOX/declined.json" > /dev/null || { echo "::error::restart_claude was not declined by the test host" >&2; cat "$SANDBOX/declined.json" >&2; exit 1; }

echo "Inspector check passed"
