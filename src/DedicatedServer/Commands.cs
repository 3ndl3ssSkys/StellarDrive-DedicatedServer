// Server commands. The same commands come from three places:
//   the server's console window    type them       full rights
//   <data>/commands.txt            one per line    full rights (for scripts)
//   in-game chat                   "/" + command   operators get everything, other players the
//                                                  read-only ones (help, players, stats, ...)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FishNet.Connection;
using MelonLoader;
using Newtonsoft.Json.Linq;
using Players;

namespace DedicatedServer
{
    internal class Caller
    {
        public string Name;
        public bool IsConsole;
        public ulong Id;
        public NetworkConnection Conn;

        public bool IsOp { get { return IsConsole || Server.Data.IsOp(Id); } }

        public static Caller Console(string from) { return new Caller { Name = from, IsConsole = true }; }

        public static Caller ForPlayer(NetworkConnection conn, TrackedPlayerServer p)
        {
            return new Caller { Name = p != null ? p.Name : "client " + conn.ClientId, Id = p != null ? p.SteamUserId : 0, Conn = conn };
        }

        public void Reply(string line)
        {
            if (IsConsole) Server.ConsoleOut(line);
            else Server.SayTo(Conn, Chat.Server(line));
        }
    }

    internal static class Commands
    {
        private class Cmd
        {
            public string Name, Usage, Help;
            public bool OpOnly;
            public Action<Caller, string[], string> Run;   // caller, words after the name, the rest as one string
        }

        private static readonly List<Cmd> _all = new List<Cmd>();
        private static readonly Dictionary<string, string> _alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { { "list", "players" }, { "who", "players" }, { "?", "help" }, { "quit", "stop" }, { "shutdown", "stop" }, { "config", "set" }, { "settings", "set" },
              { "admin", "op" }, { "deadmin", "deop" }, { "admins", "ops" },
              { "w", "whisper" }, { "msg", "whisper" }, { "tell", "whisper" }, { "pm", "whisper" }, { "dm", "whisper" }, { "reply", "r" },
              { "colour", "color" }, { "unfriend", "friend" }, { "announcement", "announce" } };

        static Commands()
        {
            Add("help", "[command]", "what you can do here", false, Help);
            Add("status", "", "server name, port, world, players, uptime, frame time, last save", false, Status);
            Add("players", "", "who is on now", false, Players);
            Add("stats", "", "players active today / this week / this month, and uptime", false, Stats);
            Add("uptime", "", "how long the server has been up, and its up/down time this week and month", false, Uptime);
            Add("mods", "", "the mods a player needs to join", false, Mods);
            Add("whoami", "", "your name, id and whether you are an operator", false, WhoAmI);
            Add("motd", "", "the message of the day", false, (c, a, r) => c.Reply(Server.Cfg.Motd.Value.Length > 0 ? Server.Cfg.Motd.Value : "(no message of the day)"));
            Add("say", "<message>", "a message to everyone, from the server", true, Say);
            Add("save", "[backup]", "save the world now (backup: keep the previous save as a backup first, as autosaves do)", true, Save);
            Add("kick", "<player> [reason]", "disconnect a player", true, Kick);
            Add("ban", "<player|id> [reason]", "kick and keep out", true, Ban);
            Add("unban", "<player|id>", "let back in", true, Unban);
            Add("bans", "", "who is banned", true, Bans);
            Add("op", "<player|id>", "make an admin (every command in chat, developer tools, Ship Tools at Max)", true, Op);
            Add("deop", "<player|id>", "no longer an admin", true, Deop);
            Add("ops", "", "who the admins are", true, Ops);
            Add("perm", "[<player> <mod> <level>]", "list mods' permission levels, or set a player's level for a mod", true, Perm);
            Add("player", "info|reset|ship|delete <player|id> [ship id] [confirm]", "a player's profile, on now or not: info (where they are), reset (back to their starting spot), ship <id> (onto a ship), delete ... confirm (their character and record; the world is saved as a backup first)", true, PlayerCmd);
            Add("unlink", "<player|id>", "let a player join from a different computer (their character is tied to the game they first joined with)", true, Unlink);
            Add("banip", "<address|player> [reason]", "ban an internet address (a player's last one)", true, BanIp);
            Add("unbanip", "<address>", "lift an address ban", true, UnbanIp);
            Add("ships", "", "the ships in the world", true, ShipsCmd);
            Add("tp", "<player> [to-player]", "go to a player (in game), or move one player to another", true, Tp);
            Add("bring", "<player>", "bring a player to you", true, (c, a, r) =>
            {
                TrackedPlayerServer me = c.IsConsole ? null : Server.PlayerByConnection(c.Conn.ClientId);
                if (me == null) { c.Reply("Only from in game. From the console: tp <player> <to-player>"); return; }
                TrackedPlayerServer who = a.Length > 0 ? Online(r.Trim()) : null;
                if (who == null) { c.Reply("No player on now called " + r.Trim()); return; }
                c.Reply(Ground.Teleport(who, me));
                Server.SayTo(Server.Connection(who.ConnectionId), c.Name + " brought you to them");
            });
#if DSTEST
            Add("tpforward", "<player> <metres> [metres sideways]", "move a player along the ground (for testing far from spawn)", true, (c, a, r) =>
            {
                TrackedPlayerServer who = a.Length > 1 ? Online(a[0]) : null;
                float fwd, side = 0f;
                if (who == null || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fwd)) { c.Reply("usage: tpforward <player> <metres> [metres sideways]"); return; }
                if (a.Length > 2) float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out side);
                c.Reply(Ground.Move(who, fwd, side));
            });
            Add("tpship", "<player> [ship id]", "(test servers only) put a player on top of a ship (the test ship if no id)", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                TrackedPlayerServer who = a.Length > 0 ? Online(a[0]) : null;
                uint sid; global::Ships.TrackedShipServer ship = a.Length > 1 && uint.TryParse(a[1], out sid) ? Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsServerTracker>().GetShip(sid) : ShipTest.Ship;
                if (who == null || ship == null) { c.Reply("usage: tpship <player> [ship id]"); return; }
                c.Reply(Ground.OntoShip(who, ship));
            });
            Add("tpat", "<player> <x> <y> <z>", "(test servers only) put a player on the ground at a point in their planet's coordinates", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                TrackedPlayerServer who = a.Length > 3 ? Online(a[0]) : null;
                double x, y, z;
                if (who == null || !double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y) || !double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                { c.Reply("usage: tpat <player> <x> <y> <z>"); return; }
                c.Reply(Ground.MoveTo(who, x, y, z));
            });
            Add("testhole", "", "(test servers only) destroy the ground pieces under the test ship, behind the server's back", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                c.Reply(Ground.PunchHole(ShipTest.Ship));
            });
#endif
            Add("ground", "", "players' and ships' height above the ground, and how often one was under it", true, (c, a, r) => { foreach (string l in Ground.Report()) c.Reply(l); });
#if DSTEST
            Add("testship", "[speed|stop]", "(test servers only) fly a small test ship past the first player, for measuring how smoothly players see ships", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                if (a.Length > 0 && a[0] == "stop") { ShipTest.StopTest(); c.Reply("test ship stopped"); return; }
                string mode = a.Length > 0 && (a[0] == "ground" || a[0] == "park" || a[0] == "ram") ? a[0] : "";
                if (mode.Length > 0) a = a.Skip(1).ToArray();
                float speed; if (a.Length == 0 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out speed)) speed = 6f;
                if (mode == "ram") { c.Reply(ShipTest.Ram(speed)); return; }
                ShipTest._size = mode == "park" ? Math.Max(3, Math.Min(12, (int)speed)) : 3;
                c.Reply(ShipTest.Spawn(mode == "park" ? 0f : speed, Server.DataDir, mode != ""));
                ShipTest.Mode = mode == "park" ? "park" : "";
            });
