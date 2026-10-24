#!/usr/bin/env bash
# Sets up a Linux machine to run StellarDrive dedicated servers with sdsm:
#   Wine (and xvfb, unzip), the game from a zip of a Windows game folder (with MelonLoader and the mods),
#   sdsm beside it, and Wine's first start.
# Usage: bash install-linux.sh <StellarDrive.zip | game folder> [install dir, default ~/stellardrive]
set -euo pipefail

SRC="${1:-}"
DIR="${2:-$HOME/stellardrive}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -z "$SRC" ]; then echo "usage: bash install-linux.sh <StellarDrive.zip | game folder> [install dir]"; exit 2; fi

say() { printf '\n== %s\n' "$*"; }

say "Wine, xvfb and unzip"
if command -v wine >/dev/null 2>&1; then
  echo "Wine is there: $(wine --version)"
elif command -v apt-get >/dev/null 2>&1; then
  sudo apt-get update
  sudo apt-get install -y wine64 xvfb unzip || sudo apt-get install -y wine xvfb unzip
elif command -v dnf >/dev/null 2>&1; then
  sudo dnf install -y wine xorg-x11-server-Xvfb unzip
elif command -v pacman >/dev/null 2>&1; then
  sudo pacman -S --needed --noconfirm wine xorg-server-xvfb unzip
else
  echo "Install Wine with your system's package manager, then run this again."; exit 1
fi
command -v wine >/dev/null 2>&1 || { [ -x /usr/lib/wine/wine64 ] && sudo ln -sf /usr/lib/wine/wine64 /usr/local/bin/wine; }

say "The game -> $DIR/game"
mkdir -p "$DIR"
if [ -d "$SRC" ]; then
  cp -a "$SRC" "$DIR/game"
else
  rm -rf "$DIR/game.tmp" && mkdir -p "$DIR/game.tmp"
  unzip -q "$SRC" -d "$DIR/game.tmp"
  # the folder with StellarDrive.exe in it (the zip may or may not have a folder at the top)
  EXE="$(find "$DIR/game.tmp" -maxdepth 3 -name StellarDrive.exe | head -1)"
  [ -n "$EXE" ] || { echo "No StellarDrive.exe in $SRC"; exit 1; }
  rm -rf "$DIR/game" && mv "$(dirname "$EXE")" "$DIR/game" && rm -rf "$DIR/game.tmp"
fi
[ -f "$DIR/game/version.dll" ] && [ -d "$DIR/game/MelonLoader" ] || echo "!! No MelonLoader in the game folder: see LINUX.md (copy it from a Windows game folder that has it)."

say "sdsm -> $DIR/sdsm"
cp "$HERE/sdsm" "$DIR/sdsm" && chmod +x "$DIR/sdsm"
for d in "$HERE"/DedicatedServer-*.dll; do [ -f "$d" ] && cp "$d" "$DIR/"; done
[ -f "$HERE/StellarDriveServerList" ] && cp "$HERE/StellarDriveServerList" "$DIR/" && chmod +x "$DIR/StellarDriveServerList"

say "Wine's first start (a minute)"
WINEDEBUG=-all wineboot -i >/dev/null 2>&1 || true

say "Check"
"$DIR/sdsm" doctor --game "$DIR/game" || true
cat <<EOF

Next:
  cd $DIR
  ./sdsm new hub --game $DIR/game --port 7777 --title "My StellarDrive server"
  ./sdsm start hub
  ./sdsm console hub          (/quit leaves; the server keeps running)
  ./sdsm service hub          (to start it at boot with systemd)
EOF
