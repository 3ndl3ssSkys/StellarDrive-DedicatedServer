# StellarDrive dedicated server on Linux (text only)

StellarDrive is a Windows game, so on Linux it runs under **Wine**. That works headless: the server starts with
no graphics device (`-batchmode -nographics`), so no desktop, GPU or monitor is needed. MelonLoader and the mods
come along inside the game folder; `sdsm` (the text Server Manager) makes the server folders, starts the game
under Wine with MelonLoader switched on, keeps it running, and gives you its console, settings, mods and saves.

Two ways, pick one:

* **A. Straight on the machine** (Ubuntu/Debian, Fedora, Arch...): install Wine, copy the game over, run `sdsm`.
* **B. In Docker**: everything (Wine, sdsm) is in the image; you only bring the game folder.

## What you need from Windows: the game folder

The easiest and surest way to get the game, MelonLoader and the mods onto Linux is to **copy the game folder
from a Windows PC where the dedicated server already works** (it has `StellarDrive.exe`, `StellarDrive_Data/`,
`MelonLoader/`, `version.dll` and `Mods/` with `DedicatedServer-1.5.2.dll` in it). Zip it on Windows and copy it
over (scp, sftp, a USB stick...):

```
scp StellarDrive.zip user@server:~/
```

On the Linux machine:

```
mkdir -p ~/stellardrive && cd ~/stellardrive
unzip ~/StellarDrive.zip -d game          # the folder with StellarDrive.exe in it is ~/stellardrive/game
```

(No Windows PC? Install MelonLoader 0.7.x into the game folder by hand: unzip `MelonLoader.x64.zip` from
https://github.com/LavaGang/MelonLoader/releases into the folder that has `StellarDrive.exe`, then put
`DedicatedServer-1.5.2.dll` in `Mods/`. If you own the game on Steam, SteamCMD can download the Windows build:
`steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir ~/stellardrive/game +login <you> +app_update <app id> +quit`.)

## A. Straight on the machine

```
# 1. Wine (64-bit is enough). Ubuntu/Debian:
sudo apt update && sudo apt install -y wine64 xvfb unzip      # xvfb is needed too (older Ubuntu: wine instead of wine64)
#    Fedora: sudo dnf install wine xorg-x11-server-Xvfb      Arch: sudo pacman -S wine xorg-server-xvfb

# 2. sdsm (one file, nothing else to install)
cp sdsm ~/stellardrive/ && chmod +x ~/stellardrive/sdsm
cd ~/stellardrive

# 3. Wine's first start makes its folder (~/.wine) - once, a minute or so
WINEDEBUG=-all wineboot -i

# 4. Check, make a server, start it
./sdsm doctor --game ~/stellardrive/game
./sdsm new hub --game ~/stellardrive/game --port 7777 --title "My StellarDrive server" --max 16
./sdsm start hub
./sdsm status hub
```

`install-linux.sh` (next to this file) does steps 1-3 for you: `bash install-linux.sh ~/StellarDrive.zip`.

Under Wine, Unity wants a window even in batch mode ("Failed to create batch mode window" otherwise), so on a
machine with no display sdsm runs the game under `xvfb-run` (a virtual display, nothing shown anywhere) by itself:
keep `xvfb` installed. (`--xvfb` / `--no-xvfb` / `--auto-xvfb` choose for good.) If the game doesn't start,
`sdsm logs hub`, and `UserData/DedicatedServer/logs/game-output.log` and `logs/sdsm.log` in the server's folder,
say why.

### Every day

```
./sdsm list                       # the servers and how each is
./sdsm console hub                # its log as it happens; type commands (help, say, kick, ban...); /quit leaves
./sdsm cmd hub players            # one command and its reply
./sdsm config hub                 # the settings; ./sdsm config hub MaxPlayers 24 changes one
./sdsm stop hub 5 maintenance     # warn the players, stop in 5 minutes (saves first)
./sdsm restart hub
./sdsm mods hub                   # mods on the server; mods hub add NewMod.dll / remove / on / off
./sdsm saves hub backup           # a backup of the world now
./sdsm logs hub -f                # follow the log
```

### Starting at boot (systemd)

```
./sdsm service hub                # writes stellardrive-hub.service and prints the commands:
sudo cp ~/.config/StellarDrive\ Server\ Manager/stellardrive-hub.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now stellardrive-hub
```

`systemctl stop stellardrive-hub` saves and stops it; a restart asked for in the game (or a planned restart)
starts it again by itself; a crash too (five crashes in ten minutes and it gives up, so it can't loop).

### Letting players in

Players join with this machine's address and the server's port (UDP), e.g. `203.0.113.5:7777`. Open the port in
the firewall (`sudo ufw allow 7777/udp`), or use playit.gg (it has a Linux agent) and give players the playit
address. The DedicatedServer mod's network guard checks that address the same way as on Windows.

## B. In Docker

```
docker build -t stellardrive-server .                     # in this folder (Dockerfile, sdsm, entrypoint.sh)
docker run -d --name sd-hub -p 7777:7777/udp \
  -v ~/stellardrive/game:/game:ro -v ~/stellardrive/data:/data \
  -e SD_NAME=hub -e SD_PORT=7777 -e SD_TITLE="My StellarDrive server" stellardrive-server
docker exec -it sd-hub sdsm console hub                     # the console (and every other sdsm command)
```

The game folder is read-only in the container; the server's own copy, its world, settings and logs are in
`~/stellardrive/data`, so they survive a new image. `docker stop sd-hub` saves and stops the server.

## Notes

* The server's files are the same as on Windows, so a server folder made by the Windows Server Manager can be
  copied over and added with `./sdsm add <name> <folder>` (and back again).
* Paths the game writes (the world in `status.json`) look like `Z:\home\...` - that's Wine's name for `/home/...`.
* The Server List (StellarDriveServerList, for the Join page's server list) has a Linux build too: one file, no
  Wine needed: `./StellarDriveServerList` (it writes `serverlist.json` beside itself; `help` lists its commands).