#endif
#if DSTEST
            Add("testtree", "near|here|ship", "(test servers only) cut a tree (8-40 m away / next to the first player / under the test ship) and make it due to grow back", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                c.Reply(Respawn.TestTree(a.Length > 0 ? a[0] : "near", ShipTest.Ship));
            });
            Add("testsignal", "[part id|big]", "(test servers only) put a math gate holding NaN (big: 1e300) on the test ship; with an id: that part's output now", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                c.Reply(TestSignal(a));
            });
            Add("testdebris", "", "(test servers only) loose objects in the world and what the debris guards did", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                WorldObjects.Services.WorldObjectsServerTracker o = Core.Services.ServiceLocator.GetService<WorldObjects.Services.WorldObjectsServerTracker>();
                if (a.Length > 0 && ShipTest.Ship != null)
                {
                    // the game's own debris maker, asked for debris of the test ship's first hull part in space 0 ("zero") or where the first player is
                    TrackedPlayerServer p0 = Server.ActivePlayers().FirstOrDefault();
                    global::Ships.Interface.Settings.HullPartSettings floor = new global::Ships.Interface.Services.PartSettingsProvider().GetAllPartSettings().First(x => x.name == "IronFloor") as global::Ships.Interface.Settings.HullPartSettings;
                    global::Ships.Interface.Model.Parts.HullPart hp = new global::Ships.Interface.Model.Parts.HullPart(floor, new global::Ships.Interface.Model.Placement.HullPartPlacement(
                        new global::Ships.Interface.Model.Placement.HullPartPosition { Type = global::Ships.Interface.Model.VoxelPartType.Floor, VoxelCoord = UnityEngine.Vector3Int.zero },
                        global::Ships.Interface.Model.Placement.PartOrientation.Up, global::Ships.Interface.Model.Placement.PartOrientation.Forward));
                    WorldObjects.Interface.Model.WorldObjectPhysicsState st = new WorldObjects.Interface.Model.WorldObjectPhysicsState
                        { SpaceId = a[0] == "zero" ? 0u : p0.SpaceId, Position = p0.Position + UnityEngine.Vector3.up * 2f, Rotation = UnityEngine.Quaternion.identity };
                    UnityEngine.Object.FindFirstObjectByType<global::Ships.Services.ShipDebrisGenerationServerService>().GenerateDebris(hp, st);
                }
                c.Reply("testdebris: world objects " + (o != null ? o.Objects.Count : -1) + ", debris skipped " + NetHealth.DebrisSkipped + ", protected crash parts left out " + Protect.CandidatesDropped);
            });
            Add("testsleep", "<player>|wake <player>|status", "(test servers only) put a player in a bed on the test ship the way the game does / get them up / what the sleep fix has done", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                c.Reply(TestSleep(a));
            });
            Add("testship2", "destroy|frozen|parts [ship id]", "(test servers only) break a hull part as a crash would / is it held still / how many parts", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                uint sid; global::Ships.TrackedShipServer ship = a.Length > 1 && uint.TryParse(a[1], out sid) ? Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsServerTracker>().GetShip(sid) : ShipTest.Ship;
                if (ship == null) { c.Reply("testship2: no ship"); return; }
                string w = a.Length > 0 ? a[0] : "parts";
                if (w == "destroy") { global::Ships.Interface.Model.Placement.HullPartPosition pos = ship.GetHullParts().Keys.First(); ship.DestroyHullPart(pos); }
                UnityEngine.Rigidbody rb = ship.RigidbodyObject != null ? ship.RigidbodyObject.GetComponent<UnityEngine.Rigidbody>() : null;
                global::Ships.Interface.Model.Placement.HullPartPosition first = ship.GetHullParts().Keys.FirstOrDefault();
                c.Reply("testship2 ship " + ship.Id + " hull " + ship.GetHullParts().Count + " parts " + ship.StatefulParts.Count() + " frozen " + (rb != null && rb.constraints == UnityEngine.RigidbodyConstraints.FreezeAll)
                    + " first " + first.VoxelCoord.x + " " + first.VoxelCoord.y + " " + first.VoxelCoord.z + " " + (int)first.Type);
            });
            Add("testspread", "<player>", "(test servers only) give a player a spread spawn spot now, wherever they are", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                TrackedPlayerServer p = a.Length > 0 ? Online(a[0]) : null;
                if (p == null) { c.Reply("testspread <player>"); return; }
                c.Reply(Spawns.TestSpread(p));
            });
            Add("testnet", "kill|deaf|hear|public <port>|status", "(test servers only) end the port's reading thread behind the library's back / stop answering status questions / answer again / check this 'public' port now / what the network guard sees", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
#if DSTEST
                LiteNetLib.NetManager nm = Query.ServerNet;
                string v = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
                if (v == "kill")
                {
                    // what a Windows NotConnected error does in the library: the reading thread ends, the flag is set, the socket stays
                    System.Threading.Thread t = HarmonyLib.AccessTools.Field(typeof(LiteNetLib.NetManager), "_receiveThread").GetValue(nm) as System.Threading.Thread;
                    try { t?.Abort(); } catch { }
                    t?.Join(2000);
                    HarmonyLib.AccessTools.Field(typeof(LiteNetLib.NetManager), "NotConnected").SetValue(nm, true);
                    c.Reply("testnet: the port's reading thread ended (as on a NotConnected error): " + (NetGuard.Dead(nm) ?? "still running"));
                }
                else if (v == "deaf") { NetGuard.TestDeaf = true; c.Reply("testnet: not answering status questions"); }
                else if (v == "hear") { NetGuard.TestDeaf = false; c.Reply("testnet: answering status questions again"); }
                else if (v == "public" && a.Length > 1) { int p; int.TryParse(a[1], out p); NetGuard.TestPublicPort = p; NetGuard.TestPublicNow = true; c.Reply("testnet: public address 127.0.0.1:" + p + ", checked next round"); }
                else c.Reply("testnet: " + NetGuard.Status().ToString(Newtonsoft.Json.Formatting.None));
#endif
            });
            Add("testhang", "<seconds>", "(test servers only) stop the server's main loop for this long (to test hang handling)", true, (c, a, r) =>
            {
                if (!Test.Armed) { c.Reply("Only on a server started by the test harness."); return; }
                float sec; if (a.Length == 0 || !float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) sec = 10f;
                c.Reply("hanging for " + sec + " s");
                Log.Msg("test: the main loop stops for " + sec + " s");
                System.Threading.Thread.Sleep((int)(sec * 1000f));
                Log.Msg("test: the main loop runs again");
            });
