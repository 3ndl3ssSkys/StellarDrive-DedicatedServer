#!/usr/bin/env bash
# The container's start: the server is made from /game the first time (into /data/servers/<name>), then run.
# docker stop sends SIGTERM: sdsm saves and stops the server.
set -e
NAME="${SD_NAME:-hub}"
if ! sdsm list | grep -q "^$NAME "; then
  sdsm new "$NAME" --game /game --folder "/data/servers/$NAME" --port "${SD_PORT:-7777}" \
       --title "${SD_TITLE:-StellarDrive server}" --max "${SD_MAX:-8}" ${SD_PASSWORD:+--password "$SD_PASSWORD"}
fi
exec sdsm run "$NAME"
