#!/usr/bin/env bash
# CI smoke test for the installed package, run under Xvfb with a D-Bus session and an unlocked
# keyring: Vesper must start, stay up, and draw its home window. Screenshots go to $1.
set -euo pipefail
shots="$1"
mkdir -p "$shots"
export XDG_CONFIG_HOME="$(mktemp -d)"
/usr/bin/vesper &
pid=$!
sleep 15
if ! kill -0 "$pid" 2>/dev/null; then
    echo "Vesper exited during start-up" >&2
    exit 1
fi
import -window root "$shots/home.png"
test -d "$XDG_CONFIG_HOME/vesper"
stat -c '%a %n' "$XDG_CONFIG_HOME/vesper"
[ "$(stat -c '%a' "$XDG_CONFIG_HOME/vesper")" = "700" ]
kill "$pid"
wait "$pid" || true
