r"""Test DedicatedServer with two real game processes: a server and a client that joins it over UDP.

    python _Mods/src/DedicatedServer/verify_server.py                     # the standard run
    python _Mods/src/DedicatedServer/verify_server.py "probe:DedicatedServer/joined" --timeout 300
    python _Mods/src/DedicatedServer/verify_server.py --mods DedicatedServer,GameTweaks,Chess,Graves,Gravity

How it runs (all through _Mods/verify.py and the shared harness, so the usual guards apply):
  * Two game copies under "StellarDrive Demo Instances": DedicatedServer-Client (the client, driven by
    DemoVerifyHarness) and DedicatedServer-Server (the server). Both get the mods in --mods.
  * The server's test data (UserData/DedicatedServer: world, settings, player data) is wiped first.
  * verify.py launches the client with SDD_VERIFY_SAVE=join:127.0.0.1:<port>. Instead of loading a
    world, the client starts the server copy (-batchmode -dedicated, no window, no console), waits
    for it to report ready, and joins it. The server stops itself when the client game exits.
  * --solo: it holds both test slots, because it is two games.

A far-away player: --latency 120 --jitter 30 --loss 1 puts a UDP relay between the two games that
holds every packet for half the round trip (+/- jitter) and drops that percentage, so the client
plays as if the server were across the internet. The client then joins the relay's port.

Exit codes are verify.py's: 0 pass, 1 checks failed, 2 not started (Ethan is playing, port busy, ...).
"""
import argparse
import heapq
import os
import random
import threading
import time
import shutil
import socket
import subprocess
import sys
from pathlib import Path

MODS_DIR = Path(__file__).resolve().parents[2]          # _Mods
sys.path.insert(0, str(MODS_DIR))
import verify  # noqa: E402  (make_instance, ethan_is_playing)

DEFAULT = ("probe:DedicatedServer/math,probe:DedicatedServer/joined,probe:DedicatedServer/playerlist,"
           "probe:DedicatedServer/walk,probe:DedicatedServer/commands,probe:DedicatedServer/stdin,"
           "probe:DedicatedServer/reject,probe:DedicatedServer/version,probe:DedicatedServer/ban,"
           "probe:DedicatedServer/stop,probe:DedicatedServer/serverlog")


