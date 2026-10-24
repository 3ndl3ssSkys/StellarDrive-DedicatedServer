// Checks run by the verify harness in the CLIENT game (probe:DedicatedServer/<name>).
// The client starts a real server process (another copy of the game, -batchmode -dedicated),
// joins it over UDP like a player would, and then checks the server from outside: through
// its commands file (as a script would), its status.json and server-data.json, its log,
// and the in-game chat (as a player would).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet;
using FishNet.Managing;
using HarmonyLib;
using Managers.Interface.Services;
using Managers.Server.Services;
using Newtonsoft.Json.Linq;
using Players.Services;
using UI.MainMenu;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DedicatedServer
{
    internal static partial class Probe
    {
        private static string _srvDir, _address;
        private static Process _srv;
        private static readonly List<string> _sys = new List<string>();

        public static void InitClient(HarmonyLib.Harmony h)
        {
            if (Environment.GetEnvironmentVariable("SDD_VERIFY") != "1") return;
            try
            {
                Type pred = typeof(Players.Network.LocalPlayerPhysicsPredictions);
                h.Patch(AccessTools.Method(pred, "Reconcile"), prefix: new HarmonyMethod(typeof(Probe).GetMethod("ReconcilePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(pred, "CorrectPrediction"), postfix: new HarmonyMethod(typeof(Probe).GetMethod("CorrectPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("probe: could not watch the prediction: " + e.Message); }
            try
            {
                MethodInfo writer = typeof(Players.Network.PlayerNetworkNameAuth).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .First(m => m.Name.StartsWith("RpcWriter___Server_AuthenticatePlayer_ServerRpc"));
                h.Patch(writer, prefix: new HarmonyMethod(typeof(Probe).GetMethod("FakeVersionPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("probe: could not hook the game's version: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(UI.Chat.ChatMenu), "ReceiveSystemMessage"),
                    prefix: new HarmonyMethod(typeof(Probe).GetMethod("SysPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("probe: could not watch chat: " + e.Message); }
        }

        private static void SysPrefix(string systemMessage)
        {
            lock (_sys) _sys.Add(systemMessage ?? "");
        }

        private static string ReadAllShared(string f) { try { return string.Join("\n", ReadShared(f)); } catch { return ""; } }

        private static string SrvData { get { return Path.Combine(_srvDir, "UserData", "DedicatedServer"); } }

        // ---- start the server, then join it ------------------------------------------------

        // ---- a test server on another PC (SDD_DS_REMOTE=<shared folder>, run by the Server Manager there) ----------
        // The folder holds TestServer (the server's game copy: its logs, status.json and commands.txt are read and written as
        // here) and control: start.json (asks for a start), state.json (the Manager's answer: pid, running/exited, exit code),
        // heartbeat.txt (touched every few seconds; the server stops 90 s after it goes quiet), stdin.txt (lines for its console).
        private static string _remote;
        private static int _remotePid;
        private static string RemoteControl(string f) { return Path.Combine(_remote, "control", f); }

        private static JObject RemoteState()
        {
            try { return JObject.Parse(string.Join("\n", ReadShared(RemoteControl("state.json")))); } catch { return null; }
        }

        private static bool ServerGone()
        {
            if (_srv != null) return _srv.HasExited;
            JObject s = _remote != null ? RemoteState() : null;
            return s != null && (int)s["pid"] == _remotePid && (string)s["state"] == "exited";
        }

        private static string ServerExit()
        {
            if (_srv != null) return _srv.HasExited ? "exit code " + _srv.ExitCode : "running";
            JObject s = RemoteState();
            return s != null && (string)s["state"] == "exited" ? "exit code " + s["exitCode"] : "running";
        }

        private static bool HaveServer { get { return _srv != null || _remotePid != 0; } }

        private static IEnumerator Heartbeat()
        {
            while (true)
            {
                try { File.WriteAllText(RemoteControl("heartbeat.txt"), DateTime.UtcNow.ToString("o")); } catch { }
                yield return new WaitForSecondsRealtime(5f);
            }
        }

        private static IEnumerator StartRemote(Action<string> note)
        {
            _srvDir = Path.Combine(_remote, "TestServer");
            Directory.CreateDirectory(Path.Combine(_remote, "control"));
            MelonLoader.MelonCoroutines.Start(Heartbeat());
            string nonce = Guid.NewGuid().ToString("N");
            JObject ask = new JObject { ["nonce"] = nonce, ["args"] = Environment.GetEnvironmentVariable("SDD_DS_SERVER_ARGS") ?? "",
                ["quitAfter"] = Environment.GetEnvironmentVariable("SDD_DS_SERVER_SECONDS") ?? "900", ["at"] = DateTime.UtcNow.ToString("o") };
            File.WriteAllText(RemoteControl("start.json"), ask.ToString());
            note("asked the Server Manager on the other PC for a test server (" + _remote + ")");
            float until = Time.realtimeSinceStartup + 600f;          // the first time it makes its game copy
            JObject st = null, rs = null;
            while (Time.realtimeSinceStartup < until)
            {
                rs = RemoteState();
                if (rs != null && (string)rs["nonce"] == nonce)
                {
                    _remotePid = (int)rs["pid"];
                    if ((string)rs["state"] == "exited" || (string)rs["state"] == "failed") break;
                    st = Status();
                    if (st != null && (int)st["pid"] == _remotePid && ((string)st["state"] == "ready" || (string)st["state"] == "failed")) break;
                }
                yield return new WaitForSecondsRealtime(1f);
            }
            if (st == null || (string)st["state"] != "ready")
            {
                note("the remote test server did not come up: " + (rs != null ? rs.ToString(Newtonsoft.Json.Formatting.None) : "no answer from the Server Manager (is its test server switched on, and the folder shared?)"));
                _remotePid = 0;
                yield break;
            }
            note("remote test server ready (pid " + _remotePid + " on the other PC) on port " + st["port"]);
        }

        public static IEnumerator Join(string address, Action<string> note)
        {
            _address = address;
            _remote = Environment.GetEnvironmentVariable("SDD_DS_REMOTE");
            if (string.IsNullOrEmpty(_remote)) _remote = null;
            _srvDir = Environment.GetEnvironmentVariable("SDD_DS_SPAWN");
            if (_remote != null)
            {
                yield return StartRemote(note);
                if (_remotePid == 0) yield break;
            }
            else if (!string.IsNullOrEmpty(_srvDir))
            {
                string exe = Path.Combine(_srvDir, "StellarDrive.exe");
                string log = Path.Combine(_srvDir, "UserData", "Player.log");
                string extra = Environment.GetEnvironmentVariable("SDD_DS_SERVER_ARGS") ?? "";
                ProcessStartInfo psi = new ProcessStartInfo(exe,
                    "-batchmode -nographics -dedicated --melonloader.hideconsole -logFile \"" + log + "\" " + extra)
                { UseShellExecute = false, WorkingDirectory = _srvDir, RedirectStandardInput = true, CreateNoWindow = true };
                foreach (string k in psi.EnvironmentVariables.Keys.Cast<string>().ToList())
                    if (k.StartsWith("SDD_VERIFY", StringComparison.OrdinalIgnoreCase)) psi.EnvironmentVariables.Remove(k);
                psi.EnvironmentVariables["SDD_DS_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString();
                psi.EnvironmentVariables["SDD_DS_QUIT_AFTER"] = Environment.GetEnvironmentVariable("SDD_DS_SERVER_SECONDS") ?? "900";      // longer for a soak
                _srv = Process.Start(psi);
                note("started the server: pid " + _srv.Id + ", " + exe + " " + psi.Arguments);
                note("process ledger: " + TestLedger.Register(_srv.Id, Process.GetCurrentProcess().Id, "DedicatedServer test server (started by the test client)"));
                float until = Time.realtimeSinceStartup + 180f;
                JObject st = null;
                while (Time.realtimeSinceStartup < until)
                {
                    st = Status();
                    if (st != null && (int)st["pid"] == _srv.Id && ((string)st["state"] == "ready" || (string)st["state"] == "failed")) break;
                    if (_srv.HasExited) break;
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                if (st == null || (string)st["state"] != "ready")
                {
                    note("the server did not come up: " + (st != null ? (string)st["state"] : "no status.json") + (_srv.HasExited ? ", exited with " + _srv.ExitCode : ""));
                    yield break;
                }
                note("server ready on port " + st["port"] + " after " + (180f - (until - Time.realtimeSinceStartup)).ToString("0") + " s");
            }
            string err = Client.Join(address);
            if (err != null) note("join failed to start: " + err);
        }

        // ---- probes --------------------------------------------------------------------------

        public static IEnumerator Run(string probe, Action<string, bool, string> check, Action<string> note)
        {
            switch (probe)
            {
                case "joined": return Joined(check, note);
                case "commands": return CommandsProbe(check, note);
                case "stdin": return Stdin(check, note);
                case "reject": return Reject(check, note);
                case "ban": return BanProbe(check, note);
                case "stop": return StopProbe(check, note);
                case "serverlog": return ServerLog(check, note);
                case "math": return Math(check, note);
                case "perf": return Perf(check, note);
                case "menudump": return MenuDump(check, note);
                case "walk": return Walk(check, note, false);
                case "playerlist": return PlayerList(check, note);
                case "version": return VersionProbe(check, note);
                case "walkfar": return Walk(check, note, true);
                default: return Run2(probe, check, note);
            }
        }

        private static IEnumerator Joined(Action<string, bool, string> check, Action<string> note)
        {
            NetworkManager nm = InstanceFinder.NetworkManager;
            check("this game is a client only (not a host)", nm.ClientManager.Started && !nm.ServerManager.Started,
                "client " + nm.ClientManager.Started + ", server " + nm.ServerManager.Started);
            check("the server let us in", Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted,
                Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict");
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            check("our player spawned in the server's world", cp != null && cp.LocalPlayer != null, cp != null && cp.LocalPlayer != null ? cp.LocalPlayer.Name : "none");
            yield return new WaitForSecondsRealtime(6f);
            JObject st = Status();
            JArray ps = st != null ? (JArray)st["players"] : null;
            check("the server lists us as its one player", ps != null && ps.Count == 1 && cp.LocalPlayer != null && (string)ps[0]["id"] == cp.LocalPlayer.SteamUserId.ToString(),
                ps != null ? ps.ToString(Newtonsoft.Json.Formatting.None) : "no status");
            if (st != null)
            {
                check("the server has no local player of its own", !(bool)st["localClient"], "localClient " + st["localClient"]);
                check("the server draws nothing (every camera off)", (int)st["enabledCameras"] == 0, st["enabledCameras"] + " enabled, " + st["camerasTurnedOff"] + " turned off");
                note("server loop: " + st["avgFrameMs"] + " ms avg, " + st["maxFrameMs"] + " ms worst, target " + st["targetFrameRate"] + " fps, tick rate " + st["tickRate"]);
                check("the server loop runs at its target rate (so it adds at most one loop of delay)", (double)st["avgFrameMs"] > 0 && (double)st["avgFrameMs"] < 1000.0 / (int)st["targetFrameRate"] * 1.6,
                    st["avgFrameMs"] + " ms per loop, target " + (1000.0 / (int)st["targetFrameRate"]).ToString("0.0") + " ms");
            }
            long rtt = nm.TimeManager.RoundTripTime;
            note("round trip to the server (localhost): " + rtt + " ms");
            string relay = Environment.GetEnvironmentVariable("SDD_DS_RELAY");
            if (string.IsNullOrEmpty(relay)) check("round trip on this machine is small", rtt >= 0 && rtt < 60, rtt + " ms");
            else note("joined through a relay (" + relay + "): round trip " + rtt + " ms");
            JObject data = DataFile();
            JToken me = data != null && cp.LocalPlayer != null ? data["Players"].FirstOrDefault(p => (string)p["Id"] == cp.LocalPlayer.SteamUserId.ToString()) : null;
            check("server-data.json recorded the visit", me != null && (int)me["Visits"] >= 1, me != null ? me.ToString(Newtonsoft.Json.Formatting.None) : "not found");
            JToken open = data != null && cp.LocalPlayer != null ? data["Sessions"].LastOrDefault(s => (string)s["Id"] == cp.LocalPlayer.SteamUserId.ToString()) : null;
            check("the visit is open while we're on", open != null && open["End"].Type == JTokenType.Null, open != null ? open.ToString(Newtonsoft.Json.Formatting.None) : "none");
        }

        private static IEnumerator CommandsProbe(Action<string, bool, string> check, Action<string> note)
        {
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            string id = cp.LocalPlayer.SteamUserId.ToString();
            CheckWrap w = new CheckWrap();

            yield return Console("status", "Players: 1/", w);
            check("console: status", w.Ok, w.Detail);

            yield return Chat("/save", "Only operators", w);
            check("chat: /save refused before we are an operator", w.Ok, w.Detail);

            yield return Console("op " + id, "is now an operator", w);
            check("console: op <id>", w.Ok, w.Detail);
            yield return WaitSys("You are now an operator", 10f, w);
            check("we are told in chat that we are an operator", w.Ok, w.Detail);

            yield return Chat("/whoami", "an operator", w);
            check("chat: /whoami says operator", w.Ok, w.Detail);

            yield return Chat("/set Motd Hello from the test", "Motd = \"Hello from the test\"", w);
            check("chat: /set Motd (an operator changing a setting in game)", w.Ok, w.Detail);
            string cfg = "";
            for (float until = Time.realtimeSinceStartup + 5f; Time.realtimeSinceStartup < until; )      // (a server on another PC: its files arrive a moment later)
            {
                cfg = File.Exists(Path.Combine(SrvData, "dedicatedserver.conf")) ? ReadAllShared(Path.Combine(SrvData, "dedicatedserver.conf")) : "";
                if (cfg.Contains("Motd = \"Hello from the test\"")) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            check("the setting is saved to dedicatedserver.conf", cfg.Contains("Motd = \"Hello from the test\""), cfg.Contains("Motd") ? cfg.Split('\n').First(l => l.Contains("Motd =")).Trim() : "no Motd line");

            yield return Chat("/set FrameRate abc", "expected a whole number", w);
            check("chat: /set refuses a bad value", w.Ok, w.Detail);

            yield return Chat("/players", "1/", w);
            check("chat: /players", w.Ok, w.Detail);

            yield return Chat("/stats", "Last 7 days", w);
            check("chat: /stats (activity + uptime)", w.Ok, w.Detail);

            JObject st = Status();
            string world = st != null ? (string)st["world"] : "";
            DateTime before = File.Exists(world) ? File.GetLastWriteTimeUtc(world) : DateTime.MinValue;
            yield return Chat("/save", "Saved.", w);
            DateTime after = File.Exists(world) ? File.GetLastWriteTimeUtc(world) : DateTime.MinValue;
            check("chat: /save writes the world file", w.Ok && after > before, w.Detail + "; world.json " + before.ToString("HH:mm:ss.fff") + " -> " + after.ToString("HH:mm:ss.fff"));

            yield return Console("deop " + id, "no longer an operator", w);
            check("console: deop", w.Ok, w.Detail);
            yield return Chat("/set Motd x", "Only operators", w);
            check("chat: /set refused again after deop", w.Ok, w.Detail);

            yield return Chat("/nosuchcommand", "Unknown command", w);
            check("chat: an unknown command gets an answer", w.Ok, w.Detail);
        }

        private static IEnumerator Stdin(Action<string, bool, string> check, Action<string> note)
        {
            if (!HaveServer) { check("stdin", false, "no server process of ours"); yield break; }
            int from = SysCount();
            if (_srv != null) { _srv.StandardInput.WriteLine("say typed into the server's standard input"); _srv.StandardInput.Flush(); }
            else File.AppendAllText(RemoteControl("stdin.txt"), "say typed into the server's standard input" + Environment.NewLine);   // the Manager there types it in
            CheckWrap w = new CheckWrap();
            yield return WaitSys("[Server] typed into the server's standard input", 10f, w, from);
            check("a command typed into the server's input reaches players (say)", w.Ok, w.Detail);
        }

        private static IEnumerator Reject(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            int savesBefore = Status() != null ? (int)Status()["saves"] : 0;
            yield return Console("set RequiredMods DedicatedServer, NoSuchMod", "RequiredMods = \"DedicatedServer, NoSuchMod\"", w);
            check("console: set RequiredMods", w.Ok, w.Detail);

            yield return Leave();
            yield return new WaitForSecondsRealtime(4f);
            JObject st = Status();
            check("the server sees nobody on after we left", st != null && ((JArray)st["players"]).Count == 0, st != null ? st["players"].ToString(Newtonsoft.Json.Formatting.None) : "no status");
            check("the server saved when we left", st != null && (int)st["saves"] > savesBefore, "saves " + savesBefore + " -> " + (st != null ? st["saves"] : "?"));
            double t1 = st != null ? (double)st["worldTime"] : -1;
            yield return new WaitForSecondsRealtime(6f);
            double t2 = Status() != null ? (double)Status()["worldTime"] : -2;
            check("with nobody on, the world clock stands still (PauseWhenEmpty)", t1 >= 0 && System.Math.Abs(t2 - t1) < 0.001, "world time " + t1.ToString("0.000") + " -> " + t2.ToString("0.000") + " over 6 s");

            int seen = Client.VerdictsSeen;
            Client.Join(_address);
            float until = Time.realtimeSinceStartup + 40f;
            while (Client.VerdictsSeen == seen && Time.realtimeSinceStartup < until) yield return null;
            bool rejected = Client.LastVerdict.HasValue && !Client.LastVerdict.Value.Accepted;
            check("a client missing a required mod is turned away", rejected, Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict within 40 s");
            check("...and told which mod", rejected && (Client.LastVerdict.Value.Missing ?? "").Contains("NoSuchMod"), Client.LastVerdict.HasValue ? (Client.LastVerdict.Value.Missing ?? "").Replace("\t", " ").Replace("\n", "; ") : "");
            until = Time.realtimeSinceStartup + 30f;
            while ((SceneManager.GetActiveScene().name == "MainGame" || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            check("...and is back on the main menu without a player", SceneManager.GetActiveScene().name != "MainGame" && !ClientStarted(), "scene " + SceneManager.GetActiveScene().name + ", client " + ClientStarted());
            yield return new WaitForSecondsRealtime(2f);
            Shot("rejected");

            // the game's own disconnect popup, saying why
            check("the menu's popup says why we were turned away", (Client.RejectionPopupText ?? "").Contains("NoSuchMod"), Flat(Client.RejectionPopupText ?? "no popup text"));

            // the Join Game page with the address box on it
            yield return OpenJoinPage(note);
            float boxUntil = Time.realtimeSinceStartup + 5f;
            while (!ClientUi.PanelPresent() && Time.realtimeSinceStartup < boxUntil) yield return null;
            RectTransform panel = ClientUi.Panel();
            string where = "";
            if (panel != null)
            {
                Vector3[] c = new Vector3[4];
                panel.GetWorldCorners(c);
                where = "panel from (" + c[0].x.ToString("0") + "," + c[0].y.ToString("0") + ") to (" + c[2].x.ToString("0") + "," + c[2].y.ToString("0") + ") on " + Screen.width + "x" + Screen.height;
            }
            check("the Join Game page has the 'Join by address' box", ClientUi.PanelPresent(), ClientUi.BuildError ?? where);
            if (panel != null)
            {
                Vector3[] c = new Vector3[4];
                panel.GetWorldCorners(c);
                check("...and it is on screen", c[0].x >= 0 && c[0].y >= 0 && c[2].x <= Screen.width && c[2].y <= Screen.height, where);
            }
            yield return new WaitForSecondsRealtime(1f);
            Shot("join-page");

            yield return Console("set RequiredMods auto", "RequiredMods = \"auto\"", w);
            seen = Client.VerdictsSeen;
            string shownName = ClientUi.NameShown;
            check("the Name box is filled in", !string.IsNullOrEmpty(shownName), "shows '" + shownName + "'");
            ClientUi.TypeName("  Test <b>Pilot</b>  ");
            bool typed = ClientUi.TypeAndJoin(_address);    // the box, as a player uses it: type, Enter
            check("joining by typing the address into the box and pressing Enter", typed, typed ? _address : "no box to type into");
            if (!typed) Client.Join(_address);
            until = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < until)
            {
                cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
                if (Client.VerdictsSeen > seen && cp != null && cp.LocalPlayer != null) break;
                yield return null;
            }
            check("with the mods matching again, the same client is let back in", Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted && cp != null && cp.LocalPlayer != null,
                (Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict") + "; player " + (cp != null && cp.LocalPlayer != null));
            yield return WaitSys("Hello from the test", 10f, w);
            check("the message of the day set in game is sent on joining", w.Ok, w.Detail);
            yield return new WaitForSecondsRealtime(3f);
            JObject stn = Status();
            string listed = stn != null && ((JArray)stn["players"]).Count > 0 ? (string)stn["players"][0]["name"] : "";
            check("the name typed in the Name box is the name on the server (tags stripped)", listed == "Test bPilot/b", "server lists '" + listed + "'");
            check("...and in our own player list", cp != null && cp.LocalPlayer != null && cp.LocalPlayer.Name == listed, cp != null && cp.LocalPlayer != null ? cp.LocalPlayer.Name : "no player");
            Client.PlayerName.Value = "";      // back to the default for later probes
            yield return new WaitForSecondsRealtime(4f);
            // status.json is written every 5 s: compare two writes, not two reads of one
            string w3 = Status() != null ? (string)Status()["writtenUtc"] : "";
            double t3 = Status() != null ? (double)Status()["worldTime"] : -3;
            float until2 = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < until2 && (Status() == null || (string)Status()["writtenUtc"] == w3)) yield return new WaitForSecondsRealtime(0.25f);
            double t4 = Status() != null ? (double)Status()["worldTime"] : -4;
            check("with a player on, the world clock runs at normal speed again", t4 - t3 > 2.0 && t4 - t3 < 9.0, "world time +" + (t4 - t3).ToString("0.00") + " between two status writes (5 s apart)");
        }

        private static IEnumerator BanProbe(Action<string, bool, string> check, Action<string> note)
        {
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            string id = cp.LocalPlayer.SteamUserId.ToString();
            CheckWrap w = new CheckWrap();
            int seen = Client.VerdictsSeen;
            yield return Console("ban " + id + " testing bans", "Banned", w);
            check("console: ban <id>", w.Ok, w.Detail);
            float until = Time.realtimeSinceStartup + 20f;
            while ((Client.VerdictsSeen == seen || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
            check("a banned player on the server is kicked, and told why", !ClientStarted() && Client.LastVerdict.HasValue && !Client.LastVerdict.Value.Accepted && Client.LastVerdict.Value.Message.Contains("testing bans"),
                (Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict") + ", client " + ClientStarted());
            until = Time.realtimeSinceStartup + 20f;
            while (SceneManager.GetActiveScene().name == "MainGame" && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(2f);

            seen = Client.VerdictsSeen;
            Client.Join(_address);
            until = Time.realtimeSinceStartup + 40f;
            while (Client.VerdictsSeen == seen && Time.realtimeSinceStartup < until) yield return null;
            check("a banned player can't join again", Client.LastVerdict.HasValue && !Client.LastVerdict.Value.Accepted && Client.LastVerdict.Value.Message.Contains("banned"),
                Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict");
            until = Time.realtimeSinceStartup + 20f;
            while ((SceneManager.GetActiveScene().name == "MainGame" || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(2f);

            yield return Console("unban " + id, "Unbanned", w);
            check("console: unban", w.Ok, w.Detail);
            seen = Client.VerdictsSeen;
            Client.Join(_address);
            until = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < until)
            {
                cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
                if (Client.VerdictsSeen > seen && cp != null && cp.LocalPlayer != null) break;
                yield return null;
            }
            check("unbanned, the player is let in again", Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted && cp != null && cp.LocalPlayer != null,
                Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict");
            JObject data = DataFile();
            int visits = 0;
            JToken me = data != null ? data["Players"].FirstOrDefault(p => (string)p["Id"] == id) : null;
            if (me != null) visits = (int)me["Visits"];
            note("visits recorded for us so far: " + visits);
        }

        private static IEnumerator StopProbe(Action<string, bool, string> check, Action<string> note)
        {
            if (!HaveServer) { check("stop", false, "no server process of ours"); yield break; }
            CheckWrap w = new CheckWrap();
            yield return Chat("/stats", "Last 24 hours", w);
            check("chat: /stats before stopping", w.Ok, w.Detail);
            note("stats as a player sees them: " + string.Join(" | ", LastSys(8)));
            yield return Console("stop", "Saving and stopping", w);
            check("console: stop", w.Ok, w.Detail);
            float until = Time.realtimeSinceStartup + 60f;
            while (!ServerGone() && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.25f);
            check("the server process exits by itself", ServerGone(), ServerGone() ? ServerExit() : "still running after 60 s");
            JObject st = Status();
            check("status.json says stopped", st != null && (string)st["state"] == "stopped", st != null ? (string)st["state"] : "none");
            JObject data = DataFile();
            JToken lastRun = data != null ? ((JArray)data["Runs"]).LastOrDefault() : null;
            check("the run is recorded as a clean stop", lastRun != null && (bool)lastRun["Clean"], lastRun != null ? lastRun.ToString(Newtonsoft.Json.Formatting.None) : "none");
            bool allClosed = data != null && data["Sessions"].All(s => s["End"].Type != JTokenType.Null);
            check("every visit is closed", allClosed, data != null ? data["Sessions"].Count() + " visit(s)" : "no data");
            until = Time.realtimeSinceStartup + 20f;
            while (ClientStarted() && Time.realtimeSinceStartup < until) yield return null;
            check("the client was disconnected", !ClientStarted(), "client " + ClientStarted());
        }

        private static IEnumerator ServerLog(Action<string, bool, string> check, Action<string> note)
        {
            if (HaveServer && !ServerGone())
            {
                float until = Time.realtimeSinceStartup + 30f;
                while (!ServerGone() && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
            }
            string path = Path.Combine(_srvDir ?? "", "MelonLoader", "Latest.log");
            if (!File.Exists(path)) { check("server log", false, "no " + path); yield break; }
            string[] lines = ReadShared(path);
            string[] errors = lines.Where(l => l.Contains("[ERROR]") || l.Contains("Failed to patch") || l.Contains("HarmonyException")).ToArray();
            check("the server log has no errors", errors.Length == 0, errors.Length + " error line(s)" + (errors.Length > 0 ? ": " + string.Join(" | ", errors.Take(6).ToArray()) : ""));
            check("the server log shows it was ready", lines.Any(l => l.Contains("READY -")), lines.FirstOrDefault(l => l.Contains("READY -")) ?? "no READY line");
            string unity = Path.Combine(_srvDir ?? "", "UserData", "Player.log");
            if (File.Exists(unity))
            {
                string[] u = ReadShared(unity);
                string[] ex = u.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\w*(Exception|Error)\b|Exception:")).ToArray();
                note("server Unity log: " + u.Length + " lines, " + ex.Length + " exception/error line(s)" + (ex.Length > 0 ? ": " + string.Join(" | ", ex.Distinct().Take(8).ToArray()) : ""));
                check("the server's Unity log has no exceptions", ex.Length == 0, ex.Length + " line(s)");
            }
            foreach (string l in lines.Where(l => l.Contains("[DedicatedServer]")).Take(60)) note("server: " + l.Trim());
        }

        /// <summary>Survey: what the main menu is made of (where the Join page lives).</summary>
        private static IEnumerator MenuDump(Action<string, bool, string> check, Action<string> note)
        {
            yield return Leave();
            yield return new WaitForSecondsRealtime(4f);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                note("scene " + sc.name + " loaded=" + sc.isLoaded + " roots=" + sc.rootCount);
            }
            int n = 0;
            foreach (MonoBehaviour b in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (b == null) continue;
                string ns = b.GetType().Namespace ?? "";
                string path = b.name;
                for (Transform t = b.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                bool inScene = b.gameObject.scene.IsValid();
                if (ns.StartsWith("UI.MainMenu") || b.GetType().Name.Contains("Join") || path.IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    note((inScene ? "[scene " + b.gameObject.scene.name + "] " : "[asset] ") + b.GetType().FullName + " @ " + path + " active=" + b.gameObject.activeInHierarchy);
                    if (++n > 80) break;
                }
            }
            foreach (UnityEngine.UI.Button btn in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string path = btn.name;
                for (Transform t = btn.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                string label = "";
                TMPro.TMP_Text tx = btn.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tx != null) label = tx.text;
                note("button " + path + " '" + label + "' active=" + btn.gameObject.activeInHierarchy);
            }
            Shot("mainmenu");
            yield return OpenJoinPage(note);
            yield return new WaitForSecondsRealtime(1.5f);
            MenuJoinGame jm = UnityEngine.Object.FindFirstObjectByType<MenuJoinGame>(FindObjectsInactive.Include);
            if (jm != null) Dump(jm.transform.root, "", note, 0);
            foreach (TMPro.TMP_InputField f in Resources.FindObjectsOfTypeAll<TMPro.TMP_InputField>())
            {
                string path = f.name;
                for (Transform t = f.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                note("input field " + path + " scene=" + f.gameObject.scene.name + " font=" + (f.textComponent != null ? f.textComponent.font.name : "?"));
            }
            Shot("joinpage");
            check("menu survey ran", true, "");
        }

        private static void Dump(Transform t, string indent, Action<string> note, int depth)
        {
            if (depth > 7) return;
            RectTransform rt = t as RectTransform;
            string comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray());
            TMPro.TMP_Text tx = t.GetComponent<TMPro.TMP_Text>();
            note(indent + t.name + (t.gameObject.activeSelf ? "" : " (off)") + " [" + comps + "]"
                 + (rt != null ? " rect " + rt.rect.width.ToString("0") + "x" + rt.rect.height.ToString("0") + " at " + rt.position.ToString("0") : "")
                 + (tx != null ? " text '" + tx.text + "' " + tx.font.name + " " + tx.fontSize : ""));
            foreach (Transform c in t) Dump(c, indent + "  ", note, depth + 1);
        }

        /// <summary>Press the main menu's Join Game button (its own click handler).</summary>
        public static IEnumerator OpenJoinPage(Action<string> note)
        {
            foreach (UnityEngine.UI.Button btn in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                TMPro.TMP_Text tx = btn.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tx == null || tx.text.IndexOf("Join Game", StringComparison.OrdinalIgnoreCase) < 0) continue;
                note("pressing '" + tx.text + "'");
                btn.onClick.Invoke();
                yield return null;
                yield break;
            }
            note("no Join Game button on the menu");
        }

        // ---- walking: prediction lag, corrections, snap-backs, frame rate --------------------

        private static bool _watch;
        private static int _corrections, _resets, _lagSamples;
        private static long _lagMax;
        private static double _lagSum, _errMax, _errSum;

        private static void ReconcilePrefix(Players.Network.LocalPlayerPhysicsPredictions __instance)
        {
            if (!_watch) return;
            try
            {
                object ns = Traverse.Create(__instance).Field("_networkState").GetValue();
                Players.Network.Model.PlayerPhysicsState st = Traverse.Create(ns).Property("PhysicsState").GetValue<Players.Network.Model.PlayerPhysicsState>();
                long lag = (long)__instance.CurrentPredictedTick - st.ClientTick;
                if (lag < 0 || lag > 100000) return;
                _lagSamples++; _lagSum += lag; if (lag > _lagMax) _lagMax = lag;
                if (lag > 100) _resets++;      // the game throws its predictions away and snaps to the server
            }
            catch { }
        }

        private static void CorrectPostfix(Players.Network.LocalPlayerPhysicsPredictions __instance)
        {
            if (!_watch) return;
            _corrections++;
            try
            {
                Vector3 e = Traverse.Create(__instance).Field("_predictionError").GetValue<Vector3>();
                double m = e.magnitude; _errSum += m; if (m > _errMax) _errMax = m;
            }
            catch { }
        }

        private static void Keys(params UnityEngine.InputSystem.Key[] keys)
        {
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            // a test game is in the background, and Unity turns its keyboard off when it loses focus
            UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && !kb.enabled) UnityEngine.InputSystem.InputSystem.EnableDevice(kb);
            if (UnityEngine.InputSystem.Keyboard.current != null)
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(UnityEngine.InputSystem.Keyboard.current, new UnityEngine.InputSystem.LowLevel.KeyboardState(keys));
        }

        private static IEnumerator Walk(Action<string, bool, string> check, Action<string> note, bool far)
        {
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            NetworkManager nm = InstanceFinder.NetworkManager;
            bool remote = nm.ClientManager.Started && !nm.ServerManager.Started;
            UnityEngine.InputSystem.InputSettings.BackgroundBehavior bg = UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior;
            string outPath = _srvDir != null ? Path.Combine(SrvData, "console-output.txt") : null;
            long outFrom = outPath != null && File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            if (remote && _srvDir != null) File.AppendAllText(Path.Combine(SrvData, "commands.txt"), (far ? "trace 70" : "trace 20") + Environment.NewLine);
            _corrections = _resets = _lagSamples = 0; _lagMax = 0; _lagSum = _errMax = _errSum = 0;
            Vector3 start = cp.LocalPlayer.Position, last = start;
            float path = 0f;
            List<float> dts = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            _watch = true;
            bool jumped1 = false, jumped2 = false;
            while (Time.realtimeSinceStartup - t0 < (far ? 70f : 20f))
            {
                float t = Time.realtimeSinceStartup - t0;
                if (far)
                {
                    bool hop = (t % 7f) < 0.15f;
                    jumped1 = jumped1 || (hop && t > 1f); jumped2 = jumped2 || (hop && t > 30f);
                    if (t < 68f) Keys(hop ? new[] { UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.LeftShift, UnityEngine.InputSystem.Key.Space } : new[] { UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.LeftShift });
                    else Keys();
                    dts.Add(Time.unscaledDeltaTime);
                    path += Vector3.Distance(last, cp.LocalPlayer.Position); last = cp.LocalPlayer.Position;
                    if (Time.frameCount % 250 == 0) note("t " + t.ToString("0") + " s: " + cp.LocalPlayer.Position.ToString("0") + ", " + _corrections + " corrections, max err " + _errMax.ToString("0.00") + " m, lag max " + _lagMax + ", resets " + _resets);
                    yield return null;
                    continue;
                }
                bool jump = (t > 4f && t < 4.15f) || (t > 14f && t < 14.15f);
                if (jump) { if (t < 10f) jumped1 = true; else jumped2 = true; }
                if (t < 8f) Keys(jump ? new[] { UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.Space } : new[] { UnityEngine.InputSystem.Key.W });
                else if (t < 10f) Keys();
                else if (t < 18f) Keys(jump ? new[] { UnityEngine.InputSystem.Key.S, UnityEngine.InputSystem.Key.Space } : new[] { UnityEngine.InputSystem.Key.S });
                else Keys();
                dts.Add(Time.unscaledDeltaTime);
                path += Vector3.Distance(last, cp.LocalPlayer.Position); last = cp.LocalPlayer.Position;
                yield return null;
            }
            _watch = false;
            Keys();
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = bg;
            float moved = Vector3.Distance(start, cp.LocalPlayer.Position);
            float total = dts.Sum();
            dts.Sort();
            double avgFps = dts.Count / (double)total;
            double low1 = 1.0 / dts[System.Math.Max(0, (int)(dts.Count * 0.99) - 1)];
            double lagAvg = _lagSamples > 0 ? _lagSum / _lagSamples : 0;
            double tickMs = 1000.0 / nm.TimeManager.TickRate;
            string mode = (remote ? "joined to the dedicated server" : "single player (this game is the host)") + (InputRelay.Enabled ? "" : " [input relay OFF]");
            if (remote) note("input relay: " + InputRelay.Sent + " unreliable input messages sent (" + InputRelay.Redundancy + " inputs each)");
            note("walk, " + mode + ": " + avgFps.ToString("0") + " fps average, " + low1.ToString("0") + " fps 1% low, " + dts.Count + " frames; RTT " + nm.TimeManager.RoundTripTime + " ms");
            note("prediction: server confirmations " + lagAvg.ToString("0.0") + " ticks behind on average (" + (lagAvg * tickMs).ToString("0") + " ms), " + _lagMax + " at most ("
                 + (_lagMax * tickMs).ToString("0") + " ms); " + _corrections + " correction(s), largest " + _errMax.ToString("0.000") + " m; " + _resets + " snap-back(s); moved " + moved.ToString("0.0") + " m");
            check("walking: the player really moved (W, then S)", jumped1 && jumped2 && _lagSamples > 100 && path > (far ? 100f : 20f), "walked " + path.ToString("0.0") + " m (" + moved.ToString("0.0") + " m net), " + _lagSamples + " predicted ticks");
            check("walking: no snap-backs (the game resets when the server is 2 s behind)", _resets == 0, _resets + " snap-back(s)");
            // The game keeps its own input buffer: walking in single player (the game is its own
            // server) measured 7.8 ticks behind on average, 14 at most. Joined, allow that plus the round trip.
            double allowed = 14 * tickMs + nm.TimeManager.RoundTripTime + 2 * tickMs;
            check("walking: moves are confirmed no later than in single player plus the round trip", _lagMax * tickMs <= allowed,
                "at most " + _lagMax + " ticks = " + (_lagMax * tickMs).ToString("0") + " ms behind (allowed " + allowed.ToString("0") + " ms: single player's 280 + RTT " + nm.TimeManager.RoundTripTime + " + 2 ticks)");
            check("walking: corrections stay small (no visible rubber-banding)", _errMax < 0.5, _corrections + " correction(s), largest " + _errMax.ToString("0.000") + " m");
            if (remote && outPath != null)
            {
                float until = Time.realtimeSinceStartup + 10f;
                string got = "";
                while (Time.realtimeSinceStartup < until) { got = ReadFrom(outPath, outFrom); if (got.Contains("Trace:")) break; yield return new WaitForSecondsRealtime(0.5f); }
                string line = got.Split('\n').FirstOrDefault(l => l.Contains("Trace:")) ?? "no trace reply";
                note("server " + line.Trim());
            }
        }

        // ---- the pause menu's player list ---------------------------------------------------

        private static IEnumerator PlayerList(Action<string, bool, string> check, Action<string> note)
        {
            // the pause menu as Escape opens it, then its Players button
            UI.Pause.PauseMenuHandler pause = UnityEngine.Object.FindFirstObjectByType<UI.Pause.PauseMenuHandler>(FindObjectsInactive.Include);
            if (pause == null) { check("player list", false, "no pause menu handler"); yield break; }
            pause.Show();
            yield return null; yield return null;
            UI.Pause.PauseButtonsActivator act = UnityEngine.Object.FindFirstObjectByType<UI.Pause.PauseButtonsActivator>(FindObjectsInactive.Include);
            GameObject btnGo = act != null ? Traverse.Create(act).Field("playerListButton").GetValue<GameObject>() : null;
            UnityEngine.UI.Button btn = btnGo != null ? btnGo.GetComponentInChildren<UnityEngine.UI.Button>(true) : null;
            note("pause menu open; players button " + (btn != null ? btn.name + " active=" + btn.gameObject.activeInHierarchy : "not found"));
            if (btn != null) btn.onClick.Invoke();
            yield return null; yield return null;
            UI.Pause.PlayerListMenu menu = UnityEngine.Object.FindFirstObjectByType<UI.Pause.PlayerListMenu>(FindObjectsInactive.Include);
            if (menu == null) { pause.Hide(); check("player list", false, "the Players button opened no PlayerListMenu"); yield break; }
            List<GameObject> opened = new List<GameObject>();
            if (!menu.gameObject.activeInHierarchy)
            {
                note("the Players button did not show the list; showing it directly");
                for (Transform t = menu.transform; t != null; t = t.parent)
                    if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); opened.Add(t.gameObject); }
            }
            int exBefore = _exceptions;
            for (int i = 0; i < 90; i++) yield return null;      // 90 frames: the old failure added a row every frame
            float hold;
            if (float.TryParse(Environment.GetEnvironmentVariable("SDD_DS_LIST_SECONDS") ?? "", out hold) && hold > 0f)
            {
                // how the game runs with the list left open
                float t0 = Time.realtimeSinceStartup, next = t0 + 5f; int f = 0; float worst = 0f;
                while (Time.realtimeSinceStartup - t0 < hold)
                {
                    f++; if (Time.unscaledDeltaTime > worst) worst = Time.unscaledDeltaTime;
                    if (Time.realtimeSinceStartup >= next)
                    {
                        note("list open " + (Time.realtimeSinceStartup - t0).ToString("0") + " s: " + (f / 5f).ToString("0") + " fps, worst frame " + (worst * 1000f).ToString("0") + " ms, "
                             + menu.GetComponentsInChildren<UI.Pause.PlayerListItem>(true).Length + " rows");
                        next += 5f; f = 0; worst = 0f;
                    }
                    yield return null;
                }
            }
            UI.Pause.PlayerListItem[] rows = menu.GetComponentsInChildren<UI.Pause.PlayerListItem>(true);
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            int players = cp.GetPlayersConnectionInfo().Count;
            List<string> names = rows.Select(r => { TMP_Text[] t = r.GetComponentsInChildren<TMP_Text>(true); return string.Join(" | ", t.Select(x => x.text).ToArray()); }).ToList();
            note("player list rows: " + string.Join(" ;; ", names.ToArray()));
            check("the player list has one row per player (after 90 frames)", rows.Length == players, rows.Length + " row(s) for " + players + " player(s)");
            check("the rows show names and ping, not the placeholder text", names.Count > 0 && names.All(n => !n.Contains("Language") && n.Contains(" ms")),
                string.Join(" ;; ", names.ToArray()));
            Shot("playerlist");
            yield return new WaitForSecondsRealtime(1f);
            foreach (GameObject g in opened) g.SetActive(false);
            pause.Hide();
        }

        private static int _exceptions;

        // ---- a client on another game version ----------------------------------------------

        public static string FakeGameVersion;

        private static void FakeVersionPrefix(ref string gameVersion)
        {
            if (FakeGameVersion != null) gameVersion = FakeGameVersion;     // what the game sends in its own join message
        }

        private static IEnumerator VersionProbe(Action<string, bool, string> check, Action<string> note)
        {
            yield return Leave();
            yield return new WaitForSecondsRealtime(3f);
            FakeGameVersion = "0.0.1-other";
            int seen = Client.VerdictsSeen;
            Client.Join(_address);
            float until = Time.realtimeSinceStartup + 40f;
            while (Client.VerdictsSeen == seen && Time.realtimeSinceStartup < until) yield return null;
            string msg = Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict";
            check("a client on another game version is turned away", Client.LastVerdict.HasValue && !Client.LastVerdict.Value.Accepted, msg);
            check("...and told both versions", msg.Contains("0.0.1-other") && msg.Contains(Application.version), msg);
            until = Time.realtimeSinceStartup + 30f;
            while ((SceneManager.GetActiveScene().name == "MainGame" || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            check("...shown in the menu's popup", (Client.RejectionPopupText ?? "").Contains("0.0.1-other"), Flat(Client.RejectionPopupText ?? "no popup"));
            Shot("version");
            FakeGameVersion = null;
            seen = Client.VerdictsSeen;
            Client.Join(_address);
            until = Time.realtimeSinceStartup + 90f;
            PlayersClientTracker cp = null;
            while (Time.realtimeSinceStartup < until)
            {
                cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
                if (Client.VerdictsSeen > seen && cp != null && cp.LocalPlayer != null) break;
                yield return null;
            }
            check("the same game version is let in", Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted && cp != null && cp.LocalPlayer != null,
                Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict");
        }

        private static IEnumerator Perf(Action<string, bool, string> check, Action<string> note)
        {
            yield return new WaitForSecondsRealtime(6f);
            JObject st = Status();
            if (st != null)
            {
                note("server work per loop: " + st["avgWorkMs"] + " ms avg, " + st["maxWorkMs"] + " ms worst; loop every " + st["avgFrameMs"] + " ms (target " + st["targetFrameRate"] + " fps)");
                check("the server's work per loop fits well inside its loop (so it never falls behind)", (double)st["avgWorkMs"] > 0 && (double)st["avgWorkMs"] < 1000.0 / (int)st["targetFrameRate"] * 0.75,
                    st["avgWorkMs"] + " ms of " + (1000.0 / (int)st["targetFrameRate"]).ToString("0.0") + " ms");
            }
            string outPath = Path.Combine(SrvData, "console-output.txt");
            long from = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            CheckWrap w = new CheckWrap();
            yield return Console("profile 5", "Profiling", w);
            float until = Time.realtimeSinceStartup + 40f;
            string got = "";
            while (Time.realtimeSinceStartup < until)
            {
                got = ReadFrom(outPath, from);
                if (got.Contains("Top:") && got.Split('\n').Length > 17) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            check("console: profile", got.Contains("Top:"), got.Contains("Top:") ? "done" : Flat(got));
            foreach (string l in got.Split('\n')) if (l.Trim().Length > 0) note("profile: " + l.Trim());
        }

        private static IEnumerator Math(Action<string, bool, string> check, Action<string> note)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ds-math-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                DateTime now = DateTime.UtcNow;
                ServerDataFile f = new ServerDataFile();
                // three runs over the last 10 days: 2 days up, down 1 day (a crash), up 3 days, down 2 days, up since 2 days ago
                f.Runs.Add(new RunRecord { Start = now.AddDays(-10), LastBeat = now.AddDays(-8), Clean = false });
                f.Runs.Add(new RunRecord { Start = now.AddDays(-7), LastBeat = now.AddDays(-4), Clean = true });
                f.Runs.Add(new RunRecord { Start = now.AddDays(-2), LastBeat = now.AddDays(-2).AddHours(1), Clean = true });
                f.Sessions.Add(new SessionRecord { Id = 1, Name = "a", Start = now.AddDays(-9), End = now.AddDays(-9).AddHours(2) });
                f.Sessions.Add(new SessionRecord { Id = 2, Name = "b", Start = now.AddDays(-5), End = now.AddDays(-5).AddHours(1) });
                f.Sessions.Add(new SessionRecord { Id = 1, Name = "a", Start = now.AddHours(-3), End = now.AddHours(-1) });
                f.Sessions.Add(new SessionRecord { Id = 3, Name = "c", Start = now.AddHours(-2), End = now.AddHours(-1.5) });
                File.WriteAllText(Path.Combine(dir, "server-data.json"), Newtonsoft.Json.JsonConvert.SerializeObject(f));
                ServerData d = new ServerData(dir);
                d.StartRun();   // a 4th run starting now: the 3rd run's last beat was 2 days - 1 h ago
                ServerData.Uptime week = d.UptimeOver(7);
                // over the last 7 days: run 2 gives 3 days, run 3 one hour, run 4 ~0 -> 3 d 1 h up of 7 d
                check("uptime over 7 days", System.Math.Abs(week.Up / 3600.0 - 73.0) < 0.1 && System.Math.Abs(week.Window / 86400.0 - 7) < 0.01,
                    (week.Up / 3600.0).ToString("0.00") + " h up of " + (week.Window / 86400.0).ToString("0.00") + " d, " + week.Percent.ToString("0.0") + "%");
                ServerData.Uptime month = d.UptimeOver(30);
                // the window starts at the first run (10 days ago), not 30 days ago
                check("uptime over 30 days only counts from the first run", System.Math.Abs(month.Window / 86400.0 - 10) < 0.01 && System.Math.Abs(month.Up / 3600.0 - (48 + 72 + 1)) < 0.1 && month.Crashes == 1 && month.Starts == 4,
                    (month.Up / 3600.0).ToString("0.00") + " h up of " + (month.Window / 86400.0).ToString("0.00") + " d, " + month.Starts + " starts, " + month.Crashes + " crash(es)");
                check("downtime before this run", d.DowntimeBeforeThisRun.HasValue && System.Math.Abs(d.DowntimeBeforeThisRun.Value.TotalHours - 47) < 0.1,
                    d.DowntimeBeforeThisRun.HasValue ? d.DowntimeBeforeThisRun.Value.TotalHours.ToString("0.00") + " h" : "none");
                ServerData.Activity day = d.ActivitySince(now.AddDays(-1)), wk = d.ActivitySince(now.AddDays(-7)), mo = d.ActivitySince(now.AddDays(-30));
                check("players active today / this week / this month", day.Players == 2 && wk.Players == 3 && mo.Players == 3 && mo.Visits == 4,
                    day.Players + " / " + wk.Players + " / " + mo.Players + " players, " + mo.Visits + " visits");
                check("play hours this month", System.Math.Abs(mo.Hours - 5.5) < 0.01, mo.Hours.ToString("0.00") + " h");
                check("most players at once today", d.PeakSince(now.AddDays(-1)) == 2, d.PeakSince(now.AddDays(-1)).ToString());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            yield break;
        }

        // ---- helpers -------------------------------------------------------------------------

        private class CheckWrap { public bool Ok; public string Detail; }

        private static bool ClientStarted()
        {
            NetworkManager nm = InstanceFinder.NetworkManager;
            return nm != null && nm.ClientManager != null && nm.ClientManager.Started;
        }

        private static IEnumerator Leave()
        {
            INetworkConnectionStopService stop = Core.Services.ServiceLocator.GetService<INetworkConnectionStopService>();
            if (stop != null) stop.StopConnection();
            SceneManager.LoadScene(0, LoadSceneMode.Single);
            float until = Time.realtimeSinceStartup + 20f;
            while ((SceneManager.GetActiveScene().name == "MainGame" || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
        }

        /// <summary>A line into the server's commands.txt, then wait for its reply in console-output.txt.</summary>
        private static IEnumerator Console(string cmd, string expect, CheckWrap w)
        {
            string outPath = Path.Combine(SrvData, "console-output.txt");
            long from = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            File.AppendAllText(Path.Combine(SrvData, "commands.txt"), cmd + Environment.NewLine);
            float until = Time.realtimeSinceStartup + 15f;
            string got = "";
            while (Time.realtimeSinceStartup < until)
            {
                got = ReadFrom(outPath, from);
                if (got.Contains(expect)) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            w.Ok = got.Contains(expect);
            w.Detail = "'" + cmd + "' -> " + Flat(got);
        }

        private static IEnumerator Chat(string msg, string expect, CheckWrap w)
        {
            int from = SysCount();
            ChatMessageBroadcaster chat = Core.Services.ServiceLocator.GetService<ChatMessageBroadcaster>();
            chat.ClientSendChatMessage(msg);
            yield return WaitSys(expect, 10f, w, from);
            w.Detail = "'" + msg + "' -> " + w.Detail;
        }

        private static IEnumerator WaitSys(string expect, float timeout, CheckWrap w, int from = 0)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                lock (_sys) if (_sys.Skip(from).Any(s => s.Contains(expect))) break;
                yield return null;
            }
            lock (_sys)
            {
                w.Ok = _sys.Skip(from).Any(s => s.Contains(expect));
                w.Detail = Flat(string.Join(" | ", _sys.Skip(from).ToArray()));
            }
        }

        private static int SysCount() { lock (_sys) return _sys.Count; }
        private static string[] LastSys(int n) { lock (_sys) return _sys.Skip(System.Math.Max(0, _sys.Count - n)).ToArray(); }

        private static string ReadFrom(string path, long from)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length <= from) return "";
                    fs.Seek(from, SeekOrigin.Begin);
                    using (StreamReader r = new StreamReader(fs)) return r.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private static string[] ReadShared(string path)
        {
            return ReadFrom(path, 0).Split('\n');
        }

        private static string Flat(string s)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " / ");
            return s.Length > 500 ? s.Substring(0, 500) + "..." : s;
        }

        private static JObject Status()
        {
            if (_srvDir == null) return null;
            string p = Path.Combine(SrvData, "status.json");
            try { return File.Exists(p) ? JObject.Parse(ReadFrom(p, 0)) : null; } catch { return null; }
        }

        private static JObject DataFile()
        {
            string p = Path.Combine(SrvData, "server-data.json");
            try { return File.Exists(p) ? JObject.Parse(ReadFrom(p, 0)) : null; } catch { return null; }
        }

        private static void Shot(string name)
        {
            try
            {
                string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DemoVerify", "shots");
                Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "dedicated-" + name + ".png"));
            }
            catch (Exception e) { Log.Warn("screenshot " + name + ": " + e.Message); }
        }
    }
}
