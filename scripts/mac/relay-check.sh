#!/usr/bin/env bash
# A running relay survives the app being replaced. A Sparkle update moves the old bundle aside, puts the
# new one in its place and ends only the app: the relay Claude started from the old bundle keeps running
# from its file. This starts the relay from a copy of a built bundle against the headless test host,
# replaces that copy under it and kills the host as an update kills the app, starts a host again, and
# checks that Claude's next request is answered through a replayed handshake, with nothing of the replay
# reaching Claude. CI cannot run a signed Sparkle update; a real one is the release checklist's.
#
#   relay-check.sh <app bundle> <connector-control-test-host>
set -euo pipefail

APP=${1:?usage: relay-check.sh <app bundle> <connector-control-test-host>}
HOST_BIN=${2:?usage: relay-check.sh <app bundle> <connector-control-test-host>}
SANDBOX=$(mktemp -d /tmp/cc-relay-XXXXXX)
COPY="$SANDBOX/Connector Control.app"
HOST_PID=""
RELAY_PID=""
cleanup() {
    if [ -n "$RELAY_PID" ]; then kill "$RELAY_PID" 2>/dev/null || true; fi
    if [ -n "$HOST_PID" ]; then kill "$HOST_PID" 2>/dev/null || true; fi
    rm -rf "$SANDBOX"
}
trap cleanup EXIT

wait_for() { # <pattern> <file> <what>
    for _ in $(seq 1 150); do
        if grep -q -- "$1" "$2" 2>/dev/null; then return 0; fi
        sleep 0.2
    done
    echo "::error::timed out waiting for $3" >&2
    cat "$2" >&2 || true
    exit 1
}

start_host() {
    : > "$SANDBOX/host.out"
    # 3>&-: the host must not inherit Claude's end of the relay's stdin, or the relay never sees it close.
    "$HOST_BIN" "$SANDBOX" >> "$SANDBOX/host.out" 2>> "$SANDBOX/host.err" 3>&- &
    HOST_PID=$!
    wait_for '^ready ' "$SANDBOX/host.out" "the test host"
}

ditto "$APP" "$COPY"
start_host
mkfifo "$SANDBOX/claude-in"
CONNECTOR_CONTROL_STORE_DIR="$SANDBOX/store" "$COPY/Contents/MacOS/connector-control" mcp \
    < "$SANDBOX/claude-in" > "$SANDBOX/claude-out" 2> "$SANDBOX/relay.err" &
RELAY_PID=$!
exec 3> "$SANDBOX/claude-in"
say() { printf '%s\n' "$1" >&3; }

say '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1.0"}}}'
say '{"jsonrpc":"2.0","method":"notifications/initialized"}'
say '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":{}}}'
wait_for '"id":1,' "$SANDBOX/claude-out" "the answer before the update"

echo "== the update: the bundle is replaced under the running relay, and the app is killed"
mv "$COPY" "$COPY.old"
ditto "$APP" "$COPY"
rm -rf "$COPY.old"
kill -KILL "$HOST_PID"
wait "$HOST_PID" 2>/dev/null || true
start_host
# Time for the relay to have read its old connection's end before Claude asks again.
sleep 1
say '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{}}}'
wait_for '"id":2,' "$SANDBOX/claude-out" "the answer after the update"

kill -0 "$RELAY_PID" 2>/dev/null || { echo "::error::the relay did not survive the update" >&2; exit 1; }
grep '"id":2,' "$SANDBOX/claude-out" | grep -q '"isError":false' \
    || { echo "::error::the request after the update was not answered" >&2; cat "$SANDBOX/claude-out" >&2; exit 1; }
[ "$(grep -c '"id":0,' "$SANDBOX/claude-out")" = 1 ] \
    || { echo "::error::Claude saw its handshake answered more than once" >&2; exit 1; }
if grep -q 'connector-control-replay-' "$SANDBOX/claude-out"; then
    echo "::error::the replayed handshake's answer reached Claude" >&2
    exit 1
fi

exec 3>&-
wait "$RELAY_PID"
RELAY_PID=""
echo "Relay check passed"
