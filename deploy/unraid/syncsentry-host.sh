#!/bin/bash
# SyncSentry host helper for the Unraid User Scripts plugin.
#
# Schedule: every minute (or * * * * *). It does not mount, unmount, or write to media.
#   1. Writes Tailscale accept-routes state into appdata for the container to read.
#   2. Runs an allowlisted script when SyncSentry drops a trigger file.
#
# Allowlisted scripts live only in $JOBDIR and are named <scriptKey>.sh.
# SyncSentry sends the script key. It does not send a shell command, and this script
# does not pass the trigger file to the job as arguments.

set -u
APPDIR="${SYNCSENTRY_APPDATA:-/mnt/user/appdata/syncsentry}"
HOSTDIR="$APPDIR/host"
TRIGGERDIR="$APPDIR/triggers"
JOBDIR="${SYNCSENTRY_JOBS:-/boot/config/plugins/user.scripts/scripts/syncsentry-jobs}"
TAILSCALE="${TAILSCALE_BIN:-}"

mkdir -p "$HOSTDIR" "$TRIGGERDIR" "$JOBDIR"

write_tailscale() {
    local tmp="$HOSTDIR/tailscale.json.tmp"
    local accept="null"
    local state="unknown"
    local bin="$TAILSCALE"
    if [ -z "$bin" ]; then
        bin=$(command -v tailscale || true)
    fi
    if [ -n "$bin" ]; then
        local prefs
        prefs=$("$bin" debug prefs 2>/dev/null || true)
        if printf '%s' "$prefs" | grep -q '"RouteAll": true'; then
            accept=true
        elif printf '%s' "$prefs" | grep -q '"RouteAll": false'; then
            accept=false
        fi
        local status
        status=$("$bin" status --json 2>/dev/null || true)
        state=$(printf '%s' "$status" | sed -n 's/.*"BackendState": "\([^"]*\)".*/\1/p' | head -1)
        [ -n "$state" ] || state="unknown"
    fi
    # Keep the state token free of quotes and backslashes so the JSON stays valid.
    state=$(printf '%s' "$state" | tr -cd 'A-Za-z0-9_-')
    [ -n "$state" ] || state="unknown"
    local now
    now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    printf '{"acceptRoutes": %s, "backendState": "%s", "updatedUtc": "%s"}\n' "$accept" "$state" "$now" > "$tmp"
    mv -f "$tmp" "$HOSTDIR/tailscale.json"
}

run_triggers() {
    local req base key ack run script real
    for req in "$TRIGGERDIR"/*.request; do
        [ -f "$req" ] || continue
        base=$(basename "$req" .request)
        case "$base" in
            *[!a-z0-9]*) continue ;;
        esac
        ack="$TRIGGERDIR/$base.ack"
        run="$TRIGGERDIR/$base.running"
        [ -f "$ack" ] && continue
        if [ -f "$run" ]; then
            continue
        fi
        key=$(sed -n 's/^scriptKey=\([a-z0-9][a-z0-9-]*\)$/\1/p' "$req" | head -1)
        if [ -z "$key" ]; then
            printf 'ok=false\nerror=missing scriptKey\n' > "$ack"
            continue
        fi
        script="$JOBDIR/$key.sh"
        if [ -L "$script" ] || [ ! -f "$script" ]; then
            printf 'ok=false\nerror=script not in allowlist\n' > "$ack"
            continue
        fi
        real=$(realpath -e "$script" 2>/dev/null || true)
        case "$real" in
            "$JOBDIR"/*) ;;
            *)
                printf 'ok=false\nerror=script not in allowlist\n' > "$ack"
                continue
                ;;
        esac
        printf '%s\n' "$key" > "$run"
        if "$script" >> "$HOSTDIR/jobs.log" 2>&1; then
            printf 'ok=true\nscriptKey=%s\n' "$key" > "$ack"
        else
            printf 'ok=false\nerror=script exited non-zero\n' > "$ack"
        fi
        rm -f "$run"
    done
}

write_tailscale
run_triggers
