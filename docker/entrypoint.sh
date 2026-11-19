#!/bin/sh
# Container entrypoint.
#
# Started as root (the default): make /config belong to PUID:PGID (default 1654, the image's app user;
# Unraid uses PUID=99 PGID=100), then drop to that user and run SyncSentry. A folder the host created
# as root on first start just works.
#
# Started as a non-root user (docker run --user ...): run SyncSentry directly; /config must already be writable.

set -eu

if [ "$(id -u)" != "0" ]; then
    exec dotnet /app/SyncSentry.dll "$@"
fi

PUID="${PUID:-1654}"
PGID="${PGID:-1654}"

case "$PUID$PGID" in
    *[!0-9]*|"") echo "PUID and PGID must be numbers (got PUID=$PUID PGID=$PGID)" >&2; exit 1 ;;
esac

if [ "$PUID" = "0" ]; then
    echo "Refusing to run SyncSentry as root. Set PUID to a non-root user id." >&2
    exit 1
fi

for dir in /config /config/keys /config/logs /config/host /config/triggers /config/cache; do
    mkdir -p "$dir"
    # Only touch files that aren't already owned correctly, so restarts stay fast.
    if ! find "$dir" \( ! -user "$PUID" -o ! -group "$PGID" \) -exec chown "$PUID:$PGID" {} + 2>/dev/null; then
        echo "Warning: couldn't change ownership of $dir to $PUID:$PGID; SyncSentry may not be able to write to it." >&2
    fi
done
chmod 700 /config/keys 2>/dev/null || true

export HOME=/tmp
exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet /app/SyncSentry.dll "$@"
