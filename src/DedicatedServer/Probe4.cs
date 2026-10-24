// Probes for 1.4: bad signal values, trees growing back, chat (inline styles, fonts, @mentions,
// whispers, announcements on screen), the chat's layout, player markers, extra mods, leaving while
// joining, a server that hangs, and the watchdog.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Planets.Network;
using Planets.Network.Model;
using Planets.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DedicatedServer
{
    internal static partial class Probe
    {
        private static IEnumerator Run4(string probe, Action<string, bool, string> check, Action<string> note)
        {
            switch (probe)
            {
                case "signals": return SignalsProbe(check, note);
                case "respawn": return RespawnProbe(check, note);
                case "chat2": return Chat2Probe(check, note);
                case "chatlayout": return ChatLayoutProbe(check, note);
                case "markers": return MarkersProbe(check, note);
                case "extramods": return ExtraModsProbe(check, note);
                case "joinleave": return JoinLeaveProbe(check, note);
                case "hang": return HangProbe(check, note);
                case "profiles": return ProfilesProbe(check, note);
                case "netguard": return NetGuardProbe(check, note);
                case "netguardrestart": return NetGuardRestartProbe(check, note);
                case "playerfile": return PlayerFileProbe(check, note);
                case "watchdog": return WatchdogProbe(check, note);
                case "give": return GiveProbe(check, note);
                case "protect": return ProtectProbe(check, note);
                case "spawns": return SpawnsProbe(check, note);
                case "complete": return CompleteProbe(check, note);
                case "menus": return MenusProbe(check, note);
                case "shipadmin": return ShipAdminProbe(check, note);
                case "records": return RecordsProbe(check, note);
                case "soak": return SoakProbe(check, note);
                case "nanship": return NanShipProbe(check, note);
                case "restarts": return RestartsProbe(check, note);
                case "sleep": return SleepProbe(check, note);
                case "netspace": return NetSpaceProbe(check, note);
                case "modsoff": return ModsOffProbe(check, note);
                case "gold": return GoldProbe(check, note);
                case "debris": return DebrisProbe(check, note);
                default: check("probe " + probe, false, "unknown probe"); return null;
            }
        }

        private static string Grab(string text, string pattern)
        {
            Match m = Regex.Match(text ?? "", pattern);
            return m.Success ? m.Groups[1].Value : "";
        }

        // ---- ships for admins: where, take players there, delete into the bin, restore ----------------

        private static IEnumerator ShipAdminProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            yield return ConsoleText("testship park 3", "test ship", w);
            string ship = Grab(w.Detail, @"#(\d+)");
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("ship where " + ship, "#" + ship, w);
            check("ship where <id>: the planet, its point in the planet's coordinates and the height above the ground", Regex.IsMatch(w.Detail, @"at -?\d+\.\d, -?\d+\.\d, -?\d+\.\d \(-?\d+\.\d m above the ground\)"), w.Detail);
            yield return ConsoleText("ship list", "#" + ship, w);
            string partsBefore = Grab(w.Detail, "#" + ship + @"\s+[^,]*, (\d+) parts");
            check("ship list: ships with their name (or no beacon), parts and place", partsBefore.Length > 0 && w.Detail.Contains("(no beacon)"), w.Detail);

            // delete it into the bin (nobody on it)
            yield return ConsoleText("tpforward " + me + " 40", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("ship delete " + ship, "eleted", w);
            string bin = Grab(w.Detail, @"ship restore (\S+)");
            check("ship delete <id>: deleted, with what it was made of (recovered) and how to restore it", w.Detail.Contains("Recovered: ") && Regex.IsMatch(w.Detail, @"Recovered: \d+ \w") && bin.Length > 0, w.Detail);
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("ship where " + ship, "ship", w);
            check("...the ship is gone from the world", w.Detail.Contains("ship where <id>"), w.Detail);
            string file = Path.Combine(_srvDir, "UserData", "DedicatedServer", "deleted-ships", bin + ".json");
            Newtonsoft.Json.Linq.JObject rec = File.Exists(file) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file)) : null;
            check("...and kept in the bin (deleted-ships/<id>.json: the ship, where it stood, its materials)", rec != null && ((string)rec["data"]).Length > 100 && ((Newtonsoft.Json.Linq.JObject)rec["materials"]).Count > 0 && rec["where"]["planet"] != null,
                rec == null ? "no " + file : "materials " + rec["materials"].ToString(Newtonsoft.Json.Formatting.None) + ", where " + rec["whereText"]);
            yield return ConsoleText("ship bin", bin, w);
            check("ship bin lists it", w.Detail.Contains(bin), w.Detail);

            // restore it where it stood
            yield return ConsoleText("ship restore " + bin, "estore", w);
            string again = Grab(w.Detail, @"as ship #(\d+)");
            check("ship restore <bin id>: it comes back where it stood", again.Length > 0 && w.Detail.Contains("where it stood"), w.Detail);
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("ship list", "#", w);
            string partsAfter = again.Length > 0 ? Grab(w.Detail, "#" + again + @"\s+[^,]*, (\d+) parts") : "";
            check("...with every part", partsAfter == partsBefore, "parts " + partsBefore + " -> " + partsAfter + "; " + w.Detail);
            yield return ConsoleText("ship where " + again, "#" + again, w);
            check("...at the same place on the planet", rec != null && w.Detail.Contains((string)rec["where"]["planet"]), w.Detail + " (was " + (rec != null ? (string)rec["whereText"] : "?") + ")");
            check("...and out of the bin", File.Exists(file + ".restored") && !File.Exists(file), Directory.Exists(Path.GetDirectoryName(file)) ? string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(file)).Select(Path.GetFileName).ToArray()) : "no bin folder");
            yield return ConsoleText("ship delete 999999", "ship", w);
            check("ship delete of a ship that isn't there: says so", w.Detail.Contains("There's no ship #999999"), w.Detail);

            // take the player to it: standing on its deck (the game counts that as aboard)
            if (again.Length == 0) yield break;
            uint sid = uint.Parse(again);
            float before = ShipDistance(sid);
            yield return ConsoleText("ship tp " + me.Substring(0, System.Math.Min(3, me.Length)) + " " + again, "ship #" + again, w);
            yield return new WaitForSecondsRealtime(3f);
            float after = ShipDistance(sid);
            check("ship tp <player> <id>: the player is put on the ship", OnShip(sid) || after >= 0 && after < 8f, "distance before " + before.ToString("0.0") + " m; now " + (OnShip(sid) ? "on its deck" : after.ToString("0.0") + " m") + "; " + w.Detail);
            yield return ConsoleText("ship delete " + again, "ship", w);
            check("ship delete refuses while someone is on it", w.Detail.Contains("is aboard"), w.Detail);
        }

        /// <summary>This player stands in the ship (its own space, as on its deck or in a room).</summary>
        private static bool OnShip(uint shipId)
        {
            try
            {
                global::Ships.Services.ShipsClientTracker t = Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsClientTracker>();
                global::Ships.TrackedShipClient s;
                return t != null && t.TryGetTrackedShip(shipId, out s) && s.RelativeSpaceId == Me().SpaceId;
            }
            catch { return false; }
        }

        /// <summary>Metres between this player and a ship (-1 if not both in the same space here).</summary>
        private static float ShipDistance(uint shipId)
        {
            try
            {
                global::Ships.Services.ShipsClientTracker t = Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsClientTracker>();
                global::Ships.TrackedShipClient s;
                if (t == null || !t.TryGetTrackedShip(shipId, out s)) return -1f;
                if (s.PhysicsState.SpaceId != Me().SpaceId) return -1f;
                return Vector3.Distance(s.PhysicsState.Position, Me().Position);
            }
            catch { return -1f; }
        }

        // ---- memory over a long run: does anything keep growing? ---------------------------------------

        private static double MbOf(string text, string pattern)
        {
            Match m = Regex.Match(text ?? "", pattern);
            if (!m.Success) return -1;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value == "GB" ? v * 1024 : v;
        }

        private static IEnumerator SoakProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            float minutes = 14f;
            float.TryParse(Environment.GetEnvironmentVariable("SDD_SOAK_MINUTES") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out minutes);
            if (minutes <= 0) minutes = 14f;
            List<string> rows = new List<string>();
            List<double> priv = new List<double>(), objs = new List<double>(), meshes = new List<double>(), cols = new List<double>(), managed = new List<double>();
            float end = Time.realtimeSinceStartup + minutes * 60f;
            int cycle = 0;
            while (Time.realtimeSinceStartup < end)
            {
                cycle++;
                // what players do: go somewhere new, talk, build and take apart, save
                yield return ConsoleText("tpforward " + me + " " + (cycle % 2 == 0 ? "160" : "-150") + " " + (cycle % 3 == 0 ? "120" : "0"), "moved", w);
                yield return ConsoleText("say soak " + cycle, "soak", w);
                if (cycle % 4 == 0)
                {
                    yield return ConsoleText("testship park 3", "test ship", w);
                    string sid = Grab(w.Detail, @"#(\d+)");
                    yield return new WaitForSecondsRealtime(3f);
                    if (sid.Length > 0) yield return ConsoleText("ship delete " + sid, "ship", w);
                }
                if (cycle % 5 == 0) yield return ConsoleText("save", "aved", w);
                if (cycle % 3 == 0) { yield return ConsoleText("testtree near", "testtree", w); yield return ConsoleText("respawn now", "restored", w); }
                yield return new WaitForSecondsRealtime(20f);
                yield return ConsoleText("resources", "server", w);
                double p = MbOf(w.Detail, @"server ([\d.]+) (MB|GB) private"), mg = MbOf(w.Detail, @"managed heap ([\d.]+) (MB|GB)");
                double go = MbOf(w.Detail + " MB", @"(\d+) game objects()"), me2 = MbOf(w.Detail + " MB", @"(\d+) meshes()"), co = MbOf(w.Detail + " MB", @"(\d+) colliders()");
                priv.Add(p); managed.Add(mg); objs.Add(go); meshes.Add(me2); cols.Add(co);
                rows.Add(cycle + ": private " + p.ToString("0") + " MB, managed " + mg.ToString("0") + " MB, objects " + go + ", meshes " + me2 + ", colliders " + co);
            }
            note("soak samples: " + string.Join(" | ", rows.ToArray()));
            // the second half against the first: steady growth is a leak, a level line is not
            int h = priv.Count / 2;
            Func<List<double>, string> trend = l => l.Count < 4 ? "?" : (l.Skip(h).Average() - l.Take(h).Average()).ToString("+0;-0") + " (first " + l.Take(h).Average().ToString("0") + ", last " + l.Skip(h).Average().ToString("0") + ")";
            Func<List<double>, double> rate = l => l.Count < 4 ? 0 : (l.Skip(h).Average() - l.Take(h).Average()) / System.Math.Max(0.1, minutes / 2f / 60f);
            check("soak (" + minutes + " min of moving to new ground, chat, ships built and deleted, saves): the server's memory doesn't keep growing",
                rate(priv) < 300, "private MB " + trend(priv) + ", about " + rate(priv).ToString("0") + " MB an hour; managed " + trend(managed) + "; game objects " + trend(objs) + "; meshes " + trend(meshes) + "; colliders " + trend(cols));
        }

        // ---- a world saved with a NaN ship (Fish2): binned at load, the player at the start, restore is sound ----

        private static IEnumerator NanShipProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            string log = Path.Combine(_srvDir, "MelonLoader", "Latest.log");
            string[] lines = ReadShared(log);
            check("at load: the ship with a NaN position went into the bin", lines.Any(l => l.Contains("had no real position (NaN)") && l.Contains("ship restore")), string.Join(" | ", lines.Where(l => l.Contains("ships:")).Take(4).ToArray()));
            check("...and the player saved on it starts at the starting point", lines.Any(l => l.Contains("was saved standing on a ship with no real position")) || lines.Any(l => l.Contains("moved to the starting point")), string.Join(" | ", lines.Where(l => l.Contains("starting point")).Take(3).ToArray()));
            yield return ConsoleText("tpforward " + me + " 0", "moved", w);
            check("...on the planet's surface (not in a ship's space)", w.Detail.Contains("moved"), w.Detail);
            yield return ConsoleText("ship bin", "deleted by", w);
            string bin = Grab(w.Detail, @"(\d{8}-\d{6}-\d+)");
            check("ship bin lists it", bin.Length > 0, w.Detail);
            yield return ConsoleText("resources", "server", w);
            double before = MbOf(w.Detail, @"server ([\d.]+) (MB|GB) private");
            Func<string, bool> stillNaN = l => l.Contains("held NaN or infinity") || l.Contains("skipped a wall") || l.Contains("watchdog");
            int nanBefore = ReadShared(log).Count(stillNaN);
            yield return ConsoleText("ship restore " + bin + " here " + me, "Restored", w);
            check("ship restore <id> here <player>: it comes back next to the player", w.Detail.Contains("Restored"), w.Detail);
            yield return new WaitForSecondsRealtime(30f);
            yield return ConsoleText("resources", "server", w);
            double after = MbOf(w.Detail, @"server ([\d.]+) (MB|GB) private");
            int nanAfter = ReadShared(log).Count(stillNaN);
            JObject st = Status();
            double loop = st != null && st["avgFrameMs"] != null ? (double)st["avgFrameMs"] : -1;
            check("...and stays sound for 30 s: no part holding NaN, no impossible walls, memory steady, the loop at speed", nanAfter == nanBefore && after - before < 300 && after > 0 && loop > 0 && loop < 12,
                "NaN-held / wall lines " + nanBefore + " -> " + nanAfter + ", memory " + before.ToString("0") + " -> " + after.ToString("0") + " MB, loop " + loop.ToString("0.0") + " ms; "
                + string.Join(" | ", ReadShared(log).Where(l => l.Contains("rotor")).Take(2).ToArray()));
        }

        // ---- players' spaces sent with every position; why a player left -------------------------------

        private static int _withSpace, _without;
        private static void WithSpacePostfix() { _withSpace++; }
        private static void WithoutSpacePostfix() { _without++; }

        private static IEnumerator NetSpaceProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("DedicatedServer.probe.netspace");
            System.Reflection.MethodInfo with = HarmonyLib.AccessTools.Method(typeof(Players.Network.PlayerPhysicsStateSync), "ClientReceiveStateWithSpace");
            System.Reflection.MethodInfo without = HarmonyLib.AccessTools.Method(typeof(Players.Network.PlayerPhysicsStateSync), "ClientReceiveState");
            h.Patch(with, postfix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("WithSpacePostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            h.Patch(without, postfix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("WithoutSpacePostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            _withSpace = _without = 0;
            yield return new WaitForSecondsRealtime(5f);
            int a = _withSpace, b = _without;
            h.UnpatchSelf();
            // (ClientReceiveStateWithSpace calls ClientReceiveState, so `without` counts every state)
            check("the server sends this player's space with every position it sends them (so no game can be left with a player in a space it never heard of)",
                a > 20 && a >= b - 2, a + " position(s) with the space, " + b + " in all, in 5 s");
            HarmonyLib.Patches pi = null;
            try { pi = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(Players.TrackedPlayerOtherClient), "UpdateVisuals")); } catch { }
            check("this game skips (and hides the marker of) a player whose position can't be worked out, instead of stopping the frame's drawing",
                pi != null && pi.Finalizers.Any(f => f.owner == "DedicatedServer" || f.PatchMethod.DeclaringType == typeof(NetHealth)), pi == null ? "not patched" : pi.Finalizers.Count + " finalizer(s)");
            // leaving: the server says why
            string me = Me().Name;
            yield return Rejoin(w);
            check("left and joined again", w.Ok, w.Detail);
            yield return new WaitForSecondsRealtime(1f);
            string line = ReadShared(Path.Combine(_srvDir, "MelonLoader", "Latest.log")).LastOrDefault(l => l.Contains("LEFT " + me)) ?? "";
            check("the server's log says why a player left (here: their game closed the connection)", line.Contains("their game closed the connection"), line);
        }

        // ---- sleeping: ship parts keep running (NecessaryFixes' fix, the server's copy) ------------------

        private static double Num(string s, string pattern) { Match m = Regex.Match(s ?? "", pattern); return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN; }

        private static IEnumerator SleepProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            yield return ConsoleText("testship park 3", "test ship", w);
            yield return new WaitForSecondsRealtime(3f);
            // the fix off first: the game as it is
            yield return ConsoleText("set SleepAirFlow off", "SleepAirFlow", w);
            yield return ConsoleText("testsleep status", "testsleep status", w);
            double ticks0 = Num(w.Detail, @"ticks (\d+)");
            yield return ConsoleText("testsleep " + me, "testsleep", w);
            check("a player on the server goes to sleep in a bed the game's own way (everyone on is asleep)", w.Detail.Contains("is asleep") && w.Detail.Contains("all asleep True"), w.Detail);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return ConsoleText("testsleep status", "testsleep status", w);
            double w0 = Num(w.Detail, @"world time ([\d.]+)");
            yield return new WaitForSecondsRealtime(5f);
            yield return ConsoleText("testsleep status", "testsleep status", w);
            double w1 = Num(w.Detail, @"world time ([\d.]+)"), speed = Num(w.Detail, @"time speed ([\d.]+)x"), ticks1 = Num(w.Detail, @"ticks (\d+)");
            note("fix off, 5 s asleep: " + w.Detail);
            check("the game speeds the world clock up while everyone sleeps (and, as it is, runs no ship parts)", speed > 5 && w1 - w0 > 25 && ticks1 == ticks0,
                "world time +" + (w1 - w0).ToString("0") + " s in 5 s, speed " + speed + "x, sleep-fix ticks " + ticks0 + " -> " + ticks1);
            yield return ConsoleText("testsleep wake " + me, "got up", w);
            yield return new WaitForSecondsRealtime(1f);
            // the fix on
            yield return ConsoleText("set SleepAirFlow on", "SleepAirFlow", w);
            yield return ConsoleText("testsleep " + me, "testsleep", w);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return ConsoleText("testsleep status", "testsleep status", w);
            double a0 = Num(w.Detail, @"world time ([\d.]+)"), s0 = Num(w.Detail, @"ship time ([\d.]+)"), t0 = Num(w.Detail, @"ticks (\d+)"), p0 = Num(w.Detail, @"part steps (\d+)");
            yield return new WaitForSecondsRealtime(8f);
            yield return ConsoleText("testsleep status", "testsleep status", w);
            double a1 = Num(w.Detail, @"world time ([\d.]+)"), s1 = Num(w.Detail, @"ship time ([\d.]+)"), t1 = Num(w.Detail, @"ticks (\d+)"), p1 = Num(w.Detail, @"part steps (\d+)");
            double cost = Num(w.Detail, @"cost ([\d.]+) ms"), sp = Num(w.Detail, @"time speed ([\d.]+)x");
            note("fix on, 8 s asleep: " + w.Detail);
            double world = a1 - a0, ship = s1 - s0;
            check("SleepAirFlow: while everyone sleeps, ship parts (pipes, vents, tanks...) run every tick, for all of the sped-up time", t1 - t0 > 100 && world > 40 && System.Math.Abs(ship - world) < world * 0.15,
                (t1 - t0) + " ticks, " + (p1 - p0) + " part steps (" + ((p1 - p0) / System.Math.Max(1, t1 - t0)).ToString("0.0") + " a tick, at most 0.1 s of ship time each); ship time +" + ship.ToString("0.0") + " s against world time +" + world.ToString("0.0") + " s at " + sp + "x");
            check("...at a small cost to the server's loop", cost >= 0 && cost < 3, cost.ToString("0.000") + " ms a tick on average");
            yield return ConsoleText("testsleep wake " + me, "got up", w);
            yield return ConsoleText("testship stop", "stopped", w);
        }

        // ---- planned restarts -----------------------------------------------------------------------------

        private static IEnumerator RestartsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("set RestartTimes 25:99", "RestartTimes", w);
            check("RestartTimes: a time that can't be read is refused, with an example", w.Detail.Contains("a time is like"), w.Detail);
            yield return ConsoleText("set RestartWarnings 2m, 1m, banana", "RestartWarnings", w);
            check("RestartWarnings: a warning that can't be read is refused", w.Detail.Contains("a warning is like"), w.Detail);
            yield return ConsoleText("set RestartWarnings 2m, 1m, 20s", "RestartWarnings", w);
            // a restart planned two and a half minutes from now (this machine's time)
            DateTime at = DateTime.Now.AddSeconds(150);
            at = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0);
            if ((at - DateTime.Now).TotalSeconds < 125) at = at.AddMinutes(1);
            yield return ConsoleText("set RestartTimes " + at.ToString("HH:mm", CultureInfo.InvariantCulture) + ", Sun 04:00", "RestartTimes", w);
            yield return ConsoleText("restarts", "Planned restarts", w);
            check("restarts: lists the next planned ones and the warnings", w.Detail.Contains(at.ToString("HH:mm")) && w.Detail.Contains("2 minutes, 1 minute, 20 seconds before"), w.Detail);
            lock (_chatSeen) _chatSeen.Clear();
            if (!_chatHooked) yield return ChatStyleProbeHookOnly();
            // the 2-minute warning comes when it is due, then the 1-minute one
            yield return SeeChat(m => m.Contains("restart in 2 minutes"), (float)(at - DateTime.Now).TotalSeconds - 100f, w);
            check("a planned restart: everyone is warned at the first warning time (2 minutes before)", w.Ok, w.Detail);
            int pings = Overlay.Shown;
            yield return SeeChat(m => m.Contains("restart in 1 minute"), 70f, w);
            check("...and again at the next (1 minute before), on screen too", w.Ok && Overlay.Shown > pings, w.Detail);
            yield return ConsoleText("restarts skip", "skip", w);
            check("restarts skip: the countdown is called off and that time skipped", w.Detail.Contains("is skipped"), w.Detail);
            yield return SeeChat(m => m.Contains("called off"), 6f, w);
            check("...and players are told", w.Ok, w.Detail);
            yield return ConsoleText("set RestartTimes \"\"", "RestartTimes", w);
            yield return ConsoleText("set RestartWarnings 10m, 5m, 2m, 1m, 30s, 10s", "RestartWarnings", w);
        }

        // ---- the numbers the Manager reads: hours, live network use, other mods' settings ------------

        private static IEnumerator RecordsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string data = Path.Combine(_srvDir, "UserData", "DedicatedServer");
            // this hour, with this player in it (written with the 30 s heartbeat)
            string hour = DateTime.UtcNow.ToString("yyyy-MM-ddTHH", System.Globalization.CultureInfo.InvariantCulture);
            Newtonsoft.Json.Linq.JObject h = null;
            for (float until = Time.realtimeSinceStartup + 40f; Time.realtimeSinceStartup < until; )
            {
                try
                {
                    Newtonsoft.Json.Linq.JObject d = Newtonsoft.Json.Linq.JObject.Parse(string.Join("\n", ReadShared(Path.Combine(data, "server-data.json")).ToArray()));
                    h = (d["Hours"] as Newtonsoft.Json.Linq.JArray)?.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault(x => (string)x["Hour"] == hour || (string)x["Hour"] == DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH"));
                }
                catch { }
                if (h != null && ((Newtonsoft.Json.Linq.JArray)h["Players"]).Count > 0) break;
                yield return new WaitForSecondsRealtime(2f);
            }
            check("server-data.json keeps hours: this hour has this player and the most on at once", h != null && ((Newtonsoft.Json.Linq.JArray)h["Players"]).Count > 0 && (int)h["Peak"] >= 1,
                h != null ? h.ToString(Newtonsoft.Json.Formatting.None) : "no hour " + hour);
            // live network use, a sample a second
            yield return ConsoleText("set TrackBandwidth on", "TrackBandwidth", w);
            yield return new WaitForSecondsRealtime(6f);
            Newtonsoft.Json.Linq.JObject live = null;
            try { live = Newtonsoft.Json.Linq.JObject.Parse(string.Join("\n", ReadShared(Path.Combine(data, "bandwidth-live.json")).ToArray())); } catch { }
            Newtonsoft.Json.Linq.JArray samples = live != null ? live["samples"] as Newtonsoft.Json.Linq.JArray : null;
            yield return ConsoleText("resources", "server", w);
            check("resources: the server's memory, CPU and object counts, and the machine's RAM, CPU and free disk (no names or addresses)",
                Regex.IsMatch(w.Detail, @"server [\d.]+ (MB|GB) private") && w.Detail.Contains("machine: RAM") && w.Detail.Contains("free on the world's drive") && w.Detail.Contains("system: ")
                && !w.Detail.Contains(Environment.UserName) && !w.Detail.Contains(Environment.MachineName), w.Detail);
            check("bandwidth-live.json: bytes in and out each second while counting", samples != null && samples.Count >= 3 && samples.Any(x => (double)x[2] > 0),
                samples != null ? samples.Count + " sample(s), last " + samples.Last().ToString(Newtonsoft.Json.Formatting.None) : "no file");
            yield return ConsoleText("set TrackBandwidth off", "TrackBandwidth", w);
            // other mods' settings
            Newtonsoft.Json.Linq.JObject ms = null;
            try { ms = Newtonsoft.Json.Linq.JObject.Parse(string.Join("\n", ReadShared(Path.Combine(data, "modsettings.json")).ToArray())); } catch { }
            Newtonsoft.Json.Linq.JArray cats = ms != null ? ms["categories"] as Newtonsoft.Json.Linq.JArray : null;
            check("modsettings.json: the other mods' settings, each with the mod it belongs to", cats != null && cats.Count > 0 && cats.All(c => c["mod"] != null) && cats.Any(c => ((string)c["mod"]).Length > 0),
                cats != null ? string.Join(", ", cats.Select(c => (string)c["id"] + "->" + ((string)c["mod"] == "" ? "?" : (string)c["mod"])).ToArray()) : "no file");
            Newtonsoft.Json.Linq.JObject cat = cats?.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault(c => ((Newtonsoft.Json.Linq.JArray)c["entries"]).Any(e => (string)e["type"] == "int"));
            Newtonsoft.Json.Linq.JObject entry = cat != null ? ((Newtonsoft.Json.Linq.JArray)cat["entries"]).OfType<Newtonsoft.Json.Linq.JObject>().First(e => (string)e["type"] == "int") : null;
            if (entry == null) { check("modset", false, "no whole-number setting to try"); yield break; }
            string was = (string)entry["value"];
            string want = (long.Parse(was) + 1).ToString();
            yield return ConsoleText("modset " + (string)cat["id"] + " " + (string)entry["id"] + " " + want, (string)entry["id"], w);
            yield return new WaitForSecondsRealtime(1f);
            string cfg = string.Join("\n", ReadShared(Path.Combine(_srvDir, "UserData", "MelonPreferences.cfg")).ToArray());
            check("modset <category> <setting> <value>: set on the server and saved in MelonPreferences.cfg", w.Detail.Contains(" = " + want) && Regex.IsMatch(cfg, @"(?m)^" + Regex.Escape((string)entry["id"]) + @" = " + want + @"\s*$"), w.Detail);
            yield return ConsoleText("modset " + (string)cat["id"] + " " + (string)entry["id"] + " banana", (string)entry["id"], w);
            check("...a value of the wrong kind is refused", w.Detail.Contains("needs a whole number"), w.Detail);
            yield return ConsoleText("modset " + (string)cat["id"] + " " + (string)entry["id"] + " " + was, (string)entry["id"], w);
        }

        // ---- give, protected builds, spawns, name completion -----------------------------------------

        private static IEnumerator GiveProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            yield return ConsoleText("give " + me.Substring(0, 2) + " wood 5", "Gave", w);
            check("/give <start of a name> <part of an item> <count>: the best match is given", Regex.IsMatch(w.Detail, @"Gave " + Regex.Escape(me) + @" 5 x \w"), w.Detail);
            yield return ConsoleText("give " + me + " indestructible build tool", "Gave", w);
            check("/give the " + AdminItems.ProtectToolName + " (a new item, on the server and in this game)", w.Detail.Contains("1 x " + AdminItems.ProtectToolName) && AdminItems.All().Any(i => i.Id == AdminItems.ProtectToolId), w.Detail);
            yield return ConsoleText("give " + me + " no such thing at all", "item", w);
            check("an item that doesn't exist: told so, with what /items lists", w.Detail.Contains("No item"), w.Detail);
            yield return new WaitForSecondsRealtime(1f);
            Shot("give-items");
        }

        private static IEnumerator ProtectProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("testship park 3", "test ship", w);
            string ship = Grab(w.Detail, @"#(\d+)");
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("testship2 parts", "testship2", w);
            if (ship.Length == 0) ship = Grab(w.Detail, @"ship (\d+)");
            string before = Grab(w.Detail, @"hull (\d+)");
            yield return ConsoleText("protect " + ship, "protected", w);
            check("/protect <ship>: protected and held still", w.Detail.Contains("is protected"), w.Detail);
            yield return new WaitForSecondsRealtime(1f);
            yield return ConsoleText("testship2 destroy", "testship2", w);
            check("a protected build doesn't break (a part destroyed as a crash would: still there)", Grab(w.Detail, @"hull (\d+)") == before && w.Detail.Contains("frozen True"), w.Detail + " (hull before " + before + ")");
            // a player without the tool tries to take a part off
            lock (_chatSeen) _chatSeen.Clear();
            if (!_chatHooked) yield return ChatStyleProbeHookOnly();
            Managers.Interface.Services.IShipInteractionNetworkService svc = Core.Services.ServiceLocator.GetService<Managers.Interface.Services.IShipInteractionNetworkService>();
            uint sid = uint.Parse(ship);
            Match fm = Regex.Match(w.Detail, @"first (-?\d+) (-?\d+) (-?\d+) (\d+)");
            bool have = fm.Success;
            global::Ships.Interface.Model.Placement.HullPartPosition hp = have
                ? new global::Ships.Interface.Model.Placement.HullPartPosition(new Vector3Int(int.Parse(fm.Groups[1].Value), int.Parse(fm.Groups[2].Value), int.Parse(fm.Groups[3].Value)), (global::Ships.Interface.Model.VoxelPartType)int.Parse(fm.Groups[4].Value))
                : default(global::Ships.Interface.Model.Placement.HullPartPosition);
            if (!have) note("no hull part position in: " + w.Detail);
            if (have) svc.RemoveHullPart(new Managers.Interface.Model.RemoveHullPartNetworkRequest { ShipId = sid, Position = hp });
            yield return SeeChat(m => m.Contains("This build is protected"), 6f, w);
            check("taking a part off without the tool: refused, and the player is told", w.Ok, w.Detail);
            yield return ConsoleText("testship2 parts", "testship2", w);
            check("...the part is still there", Grab(w.Detail, @"hull (\d+)") == before, w.Detail);
            // with the tool in hand it can be changed
            Players.Network.PlayerNetworkState ns = (Players.Network.PlayerNetworkState)HarmonyLib.Traverse.Create(Me()).Property("NetworkState").GetValue();
            ns.ClientSetSelectedItem(AdminItems.ProtectToolId);
            yield return new WaitForSecondsRealtime(1f);
            if (have) svc.RemoveHullPart(new Managers.Interface.Model.RemoveHullPartNetworkRequest { ShipId = sid, Position = hp });
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testship2 parts", "testship2", w);
            int after; int.TryParse(Grab(w.Detail, @"hull (\d+)"), out after);
            int b0; int.TryParse(before, out b0);
            check("...holding the " + AdminItems.ProtectToolName + ", it can be changed", have && after == b0 - 1, w.Detail + " (before " + before + ")");
            ns.ClientClearSelectedItem();
            // the Indestructible Recycler Tool: handed out with /give, and takes a protected build apart
            yield return ConsoleText("give " + Me().Name + " indestructible recycler", "give", w);
            string gave = ReadShared(Path.Combine(_srvDir, "MelonLoader", "Latest.log")).LastOrDefault(l => l.Contains("give:") && l.Contains(AdminItems.RecyclerToolName)) ?? "";
            check("/give finds the " + AdminItems.RecyclerToolName + " (item " + AdminItems.RecyclerToolId + ")", w.Detail.Contains("Gave") && gave.Contains("item " + AdminItems.RecyclerToolId), w.Detail + " | " + gave);
            check("...and this player's game has the item too", AdminItems.All().Any(i => i.Id == AdminItems.RecyclerToolId && i.Name == AdminItems.RecyclerToolName), "items with 398xx: " + string.Join(", ", AdminItems.All().Where(i => i.Id / 100 == 398).Select(i => i.Id + " " + i.Name).ToArray()));
            yield return ConsoleText("testship2 parts", "testship2", w);
            fm = Regex.Match(w.Detail, @"first (-?\d+) (-?\d+) (-?\d+) (\d+)");
            int r0; int.TryParse(Grab(w.Detail, @"hull (\d+)"), out r0);
            if (fm.Success)
            {
                hp = new global::Ships.Interface.Model.Placement.HullPartPosition(new Vector3Int(int.Parse(fm.Groups[1].Value), int.Parse(fm.Groups[2].Value), int.Parse(fm.Groups[3].Value)), (global::Ships.Interface.Model.VoxelPartType)int.Parse(fm.Groups[4].Value));
                ns.ClientSetSelectedItem(AdminItems.RecyclerToolId);
                yield return new WaitForSecondsRealtime(1f);
                svc.RemoveHullPart(new Managers.Interface.Model.RemoveHullPartNetworkRequest { ShipId = sid, Position = hp });
                yield return new WaitForSecondsRealtime(2f);
            }
            yield return ConsoleText("testship2 parts", "testship2", w);
            int r1; int.TryParse(Grab(w.Detail, @"hull (\d+)"), out r1);
            check("holding the " + AdminItems.RecyclerToolName + ", a part of the protected build comes off", fm.Success && r1 == r0 - 1, w.Detail + " (before " + r0 + ")");
            yield return ConsoleText("protected", "ship", w);
            check("...and the build is still protected (only the recycler took a part)", w.Detail.Contains(ship), w.Detail);
            ns.ClientClearSelectedItem();
            yield return ConsoleText("protected", "ship", w);
            note(w.Detail);
            yield return ConsoleText("unprotect " + ship, "protected", w);
            check("/unprotect lifts it", w.Detail.Contains("isn't protected any more"), w.Detail);
            yield return ConsoleText("testship stop", "stopped", w);
        }

        private static IEnumerator SpawnsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            yield return ConsoleText("set SpreadSpawnKm 3", "SpreadSpawnKm", w);
            Vector3 before = Me().Position; uint beforeSpace = Me().SpaceId;
            yield return ConsoleText("testspread " + me, "testspread", w);
            check("spread spawns: a spot of the player's own is picked (dry, flat, within SpreadSpawnKm)", w.Detail.Contains(" spot ") && w.Detail.Contains(" km "), w.Detail);
            string km = Grab(w.Detail, @"km ([\d.]+)");
            yield return new WaitForSecondsRealtime(3f);
            float h = ClientHeight(out uint space);
            check("...and the player is put there, on the ground", space != beforeSpace || (Me().Position - before).magnitude > 50f, "now " + h.ToString("0.0") + " m above the ground, " + km + " km from the start");
            Shot("spawn-spot");
            yield return new WaitForSecondsRealtime(0.3f);
            // dying (respawning) without a bed: back to that spot
            yield return ConsoleText("tpforward " + me + " 300", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            Vector3 away = Me().Position; uint awaySpace = Me().SpaceId;
            Core.Services.ServiceLocator.GetService<Managers.Interface.Services.IPlayerRespawnService>().RespawnLocalPlayer();
            yield return new WaitForSecondsRealtime(3f);
            check("respawning without a bed: back to their own spot, not the world's start", Me().SpaceId != awaySpace || (Me().Position - away).magnitude > 100f,
                "space " + awaySpace + " -> " + Me().SpaceId + ", moved " + (Me().Position - away).magnitude.ToString("0") + " m");
            JObject data = DataFile();
            JToken rec = data != null ? (data["Players"] as JArray).FirstOrDefault(p => (string)p["Name"] == me) : null;
            check("the spot is kept with the player (server-data.json)", rec != null && (ulong)rec["SpawnPlanet"] != 0, rec != null ? "planet " + rec["SpawnPlanet"] : "no record");
            yield return ConsoleText("clearspawn " + me, "spawn spot", w);
        }

        private static IEnumerator CompleteProbe(Action<string, bool, string> check, Action<string> note)
        {
            string me = Me().Name;
            UI.Chat.ChatMenu menu = UnityEngine.Object.FindFirstObjectByType<UI.Chat.ChatMenu>(FindObjectsInactive.Include);
            menu.OpenChat();
            yield return new WaitForSecondsRealtime(0.5f);
            ChatComplete.Update();
            string a = ChatComplete.TypeForTest("/give " + me.Substring(0, 2), true);
            check("Tab after the start of a name: the whole name", a == "/give " + me + " ", "'" + a + "'");
            string b = ChatComplete.TypeForTest("/give " + me + " indestr", true);
            check("...and of an item in /give", b == "/give " + me + " " + AdminItems.ProtectToolName + " ", "'" + b + "'");
            ChatComplete.TypeForTest("hi @" + me.Substring(0, 2), false);
            yield return null;
            ChatComplete.Update();
            string hint = ChatComplete.Suggestion;
            yield return new WaitForSecondsRealtime(0.3f);
            Shot("chat-complete");
            check("while typing @name, the best match shows above the box", hint == "Tab: " + me, "'" + hint + "'");
            TMPro.TMP_InputField input = HarmonyLib.Traverse.Create(menu).Field("inputField").GetValue<TMPro.TMP_InputField>();
            input.text = "hi @" + me.Substring(0, 2) + " ";      // a space typed after it
            yield return null;
            check("a space after part of a name fills it in", input.text == "hi @" + me + " ", "'" + input.text + "'");
            ChatComplete.TypeForTest("", false);
            menu.Close();
        }

        // ---- menu highlights ---------------------------------------------------------------------------

        private static IEnumerator MenusProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return Leave();
            yield return new WaitForSecondsRealtime(3f);
            UI.Common.MenuNavigation nav = UnityEngine.Object.FindFirstObjectByType<UI.Common.MenuNavigation>();
            UI.Common.MenuButtonElement[] buttons = nav != null ? HarmonyLib.Traverse.Create(nav).Field("_mainButtons").GetValue<UI.Common.MenuButtonElement[]>() : null;
            if (buttons == null || buttons.Length < 2) { check("main menu", false, "no menu buttons"); yield break; }
            UI.Common.MenuButtonElement a = buttons.Where(b => b.isActiveAndEnabled && b.submenu != null).ElementAtOrDefault(0);
            UI.Common.MenuButtonElement b2 = buttons.Where(b => b.isActiveAndEnabled && b.submenu != null).ElementAtOrDefault(1);
            note("menu buttons: " + string.Join(", ", buttons.Select(x => x.name).ToArray()));
            a.OpenMenu();
            yield return new WaitForSecondsRealtime(0.5f);
            // a highlight left behind (as when the mouse leaves while a menu changes)
            HarmonyLib.Traverse.Create(b2).Field("_isHovered").SetValue(true);
            HarmonyLib.Traverse.Create(b2).Method("UpdateSelectionColor").GetValue();
            Shot("menu-stuck");
            yield return new WaitForSecondsRealtime(0.6f);
            bool hov = HarmonyLib.Traverse.Create(b2).Field("_isHovered").GetValue<bool>();
            float alpha = b2.GetComponent<UnityEngine.UI.Image>().color.a;
            check("a menu button left lit (the mouse isn't on it) goes back to normal", !hov && alpha < 0.01f, "hovered " + hov + ", alpha " + alpha.ToString("0.00") + ", fixed so far " + MenuFix.Unstuck);
            // a hidden button keeps no old selection
            b2.gameObject.SetActive(false);
            b2.OpenMenu();               // as if it had been picked before it was hidden
            a.OpenMenu();
            bool stale = HarmonyLib.Traverse.Create(b2).Field("_isSelected").GetValue<bool>();
            b2.gameObject.SetActive(true);
            check("a button hidden while the menu changed doesn't come back looking selected", !stale, "selected " + stale);
            // clicking off
            MenuFix.CloseMenus();
            yield return new WaitForSecondsRealtime(0.4f);
            Shot("menu-clicked-off");
            bool anySelected = buttons.Any(x => HarmonyLib.Traverse.Create(x).Field("_isSelected").GetValue<bool>());
            check("clicking empty space closes the open menu and nothing stays selected", !anySelected && HarmonyLib.Traverse.Create(nav).Field("_currentMenu").GetValue() == null && HarmonyLib.Traverse.Create(nav).Field("_currentSelectorAMenu").GetValue() == null,
                "selected " + anySelected);
            yield return Rejoin(w);
        }

        // ---- signals ---------------------------------------------------------------------------------

        private static IEnumerator SignalsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("testship park 3", "test ship", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testsignal", "testsignal", w);
            string id = Grab(w.Detail, @"testsignal part (\d+)");
            check("a math gate on the test ship given NaN holds 0 from the moment it is written (it never holds NaN, not even for a tick)", id.Length > 0 && w.Detail.Contains("NaN") && Regex.IsMatch(w.Detail, @"reads 0\b"), w.Detail);
            yield return new WaitForSecondsRealtime(2.5f);
            yield return ConsoleText("testsignal " + id, "output", w);
            check("FixBadSignals: a couple of seconds on, its output is still 0", Regex.IsMatch(w.Detail, @"output 0\b"), w.Detail);
            yield return ConsoleText("testsignal big", "testsignal", w);
            check("a number too big for a float (1e300: every part reads it as infinity) is 0 too", Regex.IsMatch(w.Detail, @"set to 1E\+300, reads 0\b"), w.Detail);
            long fixedN = -1;
            for (float until = Time.realtimeSinceStartup + 7f; Time.realtimeSinceStartup < until; )   // status.json is written every 5 s
            {
                JObject st = Status();
                fixedN = st != null && st["signalsFixed"] != null ? (long)st["signalsFixed"] : -1;
                if (fixedN >= 1) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            check("...counted (status.json signalsFixed) and logged", fixedN >= 1 && ReadShared(Path.Combine(_srvDir, "MelonLoader", "Latest.log")).Any(l => l.Contains("holds 0 until its inputs give a number")), "signalsFixed " + fixedN);
            check("the value check hooks are on the server's signal code", Signals.Clean(new global::Ships.Interface.Model.Parts.State.MathGateState { Output = double.PositiveInfinity }) is global::Ships.Interface.Model.Parts.State.MathGateState g && g.Output == 0.0,
                "Clean(MathGateState { Output = infinity }) gives Output 0");
            yield return ConsoleText("testship stop", "stopped", w);
        }

        // ---- trees growing back ------------------------------------------------------------------------

        private static HashSet<uint> ClientDestroyed(ulong chunk)
        {
            PlanetsClientTracker t = Core.Services.ServiceLocator.GetService<PlanetsClientTracker>();
            foreach (TrackedPlanetClient tp in t.TrackedPlanets)
            {
                PlanetNetworkedState s = tp.Planet.GetComponent<PlanetNetworkedState>();
                Dictionary<ulong, HashSet<uint>> d = HarmonyLib.Traverse.Create(s._destroyedDenseObjects).Field("_destroyedDenseObjectsClient").GetValue<Dictionary<ulong, HashSet<uint>>>();
                HashSet<uint> set;
                if (d != null && d.TryGetValue(chunk, out set)) return set;
            }
            return new HashSet<uint>();
        }

        private static bool ClientHasGone(string id)
        {
            string[] p = id.Split(':');
            ulong c; uint i;
            return p.Length == 2 && ulong.TryParse(p[0], out c) && uint.TryParse(p[1], out i) && ClientDestroyed(c).Contains(i);
        }

        private static IEnumerator RespawnProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("set RespawnTreesMinutes 120", "RespawnTreesMinutes", w);
            // 1. a tree a little way off: cut, made due, grows back on this client
            yield return ConsoleText("testtree front", "testtree", w);
            if (!w.Detail.Contains("testtree tree")) { note("no tree in front of the camera: " + w.Detail); yield return ConsoleText("testtree near", "testtree", w); }
            string id = Grab(w.Detail, @"testtree tree (\d+:\d+)");
            yield return WaitUntil(() => id.Length > 0 && ClientHasGone(id), 5f);
            check("a tree cut on the server is gone here too (and its time is noted)", id.Length > 0 && ClientHasGone(id) && w.Detail.Contains("(due)"), w.Detail);
            int grownBefore = RespawnVisuals.Grown, instantBefore = RespawnVisuals.Instant;
            yield return ConsoleText("respawn now", "grew back", w);
            check("respawn now: the due tree grows back on the server", Regex.IsMatch(w.Detail, @"\b[1-9]\d* grew back"), w.Detail);
            yield return new WaitForSecondsRealtime(0.45f);
            Shot("respawn-growing");
            yield return new WaitForSecondsRealtime(0.5f);
            Shot("respawn-growing2");
            yield return WaitUntil(() => !ClientHasGone(id), 6f);
            check("...and in this player's game (it grows out of the ground, then the real one takes over)", !ClientHasGone(id) && (RespawnVisuals.Grown > grownBefore || RespawnVisuals.Instant > instantBefore),
                "grown " + (RespawnVisuals.Grown - grownBefore) + ", appeared " + (RespawnVisuals.Instant - instantBefore));
            yield return new WaitForSecondsRealtime(1.5f);
            Shot("respawn-grown");
            // 2. a tree right next to the player waits until they move away
            yield return ConsoleText("testtree here", "testtree", w);
            string here = Grab(w.Detail, @"testtree tree (\d+:\d+)");
            if (here.Length == 0) note("no tree within 4 m of the player here: " + w.Detail);
            else
            {
                yield return ConsoleText("respawn now", "grew back", w);
                check("a tree that would grow into a player waits", w.Detail.Contains("wait") && ClientHasGone(here), w.Detail);
                yield return ConsoleText("tpforward " + Me().Name + " 60", "moved", w);
                yield return new WaitForSecondsRealtime(2f);
                yield return ConsoleText("respawn now", "grew back", w);
                yield return WaitUntil(() => !ClientHasGone(here), 6f);
                check("...and grows back once they have moved away", !ClientHasGone(here), w.Detail);
            }
            // 3. a tree under a parked ship waits
            yield return ConsoleText("testship park 6", "test ship", w);
            yield return new WaitForSecondsRealtime(4f);
            yield return ConsoleText("testtree ship", "testtree", w);
            string under = Grab(w.Detail, @"testtree tree (\d+:\d+)");
            if (under.Length == 0) note("no tree under the test ship here: " + w.Detail);
            else
            {
                yield return ConsoleText("respawn now", "grew back", w);
                check("a tree under a parked ship (a build) waits: it would grow into it", w.Detail.Contains("wait") && ClientHasGone(under), w.Detail);
            }
            // the record survives a save
            yield return ConsoleText("save", "Saved", w);
            string rec = Path.Combine(SrvData, "worlds", "default", "respawn.json");
            string json = File.Exists(rec) ? ReadFrom(rec, 0) : "";
            check("respawn.json keeps what is still gone, with when (so a restart doesn't bring everything back)", json.Contains("\"Kind\"") && json.Contains("\"At\""), rec + " " + json.Length + " bytes");
            yield return ConsoleText("respawn", "grown back since", w);
            note(w.Detail);
            yield return ConsoleText("testship stop", "stopped", w);
        }

        // ---- chat --------------------------------------------------------------------------------------

        private static IEnumerator Chat2Probe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            if (!_chatHooked) yield return ChatStyleProbeHookOnly();
            lock (_chatSeen) _chatSeen.Clear();
            string me = Me() != null ? Me().Name : "ethan";
            // styles for part of a message
            yield return ConsoleText("say Meet at {yellow bold}the station{/} now", "said", w);
            yield return SeeChat(m => m.Contains("the station"), 8f, w);
            check("{style}...{/}: part of a message in its own style", w.Ok && w.Detail.Contains("<color=#FFE16B><b>the station</b></color>") && w.Detail.Contains(" now"), w.Detail);
            // fonts
            yield return ConsoleText("fonts", "", w);
            yield return new WaitForSecondsRealtime(1f);
            string fonts = ReadShared(Path.Combine(SrvData, "console-output.txt")).Reverse().FirstOrDefault(l => l.Contains("SDF") || l.Contains(", ")) ?? "";
            fonts = Regex.Replace(fonts, @"^\d\d:\d\d:\d\d\s+", "");
            string first = fonts.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.Contains(">") && !x.Contains("Fallback")) ?? "";
            note("fonts: " + fonts.Trim());
            if (first.Length > 0)
            {
                yield return ConsoleText("say {font=\"" + first + "\"} A different font", "said", w);
                yield return SeeChat(m => m.Contains("A different font"), 8f, w);
                check("font=: a message in one of the game's fonts", w.Ok && w.Detail.Contains("<font=\"" + first + "\">"), w.Detail);
                yield return new WaitForSecondsRealtime(0.5f);
                string drawn = DrawnLines().FirstOrDefault(t => t.Contains("A different font")) ?? "";
                check("...and the chat draws it (the font tag is read, not shown)", drawn.Length > 0 && !drawn.Contains("<font"), drawn);
            }
            // @mention (the player themself: the name is styled; nobody else to ping here)
            Core.Services.ServiceLocator.GetService<Managers.Server.Services.ChatMessageBroadcaster>().ClientSendChatMessage("hello @" + me + " and <b>you</b>");
            yield return SeeChat(m => m.StartsWith("player|") && m.Contains("hello"), 8f, w);
            check("@name: an online player's name in the mention style; the rest still shown as typed", w.Ok && w.Detail.Contains("<color=#FFD54F><b>@" + me + "</b></color>") && w.Detail.Contains("<noparse>"), w.Detail);
            // a whisper from the console: only this player, with a ping
            int pings = Overlay.Pings;
            yield return ConsoleText("whisper " + me + " just between us", "whispered", w);
            yield return SeeChat(m => m.Contains("just between us"), 8f, w);
            check("whisper: the player gets [from ...] in the whisper style", w.Ok && w.Detail.Contains("[from Server]") && w.Detail.Contains("<color=#C79BFF>"), w.Detail);
            yield return WaitUntil(() => Overlay.Pings > pings, 4f);
            check("...and a ping", Overlay.Pings > pings, "ping sound: " + (Overlay.PingSound.Length > 0 ? Overlay.PingSound : "(none found)"));
            // on screen (earlier tests' warnings may still be queued: start from an empty screen)
            Overlay.ClearForTest();
            int shown = Overlay.Shown;
            yield return ConsoleText("announce {yellow bold} Event at the {white}station{/} in 10 minutes", "announced", w);
            yield return WaitUntil(() => Overlay.Shown > shown, 5f);
            yield return new WaitForSecondsRealtime(Overlay.FadeIn + 0.3f);
            Shot("announce-onscreen");
            check("announce: shown in the middle of the screen", Overlay.Showing.Contains("Event at the") && Overlay.Showing.Contains("<color=#FFFFFF>station</color>"), Overlay.Showing);
            Overlay.CloseForTest();                // Enter
            yield return new WaitForSecondsRealtime(Overlay.FadeOut + 0.3f);
            check("...and Enter closes it", Overlay.Showing.Length == 0, "showing: '" + Overlay.Showing + "'");
            shown = Overlay.Shown;
            yield return ConsoleText("alert Look up", "alerted", w);
            yield return WaitUntil(() => Overlay.Shown > shown, 5f);
            check("alert: also on screen (AlertsOnScreen)", Overlay.Shown > shown, Overlay.Showing);
            Overlay.CloseForTest();
            yield return new WaitForSecondsRealtime(1f);
            // friends and colour
            yield return Chat("/color purple", "marker", w);
            check("/color: a player picks their marker's colour", w.Ok && w.Detail.Contains("#C79BFF"), w.Detail);
            yield return Chat("/friends", "Friends:", w);
            check("/friends lists friends and requests", w.Ok, w.Detail);
        }

        private static IEnumerator ChatStyleProbeHookOnly()
        {
            _chatHooked = true;
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("DedicatedServer.probe.chat");
            h.Patch(HarmonyLib.AccessTools.Method(typeof(UI.Chat.ChatMenu), "ReceiveSystemMessage"), prefix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("ChatSeenPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(UI.Chat.ChatMessageObjectUI), "InitializeChatMessage"), postfix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("ChatPlayerSeenPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            yield break;
        }

        private static List<string> DrawnLines()
        {
            return UnityEngine.Object.FindObjectsByType<UI.Chat.ChatMessageObjectUI>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(o => HarmonyLib.Traverse.Create(o).Field("text").GetValue<TMPro.TMP_Text>()).Where(t => t != null)
                .Select(t => { t.ForceMeshUpdate(true, true); return t.GetParsedText(); }).ToList();
        }

        // ---- the chat's layout ---------------------------------------------------------------------------

        private static IEnumerator ChatLayoutProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("say A short one", "said", w);
            yield return ConsoleText("say {size=160 bold} A big one that is long enough to wrap onto a second line in the chat box, and maybe a third", "said", w);
            yield return ConsoleText("alert An alert that is also quite long, so that it has to wrap at least once in the chat", "alerted", w);
            Core.Services.ServiceLocator.GetService<Managers.Server.Services.ChatMessageBroadcaster>().ClientSendChatMessage("the last line, from a player, which should sit just above the box where you type");
            Overlay.CloseForTest();
            yield return new WaitForSecondsRealtime(1.5f);
            UI.Chat.ChatMenu menu = UnityEngine.Object.FindFirstObjectByType<UI.Chat.ChatMenu>(FindObjectsInactive.Include);
            if (menu == null) { check("chat layout", false, "no chat"); yield break; }
            menu.OpenChat();
            yield return new WaitForSecondsRealtime(1f);
            Shot("chat-open");
            yield return new WaitForSecondsRealtime(0.3f);
            // every line tall enough for its text, and the last one above the box where you type
            List<string> cut = new List<string>();
            UI.Chat.ChatMessageObjectUI[] lines = UnityEngine.Object.FindObjectsByType<UI.Chat.ChatMessageObjectUI>(FindObjectsSortMode.None);
            UI.Chat.ChatMessageObjectUI lastLine = null; float lastBottom = float.MaxValue;
            foreach (UI.Chat.ChatMessageObjectUI l in lines)
            {
                TMPro.TMP_Text t = HarmonyLib.Traverse.Create(l).Field("text").GetValue<TMPro.TMP_Text>();
                if (t == null || !l.gameObject.activeInHierarchy) continue;
                RectTransform rt = (RectTransform)t.transform;
                t.ForceMeshUpdate(true, true);
                UnityEngine.UI.LayoutElement le = HarmonyLib.Traverse.Create(l).Field("layoutElement").GetValue<UnityEngine.UI.LayoutElement>();
                float need = t.preferredHeight, have = le != null ? le.preferredHeight : rt.rect.height;
                if (need > have + 1f) cut.Add("'" + Short(t.GetParsedText()) + "' needs " + need.ToString("0") + " has " + have.ToString("0"));
                Vector3[] c = new Vector3[4]; ((RectTransform)l.transform).GetWorldCorners(c);
                if (c[0].y < lastBottom) { lastBottom = c[0].y; lastLine = l; }
            }
            // and no two lines on top of each other
            List<UI.Chat.ChatMessageObjectUI> ordered = lines.Where(x => x.gameObject.activeInHierarchy).OrderBy(x => x.transform.GetSiblingIndex()).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                Vector3[] up = new Vector3[4], dn = new Vector3[4];
                ((RectTransform)ordered[i - 1].transform).GetWorldCorners(up); ((RectTransform)ordered[i].transform).GetWorldCorners(dn);
                TMPro.TMP_Text ta = HarmonyLib.Traverse.Create(ordered[i - 1]).Field("text").GetValue<TMPro.TMP_Text>();
                TMPro.TMP_Text tb = HarmonyLib.Traverse.Create(ordered[i]).Field("text").GetValue<TMPro.TMP_Text>();
                if (ta == null || tb == null) continue;
                Bounds ba = ta.textBounds, bb = tb.textBounds;
                float aBottom = ta.transform.TransformPoint(ba.min).y, bTop = tb.transform.TransformPoint(bb.max).y;
                if (bTop > aBottom + 1f) cut.Add("'" + Short(tb.GetParsedText()) + "' is drawn over '" + Short(ta.GetParsedText()) + "' by " + (bTop - aBottom).ToString("0") + " px");
            }
            check("every chat line is tall enough for its text (nothing cut off)", cut.Count == 0, cut.Count == 0 ? lines.Length + " line(s)" : string.Join(" | ", cut.Take(4).ToArray()));
            TMPro.TMP_InputField input = HarmonyLib.Traverse.Create(menu).Field("inputField").GetValue<TMPro.TMP_InputField>();
            UnityEngine.UI.ScrollRect scroll = HarmonyLib.Traverse.Create(menu).Field("scrollRect").GetValue<UnityEngine.UI.ScrollRect>();
            if (input == null || scroll == null) { check("chat layout", false, "input " + (input != null) + ", scroll " + (scroll != null)); yield break; }
            Vector3[] ic = new Vector3[4]; ((RectTransform)input.transform).GetWorldCorners(ic);
            Vector3[] vc = new Vector3[4]; (scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform).GetWorldCorners(vc);
            check("with the chat open, the last line is above the box where you type", lastLine != null && lastBottom >= ic[1].y - 1f && lastBottom >= vc[0].y - 1f,
                "last line bottom " + lastBottom.ToString("0") + ", typing box top " + ic[1].y.ToString("0") + ", view bottom " + vc[0].y.ToString("0"));
            menu.Close();
            yield return new WaitForSecondsRealtime(0.5f);
        }

        private static string Short(string s) { s = (s ?? "").Replace("\n", " "); return s.Length > 40 ? s.Substring(0, 40) + "..." : s; }

        // ---- player markers ------------------------------------------------------------------------------

        private static IEnumerator MarkersProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            int before = Markers.Received;
            yield return ConsoleText("set PlayerMarkers friends", "PlayerMarkers", w);
            yield return WaitUntil(() => Markers.Received > before, 8f);
            check("the server tells each player whose markers they see (PlayerMarkers friends)", Markers.Received > before && Markers.Mode == "friends", "mode " + Markers.Mode + ", messages " + (Markers.Received - before));
            // the rule applied to a marker, as the game draws one (this test has one player, so a stand-in marker)
            string r = Markers.TestApply("Stranger", "#FF6B5E", false, "Friend", "#9BE7A0", true);
            check("a hidden player's marker and arrow are off; a friend's are drawn in their colour, with their name when looked at", r == "Stranger hidden; Friend shown #9BE7A0 tag Friend", r);
            yield return ConsoleText("set PlayerMarkers everyone", "PlayerMarkers", w);
        }

        // ---- extra mods ---------------------------------------------------------------------------------

        private static IEnumerator ExtraModsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("deop " + Me().Name, "", w);            // admins are never turned away for mods
            Client.ExtraForTest.Add("Fake Client Mod\t1.2.3");                  // as if this game had a mod the server doesn't
            yield return ConsoleText("set RejectExtraMods on", "RejectExtraMods", w);
            yield return Rejoin(w);
            string msg = Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "";
            check("RejectExtraMods: a player with mods the server doesn't run is turned away, told which", !w.Ok && msg.Contains("doesn't allow these mods") && msg.Contains("Fake Client Mod 1.2.3"), msg);
            {
                // how long from the server having this game's mod list to turning it away (it used to wait 8 s for the identity proof)
                string[] lines = ReadShared(Path.Combine(_srvDir, "MelonLoader", "Latest.log"));
                string got = lines.LastOrDefault(l => l.Contains("] client ") && l.Contains(" has DedicatedServer")) ?? "";
                string away = lines.LastOrDefault(l => l.Contains("TURNED AWAY") && l.Contains("Fake Client Mod")) ?? "";
                TimeSpan t1 = TimeSpan.Zero, t2 = TimeSpan.Zero;
                bool ok = TimeSpan.TryParse(Grab(got, @"^\[([0-9:.]+)\]"), out t1) && TimeSpan.TryParse(Grab(away, @"^\[([0-9:.]+)\]"), out t2);
                double secs = ok ? (t2 - t1).TotalSeconds : -1;
                check("...and straight away: within 2 s of the server having its mod list (not after 8 s)", ok && secs >= 0 && secs < 2, secs.ToString("0.00") + " s");
            }
            yield return new WaitForSecondsRealtime(1f);
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;     // as the loading screen leaves it
            yield return null; yield return null;
            check("the turned-away popup can be clicked (the mouse is free while it is up)", ClientUi.Shown != null && ClientUi.Shown.gameObject.activeInHierarchy && Cursor.lockState == CursorLockMode.None && Cursor.visible,
                "popup " + (ClientUi.Shown != null && ClientUi.Shown.gameObject.activeInHierarchy) + ", lock " + Cursor.lockState + ", visible " + Cursor.visible);
            yield return new WaitForSecondsRealtime(1f);
            Shot("extra-mods-rejected");
            yield return new WaitForSecondsRealtime(0.5f);
            ClosablePopupClose();
            yield return new WaitForSecondsRealtime(3f);
            JObject data = DataFile();
            JArray flagged = data != null ? data["FlaggedMods"] as JArray : null;
            check("...and the mods are noted for the Server Manager (server-data.json FlaggedMods)", flagged != null && flagged.Any(f => (string)f["Name"] == "Fake Client Mod"), flagged != null ? flagged.ToString(Newtonsoft.Json.Formatting.None) : "none");
            string runExtra = Environment.GetEnvironmentVariable("SDD_DS_EXTRA_MOD");       // a run that gives this game a mod the server lacks
            yield return ConsoleText("set AllowedExtraMods Fake Client Mod, Demo Verify Harness, Demo Zoom, Demo Build Pick, Demo Mod List, ExtraCameras, Performance+"
                + (string.IsNullOrEmpty(runExtra) ? "" : ", " + runExtra), "AllowedExtraMods", w);
            yield return Rejoin(w);
            check("allowed (AllowedExtraMods): the same player joins", w.Ok, w.Detail);
            Client.ExtraForTest.Clear();
            yield return ConsoleText("set RejectExtraMods off", "RejectExtraMods", w);
        }

        // ---- mods a server doesn't allow, turned off for it (a run where this game has Demo Zoom and the server doesn't) ----

        private static IEnumerator ModsOffProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            string extra = Environment.GetEnvironmentVariable("SDD_DS_EXTRA_MOD") ?? "Demo Zoom";      // a mod this game has and the test server doesn't
            string mods = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
            string off = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-ModsOff");
            string plan = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-modsoff.json");
            yield return ConsoleText("deop " + me, "", w);
            yield return ConsoleText("set AllowedExtraMods Demo Verify Harness", "AllowedExtraMods", w);
            yield return ConsoleText("set RejectExtraMods on", "RejectExtraMods", w);
            yield return Rejoin(w);
            string msg = Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "";
            check("a server that doesn't allow " + extra + " turns this player away, naming it", !w.Ok && msg.Contains(extra), msg);
            yield return WaitUntil(() => ModsOff.State == "offered", 10f);
            check("...and the popup offers to turn it off just for this server", ModsOff.State == "offered" && (Client.RejectionPopupText ?? "").Contains("turn them off just for this server"), ModsOff.State + ": " + Client.RejectionPopupText);
            Shot("mods-off-offer");
            yield return new WaitForSecondsRealtime(0.5f);
            string file = ModsOff.OfferForTest != null && ModsOff.OfferForTest.Count > 0 ? ModsOff.OfferForTest[0].File : "";
            string said = ModsOff.Go(true);                    // "Always for this server" (the restart is skipped in tests)
            ClosablePopupClose();
            check("Always for this server: the mod's file is moved out of Mods (not deleted) and written down first", file.Length > 0 && !File.Exists(Path.Combine(mods, file)) && File.Exists(Path.Combine(off, file)) && File.Exists(plan) && File.ReadAllText(plan).Contains("joining"),
                said + "; " + file + " in Mods " + File.Exists(Path.Combine(mods, file)) + ", put aside " + File.Exists(Path.Combine(off, file)));
            SavedServer ss = SavedServers.Get(_address, false);
            check("...and remembered for this server", ss != null && ss.AutoModsOff, ss == null ? "no saved server" : "AutoModsOff " + ss.AutoModsOff);
            // as after the restart: a game without that mod, which joins by itself
            Client.HideForTest.Add(extra);
            ModsOff.StartAgainForTest();
            yield return WaitUntil(() => ModsOff.State == "in" && Me() != null, 60f);
            yield return new WaitForSecondsRealtime(2f);
            check("after the restart the game joins the server by itself, without the mod", ModsOff.State == "in" && Me() != null && Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted, "state " + ModsOff.State + ", " + (Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "no verdict"));
            yield return Leave();
            yield return WaitUntil(() => ModsOff.State == "back", 20f);
            check("leaving the server turns the mod back on (its file back in Mods, the record gone) and says so",
                ModsOff.State == "back" && File.Exists(Path.Combine(mods, file)) && !File.Exists(Path.Combine(off, file)) && !File.Exists(plan) && ModsOff.LastNotice.Contains("turned back on"),
                ModsOff.State + ": " + ModsOff.LastNotice);
            // the next time it happens by itself, and "Don't, and stop doing this" stops it
            Client.HideForTest.Remove(extra);
            yield return Rejoin(w);
            yield return WaitUntil(() => ModsOff.State == "auto", 10f);
            check("the next time: turned away, it is turned off by itself, the popup saying so", ModsOff.State == "auto" && (Client.RejectionPopupText ?? "").Contains("you chose to always do this here"), ModsOff.State + ": " + Client.RejectionPopupText);
            Shot("mods-off-auto");
            ModsOff.StopAlways(null);
            yield return new WaitForSecondsRealtime(6f);       // past the moment it would have gone
            ClosablePopupClose();
            ss = SavedServers.Get(_address, false);
            check("...and \"Don't, and stop doing this\" calls it off and forgets the choice", ss != null && !ss.AutoModsOff && File.Exists(Path.Combine(mods, file)) && !File.Exists(plan), "AutoModsOff " + (ss != null && ss.AutoModsOff) + ", file in Mods " + File.Exists(Path.Combine(mods, file)));
            yield return ConsoleText("set RejectExtraMods off", "RejectExtraMods", w);
            yield return Rejoin(w);
        }

        // ---- the indestructible tools in gold, in hand ------------------------------------------------------

        private static IEnumerator GoldProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            Players.Network.PlayerNetworkState ns = (Players.Network.PlayerNetworkState)HarmonyLib.Traverse.Create(Me()).Property("NetworkState").GetValue();
            foreach (uint id in new[] { AdminItems.ProtectToolId, AdminItems.RecyclerToolId })
            {
                int before = AdminItems.Gilded;
                ns.ClientClearSelectedItem();
                yield return new WaitForSecondsRealtime(1f);
                ns.ClientSetSelectedItem(id);
                yield return new WaitForSecondsRealtime(2.5f);
                check("item " + id + " (" + (id == AdminItems.ProtectToolId ? AdminItems.ProtectToolName : AdminItems.RecyclerToolName) + "): the tool in hand is gold", AdminItems.Gilded > before, "gilded " + before + " -> " + AdminItems.Gilded);
                Shot("gold-" + id);
                yield return new WaitForSecondsRealtime(0.5f);
            }
            ns.ClientClearSelectedItem();
        }

        // ---- crash debris: none from protected builds, and never in a space that isn't there -----------------

        private static int DebrisCount(string reply) { int n; return int.TryParse(Grab(reply, @"world objects (\d+)"), out n) ? n : -1; }

        private static IEnumerator DebrisProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            yield return ConsoleText("tpforward " + me + " 0 -40", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testship park 7", "test ship", w);
            string ship = Grab(w.Detail, @"#(\d+)");
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("protect " + ship + " movable", "protected", w);
            yield return ConsoleText("testdebris", "testdebris", w);
            int d0 = DebrisCount(w.Detail);
            yield return ConsoleText("testship ram 80", "ramming", w);
            yield return new WaitForSecondsRealtime(7f);
            yield return ConsoleText("testdebris", "testdebris", w);
            int d1 = DebrisCount(w.Detail);
            int left = int.TryParse(Grab(w.Detail, @"protected crash parts left out (\d+)"), out int lo) ? lo : 0;
            string protectedSaid = d0 + " -> " + d1 + ", protected crash parts left out " + left;
            yield return ConsoleText("unprotect " + ship, "protected", w);
            yield return ConsoleText("testship stop", "stopped", w);
            yield return ConsoleText("testship park 7", "test ship", w);
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("testship ram 80", "ramming", w);
            yield return new WaitForSecondsRealtime(7f);
            yield return ConsoleText("testdebris", "testdebris", w);
            int d2 = DebrisCount(w.Detail);
            if (d2 > d1 || left > 0)
            {
                check("a protected build rammed into the ground makes no debris (its parts are left out of the crash: no free materials)", d1 == d0 && left > 0, protectedSaid);
                check("an ordinary ship rammed into the ground still breaks and makes its debris", d2 > d1, d1 + " -> " + d2);
            }
            else note("the rams broke no parts on this ground (" + protectedSaid + "; ordinary " + d1 + " -> " + d2 + "): the crash path wasn't exercised");
            yield return ConsoleText("testship stop", "stopped", w);
            yield return ConsoleText("testship park 3", "test ship", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testdebris", "testdebris", w);
            int e0 = DebrisCount(w.Detail);
            yield return ConsoleText("testdebris zero", "testdebris", w);
            int e1 = DebrisCount(w.Detail);
            check("debris asked for in space 0 (as the crash that broke Fish2 did) isn't made", e0 >= 0 && e1 == e0 && w.Detail.Contains("debris skipped 1"), e0 + " -> " + e1 + "; " + w.Detail);
            yield return ConsoleText("testdebris here", "testdebris", w);
            int e2 = DebrisCount(w.Detail);
            check("...while debris in a real place still is", e2 == e1 + 1, e1 + " -> " + e2 + "; " + w.Detail);
            yield return ConsoleText("testship stop", "stopped", w);
        }

        // ---- leaving while joining ------------------------------------------------------------------------

        private static IEnumerator JoinLeaveProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return Leave();
            yield return new WaitForSecondsRealtime(2f);
            string err = Client.Join(_address);
            yield return WaitUntil(() => JoinWatch.State == "loading", 30f);
            check("joining: the loading screen has Cancel and leave", JoinWatch.State == "loading", "state '" + JoinWatch.State + "', error " + err);
            Shot("loading-cancel");
            yield return null;
            bool pressed = JoinWatch.PressLeave();
            note("Cancel and leave pressed " + (pressed ? "while loading" : "too late: the world had loaded"));
            yield return WaitUntil(() => SceneManager.GetActiveScene().name != "MainGame", 15f);
            check("...which goes back to the main menu", pressed && SceneManager.GetActiveScene().name != "MainGame", "scene " + SceneManager.GetActiveScene().name);
            yield return new WaitForSecondsRealtime(2f);
            yield return Rejoin(w);
            check("and it can join again after", w.Ok, w.Detail);
        }

        // ---- a server that hangs ------------------------------------------------------------------------

        private static IEnumerator HangProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            // on a server that's fine, the game sends nothing beside its own connection, and the status socket is closed
            int asked0 = JoinWatch.Asked;
            yield return new WaitForSecondsRealtime(14f);
            check("while connected, no status questions are sent beside the game connection (it counts the connection's own traffic)",
                JoinWatch.Asked == asked0, (JoinWatch.Asked - asked0) + " asked in 14 s");
            check("...and the status socket is closed once nothing is being asked (it stayed open all session)", !Query.ClientOpen,
                "open " + Query.ClientOpen + ", opened " + Query.ClientSocketsOpened + ", closed " + Query.ClientSocketsClosed);
            yield return ConsoleText("set WatchdogSeconds 0", "WatchdogSeconds", w);
            File.AppendAllText(Path.Combine(SrvData, "commands.txt"), "testhang 28" + Environment.NewLine);
            yield return WaitUntil(() => JoinWatch.State == "not responding", 30f);
            check("a frozen server: after 15 s the player is told it isn't responding, with Leave", JoinWatch.State == "not responding", "state '" + JoinWatch.State + "'");
            // as in play: the game holds the mouse for looking around
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            yield return null; yield return null;
            check("...and the mouse is free to click Leave (the game had it locked)", Cursor.lockState == CursorLockMode.None && Cursor.visible, "lock " + Cursor.lockState + ", visible " + Cursor.visible);
            yield return new WaitForSecondsRealtime(0.5f);
            Shot("server-not-responding");
            yield return WaitUntil(() => JoinWatch.State == "", 40f);
            check("...and the notice goes when it answers again", JoinWatch.State == "", "state '" + JoinWatch.State + "'");
            check("...(while it was frozen the server was asked how it is, as before)", JoinWatch.Asked > asked0, (JoinWatch.Asked - asked0) + " asked");
            check("...giving the mouse back to the game", Cursor.lockState == CursorLockMode.Locked, "lock " + Cursor.lockState);
            yield return ConsoleText("set WatchdogSeconds 120", "WatchdogSeconds", w);
        }

        private static IEnumerator WatchdogProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("set WatchdogSeconds 20", "WatchdogSeconds", w);
            File.AppendAllText(Path.Combine(SrvData, "commands.txt"), "testhang 60" + Environment.NewLine);
            string log = Path.Combine(_srvDir, "MelonLoader", "Latest.log");
            yield return WaitUntil(() => ReadShared(log).Any(l => l.Contains("watchdog: the server loop has not run")), 45f);
            yield return new WaitForSecondsRealtime(3f);
            JObject st = Status();
            check("watchdog: a server stuck for WatchdogSeconds logs it and exits to be restarted (status 'restarting')",
                ReadShared(log).Any(l => l.Contains("watchdog: the server loop has not run")) && st != null && (string)st["state"] == "restarting",
                st != null ? "state " + st["state"] : "no status");
        }
    }
}
