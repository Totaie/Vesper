#!/usr/bin/env bash
# CI smoke test for the AppImage, run under Xvfb with a D-Bus session and an unlocked keyring.
# Containers have no FUSE, so the runtime extracts itself instead of mounting. Vesper must start,
# stay up and draw its home window. Screenshots go to $2.
set -euo pipefail
image="$1"; shots="$2"
mkdir -p "$shots"
export XDG_CONFIG_HOME="$(mktemp -d)" APPIMAGE_EXTRACT_AND_RUN=1
"$image" &
pid=$!
sleep 20
if ! kill -0 "$pid" 2>/dev/null; then
    echo "Vesper exited during start-up" >&2
    exit 1
fi
import -window root "$shots/home.png"
[ "$(stat -c '%a' "$XDG_CONFIG_HOME/vesper")" = "700" ]
kill "$pid"
wait "$pid" || true
