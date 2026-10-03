#!/bin/sh
# Postarr container start-up: run the app as an unprivileged user, never as root.
#  - Started as root (the default): make /config belong to PUID:PGID (default 1654 = the .NET image's built-in
#    "app" user), then drop to that user. Set PUID/PGID to match your host user if you want the files to be yours.
#  - Started with --user / compose "user:": just run as that user (it must be able to write to /config).
set -e
PUID="${PUID:-1654}"
PGID="${PGID:-1654}"
export HOME=/tmp
if [ "$(id -u)" = "0" ]; then
  mkdir -p /config
  if [ "$(stat -c %u /config)" != "$PUID" ] || [ "$(stat -c %g /config)" != "$PGID" ]; then
    chown -R "$PUID:$PGID" /config || echo "[postarr] warning: couldn't change the owner of /config"
  fi
  exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet /app/Postarr.dll "$@"
fi
exec dotnet /app/Postarr.dll "$@"
