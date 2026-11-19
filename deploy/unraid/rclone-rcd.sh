#!/bin/bash
# Start rclone's remote control server so SyncSentry can read stats and start saved jobs.
# User Scripts schedule: At Startup of Array.
#
# The password below is the host's copy. Type the same password into SyncSentry's settings;
# the container stores it encrypted and does not read this file.
# Do not publish port 5572 on your router. Use a password. Do not pass --rc-no-auth.

set -eu
if pgrep -f 'rclone rcd' >/dev/null 2>&1; then
    exit 0
fi
mkdir -p /mnt/user/appdata/syncsentry/host
exec /usr/sbin/rclone rcd \
    --rc-addr 0.0.0.0:5572 \
    --rc-user syncsentry \
    --rc-pass 'replace-me' \
    --rc-web-gui=false \
    --log-file /mnt/user/appdata/syncsentry/host/rcd.log \
    --log-level NOTICE