def port_free(port):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.bind(("0.0.0.0", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


class Relay:
    """UDP between the client and the server, with internet-like delay, jitter and loss."""

    def __init__(self, listen_port, server_port, latency_ms, jitter_ms, loss_pct):
        self.server = ("127.0.0.1", server_port)
        self.half = latency_ms / 2000.0
        self.jitter = jitter_ms / 2000.0
        self.loss = loss_pct / 100.0
        self.front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.front.bind(("127.0.0.1", listen_port))
        self.front.settimeout(0.2)
        self.backs = {}                  # client address -> socket to the server
        self.queue = []                  # (due, seq, socket, data, address)
        self.seq = 0
        self.lock = threading.Lock()
        self.stop = False
        self.sent = self.dropped = 0

    def _later(self, sock, data, addr):
        if random.random() < self.loss:
            self.dropped += 1
            return
        due = time.perf_counter() + max(0.0, self.half + random.uniform(-self.jitter, self.jitter))
        with self.lock:
            self.seq += 1
            heapq.heappush(self.queue, (due, self.seq, sock, data, addr))

    def _from_clients(self):
        while not self.stop:
            try:
                data, addr = self.front.recvfrom(65535)
            except socket.timeout:
                continue
            except OSError:
                continue
            back = self.backs.get(addr)
            if back is None:
                back = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                back.bind(("127.0.0.1", 0))
                back.settimeout(0.2)
                self.backs[addr] = back
                threading.Thread(target=self._from_server, args=(back, addr), daemon=True).start()
            self._later(back, data, self.server)

    def _from_server(self, back, client):
        while not self.stop:
            try:
                data, _ = back.recvfrom(65535)
            except socket.timeout:
                continue
            except OSError:
                continue
            self._later(self.front, data, client)

    def _sender(self):
        while not self.stop:
            now = time.perf_counter()
            with self.lock:
                while self.queue and self.queue[0][0] <= now:
                    _, _, sock, data, addr = heapq.heappop(self.queue)
                    try:
                        sock.sendto(data, addr)
                        self.sent += 1
                    except OSError:
                        pass
            time.sleep(0.0005)

    def ready(self):
        try:
            import json
            return json.loads(self.local("TestServer/UserData/DedicatedServer/status.json").read_text(encoding="utf-8-sig")).get("state") == "ready"
        except Exception:
            return False

    def start(self):
        for f in (self._from_clients, self._sender):
            threading.Thread(target=f, daemon=True).start()

    def close(self):
        self.stop = True
        time.sleep(0.3)
        for sock in [self.front] + list(self.backs.values()):
            try:
                sock.close()
            except OSError:
                pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scenarios", nargs="?", default=DEFAULT)
    ap.add_argument("--mods", default="DedicatedServer", help="mods in BOTH games (comma separated)")
    ap.add_argument("--server-mods", default=None, help="mods in the server only, if different")
    ap.add_argument("--port", type=int, default=7790)
    ap.add_argument("--timeout", type=int, default=600)
    ap.add_argument("--server-args", default="", help="extra command line for the server")
    ap.add_argument("--latency", type=int, default=0, help="round trip to add, ms (a relay between client and server)")
    ap.add_argument("--jitter", type=int, default=0, help="+/- ms on each packet's delay")
    ap.add_argument("--loss", type=float, default=0.0, help="percent of packets dropped")
    ap.add_argument("--pair", default="DedicatedServer",
                    help="name of this run's two game copies (<pair>-Client, <pair>-Server). Keep the default: a second run on the same copies now waits for the first (verify.py locks each copy) instead of clobbering it.")
    ap.add_argument("--env", action="append", default=[], help="extra KEY=VALUE for the client game")
    ap.add_argument("--same-folder", action="store_true", help="run the server from the CLIENT's game copy (how a player runs both on one PC)")
    a = ap.parse_args()

    playing = verify.ethan_is_playing()
    if playing:
        verify.say("ABORTED - Ethan is playing StellarDrive (%s). Not starting anything." % playing[0])
        sys.exit(2)
    if not port_free(a.port):
        verify.say("ABORTED - UDP port %d is in use; pick another with --port" % a.port)
        sys.exit(2)

    server_mods = [verify.mod_name(m) for m in (a.server_mods or a.mods).split(",") if m.strip()]
    if a.same_folder:
        srv = verify.INSTANCES / (a.pair + "-Client")    # verify.py (re)builds it with --mods below
    else:
        srv = verify.make_instance(a.pair + "-Server", server_mods)
    if os.environ.get("SDD_DS_SERVER_RELEASE") == "1":      # the server runs the release DLL (what a public server has)
        rel = sorted(verify.OUT.glob("DedicatedServer-*.dll"))[-1]
        for old in (srv / "Mods").glob("DedicatedServer*.dll"):
            old.unlink()
        shutil.copy2(rel, srv / "Mods" / rel.name)
        verify.say("server mod: the release build %s" % rel.name)
    data = srv / "UserData" / "DedicatedServer"
    if data.exists():
        shutil.rmtree(data)
    for f in [srv / "UserData" / "Player.log"]:
        if f.exists():
            f.unlink()

    relay = None
    join_port = a.port
    if a.latency or a.jitter or a.loss:
        join_port = a.port + 1
        relay = Relay(join_port, a.port, a.latency, a.jitter, a.loss)
        relay.start()
        verify.say("relay on UDP %d -> %d: %d ms round trip, +/-%d ms jitter, %.1f%% loss" % (join_port, a.port, a.latency, a.jitter, a.loss))
    cmd = [sys.executable, str(MODS_DIR / "verify.py"), a.scenarios,
           "--instance", a.pair + "-Client", "--mods", a.mods,
           "--save", "join:127.0.0.1:%d" % join_port,
           "--env", "SDD_DS_SPAWN=%s" % srv,
           "--env", "SDD_DS_SERVER_ARGS=-port %d %s" % (a.port, a.server_args),
           "--env", "SDD_DS_RELAY=%s" % ("%d ms +/-%d, %.1f%% loss" % (a.latency, a.jitter, a.loss) if relay else ""),
           "--timeout", str(a.timeout), "--solo"]
    for kv in a.env:
        cmd += ["--env", kv]
    try:
        code = subprocess.run(cmd).returncode
    finally:
        if relay:
            relay.close()
            verify.say("relay passed %d packets, dropped %d" % (relay.sent, relay.dropped))
    verify.say("server copy: %s (log: MelonLoader/Latest.log, data: UserData/DedicatedServer)" % srv)
    sys.exit(code)


if __name__ == "__main__":
    main()
