// Server rules, and permissions.
//
// DevTools (admins / everyone / nobody): who may use the game's developer tools - cheat flying and
// free build, two server messages from the player (PlayerNetworkInputs) - and Ship Tools. A player
// who may not has them switched off as they join.
//
// Research (shared / individual): shared is the game's own way, one research tree the whole
// server shares. Individual: each player researches, and pays, for themselves. The server handles
// the game's "research this" message per player and keeps each player's list next to the world
// (research-by-player.json). Every client-side research check goes through
// ResearchNetworkedState.IsResearched, so a client answers those from the list the server sends it:
// the build menu, research screen and guide all follow. A player new to individual research
// starts from what the world had researched when they first joined.
//
// Permissions: admins (op) may do everything. Other mods can declare their own permission levels
// with a public static string[] DedicatedServerPermissionLevels on their MelonMod class (lowest
// first), and read a player's level with ServerPermissions.Level(playerId, "Their Mod Name").
// Levels are set from the console (`perm <player> <mod> <level>`) or the Server Manager.
// Demo Ship Tools has its own levels (Normal / Elevated / Max); on a dedicated server admins get
// Max and other players the level set here (else Ship Tools' own default).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet.Connection;
using HarmonyLib;
using Items.Model;
using MelonLoader;
using Newtonsoft.Json;
using Players;
using Players.Interface.Model;
using Players.Network;
using Research;
using Research.Interface.Values;
using UnityEngine;

namespace DedicatedServer
{
    /// <summary>For other mods: read permissions on a dedicated server (by reflection or a reference).</summary>
    public static class ServerPermissions
    {
        /// <summary>True in a process started with -dedicated.</summary>
        public static bool OnDedicatedServer { get { return DedicatedServerMod.IsServer; } }

        public static bool IsAdmin(ulong playerId) { return OnDedicatedServer && Server.Data != null && Server.Data.IsOp(playerId); }

        /// <summary>The level set for this player and mod; the mod's highest for admins; -1 if none is set.</summary>
        public static int Level(ulong playerId, string modName)
        {
            if (!OnDedicatedServer || Server.Data == null) return -1;
            string[] levels;
            if (IsAdmin(playerId) && Rules.Declared().TryGetValue(modName, out levels)) return levels.Length - 1;
            PlayerRecord p = Server.Data.Player(playerId);
            int v;
            return p != null && p.Levels.TryGetValue(modName, out v) ? v : -1;
        }
    }

    internal static class Rules
    {
        public const string ShipTools = "Demo Ship Tools";

        public static void Init(HarmonyLib.Harmony h)
        {
            MethodInfo fly = typeof(PlayerNetworkInputs).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name.StartsWith("RpcLogic___ToggleCheatFlyState_ServerRPC"));
            MethodInfo build = typeof(PlayerNetworkInputs).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name.StartsWith("RpcLogic___ToggleCheatFreeBuild_ServerRPC"));
            HarmonyMethod gate = new HarmonyMethod(typeof(Rules).GetMethod("DevToolPrefix", BindingFlags.NonPublic | BindingFlags.Static));
            if (fly != null) h.Patch(fly, prefix: gate); else Log.Error("PATCH MISSING: cheat flying (DevTools can't be enforced)");
            if (build != null) h.Patch(build, prefix: gate); else Log.Error("PATCH MISSING: free build (DevTools can't be enforced)");