#endif
            Add("parts", "", "every mod part: which research node unlocks it here, and whether it's researched; and this server's research tree", true, (c, a, r) =>
            {
                List<ResearchTree.PartRow> rows = ResearchTree.ModParts();
                foreach (ResearchTree.PartRow p in rows)
                    c.Reply("  part " + p.Id + " " + p.Label + " (" + p.Mod + "): " + (p.NodeId != 0 ? "node " + p.NodeId + " " + p.Node : "in NO research node") + ", " + (p.Unlocked ? "researched" : "not researched"));
                List<Research.Interface.Values.ResearchSettings> nodes = ResearchTree.Nodes();
                c.Reply("nodes: " + string.Join(";", nodes.Select(n => n.id + ":" + n.techName).ToArray()));
                c.Reply("research tree: " + nodes.Count + " nodes, signature " + Gate.Sha(ResearchTree.Signature()).Substring(0, 12) + "; " + rows.Count(p => p.NodeId == 0) + " mod part(s) in no node");
            });
            Add("explored", "", "how much of each planet, and of space, players have been to", false, Explored);
            Add("whitelist", "on|off|add <player>|remove <player>|list", "only listed players (and operators) may join", true, Whitelist);
            Add("profile", "[seconds]", "where the server's time goes: every script's update timed for a few seconds", true,
                (c, a, r) => { float sec; ServerPerf.Profile(a.Length > 0 && float.TryParse(a[0], out sec) ? sec : 5f, c); });
            Add("trace", "[seconds]", "record how many movement inputs each player has waiting on the server (trace.csv)", true,
                (c, a, r) => { float sec; ServerPerf.Trace(a.Length > 0 && float.TryParse(a[0], out sec) ? sec : 20f, c); });
            Add("set", "[setting [value]]", "list the settings, or change one (it's saved to server.cfg)", true, Set);
            Add("stop", "[minutes [reason]] | cancel", "save and shut the server down (now, or after warning everyone)", true, (c, a, r) => Timed(c, a, r, false));
            Add("restart", "[minutes [reason]] | cancel", "save and restart the server (after warning everyone; the Server Manager or StartDedicatedServer.bat starts it again)", true, (c, a, r) => Timed(c, a, r, true));
            Add("alert", "<message>", "a message to everyone in the alert style (StyleAlert); also on screen when AlertsOnScreen is on", true, (c, a, r) =>
            {
                if (r.Length == 0) { c.Reply("alert what?"); return; }
                Chat.AlertAll(r);
                if (c.IsConsole) c.Reply("alerted " + Server.ActivePlayers().Count + " player(s)");
            });
            Add("announce", "[{style}] <message>", "a message in the middle of everyone's screen (over menus; Enter closes it), and in chat", true, (c, a, r) =>
            {
                if (r.Length == 0) { c.Reply("announce what? (announce {yellow bold} Event at the station! - style optional)"); return; }
                string text, spec = Chat.SplitSpec(r, null, out text);
                string chat = spec != null ? Chat.Format(spec, (Server.Cfg.MessagePrefix.Value.Length > 0 ? Server.Cfg.MessagePrefix.Value + " " : "") + Chat.Inline(text)) : Chat.Announce(text);
                Server.SayAll(chat);
                Chat.OnScreen(Chat.Format(spec ?? Server.Cfg.StyleAnnounce.Value, Chat.Inline(text)));
                if (c.IsConsole) c.Reply("announced to " + Server.ActivePlayers().Count + " player(s)");
            });

            // ---- between players ----
            Add("whisper", "<player> <message>", "a message only that player sees (also /w, /msg)", false, Whisper);
            Add("r", "<message>", "answer the last player who whispered to you", false, (c, a, r) =>
            {
                string to;
                if (c.IsConsole || !_lastWhisper.TryGetValue(c.Id, out to)) { c.Reply("Nobody has whispered to you yet."); return; }
                Whisper(c, new[] { to }.Concat(a).ToArray(), to + " " + r);
            });
            Add("friend", "[player]", "add a friend (when you have both added each other, you see each other's marker if the server shows friends only); /unfriend <player> to remove; /friend alone lists them", false, Friend);
            Add("friends", "", "your friends, and who has asked", false, (c, a, r) => Friend(c, new string[0], ""));
            Add("color", "[colour]", "your marker's colour: a name (red, orange, yellow, green, teal, blue, purple, pink, white) or #rrggbb", false, Color);

            // ---- items, builds and spawns ----
            Add("give", "<player> <item> [count]", "put items in a player's inventory (any of the game's items; see /items). The " + AdminItems.ProtectToolName + " builds things nobody can break or change", true, Give);
            Add("items", "[part of a name]", "the game's items, for /give", true, (c, a, r) =>
            {
                List<AdminItems.ItemRef> all = AdminItems.All().Where(i => r.Length == 0 || i.Name.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                c.Reply(all.Count == 0 ? "No item matches '" + r + "'." : string.Join(", ", all.Select(i => i.Name).ToArray()));
            });
            Add("protect", "[ship id | here] [movable]", "make a build indestructible and unchangeable (and unmovable, unless 'movable'); here = the ship you stand on or the nearest", true, (c, a, r) =>
            {
                TrackedShipServerRef s = ShipArg(c, a.Length > 0 ? a[0] : "here");
                if (s.Ship == null) { c.Reply(s.Error); return; }
                bool movable = a.Any(x => x.Equals("movable", StringComparison.OrdinalIgnoreCase));
                Protect.Set(s.Ship.Id, movable, c.Name);
                c.Reply("Ship " + s.Ship.Id + " is protected" + (movable ? " (it can still be flown)." : " and stays where it is."));
            });
            Add("unprotect", "[ship id | here]", "lift a build's protection", true, (c, a, r) =>
            {
                TrackedShipServerRef s = ShipArg(c, a.Length > 0 ? a[0] : "here");
                if (s.Ship == null) { c.Reply(s.Error); return; }
                c.Reply(Protect.Clear(s.Ship.Id) ? "Ship " + s.Ship.Id + " isn't protected any more." : "Ship " + s.Ship.Id + " wasn't protected.");
            });
            Add("protected", "", "the protected builds", true, (c, a, r) =>
            {
                if (Server.Data.D.Protected.Count == 0) { c.Reply("No protected builds."); return; }
                foreach (ProtectedShip p in Server.Data.D.Protected) c.Reply("  ship " + p.ShipId + (p.Movable ? " (movable)" : "") + ", by " + p.By + ", " + p.At.ToString("yyyy-MM-dd"));
            });
            Add("ship", "list | where <id> | tp <player|all> <id> | delete <id> | bin | restore <bin id> [here <player>]",
                "ships by name (from their beacon): where one is, take players to it, delete it (kept in the bin for ShipBinDays, with what it was made of) and restore it", true, ShipCmd);
            Add("modset", "<category> <setting> <value>", "change another mod's setting on the server (the Manager's Mods page does this); saves UserData\\MelonPreferences.cfg", true, (c, a, r) =>
            {
                if (a.Length < 3) { c.Reply("modset <category> <setting> <value>"); return; }
                c.Reply(ModSettings.Set(a[0], a[1], Rest(Rest(r, a[0]), a[1])));
            });
            Add("restarts", "[skip]", "the planned restart times (RestartTimes) and warnings (RestartWarnings); skip = skip the next one", true, (c, a, r) =>
            {
                c.Reply(a.Length > 0 && a[0].Equals("skip", StringComparison.OrdinalIgnoreCase) ? Restarts.Skip() : Restarts.Describe());
            });
            Add("resources", "", "the server's memory and CPU now, and the machine's", true, (c, a, r) =>
            {
                Usage.Sample s = Usage.Take(true);
                c.Reply(s == null ? "Not available." : Usage.Line(s));
                if (Usage.Specs.Length > 0) c.Reply("system: " + Usage.Specs);
            });
            Add("setspawn", "[player]", "a player's own respawn spot, where they stand now (you, if no name)", true, (c, a, r) =>
            {
                TrackedPlayerServer p = a.Length > 0 ? Online(a[0]) : (c.IsConsole ? null : Server.PlayerByConnection(c.Conn.ClientId));
                if (p == null) { c.Reply("setspawn <player> (they must be on)"); return; }
                c.Reply(Spawns.SetHere(p));
            });
            Add("clearspawn", "<player>", "a player respawns at the world's starting point again (or gets a new spread spot)", true, (c, a, r) =>
            {
                PlayerRecord rec = a.Length > 0 ? (Online(a[0]) != null ? Server.Data.Player(Online(a[0]).SteamUserId) : Server.Data.PlayerByName(a[0])) : null;
                if (rec == null) { c.Reply("clearspawn <player>"); return; }
                rec.SpawnPlanet = 0; Server.Data.MarkDirty();
                c.Reply(rec.Name + " has no spawn spot of their own now.");
            });

            // ---- the world ----
            Add("respawn", "[now|all]", "trees and ores waiting to grow back; now: grow back what is due, all: everything (both skip anything a ship, build or player is in the way of)", true, (c, a, r) =>
            {
                string w = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                if (w == "now" || w == "all") { c.Reply(Respawn.Sweep(w == "all")); return; }
                foreach (string l in Respawn.Report()) c.Reply(l);
                c.Reply("Trees after " + (Server.Cfg.RespawnTreesMinutes.Value > 0 ? Server.Cfg.RespawnTreesMinutes.Value + " min" : "never") + ", ores after " + (Server.Cfg.RespawnOresMinutes.Value > 0 ? Server.Cfg.RespawnOresMinutes.Value + " min" : "never") + " (set RespawnTreesMinutes / RespawnOresMinutes).");
            });
            Add("fonts", "", "the game's fonts, for font=<name> in a style", true, (c, a, r) =>
            {
                List<string> f = Chat.Fonts;
                c.Reply(f.Count == 0 ? "No fonts found." : string.Join(", ", f.ToArray()));
            });
        }

        // ---- give ----------------------------------------------------------------------------------

        private static void Give(Caller c, string[] a, string r)
        {
            if (a.Length < 2) { c.Reply("give <player> <item> [count]  (item: a name or part of one; /items lists them)"); return; }
            TrackedPlayerServer p = Online(a[0]);
            if (p == null) { c.Reply("No player '" + a[0] + "' is on."); return; }
            int count = 1;
            List<string> words = a.Skip(1).ToList();
            int n;
            if (words.Count > 1 && int.TryParse(words[words.Count - 1], out n)) { count = n; words.RemoveAt(words.Count - 1); }
            if (count < 1 || count > 10000) { c.Reply("The count is 1 to 10000."); return; }
            string name = string.Join(" ", words.ToArray()).Replace('_', ' ');
            List<AdminItems.ItemRef> cands;
            AdminItems.ItemRef item = AdminItems.Find(name, out cands);
            if (item == null) { c.Reply("No item '" + name + "'." + (cands.Count > 0 ? " Did you mean: " + string.Join(", ", cands.Take(8).Select(x => x.Name).ToArray()) + "?" : " /items lists them.")); return; }
            int left = count, given = 0;
            while (left > 0)
            {
                byte chunk = (byte)Math.Min(left, Math.Max(1, (int)item.Stack));
                Items.Model.ItemDrop drop = new Items.Model.ItemDrop { ItemId = item.Id, Quantity = chunk };
                byte fitted;
                try
                {
                    object ops = HarmonyLib.Traverse.Create(p).Field("_inventoryOperations").GetValue();
                    fitted = ops != null ? Convert.ToByte(HarmonyLib.AccessTools.Method(ops.GetType(), "ServerAddItem").Invoke(ops, new object[] { drop })) : (byte)0;   // the game returns how many went in
                    if (ops == null) { p.AddItemToInventory(drop); fitted = chunk; }
                }
                catch (Exception e) { c.Reply("Could not give: " + e.Message); return; }
                given += fitted;
                left -= chunk;
                if (fitted < chunk) break;          // full
            }
            if (given > 0)
            {
                FishNet.Connection.NetworkConnection conn = Server.Connection(p.ConnectionId);
                try { HarmonyLib.Traverse.Create(p).Field("_notifications").Method("NotifyItemReceive", new object[] { new Items.Model.ItemDrop { ItemId = item.Id, Quantity = (byte)Math.Min(255, given) } }).GetValue(); } catch { }
                if (conn != null && !(c.Conn != null && c.Conn.ClientId == p.ConnectionId)) Server.SayTo(conn, Chat.Server((c.IsConsole ? "The server" : c.Name) + " gave you " + given + " x " + item.Name + "."));
            }
            c.Reply(given == count ? "Gave " + p.Name + " " + given + " x " + item.Name + "." : "Gave " + p.Name + " " + given + " of " + count + " x " + item.Name + " (their inventory is full).");
            Log.Msg("give: " + c.Name + " gave " + p.Name + " " + given + " x " + item.Name + " (item " + item.Id + ")");
        }

        private class TrackedShipServerRef { public global::Ships.TrackedShipServer Ship; public string Error = ""; }

        private static TrackedShipServerRef ShipArg(Caller c, string arg)
        {
            TrackedShipServerRef r = new TrackedShipServerRef();
            uint id;
            global::Ships.Services.ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsServerTracker>();
            if (uint.TryParse(arg, out id)) { r.Ship = ships != null ? ships.GetShip(id) : null; if (r.Ship == null) r.Error = "No ship " + id + "."; return r; }
            if (c.IsConsole) { r.Error = "From the console, give a ship id (ships lists them)."; return r; }
            r.Ship = Protect.ShipNear(Server.PlayerByConnection(c.Conn.ClientId));
            if (r.Ship == null) r.Error = "No ship here (stand on it, or within 40 m).";
            return r;
        }

        // ---- whispers, friends, colours ---------------------------------------------------------

        private static readonly Dictionary<ulong, string> _lastWhisper = new Dictionary<ulong, string>();   // player id -> who last whispered to them

        private static void Whisper(Caller c, string[] a, string r)
        {
            if (a.Length < 2) { c.Reply("whisper <player> <message>"); return; }
            TrackedPlayerServer to = Online(a[0]);
            if (to == null) { c.Reply("No player '" + a[0] + "' is on."); return; }
            string text = Rest(r, a[0]);
            if (text.Length == 0) { c.Reply("whisper <player> <message>"); return; }
            if (text.Length > 255) text = text.Substring(0, 255);
            string fromName = c.IsConsole ? "Server" : c.Name;
            FishNet.Connection.NetworkConnection conn = Server.Connection(to.ConnectionId);
            if (conn == null) { c.Reply(to.Name + " can't be reached."); return; }
            Server.SayTo(conn, Chat.Whisper("[from " + fromName.Replace("<", "\u2039") + "] ") + Chat.Whisper(Chat.FromPlayer(text)));
            Net.Event(conn, "ping", fromName, "whisper");
            if (!c.IsConsole) Server.SayTo(c.Conn, Chat.Whisper("[to " + to.Name.Replace("<", "\u2039") + "] ") + Chat.Whisper(Chat.FromPlayer(text)));
            else c.Reply("whispered to " + to.Name);
            _lastWhisper[to.SteamUserId] = fromName;
            if (Server.Cfg.LogWhispers.Value) Log.Msg("[whisper] " + fromName + " -> " + to.Name + ": " + text);
        }

        private static void Friend(Caller c, string[] a, string r)
        {
            if (c.IsConsole) { c.Reply("Friends are for players (in game)."); return; }
            PlayerRecord me = Server.Data.Player(c.Id);
            if (me == null) return;
            if (a.Length == 0)
            {
                List<string> mutual = new List<string>(), asked = new List<string>(), askedMe = new List<string>();
                foreach (PlayerRecord p in Server.Data.D.Players)
                {
                    if (p.Id == me.Id) continue;
                    bool mine = me.Friends.Contains(p.Id), theirs = p.Friends.Contains(me.Id);
                    if (mine && theirs) mutual.Add(p.Name); else if (mine) asked.Add(p.Name); else if (theirs) askedMe.Add(p.Name);
                }
                c.Reply("Friends: " + (mutual.Count > 0 ? string.Join(", ", mutual.ToArray()) : "none yet"));
                if (asked.Count > 0) c.Reply("You asked: " + string.Join(", ", asked.ToArray()) + " (they add you with /friend " + c.Name + ")");
                if (askedMe.Count > 0) c.Reply("Asked you: " + string.Join(", ", askedMe.ToArray()) + " (/friend <name> to accept)");
                c.Reply("Markers shown: " + Server.Cfg.PlayerMarkers.Value + ".");
                return;
            }
            bool remove = a[0].Equals("remove", StringComparison.OrdinalIgnoreCase) || a[0].Equals("-", StringComparison.Ordinal);
            string who = remove ? (a.Length > 1 ? a[1] : "") : a[0];
            TrackedPlayerServer on = Online(who);
            PlayerRecord other = on != null ? Server.Data.Player(on.SteamUserId) : Server.Data.PlayerByName(who);
            if (other == null || other.Id == me.Id) { c.Reply("No player '" + who + "'."); return; }
            if (remove)
            {
                me.Friends.Remove(other.Id); other.Friends.Remove(me.Id);
                c.Reply(other.Name + " is no longer a friend.");
            }
            else if (me.Friends.Contains(other.Id)) { c.Reply(Markers.AreFriends(me, other) ? other.Name + " is already a friend." : "You have asked " + other.Name + " already."); return; }
            else
            {
                me.Friends.Add(other.Id);
                FishNet.Connection.NetworkConnection oc = on != null ? Server.Connection(on.ConnectionId) : null;
                if (Markers.AreFriends(me, other))
                {
                    c.Reply("You and " + other.Name + " are friends now.");
                    if (oc != null) Server.SayTo(oc, Chat.Server(c.Name + " accepted: you are friends now."));
                }
                else
                {
                    c.Reply("Asked " + other.Name + ". You are friends once they add you too.");
                    if (oc != null) { Server.SayTo(oc, Chat.Server(c.Name + " wants to be friends. /friend " + c.Name + " to accept.")); Net.Event(oc, "ping", c.Name, "friend"); }
                }
            }
            Server.Data.MarkDirty();
            Markers.Changed();
        }

        private static void Color(Caller c, string[] a, string r)
        {
            if (c.IsConsole) { c.Reply("A marker colour is a player's own (in game)."); return; }
            PlayerRecord me = Server.Data.Player(c.Id);
            if (me == null) return;
            if (a.Length == 0) { c.Reply("Your marker is " + Markers.ColorOf(me) + (me.MarkerColor.Length == 0 ? " (picked for you)" : "") + ". /color <name or #rrggbb>, or /color auto."); return; }
            if (a[0].Equals("auto", StringComparison.OrdinalIgnoreCase)) me.MarkerColor = "";
            else
            {
                string col = Chat.Color(a[0].ToLowerInvariant());
                if (col == null || col.Length != 7) { c.Reply("Not a colour: " + a[0] + ". Try red, orange, yellow, green, teal, blue, purple, pink, white, or #rrggbb."); return; }
                me.MarkerColor = col;
            }
            Server.Data.MarkDirty();
            Markers.Changed();
            c.Reply(Chat.Format("color=" + Markers.ColorOf(me) + " bold", "Your marker is this colour now."));
        }

