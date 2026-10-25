#!/usr/bin/env bash
# CI helper: runs a command in a D-Bus session with an unlocked login keyring (gnome-keyring), as
# on a desktop. Run it as an ordinary user: the keyring daemon won't start as root in a container.
set -euo pipefail
exec dbus-run-session -- bash -c 'printf "ci-keyring" | gnome-keyring-daemon --unlock --components=secrets > /dev/null; exec "$@"' bash "$@"