            MethodInfo research = typeof(Managers.Server.Services.ResearchNetworkService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name.StartsWith("RpcLogic___ResearchTech_ServerRpc"));
            if (research != null) h.Patch(research, prefix: new HarmonyMethod(typeof(Rules).GetMethod("ResearchPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            else Log.Error("PATCH MISSING: research (individual research can't work)");

            // Demo Ship Tools, if it's here
            Type st = AccessTools.TypeByName("DemoShipTools.ShipToolsMod");
            if (st != null)
            {
                MethodInfo devOn = AccessTools.Method(st, "DevToolsOn");
                MethodInfo levelOf = AccessTools.Method(st, "LevelOf", new[] { typeof(ulong) });
                if (devOn != null) h.Patch(devOn, prefix: new HarmonyMethod(typeof(Rules).GetMethod("ShipToolsDevToolsPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                if (levelOf != null) h.Patch(levelOf, prefix: new HarmonyMethod(typeof(Rules).GetMethod("ShipToolsLevelPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                Log.Msg("Demo Ship Tools found: its permissions come from this server (admins get Max)");
            }
        }

        // ---- dev tools -------------------------------------------------------------------------

        public static bool MayUseDevTools(ulong playerId)
        {
            string rule = (Server.Cfg.DevTools.Value ?? "admins").ToLowerInvariant();
            if (rule == "everyone") return true;
            if (rule == "nobody") return false;
            return Server.Data.IsOp(playerId);
        }

        private static bool DevToolPrefix(PlayerNetworkInputs __instance, MethodBase __originalMethod)
        {
            try
            {
                PlayerNetworkState ns = Traverse.Create(__instance).Field("_networkState").GetValue<PlayerNetworkState>();
                if (ns == null || MayUseDevTools(ns.SteamUserId)) return true;
                bool turningOff = __originalMethod.Name.Contains("FreeBuild") ? ns.IsUsingCheatFreeBuild
                                : ns.ServerState.Type == PlayerStateType.CheatFlying || ns.ServerState.Type == PlayerStateType.CheatFlyingHoldingCable;
                if (turningOff) return true;       // switching it off is always allowed
                Server.SayTo(__instance.Owner, "Developer tools are " + ((Server.Cfg.DevTools.Value ?? "") == "nobody" ? "off on this server." : "for admins only on this server."));
                return false;
            }
            catch (Exception e) { Log.Once("devtools", "dev tools check: " + e.Message); return true; }
        }

        /// <summary>As a player joins: switch off what they may not use.</summary>
        public static void OnJoined(TrackedPlayerServer player)
        {
            try
            {
                if (MayUseDevTools(player.SteamUserId)) return;
                PlayerNetworkState ns = Traverse.Create(player).Property("NetworkState").GetValue<PlayerNetworkState>();
                if (ns == null) return;
                if (ns.IsUsingCheatFreeBuild) ns.ServerSetUsingCheatFreeBuild(false);
                if (ns.ServerState.Type == PlayerStateType.CheatFlying) ns.ServerSetState(new PlayerState { Type = PlayerStateType.Walking });
            }
            catch (Exception e) { Log.Once("devtools-join", "dev tools on join: " + e.Message); }
        }

        // ---- Ship Tools --------------------------------------------------------------------------

        private static bool ShipToolsDevToolsPrefix(ref bool __result)
        {
            // the server's DevTools rule decides who reaches Ship Tools at all; its levels decide what they may do
            __result = (Server.Cfg.DevTools.Value ?? "") != "nobody";
            return false;
        }

        private static bool ShipToolsLevelPrefix(ulong steamId, ref byte __result)
        {
            if (Server.Data.IsOp(steamId)) { __result = 2; return false; }
            if ((Server.Cfg.DevTools.Value ?? "") == "admins") { __result = 0; return false; }
            PlayerRecord p = Server.Data.Player(steamId);
            int v;
            if (p != null && p.Levels.TryGetValue(ShipTools, out v)) { __result = (byte)Mathf.Clamp(v, 0, 2); return false; }
            return true;    // Ship Tools' own default
        }

        /// <summary>Mod name -> its permission level names, lowest first.</summary>
        public static Dictionary<string, string[]> Declared()
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (MelonMod m in MelonMod.RegisteredMelons)
            {
                if (m == null || m.Info == null) continue;
                try
                {
                    Type t = m.GetType();
                    object v = null;
                    FieldInfo f = t.GetField("DedicatedServerPermissionLevels", BindingFlags.Public | BindingFlags.Static);
                    if (f != null) v = f.GetValue(null);
                    PropertyInfo pr = t.GetProperty("DedicatedServerPermissionLevels", BindingFlags.Public | BindingFlags.Static);
                    if (v == null && pr != null) v = pr.GetValue(null, null);
                    string[] levels = v as string[];
                    if (levels != null && levels.Length > 0) d[m.Info.Name] = levels;
                    else if (m.Info.Name == ShipTools) d[ShipTools] = new[] { "Normal", "Elevated", "Max" };
                }
                catch { }
            }
            return d;
        }

        // ---- individual research -------------------------------------------------------------

        private static Dictionary<ulong, HashSet<ushort>> _research;
        private static string _researchFile;

        public static bool Individual { get { return (Server.Cfg.Research.Value ?? "shared").ToLowerInvariant() == "individual"; } }

        private static ResearchNetworkedState Global() { return UnityEngine.Object.FindFirstObjectByType<ResearchNetworkedState>(); }

        private static void LoadResearch()
        {
            if (_research != null) return;
            _research = new Dictionary<ulong, HashSet<ushort>>();
            _researchFile = Path.Combine(Path.GetDirectoryName(Server.WorldPath) ?? Server.DataDir, "research-by-player.json");
            try
            {
                if (File.Exists(_researchFile))
                {
                    Dictionary<string, ushort[]> raw = JsonConvert.DeserializeObject<Dictionary<string, ushort[]>>(File.ReadAllText(_researchFile));
                    foreach (KeyValuePair<string, ushort[]> kv in raw)
                    {
                        ulong id;
                        if (ulong.TryParse(kv.Key, out id)) _research[id] = new HashSet<ushort>(kv.Value);
                    }
                }
            }
            catch (Exception e) { Log.Warn("could not read research-by-player.json: " + e.Message); }
        }

        private static void SaveResearch()
        {
            try
            {
                Dictionary<string, ushort[]> raw = _research.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.OrderBy(x => x).ToArray());
                File.WriteAllText(_researchFile, JsonConvert.SerializeObject(raw, Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("could not write research-by-player.json: " + e.Message); }
        }

        public static HashSet<ushort> ResearchOf(ulong playerId)
        {
            LoadResearch();
            HashSet<ushort> set;
            if (!_research.TryGetValue(playerId, out set))
            {
                ResearchNetworkedState g = Global();
                set = new HashSet<ushort>(g != null ? g.Researched : new ushort[0]);
                _research[playerId] = set;
                SaveResearch();
            }
            return set;
        }

        /// <summary>Tell a player which research counts for them (their own, or the shared tree).</summary>
        public static void SendResearch(TrackedPlayerServer player)
        {
            NetworkConnection conn = Server.Connection(player.ConnectionId);
            if (conn == null) return;
            if (!Individual) { Net.ToClient(conn, new DsResearch { Individual = false, Ids = "" }); return; }
            Net.ToClient(conn, new DsResearch { Individual = true, Ids = string.Join(",", ResearchOf(player.SteamUserId).Select(x => x.ToString()).ToArray()) });
        }

        /// <summary>After `set Research`: everyone on gets the new state.</summary>
        public static void ResearchModeChanged()
        {
            foreach (TrackedPlayerServer p in Server.ActivePlayers()) SendResearch(p);
        }

        private static bool ResearchPrefix(Managers.Server.Services.ResearchNetworkService __instance, object[] __args)
        {
            if (!Individual) return true;
            try
            {
                ushort techId = (ushort)__args[0];
                NetworkConnection caller = __args[1] as NetworkConnection;
                if (caller == null) return false;
                TrackedPlayerServer player = Server.PlayerByConnection(caller.ClientId);
                if (player == null) return false;
                ResearchSettingsList list = Traverse.Create(__instance).Field("researchList").GetValue<ResearchSettingsList>();
                ResearchSettings rs = list != null ? list.GetResearchSettingById(techId) : null;
                HashSet<ushort> mine = ResearchOf(player.SteamUserId);
                if (rs == null || mine.Contains(techId)) return false;
                if (!player.IsUsingCheatFreeBuild)
                {
                    ItemDrop[] cost = rs.resourceCost.Select(i => i.ItemDrop).ToArray();
                    if (!player.HasItemsInInventory(cost)) return false;
                    player.RemoveItemsFromInventory(cost);
                }
                mine.Add(techId);
                SaveResearch();
                SendResearch(player);
                if (rs.unlockedGuideEntriesIds != null)
                    foreach (uint guide in rs.unlockedGuideEntriesIds) player.ShowGuideEntryNotification(guide);
                Log.Msg(player.Name + " researched " + techId + " (individual research)");
            }
            catch (Exception e) { Log.Once("research", "individual research: " + e); }
            return false;
        }

        // ---- the client side of individual research -----------------------------------------

        private static HashSet<ushort> _mine;       // this client's own list, or null for the shared tree

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ResearchNetworkedState), "IsResearched"),
                    prefix: new HarmonyMethod(typeof(Rules).GetMethod("IsResearchedPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Error("could not patch research (individual research won't show): " + e.Message); }
            Net.OnResearch = OnResearch;
        }

        public static void ClientReset() { _mine = null; }

        private static bool IsResearchedPrefix(ushort researchId, ref bool __result)
        {
            if (_mine == null || Net.ServerStarted) return true;
            __result = _mine.Contains(researchId);
            return false;
        }

        private static void OnResearch(DsResearch r)
        {
            if (!r.Individual) { _mine = null; return; }
            HashSet<ushort> next = new HashSet<ushort>();
            foreach (string s in (r.Ids ?? "").Split(','))
            {
                ushort v;
                if (ushort.TryParse(s, out v)) next.Add(v);
            }
            HashSet<ushort> before = _mine;
            _mine = next;
            ResearchNetworkedState g = Global();
            if (before != null && g != null && g.OnResearchedClient != null)
                foreach (ushort id in next.Where(x => !before.Contains(x))) g.OnResearchedClient(id);   // the game's own "research done" (menus, notification)
            Log.Msg("individual research: " + next.Count + " researched for this player");
        }

        public static int ClientResearchCount { get { return _mine != null ? _mine.Count : -1; } }
    }
}
