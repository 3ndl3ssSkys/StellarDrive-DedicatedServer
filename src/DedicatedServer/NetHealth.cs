// Players' games losing track of where other players are, and why players drop off the server.
//
// Where a player is: the game sends each player's position to every other player (and to themselves)
// with the id of the "space" it is in (a patch of planet surface, a ship's inside). A space a game hasn't
// heard of yet is only sent along when the server thinks that game hasn't seen the player in it yet
// (PlayerPhysicsStateSync.TryGetAckedTick). That guess comes from the last network tick the game heard
// anything at all, so it is often wrong over the internet: a lost packet, or a ship update read in the same
// frame, and the space is never sent. The player's game then has someone in a space it doesn't know, and
// "Space id N does not exist" is thrown every frame in the middle of ClientVisualsUpdater.Update - which
// leaves everything after it undrawn, the player markers among them: every dot freezes on the screen.
//   Server: the space is sent with every position (TryGetAckedTick always says "not seen").
//   Players' games: a player whose position can't be worked out is skipped (their dot hidden) instead of
//   stopping the rest of the frame, and the first error of each kind is logged with where it came from.
//
// Why players drop: the server logs the network's own reason for each disconnect (timed out, closed by
// their game, refused) and any kick FishNet makes by itself, and logs every ship that is removed.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DedicatedServer
{
    internal static class NetHealth
    {
        // ---- server ----------------------------------------------------------------------------------

        public static long SpaceSends;
        private static readonly ConcurrentDictionary<int, string> _why = new ConcurrentDictionary<int, string>();

        public static void InitServer(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Players.Network.PlayerPhysicsStateSync), "TryGetAckedTick"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("AckPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("players' spaces: could not make the server always send them: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(FishNet.Transporting.Tugboat.Server.ServerSocket), "Listener_PeerDisconnectedEvent"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("PeerGonePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                foreach (MethodInfo m in typeof(FishNet.Managing.Server.ServerManager).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    if (m.Name == "Kick")
                        h.Patch(m, prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("KickPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Ships.Services.ShipsServerTracker), "RemoveShip"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("ShipRemovedPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("disconnect reasons: could not hook the network: " + e.Message); }
            try
            {
                // something saved in a space that doesn't exist (3 bits of debris in "space 0" in Fish2, 2026-10-09) made the
                // game's position sync throw on every tick, which stopped the rest of the network tick: nobody could join
                h.Patch(AccessTools.Method(typeof(Managers.Server.Network.MasterSyncGameObject), "EnsureSpaceIsLoaded"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("SpacePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("position sync: could not guard it against missing spaces: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(Ships.Services.ShipDebrisGenerationServerService), "GenerateDebris"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("DebrisPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("crash debris: could not guard it: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(WorldObjects.Services.WorldObjectsServerTracker), "LoadObject"),
                    prefix: new HarmonyMethod(typeof(NetHealth).GetMethod("LoadObjectPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("world objects: could not guard loading them: " + e.Message); }
        }

        public static int DebrisSkipped;

        /// <summary>
        /// Crash debris is put in the crashing ship's space with positions from its physics. For a ship inside another ship's
        /// interior (Ethan's indestructible block in awesome mpd's ship), that space is the interior while the positions are
        /// outside it: the debris landed kilometres off inside the ship. And a ship whose space was already gone gave
        /// "space 0", which then stopped everyone joining (Fish2, 2026-10-09). Debris like that isn't made.
        /// </summary>
        private static bool DebrisPrefix(WorldObjects.Interface.Model.WorldObjectPhysicsState physicsState)
        {
            try
            {
                WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
                WorldTracking.Interface.Model.Spaces.Common.ISpace sp;
                if (spaces == null) return true;
                if (!spaces.TryGetSpace(physicsState.SpaceId, out sp))
                {
                    DebrisSkipped++;
                    Log.Once("debris-nospace-" + physicsState.SpaceId, "world: crash debris for space " + physicsState.SpaceId + ", which doesn't exist, wasn't made");
                    return false;
                }
                if (sp is WorldTracking.Interface.Model.Spaces.ShipRelativeSpace)
                {
                    DebrisSkipped++;
                    Log.Once("debris-inship", "world: crash debris of a ship inside another ship isn't made (the game would put it in the wrong place)");
                    return false;
                }
            }
            catch { }
            return true;
        }

        public static int ObjectsDropped;
        private static float _nextObjects;

        private static bool SpaceExists(uint id)
        {
            WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
            WorldTracking.Interface.Model.Spaces.Common.ISpace sp;
            return spaces == null || spaces.TryGetSpace(id, out sp);
        }

        /// <summary>A loose object (debris, a dropped item) saved in a space the world doesn't have isn't loaded.</summary>
        private static bool LoadObjectPrefix(WorldObjects.Interface.Model.WorldObjectStateSnapshot stateSnapshot)
        {
            try
            {
                if (SpaceExists(stateSnapshot.SpaceId)) return true;
                ObjectsDropped++;
                Log.Warn("world: object #" + stateSnapshot.Id + " was saved in space " + stateSnapshot.SpaceId + ", which doesn't exist; left out (it stopped players joining)");
                return false;
            }
            catch { return true; }
        }

        /// <summary>Every few seconds on the server: a loose object that ends up in a space that doesn't exist is removed.</summary>
        public static void Update()
        {
            if (Time.realtimeSinceStartup < _nextObjects) return;
            _nextObjects = Time.realtimeSinceStartup + 5f;
            WorldObjects.Services.WorldObjectsServerTracker objs = Core.Services.ServiceLocator.GetService<WorldObjects.Services.WorldObjectsServerTracker>();
            if (objs == null) return;
            foreach (WorldObjects.TrackedWorldObjectServer o in objs.Objects.ToList())
            {
                if (o == null || SpaceExists(o.SpaceId)) continue;
                ObjectsDropped++;
                Log.Warn("world: object #" + o.Id + " was in space " + o.SpaceId + ", which doesn't exist; removed");
                objs.RemoveWorldObject(o.Id);
            }
        }

        public static int MissingSpaces;

        private static bool SpacePrefix(uint spaceId)
        {
            WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
            WorldTracking.Interface.Model.Spaces.Common.ISpace sp;
            if (spaces == null || spaces.TryGetSpace(spaceId, out sp)) return true;
            MissingSpaces++;
            Log.Once("missing-space-" + spaceId, "world: something (a ship or a dropped object) is in space " + spaceId + ", which doesn't exist; it is left out of the position updates players get, so joining still works");
            return false;
        }

        private static bool AckPrefix(ref bool __result, out uint ackedTick)
        {
            ackedTick = 0;
            __result = false;
            SpaceSends++;
            return false;
        }

        private static void PeerGonePrefix(LiteNetLib.NetPeer peer, LiteNetLib.DisconnectInfo disconnectInfo)
        {
            try
            {
                string why;
                switch (disconnectInfo.Reason)
                {
                    case LiteNetLib.DisconnectReason.Timeout: why = "timed out: nothing heard from their game for a while (their connection or the server's dropped, or their game froze)"; break;
                    case LiteNetLib.DisconnectReason.RemoteConnectionClose: why = "their game closed the connection (left, quit or crashed out of the game)"; break;
                    case LiteNetLib.DisconnectReason.DisconnectPeerCalled: why = "the server closed the connection"; break;
                    case LiteNetLib.DisconnectReason.HostUnreachable:
                    case LiteNetLib.DisconnectReason.NetworkUnreachable: why = "their address became unreachable"; break;
                    default: why = disconnectInfo.Reason.ToString(); break;
                }
                if (disconnectInfo.SocketErrorCode != System.Net.Sockets.SocketError.Success) why += " (socket " + disconnectInfo.SocketErrorCode + ")";
                _why[peer.Id] = why;
            }
            catch { }
        }

        /// <summary>The network's reason the connection with this id ended (once), or "".</summary>
        public static string WhyGone(int connectionId)
        {
            string s;
            return _why.TryRemove(connectionId, out s) ? s : "";
        }

        private static void KickPrefix(object[] __args)
        {
            try
            {
                string who = "?", reason = "?", log = "";
                foreach (object a in __args)
                {
                    if (a is FishNet.Connection.NetworkConnection c) who = Server.NameOf(c.ClientId);
                    else if (a is int id) who = Server.NameOf(id);
                    else if (a is FishNet.Managing.Server.KickReason k) reason = k.ToString();
                    else if (a is string s) log = s;
                }
                Log.Warn("network: FishNet kicked " + who + " (" + reason + ")" + (log.Length > 0 ? ": " + log : "") + " from " + Server.Step);
            }
            catch { }
        }

        private static void ShipRemovedPrefix(Ships.Interface.Model.ShipState shipState)
        {
            try { Log.Msg("ships: ship #" + shipState.Id + " removed (its last part went) during: " + Server.Step); } catch { }
        }

        // ---- players' games --------------------------------------------------------------------------

        /// <summary>Players whose position couldn't be worked out this frame: their marker is hidden.</summary>
        public static readonly HashSet<string> Lost = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static int Skipped;

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Players.TrackedPlayerOtherClient), "UpdateVisuals"),
                    finalizer: new HarmonyMethod(typeof(NetHealth).GetMethod("OtherVisualsFinalizer", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Managers.Client.ClientVisualsUpdater), "Update"),
                    finalizer: new HarmonyMethod(typeof(NetHealth).GetMethod("VisualsFinalizer", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("other players' positions: could not guard them: " + e.Message); }
        }

        private static Exception OtherVisualsFinalizer(Players.TrackedPlayerOtherClient __instance, Exception __exception)
        {
            string name = "";
            try { name = __instance.PlayerName ?? ""; } catch { }
            if (__exception == null) { if (Lost.Count > 0) Lost.Remove(name); return null; }
            Skipped++;
            if (Lost.Add(name))
                Log.Warn("other players: " + name + "'s position can't be worked out (" + __exception.Message + "); their marker is hidden until it can");
            return null;
        }

        private static readonly HashSet<string> _said = new HashSet<string>();

        private static Exception VisualsFinalizer(Exception __exception)
        {
            if (__exception != null && _said.Count < 20 && _said.Add(__exception.GetType().Name + __exception.Message))
                Log.Warn("this game's drawing stopped part-way through a frame (everything after it, player markers too, wasn't drawn): " + __exception);
            return __exception;
        }
    }
}
