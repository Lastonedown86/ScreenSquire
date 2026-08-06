#!/usr/bin/env bash
# Runs as ExecStartPre= on every signage-agent start (see install.sh).
#
# Contract with agent/main.py's /api/update: an "update-pending" marker holding
# the failed-start count is written durably before the swap touches any
# installed path, and a healthy agent startup deletes it. While the marker
# exists this script counts starts; once MAX_ATTEMPTS starts have failed to
# clear it, the update-backup snapshot is restored so a bundle that compiles
# but cannot run — or a half-swapped install after power loss — cannot
# crash-loop a remote Pi forever.
#
# Always exits 0: recovery trouble must never stop the agent from starting.
set -u

AGENT_DIR="${1:-}"
[ -n "$AGENT_DIR" ] && [ -d "$AGENT_DIR" ] || exit 0

MARKER="$AGENT_DIR/update-pending"
BACKUP="$AGENT_DIR/update-backup"
MAX_ATTEMPTS=3

[ -f "$MARKER" ] || exit 0

attempts="$(cat "$MARKER" 2>/dev/null || echo "$MAX_ATTEMPTS")"
case "$attempts" in
  '' | *[!0-9]*) attempts="$MAX_ATTEMPTS" ;; # unreadable marker: recover now
esac

if [ "$attempts" -lt "$MAX_ATTEMPTS" ]; then
  echo "$((attempts + 1))" > "$MARKER"
  exit 0
fi

if [ -d "$BACKUP" ]; then
  echo "agent failed to start $attempts times since the last update; restoring the previous bundle" >&2
  for f in main.py trust.py control_auth.py delivery_reset.py; do
    if [ -f "$BACKUP/$f" ]; then
      cp -p "$BACKUP/$f" "$AGENT_DIR/$f"
    fi
  done
  if [ -d "$BACKUP/static" ]; then
    rm -rf "$AGENT_DIR/static"
    cp -rp "$BACKUP/static" "$AGENT_DIR/static"
  fi
  sync
fi

rm -f "$MARKER"
exit 0
