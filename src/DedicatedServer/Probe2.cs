// Probes for the server rules (1.2): developer tools, individual research, identity, password,
// protected names, address bans, permissions, stats/exploration and backup retention. Everything
// the client does goes through the game's own paths (its RPCs, its join); the server is driven
// through its commands file, as the Server Manager drives it.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using FishNet;
using HarmonyLib;
using Managers.Server.Services;
using Newtonsoft.Json.Linq;
using Players;
using Players.Interface.Model;
using Players.Network;
using Players.Services;
using Research;
using Research.Interface.Values;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DedicatedServer
{
    internal static partial class Probe
    {
        private static IEnumerator Run2(string probe, Action<string, bool, string> check, Action<string> note)
        {
            switch (probe)
            {
                case "devtools": return DevToolsProbe(check, note);
                case "research": return ResearchProbe(check, note);
                case "identity": return IdentityProbe(check, note);
                case "password": return PasswordProbe(check, note);
                case "names": return NamesProbe(check, note);
                case "ipban": return IpBanProbe(check, note);
                case "perm": return PermProbe(check, note);
                case "stats2": return Stats2Probe(check, note);
                case "backups": return BackupsProbe(check, note);
                case "shipsync": return ShipSyncProbe(check, note);
                case "modresearch": return ModResearchProbe(check, note);
                default: return Run3(probe, check, note);
            }
        }

        private static TrackedPlayerLocalClient Me()
        {
            PlayersClientTracker cp = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            return cp != null ? cp.LocalPlayer : null;
        }

        private static PlayerNetworkState MyState()
        {
            TrackedPlayerLocalClient me = Me();
            return me != null ? Traverse.Create(me).Property("NetworkState").GetValue<PlayerNetworkState>() : null;
        }

        /// <summary>Leave, join again, and wait for the verdict (and the player, if let in).</summary>
        private static IEnumerator Rejoin(CheckWrap w)
        {
            if (ClientStarted() || SceneManager.GetActiveScene().name == "MainGame") yield return Leave();
            yield return new WaitForSecondsRealtime(2f);
            int seen = Client.VerdictsSeen;
            string err = Client.Join(_address);
            if (err != null) { w.Ok = false; w.Detail = "join didn't start: " + err; yield break; }
            float until = Time.realtimeSinceStartup + 40f;
            while (Client.VerdictsSeen == seen && Time.realtimeSinceStartup < until) yield return null;
            bool accepted = Client.LastVerdict.HasValue && Client.LastVerdict.Value.Accepted && Client.VerdictsSeen > seen;
            w.Detail = Client.VerdictsSeen > seen ? Client.LastVerdict.Value.Message : "no answer in 40 s";
            if (accepted)
            {
                until = Time.realtimeSinceStartup + 60f;
                while (Me() == null && Time.realtimeSinceStartup < until) yield return null;
                accepted = Me() != null;
                yield return new WaitForSecondsRealtime(2f);
            }
            else
            {
                until = Time.realtimeSinceStartup + 30f;
                while ((SceneManager.GetActiveScene().name == "MainGame" || ClientStarted()) && Time.realtimeSinceStartup < until) yield return null;
            }
            w.Ok = accepted;
        }

        // ---- developer tools ---------------------------------------------------------------------

        private static bool Flying()
        {
            PlayerNetworkState ns = MyState();
            return ns != null && (ns.ClientState.Type == PlayerStateType.CheatFlying || ns.ClientState.Type == PlayerStateType.CheatFlyingHoldingCable);
        }

        private static IEnumerator DevToolsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me().SteamUserId.ToString();
            yield return Console("deop " + id, "", w);               // start as a plain player
            yield return Console("set DevTools admins", "DevTools = \"admins\"", w);
            PlayerNetworkInputs inputs = MyState().GetComponent<PlayerNetworkInputs>();
            int from = SysCount();
            inputs.ToggleCheatFlyState_ServerRPC();                  // what the game's cheat-fly key sends
            yield return new WaitForSecondsRealtime(1.5f);
            CheckWrap sys = new CheckWrap();
            yield return WaitSys("for admins only", 3f, sys, from);
            check("a player who isn't an admin can't cheat-fly (DevTools = admins)", !Flying() && sys.Ok, "flying " + Flying() + "; chat: " + sys.Detail);

            yield return Console("op " + id, "is now an operator", w);
            yield return new WaitForSecondsRealtime(1f);
            inputs.ToggleCheatFlyState_ServerRPC();
            float until = Time.realtimeSinceStartup + 5f;
            while (!Flying() && Time.realtimeSinceStartup < until) yield return null;
            check("an admin can", Flying(), "flying " + Flying());
            inputs.ToggleCheatFlyState_ServerRPC();
            until = Time.realtimeSinceStartup + 5f;
            while (Flying() && Time.realtimeSinceStartup < until) yield return null;

            yield return Console("set DevTools nobody", "DevTools = \"nobody\"", w);
            inputs.ToggleCheatFlyState_ServerRPC();
            yield return new WaitForSecondsRealtime(1.5f);
            check("with DevTools = nobody, not even an admin", !Flying(), "flying " + Flying());
            yield return Console("set DevTools admins", "DevTools = \"admins\"", w);
        }

        // ---- individual research ---------------------------------------------------------------

        private static IEnumerator ResearchProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me().SteamUserId.ToString();
            yield return Console("op " + id, "", w);                 // free build (to skip the materials) is a developer tool
            yield return Console("set Research individual", "Research = \"individual\"", w);
            float until = Time.realtimeSinceStartup + 5f;
            while (Rules.ClientResearchCount < 0 && Time.realtimeSinceStartup < until) yield return null;
            check("the client gets its own research list", Rules.ClientResearchCount >= 0, Rules.ClientResearchCount + " researched");

            ResearchClientProxy proxy = Core.Services.ServiceLocator.GetService<ResearchClientProxy>();
            ResearchNetworkedState global = UnityEngine.Object.FindFirstObjectByType<ResearchNetworkedState>();
            ResearchSettings pick = proxy.Researches.FirstOrDefault(r => !proxy.IsResearched(r.id) && proxy.HasResearchPrerequisite(r.id));
            if (pick == null) { check("individual research", false, "nothing left to research"); yield break; }
            PlayerNetworkState ns = MyState();
            if (!ns.IsUsingCheatFreeBuild) ns.GetComponent<PlayerNetworkInputs>().ToggleCheatFreeBuild_ServerRPC();
            until = Time.realtimeSinceStartup + 5f;
            while (!ns.IsUsingCheatFreeBuild && Time.realtimeSinceStartup < until) yield return null;
            int before = Rules.ClientResearchCount;
            Core.Services.ServiceLocator.GetService<ResearchNetworkService>().ResearchTech(pick.id);   // the research screen's button
            until = Time.realtimeSinceStartup + 5f;
            while (Rules.ClientResearchCount == before && Time.realtimeSinceStartup < until) yield return null;
            check("researching adds it to this player's own list", proxy.IsResearched(pick.id), "research " + pick.id + " (" + pick.name + "): researched for us " + proxy.IsResearched(pick.id));
            check("...and not to the server's shared tree", !global._researched.Contains(pick.id), "shared tree has it: " + global._researched.Contains(pick.id));
            string file = Path.Combine(Path.GetDirectoryName((string)Status()["world"]), "research-by-player.json");
            string text = File.Exists(file) ? File.ReadAllText(file) : "";
            check("...and it is saved with the world", text.Contains(id) && text.Contains(pick.id.ToString()), File.Exists(file) ? Flat(text) : "no " + file);

            yield return Console("set Research shared", "Research = \"shared\"", w);
            until = Time.realtimeSinceStartup + 5f;
            while (Rules.ClientResearchCount >= 0 && Time.realtimeSinceStartup < until) yield return null;
            check("back to shared, the client follows the shared tree again", Rules.ClientResearchCount < 0 && !proxy.IsResearched(pick.id),
                "own list " + Rules.ClientResearchCount + "; researched " + proxy.IsResearched(pick.id));
            if (ns.IsUsingCheatFreeBuild) ns.GetComponent<PlayerNetworkInputs>().ToggleCheatFreeBuild_ServerRPC();
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Console("deop " + id, "", w);           // as it was, for the probes after this one
        }

        // ---- identity -----------------------------------------------------------------------------

        private static IEnumerator IdentityProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me().SteamUserId.ToString();
            JObject data = DataFile();
            JToken me = data["Players"].FirstOrDefault(p => (string)p["Id"] == id);
            check("the server tied this player to this game", me != null && ((string)me["Token"]).Length == 64, me != null ? "token hash " + ((string)me["Token"]).Substring(0, 8) + "..." : "no record");
            check("...and noted the address", me != null && (string)me["LastIp"] != "", me != null ? (string)me["LastIp"] : "");

            string oldKey = Client.IdentityKey.Value;
            Client.IdentityKey.Value = new string('a', 64);          // another computer's game claiming this player
            yield return Rejoin(w);
            check("another game can't join as this player", !w.Ok && w.Detail.Contains("another computer"), w.Detail);

            yield return Console("unlink " + id, "can join from their next game", w);
            check("console: unlink", w.Ok, w.Detail);
            yield return Rejoin(w);
            check("after unlink, the new game is let in (and becomes theirs)", w.Ok, w.Detail);
            Client.IdentityKey.Value = oldKey;
            yield return Rejoin(w);
            check("...so now the old one is the stranger", !w.Ok && w.Detail.Contains("another computer"), w.Detail);
            yield return Console("unlink " + id, "", w);
            yield return Rejoin(w);
            check("unlink again, and the original game is back", w.Ok, w.Detail);
        }

        // ---- password -------------------------------------------------------------------------------

        private static IEnumerator PasswordProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me().SteamUserId.ToString();
            yield return Console("deop " + id, "", w);                // admins skip the password
            yield return Console("set Password hunter22", "Password = (set)", w);
            check("console: set Password (shown hidden)", w.Ok, w.Detail);
            SavedServers.Get(_address, true).Pass = ""; SavedServers.Typed = null;
            yield return Rejoin(w);
            check("no password: turned away and told a password is needed", !w.Ok && w.Detail.Contains("needs a password"), w.Detail);
            check("...with the code that makes the game ask for it", Client.LastVerdict.HasValue && SavedServers.NeedsPassword(Client.LastVerdict.Value), Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Missing : "no verdict");
            SavedServers.Typed = "wrong";
            yield return Rejoin(w);
            check("wrong password: turned away", !w.Ok && w.Detail.Contains("Wrong password"), w.Detail);
            SavedServers.Typed = "hunter22";
            yield return Rejoin(w);
            check("right password: let in", w.Ok, w.Detail);
            SavedServer saved = SavedServers.Get(_address, false);
            check("...and the game keeps it for this server (only after it worked)", saved != null && SavedServers.PasswordFor(_address) == "hunter22", saved != null ? "kept" : "no entry");
            yield return Rejoin(w);
            check("joining again doesn't ask: the kept password is used", w.Ok, w.Detail);
            yield return Console("set Password \"\"", "Password = (none)", w);
        }

        // ---- protected names ----------------------------------------------------------------------

        private static IEnumerator NamesProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string oldId = Client.OfflineId.Value;
            if (string.IsNullOrEmpty(oldId)) { check("names", false, "this test needs a no-Steam client (its id is made up and can be swapped)"); yield break; }
            Client.OfflineId.Value = (0xD5UL << 56 | 12345UL).ToString();       // a different player ...
            string oldKey = Client.IdentityKey.Value;
            Client.IdentityKey.Value = new string('b', 64);
            yield return Rejoin(w);                                             // ... with the same name
            check("a different player can't take a name already used on the server", !w.Ok && w.Detail.Contains("belongs to another player"), w.Detail);
            Client.OfflineId.Value = oldId;
            Client.IdentityKey.Value = oldKey;
            yield return Rejoin(w);
            check("the name's owner still gets in", w.Ok, w.Detail);
        }

        // ---- address bans -------------------------------------------------------------------------

        private static IEnumerator IpBanProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            int seen = Client.VerdictsSeen;
            yield return Console("banip 127.0.0.1 testing", "is banned", w);
            check("console: banip", w.Ok, w.Detail);
            float until = Time.realtimeSinceStartup + 20f;
            while (ClientStarted() && Time.realtimeSinceStartup < until) yield return null;
            check("a player on that address is kicked", !ClientStarted(), Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "");
            yield return Rejoin(w);
            check("...and can't come back from it", !w.Ok && w.Detail.Contains("banned"), w.Detail);
            yield return Console("unbanip 127.0.0.1", "no longer banned", w);
            yield return Rejoin(w);
            check("unbanip lets them in again", w.Ok, w.Detail);
        }

        // ---- permissions ----------------------------------------------------------------------------

        private static IEnumerator PermProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me().SteamUserId.ToString();
            JObject st = Status();
            note("mods with permission levels: " + (st != null ? st["permissionLevels"].ToString(Newtonsoft.Json.Formatting.None) : "?"));
            check("status lists Demo Ship Tools' levels", st != null && st["permissionLevels"]["Demo Ship Tools"] != null, st != null ? st["permissionLevels"].ToString(Newtonsoft.Json.Formatting.None) : "");
            yield return Console("perm " + id + " \"Demo Ship Tools\" Elevated", "is now Elevated in Demo Ship Tools", w);
            check("console: perm <player> <mod> <level>", w.Ok, w.Detail);
            JToken me = DataFile()["Players"].FirstOrDefault(p => (string)p["Id"] == id);
            check("...kept with the player", me != null && (int)me["Levels"]["Demo Ship Tools"] == 1, me != null ? me["Levels"].ToString(Newtonsoft.Json.Formatting.None) : "");
        }

        // ---- stats and exploration ---------------------------------------------------------------

        private static IEnumerator Stats2Probe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            Core.Services.ServiceLocator.GetService<ChatMessageBroadcaster>().ClientSendChatMessage("hello from the stats test");
            yield return new WaitForSecondsRealtime(7f);      // status is rewritten every 5 s
            JObject st = Status();
            check("chat messages are counted", st != null && (int)st["totals"]["messages"] >= 1, st != null ? st["totals"].ToString(Newtonsoft.Json.Formatting.None) : "");
            JObject ex = st != null ? st["planetsExplored"] as JObject : null;
            double best = ex != null && ex.Count > 0 ? ex.Properties().Max(p => (double)p.Value) : 0;
            note("explored: " + (ex != null ? ex.ToString(Newtonsoft.Json.Formatting.None) : "?") + ", space " + (st != null ? st["spaceExploredPercent"] : "?") + "%");
            check("the planet we walked on counts as explored", best > 0, "best " + best + "% (one cell of " + Stats.PlanetCells + " = " + (100.0 / Stats.PlanetCells).ToString("0.000") + "%)");
            check("space explored is counted", st != null && (double)st["spaceExploredPercent"] > 0, st != null ? st["spaceExploredPercent"] + "%" : "");
            yield return Console("explored", "Planets on average", w);
            check("console: explored", w.Ok, w.Detail);
            string ships = Path.Combine(SrvData, "ships.json");
            check("ships.json is written", File.Exists(ships), File.Exists(ships) ? Flat(File.ReadAllText(ships)) : "no file");
            JObject data = DataFile();
            JToken today = ((JArray)data["Days"]).LastOrDefault();
            check("daily numbers are kept (joins, players, messages)", today != null && ((JArray)data["Days"]).Sum(d => (int)d["Joins"]) >= 1 && ((JArray)data["Days"]).Sum(d => (int)d["Messages"]) >= 1, today != null ? today.ToString(Newtonsoft.Json.Formatting.None) : "");
            yield return Console("set TrackBandwidth on", "TrackBandwidth = on", w);
            yield return new WaitForSecondsRealtime(11f);
            st = Status();
            check("bandwidth is counted when switched on", st != null && (double)st["bandwidth"]["outBytesPerSec"] > 0 && (double)st["bandwidth"]["inBytesPerSec"] > 0,
                st != null ? st["bandwidth"].ToString(Newtonsoft.Json.Formatting.None) : "");
            note("bandwidth with one player standing still: " + (st != null ? st["bandwidth"].ToString(Newtonsoft.Json.Formatting.None) : ""));
        }

        // ---- mod parts in the research tree, server and client alike -----------------------------

        private static IEnumerator ConsoleText(string cmd, string until, CheckWrap w)
        {
            string outPath = Path.Combine(SrvData, "console-output.txt");
            long from = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            File.AppendAllText(Path.Combine(SrvData, "commands.txt"), cmd + Environment.NewLine);
            float t = Time.realtimeSinceStartup + 15f;
            string got = "";
            while (Time.realtimeSinceStartup < t) { got = ReadFrom(outPath, from); if (got.Contains(until)) break; yield return new WaitForSecondsRealtime(0.25f); }
            w.Ok = got.Contains(until);
            w.Detail = got;
        }

        /// <summary>part id -> (node id, researched) from the server's `parts` reply.</summary>
        private static System.Collections.Generic.Dictionary<ushort, System.Tuple<ushort, bool>> ParseParts(string text, out string signature)
        {
            System.Collections.Generic.Dictionary<ushort, System.Tuple<ushort, bool>> d = new System.Collections.Generic.Dictionary<ushort, System.Tuple<ushort, bool>>();
            signature = "";
            foreach (string line in text.Split('\n'))
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"part (\d+) .*?: (?:node (\d+) .*|in NO research node), (researched|not researched)");
                if (m.Success) d[ushort.Parse(m.Groups[1].Value)] = System.Tuple.Create(m.Groups[2].Success ? ushort.Parse(m.Groups[2].Value) : (ushort)0, m.Groups[3].Value == "researched");
                System.Text.RegularExpressions.Match s = System.Text.RegularExpressions.Regex.Match(line, @"signature (\w+)");
                if (s.Success) signature = s.Groups[1].Value;
            }
            return d;
        }

        private static IEnumerator ModResearchProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("parts", "research tree:", w);
            string serverSig;
            System.Collections.Generic.Dictionary<ushort, System.Tuple<ushort, bool>> server = ParseParts(w.Detail, out serverSig);
            System.Collections.Generic.List<ResearchTree.PartRow> mine = ResearchTree.ModParts();
            string mySig = Gate.Sha(ResearchTree.Signature()).Substring(0, 12);
            note("server: " + server.Count + " mod parts, tree " + serverSig + "; this client: " + mine.Count + " mod parts, tree " + mySig + " (" + ResearchTree.Nodes().Count + " nodes)");
            foreach (ResearchTree.PartRow p in mine)
            {
                System.Tuple<ushort, bool> s;
                server.TryGetValue(p.Id, out s);
                note("  " + p.Label + " (" + p.Mod + "): client node " + p.NodeId + " " + p.Node + " | server node " + (s != null ? s.Item1.ToString() : "?"));
            }
            string nodesLine = w.Detail.Split('\n').FirstOrDefault(l => l.Contains("nodes: ")) ?? "";
            System.Collections.Generic.HashSet<string> serverNodes = new System.Collections.Generic.HashSet<string>(nodesLine.Substring(nodesLine.IndexOf("nodes: ") + 7 > 6 ? nodesLine.IndexOf("nodes: ") + 7 : 0).Trim().Split(';'));
            System.Collections.Generic.List<string> missing = ResearchTree.Nodes().Select(r => r.id + ":" + r.techName).Where(x => !serverNodes.Contains(x)).ToList();
            check("every research node this player has is on the server too (the ones mods add included)", serverNodes.Count > 10 && missing.Count == 0,
                ResearchTree.Nodes().Count + " here, " + serverNodes.Count + " on the server (tree " + serverSig + " / " + mySig + ")" + (missing.Count > 0 ? "; missing there: " + string.Join(", ", missing.ToArray()) : ""));
            System.Collections.Generic.List<string> wrong = mine.Where(p => !server.ContainsKey(p.Id) || server[p.Id].Item1 != p.NodeId || p.NodeId == 0).Select(p => p.Label + " (client " + p.NodeId + ", server " + (server.ContainsKey(p.Id) ? server[p.Id].Item1.ToString() : "-") + ")").ToList();
            check("every mod part is in the same research node on the server as on the client", mine.Count > 0 && wrong.Count == 0, mine.Count + " part(s)" + (wrong.Count > 0 ? "; different: " + string.Join(", ", wrong.ToArray()) : ""));

            // research a node a mod added, the way a player does, and see the server take it
            ResearchClientProxy proxy = Core.Services.ServiceLocator.GetService<ResearchClientProxy>();
            ResearchNetworkedState global = UnityEngine.Object.FindFirstObjectByType<ResearchNetworkedState>();
            ResearchSettings pick = proxy.Researches.FirstOrDefault(r => r.techName == "Spaceship Necessities" && !global._researched.Contains(r.id))
                                    ?? proxy.Researches.FirstOrDefault(r => r.id >= 1000 && !global._researched.Contains(r.id) && proxy.HasResearchPrerequisite(r.id));
            if (pick == null) { check("a node a mod added can be researched", false, "no such node on this client (is Demo Unlock All loaded?)"); yield break; }
            string id = Me().SteamUserId.ToString();
            yield return Console("op " + id, "", w);
            yield return new WaitForSecondsRealtime(1f);
            PlayerNetworkState ns = MyState();
            if (!ns.IsUsingCheatFreeBuild) ns.GetComponent<PlayerNetworkInputs>().ToggleCheatFreeBuild_ServerRPC();
            float until = Time.realtimeSinceStartup + 5f;
            while (!ns.IsUsingCheatFreeBuild && Time.realtimeSinceStartup < until) yield return null;
            Core.Services.ServiceLocator.GetService<ResearchNetworkService>().ResearchTech(pick.id);    // the research screen's button
            until = Time.realtimeSinceStartup + 6f;
            while (!global._researched.Contains(pick.id) && Time.realtimeSinceStartup < until) yield return null;
            check("researching a node a mod added works on the server (and comes back to the player)", global._researched.Contains(pick.id), "node " + pick.id + " " + pick.techName + ": researched " + global._researched.Contains(pick.id));
            yield return ConsoleText("parts", "research tree:", w);
            server = ParseParts(w.Detail, out serverSig);
            System.Collections.Generic.List<ResearchTree.PartRow> inNode = mine.Where(p => p.NodeId == pick.id).ToList();
            bool allOn = inNode.Count > 0 && inNode.All(p => server.ContainsKey(p.Id) && server[p.Id].Item2);
            check("...and the server then counts that node's mod parts as researched", inNode.Count == 0 || allOn,
                inNode.Count + " mod part(s) in it: " + string.Join(", ", inNode.Select(p => p.Label + " " + (server.ContainsKey(p.Id) && server[p.Id].Item2 ? "yes" : "no")).ToArray()));
            if (ns.IsUsingCheatFreeBuild) ns.GetComponent<PlayerNetworkInputs>().ToggleCheatFreeBuild_ServerRPC();
        }

        // ---- how players see a moving ship ----------------------------------------------------------

        private static IEnumerator ShipSyncProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            bool remote = !Net.ServerStarted;
            string reply, trace;
            if (remote)
            {
                yield return Console("testship 6", "test ship #", w);
                reply = w.Detail;
                trace = Path.Combine(SrvData, "shiptrace.csv");
            }
            else
            {
                reply = ShipTest.Spawn(6f, MelonLoader.Utils.MelonEnvironment.UserDataDirectory);
                trace = ShipTest.TracePath;
            }
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(reply ?? "", "#(\\d+)");
            if (!m.Success) { check("a test ship is flown past the player", false, reply); yield break; }
            uint id = uint.Parse(m.Groups[1].Value);
            Ships.Services.ShipsClientTracker ct = Core.Services.ServiceLocator.GetService<Ships.Services.ShipsClientTracker>();
            Ships.TrackedShipClient ship = null;
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && !(ct.TryGetTrackedShip(id, out ship) && ship.IsVisualLoaded)) yield return null;
            check("the test ship reaches this client and is drawn", ship != null && ship.IsVisualLoaded, reply);
            if (ship == null || !ship.IsVisualLoaded) yield break;
            yield return new WaitForSecondsRealtime(2f);

            System.Collections.Generic.List<double> ct_ = new System.Collections.Generic.List<double>();
            System.Collections.Generic.List<Vector3> cp = new System.Collections.Generic.List<Vector3>();
            System.Collections.Generic.List<float> dts = new System.Collections.Generic.List<float>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 20f)
            {
                ct_.Add(ShipTest.NowMs());
                cp.Add(ship.VisualPhysicsState.Position);
                dts.Add(Time.unscaledDeltaTime);
                yield return null;
            }
            if (remote) yield return Console("testship stop", "stopped", w); else ShipTest.StopTest();
            try
            {
                // both sides' traces, for looking at by hand
                string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DemoVerify");
                Directory.CreateDirectory(dir);
                System.Text.StringBuilder sb = new System.Text.StringBuilder("ms,x,y,z\n");
                for (int i = 0; i < ct_.Count; i++) sb.Append(ct_[i].ToString("0.0")).Append(',').Append(cp[i].x.ToString("0.0000")).Append(',').Append(cp[i].y.ToString("0.0000")).Append(',').Append(cp[i].z.ToString("0.0000")).Append('\n');
                File.WriteAllText(Path.Combine(dir, "shipsync-client.csv"), sb.ToString());
                File.Copy(trace, Path.Combine(dir, "shipsync-server.csv"), true);
                note("client ship space " + ship.SpaceId + "; state position " + ship.State.PhysicsState.Position.ToString("F2") + ", drawn " + ship.VisualPhysicsState.Position.ToString("F2"));
            }
            catch (Exception e) { note("could not save the traces: " + e.Message); }
            yield return new WaitForSecondsRealtime(0.5f);

            // the server's truth
            System.Collections.Generic.List<double> st = new System.Collections.Generic.List<double>();
            System.Collections.Generic.List<Vector3> sp = new System.Collections.Generic.List<Vector3>();
            foreach (string line in ReadFrom(trace, 0).Split('\n').Skip(1))
            {
                string[] f = line.Trim().Split(',');
                if (f.Length < 5) continue;
                double tm; float x, y, z;
                if (double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tm)
                    && float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                    && float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)
                    && float.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                { st.Add(tm); sp.Add(new Vector3(x, y, z)); }
            }
            if (st.Count < 50) { check("the server traced the ship", false, st.Count + " rows in " + trace); yield break; }
            Func<double, Vector3?> at = t =>
            {
                if (t < st[0] || t > st[st.Count - 1]) return null;
                int lo = 0, hi = st.Count - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (st[mid] <= t) lo = mid; else hi = mid; }
                double span = st[hi] - st[lo];
                return span <= 0 ? sp[lo] : Vector3.Lerp(sp[lo], sp[hi], (float)((t - st[lo]) / span));
            };
            // the delay that best lines the client's picture up with the server's truth
            double bestD = 0, bestErr = double.MaxValue;
            for (double d = 0; d <= 1500; d += 2)
            {
                double sum = 0; int n = 0;
                for (int i = 0; i < ct_.Count; i += 2)
                {
                    Vector3? s = at(ct_[i] - d);
                    if (s == null) continue;
                    sum += (cp[i] - s.Value).sqrMagnitude; n++;
                }
                if (n < 100) continue;
                double rms = System.Math.Sqrt(sum / n);
                if (rms < bestErr) { bestErr = rms; bestD = d; }
            }
            // freezes: time the drawn ship stood still while the real one was moving
            double longestFreeze = 0, freeze = 0;
            for (int i = 1; i < cp.Count; i++)
            {
                if ((cp[i] - cp[i - 1]).sqrMagnitude < 1e-8) freeze += ct_[i] - ct_[i - 1]; else freeze = 0;
                if (freeze > longestFreeze) longestFreeze = freeze;
            }
            double fps = dts.Count / dts.Sum();
            long rtt = InstanceFinder.NetworkManager.TimeManager.RoundTripTime;
            string mode = remote ? "joined to the dedicated server" : "single player (host)";
            note("ship sync, " + mode + ": drawn " + bestD.ToString("0") + " ms behind the server; after that, " + (bestErr * 100).ToString("0.0") + " cm off on average; longest freeze " + longestFreeze.ToString("0")
                 + " ms; " + fps.ToString("0") + " fps; RTT " + rtt + " ms; " + st.Count + " server ticks, " + ct_.Count + " frames");
            check("a moving ship is drawn close behind the server (" + mode + ")", bestD <= 250 + rtt, bestD.ToString("0") + " ms behind (RTT " + rtt + " ms)");
            check("...and smoothly: within 15 cm of the true path once that delay is allowed for", bestErr < 0.15, (bestErr * 100).ToString("0.0") + " cm RMS");
            check("...never freezing longer than 150 ms", longestFreeze <= 150, longestFreeze.ToString("0") + " ms");
        }

        // ---- backups ------------------------------------------------------------------------------

        private static IEnumerator BackupsProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return Console("set BackupsToKeep 2", "BackupsToKeep = 2", w);
            string dir = Path.GetDirectoryName((string)Status()["world"]);
            for (int i = 0; i < 4; i++)
            {
                yield return Console("save backup", "Saved.", w);
                if (!w.Ok) { check("save backup", false, w.Detail); yield break; }
                yield return new WaitForSecondsRealtime(1.2f);        // backups are named by the second
            }
            int n = Directory.GetFiles(dir, "backup_*.json").Length;
            check("with BackupsToKeep 2, four saves leave at most 2 (+1 being made) backups", n >= 1 && n <= 3, n + " backup(s) in " + dir);
            yield return Console("set BackupsToKeep 10", "", w);
        }
    }
}
