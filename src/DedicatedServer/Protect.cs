// Protected builds: ships (and the bases and markers built as ships) that nothing breaks and nobody
// changes, for marking places and leaving things on display.
//
//   * built with the Indestructible Build Tool (AdminItems.cs): protected from the first part on;
//   * or an admin protects one: /protect [ship id | here] [movable], /unprotect, /protected.
// A protected ship:
//   * is never broken by a crash or anything else that destroys parts;
//   * can't be built on, taken apart, painted, re-wired, picked up from or moved (re-positioned) except
//     by a player holding the Indestructible Build Tool;
//   * can be taken apart with the Indestructible Recycler Tool (the game's recycler can't);
//   * stays where it is (its rigidbody is frozen), unless it was protected as "movable".
// Doors, levers, seats, beds and computers on it still work as usual.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet.Connection;
using HarmonyLib;
using Players;
using Players.Interface.Model;
using Ships;
using Ships.Services;
using UnityEngine;

namespace DedicatedServer
{
    public class ProtectedShip
    {
        public uint ShipId;
        public bool Movable;
        public string By = "";
        public DateTime At;
    }

    internal static class Protect
    {
        private static readonly string[] Guarded =
        {
            "PlaceHullPart", "PaintHullPart", "ReplaceHullColor", "PlaceFloatingPart", "RemoveHullPart", "RemoveFloatingPart",
            "PickUpPart", "PlaceHeldPart", "AddNewCable", "PickUpCable", "PlugInCable", "ChangeMathOperation", "SelectTarget", "ClearTarget",
        };
        private static readonly Dictionary<int, float> _toldAt = new Dictionary<int, float>();
        private static HashSet<uint> _before;
        public static int Blocked, Saved;
        private static float _next;

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                Type svc = typeof(Managers.Server.Services.ShipInteractionService);
                foreach (MethodInfo m in svc.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!m.Name.StartsWith("RpcLogic___")) continue;
                    string what = m.Name.Substring("RpcLogic___".Length);
                    what = what.Substring(0, what.IndexOf("_ServerRPC", StringComparison.Ordinal) >= 0 ? what.IndexOf("_ServerRPC", StringComparison.Ordinal) : what.Length);
                    if (Guarded.Contains(what)) h.Patch(m, prefix: new HarmonyMethod(typeof(Protect).GetMethod("GuardPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    else if (what == "StartRepositionShip" || what == "ConfirmRepositionShip") h.Patch(m, prefix: new HarmonyMethod(typeof(Protect).GetMethod("RepositionPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    else if (what == "CreateShip") h.Patch(m, prefix: new HarmonyMethod(typeof(Protect).GetMethod("CreatePrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                                                              postfix: new HarmonyMethod(typeof(Protect).GetMethod("CreatePostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                }
                h.Patch(AccessTools.Method(typeof(TrackedShipServer), "DestroyHullPart"), prefix: new HarmonyMethod(typeof(Protect).GetMethod("DestroyPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                // a crash makes the debris of a part BEFORE breaking it: with the break refused, every bump of a protected build made
                // debris (free materials) for a part that stayed. Its parts are taken out of the crash's list instead.
                h.Patch(AccessTools.Method(typeof(Ships.Physics.Collision.ShipCollisionDestruction), "FetchDestroyPartsCandidates"),
                    postfix: new HarmonyMethod(typeof(Protect).GetMethod("CandidatesPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(TrackedShipServer), "DestroyStatefulPart"), prefix: new HarmonyMethod(typeof(Protect).GetMethod("DestroyPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("protected builds: could not hook building: " + e.Message); }
        }

        public static ProtectedShip Get(uint ship) { return Server.Data.D.Protected.FirstOrDefault(p => p.ShipId == ship); }
        public static bool IsProtected(uint ship) { return ship != 0 && Get(ship) != null; }

        public static void Set(uint ship, bool movable, string by)
        {
            ProtectedShip p = Get(ship);
            if (p == null) { p = new ProtectedShip { ShipId = ship, At = DateTime.UtcNow }; Server.Data.D.Protected.Add(p); }
            p.Movable = movable; p.By = by ?? "";
            Server.Data.MarkDirty();
            Log.Msg("protected: ship " + ship + (movable ? " (movable)" : "") + " by " + by);
        }

        public static bool Clear(uint ship)
        {
            int n = Server.Data.D.Protected.RemoveAll(p => p.ShipId == ship);
            if (n == 0) return false;
            Server.Data.MarkDirty();
            Unfreeze(ship);
            Log.Msg("protection lifted: ship " + ship);
            return true;
        }

        /// <summary>The item the player behind a connection is holding (0 = nothing).</summary>
        public static uint Held(NetworkConnection conn)
        {
            TrackedPlayerServer p = conn != null ? Server.PlayerByConnection(conn.ClientId) : null;
            if (p == null) return 0;
            try
            {
                object ns = Traverse.Create(p).Property("NetworkState").GetValue();
                ItemSelection sel = Traverse.Create(ns).Property("ItemSelection").GetValue<ItemSelection>();
                return sel.HasItemSelected ? sel.ItemId : 0;
            }
            catch { return 0; }
        }

        public static bool HoldsTool(NetworkConnection conn) { return AdminItems.IsProtectTool(Held(conn)); }

        /// <summary>What the Indestructible Recycler Tool may do to a protected build: what the recycler does.</summary>
        private static bool RecyclerMay(string request) { return request.Contains("RemoveHullPart") || request.Contains("RemoveFloatingPart"); }

        public static int Recycled;

        /// <summary>The ship a request is about (its ShipId, a targeter's or a socket's).</summary>
        private static uint ShipOf(object req)
        {
            if (req == null) return 0;
            Traverse t = Traverse.Create(req);
            if (t.Field("ShipId").FieldExists()) return t.Field("ShipId").GetValue<uint>();
            if (t.Field("Targeter").FieldExists()) return Traverse.Create(t.Field("Targeter").GetValue()).Property("ShipId").FieldExists() ? Traverse.Create(t.Field("Targeter").GetValue()).Property("ShipId").GetValue<uint>() : Traverse.Create(t.Field("Targeter").GetValue()).Field("ShipId").GetValue<uint>();
            if (t.Field("EntityPartSocket").FieldExists())
            {
                object eps = t.Field("EntityPartSocket").GetValue();
                Traverse e = Traverse.Create(eps);
                return e.Field("EntityId").FieldExists() ? e.Field("EntityId").GetValue<uint>() : e.Property("EntityId").GetValue<uint>();
            }
            return 0;
        }

        private static NetworkConnection Sender(object[] args) { return args.OfType<NetworkConnection>().LastOrDefault(); }

        private static bool GuardPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                NetworkConnection conn = Sender(__args);
                uint ship = ShipOf(__args.Length > 0 ? __args[0] : null);
                uint held = Held(conn);
                bool tool = AdminItems.IsProtectTool(held);
                if (IsProtected(ship) && !tool)
                {
                    if (AdminItems.IsRecyclerTool(held) && RecyclerMay(__originalMethod.Name)) { Recycled++; return true; }
                    Refuse(conn, ship, __originalMethod.Name);
                    return false;
                }
                // building with the tool protects what it builds on
                if (tool && ship != 0 && !IsProtected(ship) && (__originalMethod.Name.Contains("PlaceHullPart") || __originalMethod.Name.Contains("PlaceFloatingPart")))
                {
                    TrackedPlayerServer p = Server.PlayerByConnection(conn.ClientId);
                    Set(ship, false, p != null ? p.Name : "?");
                }
            }
            catch (Exception e) { Log.Once("protect-guard", "protected builds: " + e.Message); }
            return true;
        }

        private static bool RepositionPrefix(object[] __args)
        {
            try
            {
                NetworkConnection conn = Sender(__args);
                TrackedPlayerServer p = conn != null ? Server.PlayerByConnection(conn.ClientId) : null;
                if (p == null || HoldsTool(conn)) return true;
                ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
                Ships.Interface.Model.State.IShipStateRead s;
                if (ships != null && ships.TryGetNearestRepositionableShip(p.SpaceId, p.Position, out s) && s != null)
                {
                    ProtectedShip ps = Get(s.Id);
                    if (ps != null && !ps.Movable) { Refuse(conn, s.Id, "reposition"); return false; }
                }
            }
            catch (Exception e) { Log.Once("protect-repos", "protected builds: " + e.Message); }
            return true;
        }

        private static void CreatePrefix(object[] __args)
        {
            _before = null;
            if (!HoldsTool(Sender(__args))) return;
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships != null) _before = new HashSet<uint>(ships.Ships.Select(s => s.Id));
        }

        private static void CreatePostfix(object[] __args)
        {
            if (_before == null) return;
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            TrackedPlayerServer p = Server.PlayerByConnection(Sender(__args).ClientId);
            foreach (TrackedShipServer s in ships.Ships.Where(s => !_before.Contains(s.Id)).ToList()) Set(s.Id, false, p != null ? p.Name : "?");
            _before = null;
        }

        public static int CandidatesDropped;

        private static void CandidatesPostfix(object __instance)
        {
            if (Server.Data.D.Protected.Count == 0) return;
            try
            {
                System.Collections.IList l = Traverse.Create(__instance).Field("_destroyCandidates").GetValue() as System.Collections.IList;
                if (l == null) return;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    object ship = Traverse.Create(l[i]).Field("Ship").GetValue();
                    uint id = ship != null ? Traverse.Create(ship).Property("Id").GetValue<uint>() : 0;
                    if (IsProtected(id)) { l.RemoveAt(i); CandidatesDropped++; }
                }
            }
            catch (Exception e) { Log.Once("protect-candidates", "protected builds: crash parts: " + e.Message); }
        }

        private static bool DestroyPrefix(TrackedShipServer __instance)
        {
            if (__instance == null || !IsProtected(__instance.Id)) return true;
            Saved++;
            Log.Debug("ground", "protected ship " + __instance.Id + ": a part would have broken; kept");
            return false;
        }

        private static void Refuse(NetworkConnection conn, uint ship, string what)
        {
            Blocked++;
            Log.Debug("gate", "protected ship " + ship + ": " + what + " refused");
            if (conn == null) return;
            float now = Time.realtimeSinceStartup, last;
            if (_toldAt.TryGetValue(conn.ClientId, out last) && now - last < 4f) return;
            _toldAt[conn.ClientId] = now;
            Server.SayTo(conn, Chat.Server("This build is protected. Only someone holding the " + AdminItems.ProtectToolName + " can change it, or the " + AdminItems.RecyclerToolName + " take it apart."));
        }

        // ---- staying put -------------------------------------------------------------------------

        private static float _nextPrune;

        /// <summary>Every few frames: protected ships that are loaded are held still. Every 30 s: ships that are gone (taken apart) are forgotten.</summary>
        public static void Update()
        {
            if (!Server.Ready || Time.realtimeSinceStartup < _next || Server.Data.D.Protected.Count == 0) return;
            _next = Time.realtimeSinceStartup + 0.25f;
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships == null) return;
            if (Time.realtimeSinceStartup >= _nextPrune)
            {
                _nextPrune = Time.realtimeSinceStartup + 30f;
                int gone = Server.Data.D.Protected.RemoveAll(p => ships.GetShip(p.ShipId) == null);
                if (gone > 0) { Server.Data.MarkDirty(); Log.Msg("protected builds: " + gone + " that no longer exist (taken apart) forgotten"); }
            }
            foreach (ProtectedShip p in Server.Data.D.Protected)
            {
                if (p.Movable) continue;
                TrackedShipServer s = ships.GetShip(p.ShipId);
                Rigidbody rb = s != null && s.IsRigidbodyLoaded && s.RigidbodyObject != null ? s.RigidbodyObject.GetComponent<Rigidbody>() : null;
                if (rb == null || rb.constraints == RigidbodyConstraints.FreezeAll) continue;
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                rb.constraints = RigidbodyConstraints.FreezeAll;
            }
        }

        private static void Unfreeze(uint ship)
        {
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            TrackedShipServer s = ships != null ? ships.GetShip(ship) : null;
            Rigidbody rb = s != null && s.RigidbodyObject != null ? s.RigidbodyObject.GetComponent<Rigidbody>() : null;
            if (rb != null) rb.constraints = RigidbodyConstraints.None;
        }

        /// <summary>The ship a player is standing on, or the nearest within 40 m.</summary>
        public static TrackedShipServer ShipNear(TrackedPlayerServer p)
        {
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships == null || p == null) return null;
            foreach (TrackedShipServer s in ships.Ships) if (s.RelativeSpaceId == p.SpaceId) return s;
            WorldTracking.Interface.Services.IWorldTranspose tr = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.IWorldTranspose>();
            TrackedShipServer best = null; float bestD = 40f;
            foreach (TrackedShipServer s in ships.Ships)
            {
                try
                {
                    Vector3 at = tr.Position(s.PhysicsState.SpaceId, p.SpaceId, s.PhysicsState.Position);
                    float d = (at - p.Position).magnitude;
                    if (d < bestD) { bestD = d; best = s; }
                }
                catch { }
            }
            return best;
        }
    }
}
