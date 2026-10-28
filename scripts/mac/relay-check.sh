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
    if [ "$code" != 0 ]; then
        for log in relay.err host.err; do
            if [ -s "$SANDBOX/$log" ]; then
                echo "== $log" >&2
                cat "$SANDBOX/$log" >&2
            fi
        done
    fi
    if [ -n "$RELAY_PID" ]; then stop_process "$RELAY_PID"; fi
    if [ -n "$HOST_PID" ]; then stop_process "$HOST_PID"; fi
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

[ -x "$APP/Contents/MacOS/connector-control" ] \
    || { echo "::error::$APP has no executable Contents/MacOS/connector-control" >&2; exit 1; }
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
# What this proves: a relay that is already running outlives its bundle being replaced and the app
# being restarted. Starting the new app from the replaced bundle is the release checklist's.
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

# Claude closing its end ends the relay, within a bounded wait.
exec 3>&-
for _ in $(seq 1 150); do
    kill -0 "$RELAY_PID" 2>/dev/null || break
    sleep 0.2
done
if kill -0 "$RELAY_PID" 2>/dev/null; then
    echo "::error::the relay did not end when Claude closed its end" >&2
    exit 1
fi
wait "$RELAY_PID" || { echo "::error::the relay ended with an error" >&2; exit 1; }
RELAY_PID=""
echo "Relay check passed"