#if DSTEST
        private static string TestSignal(string[] a)
        {
            global::Ships.TrackedShipServer ship = ShipTest.Ship;
            if (ship == null) return "testsignal: no test ship (testship park 3 first)";
            ushort pid;
            if (a.Length > 0 && ushort.TryParse(a[0], out pid))
            {
                global::Ships.Interface.Model.Parts.StatefulPart part;
                if (!ship.TryGetStatefulPart(pid, out part)) return "testsignal: no part " + pid;
                object v = part.State.GetType().GetProperty("Output").GetValue(part.State, null);
                return "testsignal part " + pid + " output " + Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            double bad = a.Length > 0 && a[0] == "big" ? 1e300 : double.NaN;
            global::Ships.Interface.Settings.PartSettings gate = new global::Ships.Interface.Services.PartSettingsProvider().GetAllPartSettings()
                .FirstOrDefault(p => p.name.IndexOf("Math", StringComparison.OrdinalIgnoreCase) >= 0 && p.name.IndexOf("Unary", StringComparison.OrdinalIgnoreCase) < 0);
            if (gate == null) return "testsignal: no math gate part in the game's parts";
            ushort id = ship.AddStatefulPart(gate, new global::Ships.Interface.Model.Placement.FloatingPartPlacement(new UnityEngine.Vector3(1f, 0.5f, 1f), global::Ships.Interface.Model.Placement.PartOrientation.Forward, global::Ships.Interface.Model.Placement.PartOrientation.Up));
            global::Ships.Interface.Model.Parts.StatefulPart added;
            if (!ship.TryGetStatefulPart(id, out added)) return "testsignal: the part wasn't added";
            object st = added.State;
            object copy = st.GetType().GetMethod("<Clone>$").Invoke(st, null);
            st.GetType().GetProperty("Output").SetValue(copy, bad, null);
            ship.UpdatePartState(id, (global::Ships.Interface.Model.Parts.StateTypes.IStatefulPartState)copy);
            ship.TryGetStatefulPart(id, out added);
            object now = added.State.GetType().GetProperty("Output").GetValue(added.State, null);
            return "testsignal part " + id + " (" + gate.name + ") set to " + Convert.ToString(bad, CultureInfo.InvariantCulture) + ", reads " + Convert.ToString(now, CultureInfo.InvariantCulture);
        }

        private static ushort _bed;

        private static string TestSleep(string[] a)
        {
            global::Players.Services.PlayersServerTracker players = Core.Services.ServiceLocator.GetService<global::Players.Services.PlayersServerTracker>();
            WorldTracking.Services.WorldTimeServerTracker time = Core.Services.ServiceLocator.GetService<WorldTracking.Services.WorldTimeServerTracker>();
            if (a.Length > 0 && a[0] == "status")
                return "testsleep status: all asleep " + players.AreAllPlayersWaiting() + ", time speed " + time.CurrentTimeSpeed.ToString("0.0", CultureInfo.InvariantCulture) + "x, world time " + time.CurrentWorldTime.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; fix " + (Server.Cfg.SleepAirFlow.Value ? "on" : "off") + ": ticks " + Sleep.Ticks + ", part steps " + Sleep.Steps + ", ship time " + Sleep.SimulatedSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                    + " s, cost " + (Sleep.CostMs / Math.Max(1, Sleep.Ticks)).ToString("0.000", CultureInfo.InvariantCulture) + " ms a tick (worst " + Sleep.WorstTickMs.ToString("0.00", CultureInfo.InvariantCulture) + ")";
            bool wake = a.Length > 1 && a[0] == "wake";
            TrackedPlayerServer who = Online(wake ? a[1] : (a.Length > 0 ? a[0] : ""));
            if (who == null) return "testsleep: no such player";
            object ns = HarmonyLib.Traverse.Create(who).Property("NetworkState").GetValue();
            if (wake)
            {
                HarmonyLib.AccessTools.Method(ns.GetType(), "ServerSetState").Invoke(ns, new object[] { new global::Players.Interface.Model.PlayerState { Type = global::Players.Interface.Model.PlayerStateType.Walking } });
                return "testsleep: " + who.Name + " got up (" + who.StateVal + ")";
            }
            global::Ships.TrackedShipServer ship = ShipTest.Ship;
            if (ship == null) return "testsleep: no test ship (testship park 3 first)";
            global::Ships.Interface.Model.Parts.StatefulPart bp;
            if (_bed == 0 || !ship.TryGetStatefulPart(_bed, out bp))
            {
                global::Ships.Interface.Settings.PartSettings bed = new global::Ships.Interface.Services.PartSettingsProvider().GetAllPartSettings().FirstOrDefault(p => p.name == "Bed");
                if (bed == null) return "testsleep: no bed in the game's parts";
                _bed = ship.AddStatefulPart(bed, new global::Ships.Interface.Model.Placement.FloatingPartPlacement(new UnityEngine.Vector3(1f, 0.5f, 1f), global::Ships.Interface.Model.Placement.PartOrientation.Forward, global::Ships.Interface.Model.Placement.PartOrientation.Up));
            }
            // what the bed's "Wait" request does on the server (ShipInteractionService): the game's own StartWaiting
            bool ok = who.StartWaiting(new global::Ships.Interface.Model.Parts.PartContext(ship.Id, _bed));
            return "testsleep: " + who.Name + (ok ? " is asleep in bed " + _bed + " on ship #" + ship.Id : " could not sleep (" + who.StateVal + ")") + "; all asleep " + players.AreAllPlayersWaiting();
        }
#endif

        private static void Add(string name, string usage, string help, bool opOnly, Action<Caller, string[], string> run)
        {
            _all.Add(new Cmd { Name = name, Usage = usage, Help = help, OpOnly = opOnly, Run = run });
        }

        public static void Run(string line, Caller c)
        {
            line = (line ?? "").Trim();
            if (line.StartsWith("/")) line = line.Substring(1);
            if (line.Length == 0) return;
            Server.Step = "command from " + c.Name + ": " + (line.Length > 60 ? line.Substring(0, 60) : line);
            if (c.IsConsole) Server.ConsoleOut("> " + line);
            int sp = line.IndexOf(' ');
            string name = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            string target;
            if (_alias.TryGetValue(name, out target)) name = target;
            Cmd cmd = _all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cmd == null) { c.Reply("Unknown command '" + name + "'. Try " + (c.IsConsole ? "" : "/") + "help."); return; }
            if (cmd.OpOnly && !c.IsOp) { c.Reply("Only operators can use " + (c.IsConsole ? "" : "/") + cmd.Name + "."); return; }
            if (!c.IsConsole) Log.Msg("[" + c.Name + "] /" + line);
            string[] words = rest.Length == 0 ? new string[0] : rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            try { cmd.Run(c, words, rest); }
            catch (Exception e) { c.Reply(cmd.Name + " failed: " + e.Message); Log.Warn(cmd.Name + ": " + e); }
        }

        // ---- read-only -----------------------------------------------------------------------

        private static void Help(Caller c, string[] a, string r)
        {
            string p = c.IsConsole ? "" : "/";
            if (a.Length > 0)
            {
                string n = a[0].TrimStart('/'); string t; if (_alias.TryGetValue(n, out t)) n = t;
                Cmd cmd = _all.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (cmd == null) { c.Reply("No command '" + n + "'."); return; }
                c.Reply(p + cmd.Name + (cmd.Usage.Length > 0 ? " " + cmd.Usage : "") + " - " + cmd.Help + (cmd.OpOnly ? " (operators)" : ""));
                return;
            }
            IEnumerable<Cmd> mine = _all.Where(x => !x.OpOnly || c.IsOp);
            if (c.IsConsole)
            {
                foreach (Cmd cmd in mine) c.Reply("  " + (cmd.Name + (cmd.Usage.Length > 0 ? " " + cmd.Usage : "")).PadRight(44) + cmd.Help);
            }
            else
            {
                c.Reply("Commands: " + string.Join(", ", mine.Select(x => "/" + x.Name).ToArray()));
                c.Reply("/help <command> explains one." + (c.IsOp ? "" : " Operators can do more."));
            }
        }

        private static void Status(Caller c, string[] a, string r)
        {
            int on = Server.ActivePlayers().Count;
            c.Reply(Server.Cfg.ServerName.Value + " - UDP port " + Server.Port + ", world " + Server.WorldPath);
            c.Reply("Players: " + on + "/" + Server.MaxPlayers + "   Up: " + ServerData.Span((DateTime.UtcNow - Server.Data.RunStart).TotalSeconds)
                    + (on == 0 && Server.Cfg.PauseWhenEmpty.Value ? "   (world paused: nobody on)" : ""));
            c.Reply("Loop: every " + Server.AvgFrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms (worst " + Server.MaxFrameMs.ToString("0.0", CultureInfo.InvariantCulture)
                    + "), working " + ServerPerf.AvgWorkMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms of it (worst " + ServerPerf.MaxWorkMs.ToString("0.0", CultureInfo.InvariantCulture) + ")   Last save: " + (Server.LastSaveUtc.HasValue ? ServerData.Span((DateTime.UtcNow - Server.LastSaveUtc.Value).TotalSeconds) + " ago (" + Server.LastSaveNote + ")" : "not yet this run"));
            c.Reply(Server.Stalls == 0 ? "No stalls this run." : "Stalls this run (a frame over 250 ms): " + Server.Stalls + ", longest " + Server.LongestStallMs.ToString("0") + " ms. Last: " + ServerPerf.LastStall);
        }

        private static void Players(Caller c, string[] a, string r)
        {
            IReadOnlyList<TrackedPlayerServer> ps = Server.ActivePlayers();
            c.Reply(ps.Count + "/" + Server.MaxPlayers + " on");
            DateTime now = DateTime.UtcNow;
            foreach (TrackedPlayerServer p in ps)
            {
                SessionRecord s = Server.Data.D.Sessions.LastOrDefault(x => x.Id == p.SteamUserId && x.End == null);
                c.Reply("  " + p.Name + (Server.Data.IsOp(p.SteamUserId) ? " [op]" : "") + (c.IsOp ? "  id " + p.SteamUserId : "")
                        + (s != null ? "  on for " + ServerData.Span((now - s.Start).TotalSeconds) : ""));
            }
        }

        private static void Stats(Caller c, string[] a, string r)
        {
            DateTime now = DateTime.UtcNow;
            c.Reply("Online now: " + Server.ActivePlayers().Count + "/" + Server.MaxPlayers + "   most at once today: " + Server.Data.PeakSince(now.AddDays(-1))
                    + ", this week: " + Server.Data.PeakSince(now.AddDays(-7)));
            foreach (var w in new[] { new { N = "Last 24 hours", D = 1.0 }, new { N = "Last 7 days", D = 7.0 }, new { N = "Last 30 days", D = 30.0 } })
            {
                ServerData.Activity act = Server.Data.ActivitySince(now.AddDays(-w.D));
                c.Reply(w.N + ": " + act.Players + " player(s), " + act.Visits + " visit(s), " + act.Hours.ToString("0.0", CultureInfo.InvariantCulture) + " player-hours");
            }
            c.Reply("Players ever seen: " + Server.Data.D.Players.Count);
            Uptime(c, a, r);
        }

        private static void Uptime(Caller c, string[] a, string r)
        {
            c.Reply("Up for " + ServerData.Span((DateTime.UtcNow - Server.Data.RunStart).TotalSeconds) + " (since " + Server.Data.RunStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ")");
            TimeSpan? down = Server.Data.DowntimeBeforeThisRun;
            if (down.HasValue) c.Reply("Before that it was down " + ServerData.Span(down.Value.TotalSeconds) + (Server.Data.PreviousRun != null && !Server.Data.PreviousRun.Clean ? " (the last run ended in a crash)" : ""));
            foreach (var w in new[] { new { N = "Last 7 days", D = 7.0 }, new { N = "Last 30 days", D = 30.0 } })
            {
                ServerData.Uptime u = Server.Data.UptimeOver(w.D);
                c.Reply(w.N + ": up " + u.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "% (" + ServerData.Span(u.Up) + " up, " + ServerData.Span(u.Down) + " down), "
                        + u.Starts + " start(s), " + u.Crashes + " crash(es)");
            }
        }

        private static void Mods(Caller c, string[] a, string r)
        {
            if (!Server.Cfg.RequireMods.Value) { c.Reply("This server doesn't check mods (RequireMods is off)."); return; }
            c.Reply("To join you need: " + string.Join(", ", Gate.Required().Select(m => m.Key + (m.Value.Length > 0 && Server.Cfg.MatchModVersions.Value ? " " + m.Value : "")).ToArray()));
        }

        private static void WhoAmI(Caller c, string[] a, string r)
        {
            if (c.IsConsole) { c.Reply("You are the server console (every command)."); return; }
            c.Reply("You are " + c.Name + ", id " + c.Id + (c.IsOp ? ", an operator." : ", not an operator."));
        }

        // ---- operator ------------------------------------------------------------------------

        private static void Say(Caller c, string[] a, string r)
        {
            if (r.Length == 0) { c.Reply("say what? (say {color=blue bold size=120} Hello - style optional)"); return; }
            string text, spec = Chat.SplitSpec(r, null, out text);
            Server.SayAll(spec != null ? Chat.Format(spec, (Server.Cfg.MessagePrefix.Value.Length > 0 ? Server.Cfg.MessagePrefix.Value + " " : "") + Chat.Inline(text)) : Chat.Announce(text));
            if (c.IsConsole) c.Reply("said to " + Server.ActivePlayers().Count + " player(s)");
        }

        private static void Timed(Caller c, string[] a, string r, bool restart)
        {
            if (a.Length > 0 && a[0].Equals("cancel", StringComparison.OrdinalIgnoreCase)) { c.Reply(Chat.Cancel()); return; }
            float minutes;
            if (a.Length > 0 && float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) && minutes > 0)
            {
                c.Reply(Chat.Schedule(restart, minutes * 60f, Rest(r, a[0])));
                return;
            }
            c.Reply(restart ? "Saving and restarting..." : "Saving and stopping...");
            Server.Stop("'" + (restart ? "restart" : "stop") + "' from " + c.Name + (r.Length > 0 && a.Length > 0 && !char.IsDigit(a[0][0]) ? ": " + r : ""), restart);
        }

        private static void Save(Caller c, string[] a, string r)
        {
            bool backup = a.Length > 0 && a[0].ToLowerInvariant() == "backup";      // an autosave-style save: keeps the previous world as a backup first
            string err = Server.SaveNow("'save' from " + c.Name, backup, e => c.Reply(e == null ? "Saved." : "Not saved: " + e));
            if (err != null) c.Reply("Not saved: " + err);
        }

        private static void Kick(Caller c, string[] a, string r)
        {
            if (a.Length == 0) { c.Reply("kick <player> [reason]"); return; }
            TrackedPlayerServer p = Online(a[0]);
            if (p == null) { c.Reply("No one called '" + a[0] + "' is on."); return; }
            string reason = Rest(r, a[0]);
            Server.Kick(Server.Connection(p.ConnectionId), "You were kicked" + (reason.Length > 0 ? ": " + reason : "."));
            Server.SayAll(p.Name + " was kicked.");
            c.Reply("Kicked " + p.Name + ".");
        }

        private static void Ban(Caller c, string[] a, string r)
        {
            if (a.Length == 0) { c.Reply("ban <player|id> [reason]"); return; }
            ulong id; string name;
            if (!Resolve(a[0], out id, out name)) { c.Reply("No player '" + a[0] + "' (on now, seen before, or a numeric id)."); return; }
            if (!c.IsConsole && id == c.Id) { c.Reply("You can't ban yourself."); return; }
            string reason = Rest(r, a[0]);
            if (!Server.Data.IsBanned(id)) { Server.Data.D.Bans.Add(new BanRecord { Id = id, Name = name ?? "", Reason = reason, At = DateTime.UtcNow }); Server.Data.CountBan(); }
            Server.Data.MarkDirty(); Server.Data.Flush();
            TrackedPlayerServer p = Server.ActivePlayers().FirstOrDefault(x => x.SteamUserId == id);
            if (p != null) Server.Kick(Server.Connection(p.ConnectionId), "You are banned from this server" + (reason.Length > 0 ? ": " + reason : "."), Gate.CodeBanned);
            c.Reply("Banned " + (name ?? id.ToString()) + ".");
        }

        private static void Unban(Caller c, string[] a, string r)
        {
            if (a.Length == 0) { c.Reply("unban <player|id>"); return; }
            int n = Server.Data.D.Bans.RemoveAll(b => b.Id.ToString() == a[0] || string.Equals(b.Name, r, StringComparison.OrdinalIgnoreCase));
            Server.Data.MarkDirty(); Server.Data.Flush();
            c.Reply(n > 0 ? "Unbanned." : "No ban for '" + r + "'.");
        }

        private static void Bans(Caller c, string[] a, string r)
        {
            if (Server.Data.D.Bans.Count == 0) { c.Reply("Nobody is banned."); return; }
            foreach (BanRecord b in Server.Data.D.Bans)
                c.Reply("  " + (b.Ip.Length > 0 ? "address " + b.Ip : b.Name + "  id " + b.Id) + "  " + b.At.ToLocalTime().ToString("yyyy-MM-dd") + (b.Reason.Length > 0 ? "  \"" + b.Reason + "\"" : ""));
        }

        private static void Op(Caller c, string[] a, string r)
        {
            ulong id; string name;
            if (!Resolve(r, out id, out name)) { c.Reply("No player '" + r + "' (on now, seen before, or a numeric id)."); return; }
            PlayerRecord p = Server.Data.Touch(id, name);
            p.Op = true;
            Server.Data.Flush();
            c.Reply((name ?? id.ToString()) + " is now an operator.");
            TrackedPlayerServer on = Server.ActivePlayers().FirstOrDefault(x => x.SteamUserId == id);
            if (on != null && (c.IsConsole || on.SteamUserId != c.Id)) Server.SayTo(Server.Connection(on.ConnectionId), "You are now an operator. /help lists your commands.");
        }

        private static void Deop(Caller c, string[] a, string r)
        {
            ulong id; string name;
            if (!Resolve(r, out id, out name)) { c.Reply("No player '" + r + "'."); return; }
            PlayerRecord p = Server.Data.Player(id);
            if (p == null || !p.Op) { c.Reply((name ?? r) + " isn't an operator."); return; }
            p.Op = false;
            Server.Data.MarkDirty(); Server.Data.Flush();
            c.Reply((name ?? id.ToString()) + " is no longer an operator.");
        }

        private static void Ops(Caller c, string[] a, string r)
        {
            List<PlayerRecord> ops = Server.Data.D.Players.Where(p => p.Op).ToList();
            c.Reply(ops.Count == 0 ? "No operators. Make one from the console: op <name>" : "Operators: " + string.Join(", ", ops.Select(p => p.Name + " (" + p.Id + ")").ToArray()));
        }

        private static void Perm(Caller c, string[] a, string r)
        {
            Dictionary<string, string[]> declared = Rules.Declared();
            if (a.Length < 3)
            {
                if (declared.Count == 0) c.Reply("No mod here declares permission levels.");
                foreach (KeyValuePair<string, string[]> kv in declared) c.Reply("  " + kv.Key + ": " + string.Join(" < ", kv.Value));
                c.Reply("perm <player> <mod> <level>  (the mod name in quotes if it has spaces; the level by name or number). Admins always have the highest.");
                return;
            }
            // player first, then the mod (possibly quoted), then the level
            string player = a[0];
            string rest = Rest(r, player);
            string mod, levelText;
            if (rest.StartsWith("\""))
            {
                int close = rest.IndexOf('"', 1);
                if (close < 0) { c.Reply("Missing closing quote."); return; }
                mod = rest.Substring(1, close - 1); levelText = rest.Substring(close + 1).Trim();
            }
            else
            {
                int sp = rest.LastIndexOf(' ');
                mod = rest.Substring(0, sp).Trim(); levelText = rest.Substring(sp + 1).Trim();
            }
            string key = declared.Keys.FirstOrDefault(k => string.Equals(k, mod, StringComparison.OrdinalIgnoreCase));
            if (key == null) { c.Reply("No mod '" + mod + "' with permission levels. perm lists them."); return; }
            string[] levels = declared[key];
            int level = Array.FindIndex(levels, l => string.Equals(l, levelText, StringComparison.OrdinalIgnoreCase));
            if (level < 0 && (!int.TryParse(levelText, out level) || level < 0 || level >= levels.Length)) { c.Reply(key + " levels: " + string.Join(", ", levels)); return; }
            ulong id; string name;
            if (!Resolve(player, out id, out name)) { c.Reply("No player '" + player + "'."); return; }
            PlayerRecord p = Server.Data.Touch(id, name);
            p.Levels[key] = level;
            Server.Data.Flush();
            c.Reply((name ?? id.ToString()) + " is now " + levels[level] + " in " + key + ".");
        }

        private static void PlayerCmd(Caller c, string[] a, string r)
        {
            const string usage = "player info <player> | player reset <player> | player ship <player> <ship id> | player delete <player> confirm";
            if (a.Length < 2) { c.Reply(usage); return; }
            string verb = a[0].ToLowerInvariant();
            // the name can have spaces: everything after the verb, less a trailing ship id or 'confirm'
            List<string> rest = a.Skip(1).ToList();
            bool confirm = rest.Count > 1 && rest[rest.Count - 1].Equals("confirm", StringComparison.OrdinalIgnoreCase);
            if (confirm) rest.RemoveAt(rest.Count - 1);
            uint shipId = 0;
            if (verb == "ship" && rest.Count > 1 && uint.TryParse(rest[rest.Count - 1].TrimStart('#'), out shipId)) rest.RemoveAt(rest.Count - 1);
            ulong id; string name;
            string who = string.Join(" ", rest.ToArray());
            if (!Resolve(who, out id, out name)) { c.Reply("No player '" + who + "' (on now, seen before, or a numeric id)."); return; }
            switch (verb)
            {
                case "info":
                {
                    PlayerRecord rec = Server.Data.Player(id);
                    c.Reply((name ?? id.ToString()) + " (" + id + "): " + Profiles.Where(id)
                        + (rec != null ? "; " + rec.Visits + " visit(s), " + (rec.Seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " h played" + (rec.SpawnPlanet != 0 ? ", has their own starting spot" : "") + (rec.Op ? ", admin" : "") : "; no record"));
                    return;
                }
                case "reset":
                    c.Reply(Profiles.Reset(id, name));
                    return;
                case "ship":
                    if (shipId == 0) { c.Reply("player ship <player> <ship id> (the ids are in 'ship list')"); return; }
                    c.Reply(Profiles.ToShip(id, name, shipId));
                    return;
                case "delete":
                    if (!c.IsConsole && id == c.Id) { c.Reply("You can't delete yourself."); return; }
                    if (!confirm)
                    {
                        c.Reply("This deletes " + (name ?? id.ToString()) + "'s character (inventory, place, bed) and the server's record of them (play time, permissions, friends, spawn spot, the game they're tied to). "
                            + "Bans, the whitelist and admin status stay. The world is saved first, keeping the previous save as a backup. To do it: player delete " + who + " confirm");
                        return;
                    }
                    c.Reply(Profiles.Delete(id, name, c.Name));
                    return;
                default:
                    c.Reply(usage);
                    return;
            }
        }

        private static void Unlink(Caller c, string[] a, string r)
        {
            ulong id; string name;
            if (!Resolve(r, out id, out name)) { c.Reply("No player '" + r + "'."); return; }
            PlayerRecord p = Server.Data.Player(id);
            if (p == null || p.Token.Length == 0) { c.Reply((name ?? r) + " isn't tied to a game yet."); return; }
            p.Token = "";
            Server.Data.MarkDirty(); Server.Data.Flush();
            c.Reply((name ?? id.ToString()) + " can join from their next game (it becomes theirs).");
        }

        private static void BanIp(Caller c, string[] a, string r)
        {
            if (a.Length == 0) { c.Reply("banip <address|player> [reason]"); return; }
            string ip = a[0];
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(ip, out parsed))
            {
                ulong id; string name;
                if (!Resolve(a[0], out id, out name)) { c.Reply("'" + a[0] + "' is neither an address nor a known player."); return; }
                PlayerRecord p = Server.Data.Player(id);
                if (p == null || p.LastIp.Length == 0) { c.Reply((name ?? a[0]) + " has no known address yet."); return; }
                ip = p.LastIp;
            }
            string reason = Rest(r, a[0]);
            if (Server.Data.IpBan(ip) == null) { Server.Data.D.Bans.Add(new BanRecord { Ip = ip, Reason = reason, At = DateTime.UtcNow }); Server.Data.CountBan(); }
            Server.Data.MarkDirty(); Server.Data.Flush();
            foreach (TrackedPlayerServer on in Server.ActivePlayers().ToList())
            {
                FishNet.Connection.NetworkConnection conn = Server.Connection(on.ConnectionId);
                if (conn != null && Gate.AddressOf(conn) == ip) Server.Kick(conn, "You are banned from this server" + (reason.Length > 0 ? ": " + reason : "."), Gate.CodeBanned);
            }
            c.Reply("Address " + ip + " is banned.");
        }

        private static void UnbanIp(Caller c, string[] a, string r)
        {
            int n = Server.Data.D.Bans.RemoveAll(b => b.Ip.Length > 0 && b.Ip == r.Trim());
            Server.Data.MarkDirty(); Server.Data.Flush();
            c.Reply(n > 0 ? "Address " + r.Trim() + " is no longer banned." : "No ban on " + r.Trim() + ".");
        }

        private static void ShipsCmd(Caller c, string[] a, string r)
        {
            global::Ships.Services.ShipsServerTracker t = Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsServerTracker>();
            if (t == null) { c.Reply("No ships tracker yet."); return; }
            c.Reply(t.Ships.Count + " ship(s):");
            foreach (global::Ships.TrackedShipServer s in t.Ships.OrderByDescending(x => x.State.PartCount).Take(c.IsConsole ? 200 : 10))
                c.Reply("  #" + s.Id + "  " + s.State.PartCount + " parts" + (s.IsRigidbodyLoaded ? "" : "  (asleep)"));
        }

        private static void Explored(Caller c, string[] a, string r)
        {
            foreach (KeyValuePair<string, double> kv in DedicatedServer.Stats.PlanetPercents())
                c.Reply("  " + kv.Key + ": " + kv.Value.ToString("0.00", CultureInfo.InvariantCulture) + "% of its surface");
            c.Reply("Planets on average: " + DedicatedServer.Stats.WorldPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%   Space: " + DedicatedServer.Stats.SpacePercent.ToString("0.000", CultureInfo.InvariantCulture) + "% of the system");
        }

        private static void Whitelist(Caller c, string[] a, string r)
        {
            string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
            string arg = Rest(r, a.Length > 0 ? a[0] : "");
            ulong id; string name;
            switch (sub)
            {
                case "on": Server.Cfg.Set(Server.Cfg.Whitelist, "on"); c.Reply("Whitelist on: only listed players and operators may join."); return;
                case "off": Server.Cfg.Set(Server.Cfg.Whitelist, "off"); c.Reply("Whitelist off: anyone may join."); return;
                case "add":
                    if (!Resolve(arg, out id, out name)) { c.Reply("No player '" + arg + "' (they must have tried to join once, or give their numeric id)."); return; }
                    if (!Server.Data.D.Whitelist.Contains(id)) Server.Data.D.Whitelist.Add(id);
                    Server.Data.Touch(id, name); Server.Data.Flush();
                    c.Reply("Added " + (name ?? id.ToString()) + " to the whitelist."); return;
                case "remove":
                    if (!Resolve(arg, out id, out name)) { c.Reply("No player '" + arg + "'."); return; }
                    Server.Data.D.Whitelist.Remove(id); Server.Data.MarkDirty(); Server.Data.Flush();
                    c.Reply("Removed " + (name ?? id.ToString()) + " from the whitelist."); return;
                default:
                    c.Reply("Whitelist is " + (Server.Cfg.Whitelist.Value ? "ON" : "off") + ". On it: " +
                            (Server.Data.D.Whitelist.Count == 0 ? "nobody" : string.Join(", ", Server.Data.D.Whitelist.Select(w => { PlayerRecord p = Server.Data.Player(w); return p != null ? p.Name : w.ToString(); }).ToArray())));
                    return;
            }
        }

        private static void Set(Caller c, string[] a, string r)
        {
            if (a.Length == 0)
            {
                foreach (MelonPreferences_Entry e in Server.Cfg.Entries)
                    c.Reply("  " + e.Identifier + " = " + ServerConfig.Show(e) + (ServerConfig.RestartOnly.Contains(e.Identifier) ? "  (restart)" : ""));
                c.Reply((c.IsConsole ? "" : "/") + "set <setting> <value> changes one.");
                return;
            }
            MelonPreferences_Entry entry = Server.Cfg.Find(a[0]);
            if (entry == null) { c.Reply("No setting '" + a[0] + "'. " + (c.IsConsole ? "" : "/") + "set lists them."); return; }
            if (a.Length == 1) { c.Reply(entry.Identifier + " = " + ServerConfig.Show(entry) + "  - " + entry.Description); return; }
            string err = Server.Cfg.Set(entry, Rest(r, a[0]));
            if (err != null) { c.Reply(entry.Identifier + ": " + err); return; }
            c.Reply(entry.Identifier + " = " + ServerConfig.Show(entry) + (ServerConfig.RestartOnly.Contains(entry.Identifier) ? " (takes effect when the server restarts)" : ""));
            Log.Msg("setting " + entry.Identifier + " = " + ServerConfig.Show(entry) + " (by " + c.Name + ")");
            Server.ApplySetting(entry.Identifier);
        }

        // ---- helpers -------------------------------------------------------------------------

        private static string Rest(string all, string first)
        {
            all = all ?? "";
            int i = all.IndexOf(first, StringComparison.Ordinal);
            return i < 0 ? "" : all.Substring(i + first.Length).Trim();
        }

        private static void ShipCmd(Caller c, string[] a, string r)
        {
            string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
            global::Ships.Services.ShipsServerTracker t = Core.Services.ServiceLocator.GetService<global::Ships.Services.ShipsServerTracker>();
            if (t == null) { c.Reply("The world isn't loaded."); return; }
            uint id;
            Func<int, uint> idAt = i => a.Length > i && uint.TryParse(a[i].TrimStart('#'), out id) ? id : 0u;
            switch (sub)
            {
                case "list":
                {
                    string filter = a.Length > 1 ? Rest(r, a[0]).ToLowerInvariant() : "";
                    List<global::Ships.TrackedShipServer> roots = t.Ships.Where(s => ShipAdmin.RootOf(s.Id) == s.Id).ToList();
                    int shown = 0;
                    foreach (global::Ships.TrackedShipServer s in roots.OrderByDescending(s => s.PartCount))
                    {
                        string name = ShipAdmin.GroupName(s.Id);
                        if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter) && s.Id.ToString() != filter) continue;
                        if (++shown > 20) { c.Reply("  ... (ship list <part of a name> to narrow it)"); break; }
                        c.Reply("  #" + s.Id + "  " + (name.Length > 0 ? name : "(no beacon)") + ", " + s.PartCount + " parts, " + ShipAdmin.WhereText(s));
                    }
                    if (shown == 0) c.Reply(roots.Count == 0 ? "No ships." : "No ship matches.");
                    return;
                }
                case "where":
                {
                    global::Ships.TrackedShipServer s;
                    if (!t.TryGetTrackedShip(idAt(1), out s)) { c.Reply("ship where <id>"); return; }
                    string name = ShipAdmin.GroupName(ShipAdmin.RootOf(s.Id));
                    c.Reply("#" + s.Id + (name.Length > 0 ? " \"" + name + "\"" : "") + ": " + ShipAdmin.WhereText(s));
                    return;
                }
                case "tp":
                {
                    if (a.Length < 3) { c.Reply("ship tp <player|all> <id>"); return; }
                    uint sid = idAt(2);
                    List<TrackedPlayerServer> who = a[1].Equals("all", StringComparison.OrdinalIgnoreCase) ? Server.ActivePlayers().ToList() : new List<TrackedPlayerServer> { Online(a[1]) };
                    if (who.Count == 0 || who[0] == null) { c.Reply("No player on now called " + a[1]); return; }
                    foreach (TrackedPlayerServer p in who)
                    {
                        c.Reply(ShipAdmin.TakeTo(p, sid));
                        Server.SayTo(Server.Connection(p.ConnectionId), Chat.Server("You were moved to ship #" + sid + "."));
                    }
                    return;
                }
                case "delete":
                    if (idAt(1) == 0) { c.Reply("ship delete <id>"); return; }
                    c.Reply(ShipAdmin.Delete(idAt(1), c.Name));
                    return;
                case "bin":
                {
                    List<JObject> bin = ShipAdmin.Bin().Where(o => true).ToList();
                    if (bin.Count == 0) { c.Reply("The ship bin is empty."); return; }
                    foreach (JObject o in bin.Take(15))
                        c.Reply("  " + (string)o["id"] + "  " + ((string)o["name"] != "" ? (string)o["name"] : "ship #" + (string)o["shipId"]) + ", " + (int)o["parts"] + " parts, deleted by " + (string)o["by"]
                            + "; recovered " + ShipAdmin.ListText((JObject)o["materials"], 4));
                    return;
                }
                case "restore":
                {
                    if (a.Length < 2) { c.Reply("ship restore <bin id> [here <player>]"); return; }
                    TrackedPlayerServer near = null;
                    if (a.Length > 2 && a[2].Equals("here", StringComparison.OrdinalIgnoreCase))
                    {
                        near = a.Length > 3 ? Online(a[3]) : (c.IsConsole ? null : Server.PlayerByConnection(c.Conn.ClientId));
                        if (near == null) { c.Reply("ship restore <bin id> here <player> (they must be on)"); return; }
                    }
                    c.Reply(ShipAdmin.Restore(a[1], near, c.Name));
                    return;
                }
                default:
                    c.Reply("ship list | where <id> | tp <player|all> <id> | delete <id> | bin | restore <bin id> [here <player>]");
                    return;
            }
        }

        private static void Tp(Caller c, string[] a, string r)
        {
            if (a.Length == 0) { c.Reply("usage: tp <player> (go to them), or tp <player> <to-player>"); return; }
            if (a.Length == 1)
            {
                TrackedPlayerServer me = c.IsConsole ? null : Server.PlayerByConnection(c.Conn.ClientId);
                if (me == null) { c.Reply("From the console: tp <player> <to-player>"); return; }
                TrackedPlayerServer to = Online(a[0]);
                if (to == null) { c.Reply("No player on now called " + a[0]); return; }
                c.Reply(Ground.Teleport(me, to));
                return;
            }
            TrackedPlayerServer who = Online(a[0]), dest = Online(a[1]);
            if (who == null || dest == null) { c.Reply("No player on now called " + (who == null ? a[0] : a[1]) + " (names with spaces: use the start of the name)"); return; }
            c.Reply(Ground.Teleport(who, dest));
            Server.SayTo(Server.Connection(who.ConnectionId), "You were moved to " + dest.Name);
        }

        private static TrackedPlayerServer Online(string s)
        {
            IReadOnlyList<TrackedPlayerServer> ps = Server.ActivePlayers();
            s = (s ?? "").Trim().TrimStart('@');
            TrackedPlayerServer p = ps.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase))
                                 ?? ps.FirstOrDefault(x => x.SteamUserId.ToString() == s);
            if (p != null || s.Length == 0) return p;
            // the best match for what was typed: the shortest name it starts, then one it is in
            return ps.Where(x => x.Name != null && x.Name.StartsWith(s, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name.Length).FirstOrDefault()
                ?? ps.Where(x => x.Name != null && x.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.Name.Length).FirstOrDefault();
        }

        /// <summary>A player on now, one seen before (by name), or a raw numeric id.</summary>
        private static bool Resolve(string s, out ulong id, out string name)
        {
            id = 0; name = null;
            s = (s ?? "").Trim().Trim('"');
            if (s.Length == 0) return false;
            TrackedPlayerServer on = Online(s);
            if (on != null) { id = on.SteamUserId; name = on.Name; return true; }
            PlayerRecord known = Server.Data.PlayerByName(s);
            if (known != null) { id = known.Id; name = known.Name; return true; }
            ulong n;
            if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                id = n;
                PlayerRecord p = Server.Data.Player(n);
                name = p != null ? p.Name : null;
                return true;
            }
            return false;
        }
    }
}
