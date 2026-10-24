// Players and ships in the ground, and moving players around.
//
// How the game gives things ground (on the server): every physics tick PlanetColliders makes the terrain
// pieces (PlanetMeshColliderChunk, ~110 m across) within 150 m of every object in a planet-surface space,
// and each space's objects and pieces go into that space's own physics scene. A player is the game's own
// character controller, not a body: once below the terrain mesh it never finds ground again and falls.
//
// The fixes are NecessaryFixes' (src/NecessaryFixes, which measured all of this - its handoff has the
// numbers), so a server without that mod gets the same. With NecessaryFixes loaded it does them itself, and
// only the commands here are used (tp, bring, ground). All behind GroundRescue:
//   - a hole: every 10 ticks, everything near the ground must have a terrain piece under it in its OWN
//     physics scene; if not, why is logged, dead piece records are dropped and its ground is made again;
//   - a piece destroyed while the game still lists it makes PhysicsScenesServerUpdater.BuildActiveSpacesCache
//     throw every tick, which stops ALL physics: such records are dropped before the scene step;
//   - after a crash that breaks parts the game gives a ship back part of its speed from before the hit
//     (ShipRigidbody.RestoreVelocity) - into the ground too: for terrain contacts that part is taken out;
//   - last resort: a ship SINKING (moving down 0.5 m/s or more) with a part 0.8 m into a piece (two checks,
//     or 3 m at once), or a player's feet 0.3 m in for three ticks, is put back on top; at most 5 in 10 s for
//     one thing. A ship at rest is never moved, even with parts dug into the ground as built.
// (Ships already sweep along their path: the game makes every ship body ContinuousDynamic.)
//
// Teleporting (`tp`) uses the same path as putting a player back: the server moves the player, then sends
// them the game's respawn message with their new place.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Core.Values;
using FishNet.Connection;
using HarmonyLib;
using Planets.EnvironmentGeneration.Tracking;
using Planets.Services;
using Players;
using Players.Network.Services;
using Ships;
using Ships.Physics;
using Ships.Physics.Collision;
using Ships.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static class Ground
    {
        public static int PlayerEvents, ShipEvents, Rescues, ShipRescues, Holes, Repairs, DeadDropped, RestoresClamped, Audits;
        public static string LastEvent = "";
        private static readonly Dictionary<uint, int> _below = new Dictionary<uint, int>();       // object id -> checks in a row in the ground
        private static readonly Dictionary<uint, float> _loggedAt = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, float> _minH = new Dictionary<uint, float>();     // lowest height seen per player (for `ground`)
        private static uint _tick;
        private static ISpacesServerProvider _spaces;
        private static PlanetsServerTracker _planets;

        public const float PlayerDepth = 0.3f, FeetBelowBody = 0.825f, ShipDepth = 0.8f, ShipDeepAtOnce = 3f, SinkingSpeed = 0.5f;

        private static bool On { get { return Server.Cfg.GroundRescue.Value; } }

        public static void Init(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PostPhysics"),
                postfix: new HarmonyMethod(typeof(Ground).GetMethod("PostPhysicsPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            try
            {
                h.Patch(AccessTools.Method(typeof(Managers.Server.Updaters.PhysicsScenesServerUpdater), "PreparePhysicsScenes"),
                    prefix: new HarmonyMethod(typeof(Ground).GetMethod("PrepareScenesPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("ground: could not guard the physics scenes: " + e.Message); }
            try
            {
                _contacts = AccessTools.FieldRefAccess<ShipCollisionDestruction, List<ContactPoint>>("_contacts");
                _rb = AccessTools.FieldRefAccess<ShipCollisionDestruction, ShipRigidbody>("shipRigidbody");
                _body = AccessTools.FieldRefAccess<ShipRigidbody, Rigidbody>("_rigidbody");
                h.Patch(AccessTools.Method(typeof(ShipCollisionDestruction), "UpdateDestruction"),
                    prefix: new HarmonyMethod(typeof(Ground).GetMethod("DestructionPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    finalizer: new HarmonyMethod(typeof(Ground).GetMethod("DestructionFinalizer", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(ShipRigidbody), "RestoreVelocity"),
                    postfix: new HarmonyMethod(typeof(Ground).GetMethod("RestorePostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("ground: could not guard crash speeds: " + e.Message); }
        }

        private static bool Services()
        {
            if (_spaces == null) _spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            if (_planets == null) _planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            return _spaces != null && _planets != null;
        }

        /// <summary>Height above the terrain of a point in a planet-surface space; NaN anywhere else.</summary>
        public static float Height(uint spaceId, Vector3 local, out Vector3Double planetPos, out ulong planetId, out float elevation)
        {
            planetPos = Vector3Double.Zero; planetId = 0; elevation = 0f;
            if (!Services()) return float.NaN;
            PlanetSurfaceSpace s = _spaces.GetSpace(spaceId) as PlanetSurfaceSpace;
            if (s == null) return float.NaN;
            planetId = s.PlanetId;
            planetPos = s.Center + (Vector3Double)local;
            double r = planetPos.Magnitude;
            if (r < 1.0) return float.NaN;
            elevation = _planets.GetPlanetTerrainElevationAtPosition(s.PlanetId, (Vector3)(planetPos / r));
            return (float)(r - elevation);
        }

        public static float Height(uint spaceId, Vector3 local)
        {
            Vector3Double pp; ulong pid; float e;
            return Height(spaceId, local, out pp, out pid, out e);
        }

        /// <summary>The planet's up at a point in a planet-surface space (zero anywhere else).</summary>
        private static Vector3 Up(uint spaceId, Vector3 local)
        {
            Vector3Double pp; ulong pid; float e;
            if (float.IsNaN(Height(spaceId, local, out pp, out pid, out e))) return Vector3.zero;
            return (Vector3)(pp / pp.Magnitude);
        }

        private static void PostPhysicsPostfix()
        {
            _tick++;
            try
            {
                if (!Services()) return;
                foreach (TrackedPlayerServer p in Server.ActivePlayers()) { Server.Step = "ground: " + p.Name; CheckPlayer(p); }
                Server.Step = "ground: ships";
                if (_tick % 5 == 0) CheckShips();
                Server.Step = "ground: holes";
                if (_tick % 10 == 0) Audit();
                Server.Step = "frame";
            }
            catch (Exception e) { Log.Once("ground-watch", "ground watch: " + e); }
        }

        // ---- the terrain an object's physics really stands on -----------------------------------

        private static readonly RaycastHit[] _hits = new RaycastHit[128];
        private static readonly Dictionary<int, bool> _isTerrain = new Dictionary<int, bool>();

        private static bool IsTerrain(Collider c)
        {
            bool t;
            int id = c.GetInstanceID();
            if (!_isTerrain.TryGetValue(id, out t))
            {
                t = c.GetComponentInParent<Planets.Terrain.Meshes.Colliders.PlanetMeshColliderChunk>() != null;
                if (_isTerrain.Count > 20000) _isTerrain.Clear();
                _isTerrain[id] = t;
            }
            return t;
        }

        /// <summary>
        /// The highest terrain-piece surface straight above or below p in this physics scene, as a height relative
        /// to p (positive: p is under it; NaN: no piece there). Only the pieces in the object's own scene count:
        /// that is what its physics stands on. (The exact height formula is never used to decide: the collider
        /// mesh is up to a couple of metres off it on rough ground.)
        /// </summary>
        public static float TerrainAt(Scene scene, Vector3 p, Vector3 up, float reach = 200f)
        {
            if (!scene.IsValid()) return float.NaN;
            int n = scene.GetPhysicsScene().Raycast(p + up * reach, -up, _hits, reach * 2f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.NaN;
            for (int i = 0; i < n; i++)
            {
                Collider c = _hits[i].collider;
                if (c == null || !IsTerrain(c)) continue;
                float d = Vector3.Dot(_hits[i].point - p, up);
                if (float.IsNaN(best) || d > best) best = d;
            }
            return best;
        }

        // ---- holes: no terrain piece under something, in its own physics scene --------------------

        private struct Seen
        {
            public string What;
            public uint Id, SpaceId;
            public Vector3 Pos;
            public GameObject Body;
        }

        /// <summary>Every player, ship and loose object in a planet-surface space with a body in the physics.</summary>
        private static List<Seen> Objects()
        {
            List<Seen> l = new List<Seen>();
            foreach (TrackedPlayerServer p in Server.ActivePlayers())
                if (p != null && p.ServerRigidbody != null && !p.IsCheatFlying && _spaces.GetSpace(p.SpaceId) is PlanetSurfaceSpace)
                    l.Add(new Seen { What = "player " + p.Name, Id = p.Id, SpaceId = p.SpaceId, Pos = p.Position, Body = p.ServerRigidbody.gameObject });
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships != null)
                foreach (TrackedShipServer s in ships.LoadedRootShips)
                    if (s != null && s.IsRigidbodyLoaded && s.RigidbodyObject != null && _spaces.GetSpace(s.SpaceId) is PlanetSurfaceSpace)
                        l.Add(new Seen { What = "ship #" + s.Id + " (" + s.PartCount + " parts)", Id = s.Id, SpaceId = s.SpaceId, Pos = s.Position, Body = s.RigidbodyObject });
            WorldObjects.Services.WorldObjectsServerTracker objs = Core.Services.ServiceLocator.GetService<WorldObjects.Services.WorldObjectsServerTracker>();
            if (objs != null)
                foreach (WorldObjects.TrackedWorldObjectServer o in objs.Objects)
                    if (o != null && o.IsRigidbodyLoaded && o.LoadedRigidbody != null && _spaces.GetSpace(o.SpaceId) is PlanetSurfaceSpace)
                        l.Add(new Seen { What = "object #" + o.Id, Id = o.Id, SpaceId = o.SpaceId, Pos = o.Position, Body = o.LoadedRigidbody.gameObject });
            return l;
        }

        /// <summary>Is there ground in each object's own physics scene where the planet says there is? Any hole is logged and, with GroundRescue, made good.</summary>
        private static void Audit()
        {
            Audits++;
            foreach (Seen o in Objects())
            {
                float height = Height(o.SpaceId, o.Pos);
                if (float.IsNaN(height) || height > 40f || height < -60f) continue;     // flying, or far below: not a missing piece
                // simulated in another space's physics scene: a player riding on or beside a ship stands on the ship, not on the
                // ground of their own space (93 false "holes" a night on Ethan's server); the ship's own check covers them
                if (o.Body.scene.name != "Server physics space " + o.SpaceId) continue;
                Vector3 up = Up(o.SpaceId, o.Pos);
                if (!float.IsNaN(TerrainAt(o.Body.scene, o.Pos, up))) continue;
                Holes++;
                float last;
                if (_loggedAt.TryGetValue(o.Id, out last) && Time.realtimeSinceStartup - last < 5f) continue;
                _loggedAt[o.Id] = Time.realtimeSinceStartup;
                string why = Explain(o, up);
                string how = On ? Repair(o) : "GroundRescue off: left as it is";
                LastEvent = DateTime.Now.ToString("HH:mm:ss") + " " + o.What + " " + height.ToString("0.0", CultureInfo.InvariantCulture) + " m above the exact ground, no terrain piece under it in its own physics scene";
                Log.Warn("ground: " + LastEvent + "; " + why + " -> " + how);
            }
        }

        private static PlanetColliders PieceBook()
        {
            PlanetCollidersServer pcs = Core.Services.ServiceLocator.GetService<PlanetCollidersServer>();
            return pcs != null ? AccessTools.Field(typeof(PlanetCollidersServer), "_planetColliders").GetValue(pcs) as PlanetColliders : null;
        }

        /// <summary>
        /// The ground under an object is missing in its own physics scene: the game's record of every piece that is
        /// gone or has lost its collider is thrown away, and the game works out this object's ground afresh on the
        /// next tick (its generation pass forgotten, as if it had just moved 50 m).
        /// </summary>
        private static string Repair(Seen o)
        {
            try
            {
                PlanetColliders pc = PieceBook();
                System.Collections.IDictionary spawned = AccessTools.Field(typeof(PlanetColliders), "_spawnedColliders").GetValue(pc) as System.Collections.IDictionary;
                System.Collections.IDictionary passes = AccessTools.Field(typeof(PlanetColliders), "_generationPasses").GetValue(pc) as System.Collections.IDictionary;
                int dead = 0;
                foreach (object k in spawned.Keys.Cast<object>().ToList())
                {
                    PlanetColliders.ISpawnedPlanetCollider c = spawned[k] as PlanetColliders.ISpawnedPlanetCollider;
                    bool gone = c == null || c.SpawnedObject == null;
                    if (!gone && c.SpawnedObject.GetComponentInChildren<Planets.Terrain.Meshes.Colliders.PlanetMeshColliderChunk>(true) != null)
                    {
                        MeshCollider mc = c.SpawnedObject.GetComponentInChildren<MeshCollider>(true);
                        gone = mc == null || mc.sharedMesh == null;
                    }
                    if (!gone) continue;
                    if (c != null && c.SpawnedObject != null) UnityEngine.Object.Destroy(c.SpawnedObject.gameObject);
                    spawned.Remove(k);
                    dead++;
                }
                bool forgot = passes != null && passes.Contains(o.Id);
                if (forgot) passes.Remove(o.Id);
                Repairs++;
                return "repaired: " + dead + " dead piece record(s) dropped, " + (forgot ? "its ground made afresh next tick" : "it had no ground record");
            }
            catch (Exception e) { Log.Once("ground-repair", "ground: could not repair: " + e.Message); return "repair failed: " + e.Message; }
        }

        /// <summary>
        /// A ground piece destroyed while the game still lists it makes PhysicsScenesServerUpdater.BuildActiveSpacesCache
        /// throw on it every tick, which stops ALL of the server's physics. Such records are dropped first, and every
        /// object's ground is made again.
        /// </summary>
        private static void PrepareScenesPrefix()
        {
            if (!On) return;
            try
            {
                PlanetCollidersServer pcs = Core.Services.ServiceLocator.GetService<PlanetCollidersServer>();
                if (pcs == null) return;
                bool any = false;
                foreach (PlanetColliders.ISpawnedPlanetCollider c in pcs.Colliders) if (c == null || c.SpawnedObject == null) { any = true; break; }
                if (!any) return;
                PlanetColliders pc = PieceBook();
                System.Collections.IDictionary spawned = AccessTools.Field(typeof(PlanetColliders), "_spawnedColliders").GetValue(pc) as System.Collections.IDictionary;
                System.Collections.IDictionary passes = AccessTools.Field(typeof(PlanetColliders), "_generationPasses").GetValue(pc) as System.Collections.IDictionary;
                int n = 0;
                foreach (object k in spawned.Keys.Cast<object>().ToList())
                {
                    PlanetColliders.ISpawnedPlanetCollider c = spawned[k] as PlanetColliders.ISpawnedPlanetCollider;
                    if (c != null && c.SpawnedObject != null) continue;
                    spawned.Remove(k);
                    n++;
                }
                if (passes != null) passes.Clear();   // every object works out its ground afresh this tick
                DeadDropped += n;
                Log.Once("dead-pieces", "ground: " + n + " ground piece(s) were destroyed while the game still listed them (that stops all physics); dropped them and made the ground again");
            }
            catch (Exception e) { Log.Once("dead-pieces-fail", "ground: could not drop destroyed ground pieces: " + e.Message); }
        }

        /// <summary>For the log: where the terrain pieces that should be under an object are - which space and scene, and who they were made for.</summary>
        private static string Explain(Seen o, Vector3 up)
        {
            List<string> parts = new List<string>();
            try
            {
                PlanetCollidersServer pcs = Core.Services.ServiceLocator.GetService<PlanetCollidersServer>();
                IWorldTransposeServer tr = Core.Services.ServiceLocator.GetService<IWorldTransposeServer>();
                parts.Add("it is in space " + o.SpaceId + " (" + _spaces.GetSpace(o.SpaceId) + "), scene '" + o.Body.scene.name + "'");
                int crossing = 0, sameScene = 0, dead = 0;
                Ray ray = new Ray(o.Pos + up * 300f, -up);
                foreach (PlanetColliders.ISpawnedPlanetCollider c in pcs.Colliders)
                {
                    if (c == null || c.SpawnedObject == null) { dead++; continue; }
                    PlanetObjectPhysics po = c.SpawnedObject;
                    foreach (MeshCollider mc in po.GetComponentsInChildren<MeshCollider>(true))
                    {
                        if (!IsTerrain(mc)) continue;
                        // the piece's box, moved into the object's space, crossing a vertical line through it
                        Vector3 centre = mc.bounds.center;
                        if (po.SpaceId != o.SpaceId)
                        {
                            if (tr == null) continue;
                            try { centre = tr.Position(po.SpaceId, o.SpaceId, centre); } catch { continue; }
                        }
                        if (!new Bounds(centre, mc.bounds.size).IntersectRay(ray)) continue;
                        crossing++;
                        bool same = mc.gameObject.scene == o.Body.scene;
                        if (same) sameScene++;
                        if (crossing <= 3)
                            parts.Add("piece '" + mc.name + "' in space " + po.SpaceId + ", scene '" + mc.gameObject.scene.name + "'" + (same ? "" : " (ANOTHER SCENE)") + ", enabled " + mc.enabled + "/" + mc.gameObject.activeInHierarchy
                                + ", mesh " + (mc.sharedMesh != null ? mc.sharedMesh.vertexCount + " verts" : "none"));
                    }
                }
                if (dead > 0) parts.Add(dead + " listed piece(s) whose object is gone");
                parts.Add(crossing + " terrain piece(s) on a vertical line through it, " + sameScene + " in its scene");
            }
            catch (Exception e) { parts.Add("(" + e.Message + ")"); }
            return string.Join("; ", parts.ToArray());
        }

        // ---- a crash doesn't drive a ship into the ground ------------------------------------------
        // When a ship hits something hard enough to break its own parts, ShipCollisionDestruction.UpdateDestruction
        // gives it back part of its speed from before the hit (RestoreVelocity: it "breaks through" what it broke).
        // Against the ground nothing broke but the ship's own parts, and that speed points INTO the ground. For every
        // contact this step with a terrain piece, the part of the restored speed going into it is taken out; crashes
        // with other ships are left as the game has them.

        private static AccessTools.FieldRef<ShipCollisionDestruction, List<ContactPoint>> _contacts;
        private static AccessTools.FieldRef<ShipCollisionDestruction, ShipRigidbody> _rb;
        private static AccessTools.FieldRef<ShipRigidbody, Rigidbody> _body;
        private static ShipRigidbody _current;
        private static readonly List<Vector3> _normals = new List<Vector3>();     // out of the ground, at this ship's terrain contacts

        private static void DestructionPrefix(ShipCollisionDestruction __instance)
        {
            _current = null;
            _normals.Clear();
            if (!On) return;
            List<ContactPoint> cs = _contacts(__instance);
            if (cs == null || cs.Count == 0) return;
            ShipRigidbody srb = _rb(__instance);
            Rigidbody body = srb != null ? _body(srb) : null;
            if (body == null) return;
            foreach (ContactPoint c in cs)
            {
                if (c.otherCollider == null || !IsTerrain(c.otherCollider)) continue;
                Vector3 n = c.normal;
                if (Vector3.Dot(body.worldCenterOfMass - c.point, n) < 0f) n = -n;    // the side the ship's middle is on
                _normals.Add(n.normalized);
            }
            if (_normals.Count > 0) _current = srb;
        }

        private static Exception DestructionFinalizer(Exception __exception)
        {
            _current = null;
            _normals.Clear();
            return __exception;
        }

        private static void RestorePostfix(ShipRigidbody __instance)
        {
            if (__instance != _current || _normals.Count == 0) return;
            Rigidbody body = _body(__instance);
            if (body == null || body.isKinematic) return;
            Vector3 v = body.linearVelocity, v0 = v;
            foreach (Vector3 n in _normals)
            {
                float into = Vector3.Dot(v, n);
                if (into < 0f) v -= into * n;
            }
            if (v == v0) return;
            body.linearVelocity = v;
            RestoresClamped++;
            Log.Msg("ground: a crash into the ground would have pushed a ship on into it at " + (v0 - v).magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s; kept it out");
        }

        // ---- last resort: anything still in the ground is put back on top ---------------------------

        private static readonly Dictionary<uint, List<float>> _lifts = new Dictionary<uint, List<float>>();

        /// <summary>At most five times in ten seconds for one thing: if it keeps going back in, lifting it again is only hopping.</summary>
        private static bool MayLift(uint id, string what)
        {
            float now = Time.realtimeSinceStartup;
            List<float> l;
            if (!_lifts.TryGetValue(id, out l)) _lifts[id] = l = new List<float>();
            l.RemoveAll(x => now - x > 10f);
            if (l.Count >= 5) { Log.Once("lifts-" + id, "ground: " + what + " keeps ending up in the ground; leaving it be"); return false; }
            l.Add(now);
            return true;
        }

        private static void CheckPlayer(TrackedPlayerServer p)
        {
            if (p == null || p.IsCheatFlying || p.ServerRigidbody == null) { if (p != null) _below.Remove(p.Id); return; }
            float h = Height(p.SpaceId, p.Position);
            if (float.IsNaN(h)) { _below.Remove(p.Id); return; }
            float min;
            if (!_minH.TryGetValue(p.Id, out min) || h < min) _minH[p.Id] = h;
            Vector3 up = Up(p.SpaceId, p.Position);
            float depth = TerrainAt(p.ServerRigidbody.gameObject.scene, p.Position - up * FeetBelowBody, up, 60f);
            if (float.IsNaN(depth) || depth < PlayerDepth) { _below.Remove(p.Id); return; }
            int n; _below.TryGetValue(p.Id, out n); _below[p.Id] = ++n;
            if (n == 1)
            {
                PlayerEvents++;
                Note("player " + p.Name + "'s feet are " + depth.ToString("0.00", CultureInfo.InvariantCulture) + " m into the ground");
            }
            // three ticks in a row (not a single bad step): put them back on top
            if (n < 3 || !On || !MayLift(p.Id, "player " + p.Name)) return;
            Place(p, p.SpaceId, p.Position + up * (depth + 0.05f), p.Rotation, CameraRotation(p));
            Rescues++;
            _below.Remove(p.Id);
            Log.Msg("ground: put " + p.Name + " back on the surface");
        }

        private static readonly Dictionary<uint, Collider[]> _shipColliders = new Dictionary<uint, Collider[]>();
        private static readonly Dictionary<uint, float> _shipReach = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, Quaternion> _shipTurn = new Dictionary<uint, Quaternion>();
        private static readonly Dictionary<uint, int> _shipParts = new Dictionary<uint, int>();

        private static void CheckShips()
        {
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (t == null) return;
            foreach (TrackedShipServer s in t.LoadedRootShips)
            {
                if (s == null || !s.IsRigidbodyLoaded || s.RigidbodyObject == null) continue;
                Vector3 up = Up(s.SpaceId, s.Position);
                if (up == Vector3.zero) { _below.Remove(s.Id); continue; }
                Rigidbody rb = s.RigidbodyObject.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue;
                // only a ship going DOWN can be sinking through the ground: one at rest, even with parts dug into the
                // ground as built, is held up by it and left exactly where it is (and costs nothing here)
                if (-Vector3.Dot(rb.linearVelocity, up) < SinkingSpeed) { _below.Remove(s.Id); continue; }
                // its 16 lowest colliders along up and how far its parts reach, worked out again when it turns or changes
                Collider[] cs; Quaternion turned; int parts;
                if (!_shipColliders.TryGetValue(s.Id, out cs) || !_shipTurn.TryGetValue(s.Id, out turned) || !_shipParts.TryGetValue(s.Id, out parts)
                    || parts != s.PartCount || Quaternion.Angle(turned, s.RigidbodyObject.transform.rotation) > 5f || cs.Any(c => c == null))
                {
                    Collider[] all = s.RigidbodyObject.GetComponentsInChildren<Collider>();
                    List<KeyValuePair<float, Collider>> low = new List<KeyValuePair<float, Collider>>(all.Length);
                    float r = 0f;
                    Vector3 mid = s.RigidbodyObject.transform.position;
                    foreach (Collider c in all)
                    {
                        if (c == null || !c.enabled || c.isTrigger) continue;
                        Vector3 p = Lowest(c, up);
                        low.Add(new KeyValuePair<float, Collider>(Vector3.Dot(p, up), c));
                        float d = (p - mid).magnitude; if (d > r) r = d;
                    }
                    low.Sort((x, y) => x.Key.CompareTo(y.Key));
                    cs = low.Take(16).Select(x => x.Value).ToArray();
                    _shipColliders[s.Id] = cs; _shipReach[s.Id] = r;
                    _shipTurn[s.Id] = s.RigidbodyObject.transform.rotation; _shipParts[s.Id] = s.PartCount;
                }
                if (cs.Length == 0) continue;
                float reach; _shipReach.TryGetValue(s.Id, out reach);
                float centre = Height(s.SpaceId, s.RigidbodyObject.transform.position);
                if (!float.IsNaN(centre) && centre > reach + 10f) { _below.Remove(s.Id); continue; }    // well clear of the ground
                Scene scene = s.RigidbodyObject.scene;
                float depth = 0f;
                foreach (Collider c in cs)
                {
                    if (c == null) continue;
                    float d = TerrainAt(scene, Lowest(c, up), up, 60f);
                    if (!float.IsNaN(d) && d > depth) depth = d;
                }
                if (depth < ShipDepth) { _below.Remove(s.Id); continue; }
                int n; _below.TryGetValue(s.Id, out n); _below[s.Id] = ++n;
                if (n == 1)
                {
                    ShipEvents++;
                    Note("ship #" + s.Id + " (" + s.PartCount + " parts) is sinking, " + depth.ToString("0.00", CultureInfo.InvariantCulture) + " m into the ground");
                }
                // two checks in a row (or deep at once): lift it back on top, and stop it going down
                if ((n < 2 && depth < ShipDeepAtOnce) || !On || !MayLift(s.Id, "ship #" + s.Id)) continue;
                rb.position += up * (depth + 0.1f);
                Vector3 v = rb.linearVelocity;
                float down = Vector3.Dot(v, up);
                if (down < 0f) rb.linearVelocity = v - up * down;
                ShipRescues++;
                _below.Remove(s.Id);
                Log.Msg("ground: lifted ship #" + s.Id + " " + depth.ToString("0.0", CultureInfo.InvariantCulture) + " m back out of the ground");
            }
        }

        private static void Note(string what)
        {
            LastEvent = DateTime.Now.ToString("HH:mm:ss") + " " + what;
            Log.Warn("ground: " + what);
        }

        /// <summary>
        /// The lowest point of a collider along `up`: the lowest corner of its own box (in its own
        /// axes). The world box Unity keeps (Collider.bounds) is aligned with the space's axes, which on
        /// a planet aren't up and down, so it reaches far below a part that's tilted against them.
        /// </summary>
        public static Vector3 Lowest(Collider c, Vector3 up)
        {
            Vector3 center, size;
            if (c is BoxCollider bx) { center = bx.center; size = bx.size; }
            else if (c is MeshCollider mc && mc.sharedMesh != null) { center = mc.sharedMesh.bounds.center; size = mc.sharedMesh.bounds.size; }
            else if (c is SphereCollider sc) { center = sc.center; size = Vector3.one * sc.radius * 2f; }
            else if (c is CapsuleCollider cc) { center = cc.center; size = new Vector3(cc.radius * 2f, cc.radius * 2f, cc.radius * 2f); size[cc.direction] = Mathf.Max(cc.height, cc.radius * 2f); }
            else { Bounds b = c.bounds; float ext = Mathf.Abs(b.extents.x * up.x) + Mathf.Abs(b.extents.y * up.y) + Mathf.Abs(b.extents.z * up.z); return b.center - up * ext; }
            Transform t = c.transform;
            Vector3 best = t.TransformPoint(center);
            float bestD = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + Vector3.Scale(size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 w = t.TransformPoint(corner);
                float d = Vector3.Dot(w, up);
                if (d < bestD) { bestD = d; best = w; }
            }
            return best;
        }

#if DSTEST
        /// <summary>For tests: the terrain pieces under a ship are destroyed behind the server's back (it still lists them).</summary>
        public static string PunchHole(TrackedShipServer ship)
        {
            if (ship == null || ship.RigidbodyObject == null) return "no ship";
            Vector3Double pp; ulong pid; float e;
            Height(ship.SpaceId, ship.Position, out pp, out pid, out e);
            Vector3 up = (Vector3)(pp / pp.Magnitude);
            PlanetCollidersServer pcs = Core.Services.ServiceLocator.GetService<PlanetCollidersServer>();
            int n = 0;
            foreach (PlanetColliders.ISpawnedPlanetCollider c in pcs.Colliders.ToList())
            {
                if (c == null || c.SpawnedObject == null) continue;
                foreach (MeshCollider mc in c.SpawnedObject.GetComponentsInChildren<MeshCollider>(true))
                    if (IsTerrain(mc) && (mc.bounds.SqrDistance(ship.Position) < 40f * 40f)) { UnityEngine.Object.DestroyImmediate(mc); n++; break; }
            }
            return "destroyed " + n + " ground piece(s) around ship #" + ship.Id;
        }

#endif
        public static float MinHeight(uint playerId) { float m; return _minH.TryGetValue(playerId, out m) ? m : float.NaN; }

        // ---- moving players ----------------------------------------------------------------

        internal static Quaternion CameraRotation(TrackedPlayerServer p)
        {
            return Quaternion.LookRotation(p.Rotation * Vector3.forward, p.Rotation * Vector3.up);
        }

        /// <summary>Puts a player at a place in a space, still, and tells their game.</summary>
        public static bool Place(TrackedPlayerServer p, uint spaceId, Vector3 pos, Quaternion rot, Quaternion cam)
        {
            if (!Services() || p == null) return false;
            NetworkConnection conn = Server.Connection(p.ConnectionId);
            _spaces.SetWorldObjectPhysics(p, spaceId, new LocalPhysicsState { Position = pos, Rotation = rot, Velocity = Vector3.zero });
            try { if (p.ServerRigidbody != null && !p.ServerRigidbody.isKinematic) { p.ServerRigidbody.linearVelocity = Vector3.zero; p.ServerRigidbody.angularVelocity = Vector3.zero; } } catch { }
            PlayerRespawnService rs = UnityEngine.Object.FindFirstObjectByType<PlayerRespawnService>();
            if (rs == null || conn == null) return false;
            PlanetSurfaceSpace surface = _spaces.GetSpace(p.SpaceId) as PlanetSurfaceSpace;
            if (surface != null)
                AccessTools.Method(typeof(PlayerRespawnService), "NotifyRespawnAtSurface").Invoke(rs, new object[] { conn, surface, p.PhysicsState, cam, FishNet.Transporting.Channel.Reliable });
            else
                AccessTools.Method(typeof(PlayerRespawnService), "NotifyRespawnAtShip").Invoke(rs, new object[] { conn, p.PhysicsState, cam, FishNet.Transporting.Channel.Reliable });
            return true;
        }

        /// <summary>Moves `who` next to `to` (a step behind them, facing the same way).</summary>
        public static string Teleport(TrackedPlayerServer who, TrackedPlayerServer to)
        {
            if (who == null || to == null) return "no such player on now";
            if (who == to) return "that's the same player";
            Vector3 pos = to.Position + to.Rotation * Vector3.back * 1.2f;
            float h = Height(to.SpaceId, pos);
            if (!float.IsNaN(h) && h < 0.3f)
            {
                Vector3Double pp; ulong pid; float e;
                Height(to.SpaceId, pos, out pp, out pid, out e);
                pos += (Vector3)(pp / pp.Magnitude) * (0.3f - h);
            }
            return Place(who, to.SpaceId, pos, to.Rotation, CameraRotation(to))
                ? "moved " + who.Name + " to " + to.Name
                : "moved " + who.Name + " on the server, but could not tell their game";
        }

        /// <summary>Puts a player standing on top of a ship.</summary>
        public static string OntoShip(TrackedPlayerServer who, TrackedShipServer ship)
        {
            if (who == null) return "no such player on now";
            Vector3 pos, up;
            bool onIt = ShipTop(ship, out pos, out up);
            Place(who, ship.SpaceId, pos, who.Rotation, CameraRotation(who));
            return (onIt ? "put " + who.Name + " on ship #" : "put " + who.Name + " above ship #") + ship.Id;
        }

        /// <summary>
        /// A spot standing on top of a ship, in its space, and which way is up there. False when the ship is asleep (no
        /// colliders yet): then the spot is a little above where it is.
        /// </summary>
        public static bool ShipTop(TrackedShipServer ship, out Vector3 pos, out Vector3 up)
        {
            Collider[] cs = ship.RigidbodyObject != null ? ship.RigidbodyObject.GetComponentsInChildren<Collider>().Where(c => c != null && c.enabled && !c.isTrigger).ToArray() : new Collider[0];
            Vector3Double pp; ulong pid; float e;
            if (cs.Length == 0)
            {
                Height(ship.SpaceId, ship.Position, out pp, out pid, out e);
                up = pp.Magnitude > 1 ? (Vector3)(pp / pp.Magnitude) : Vector3.up;
                pos = ship.Position + up * 12f;
                return false;
            }
            Bounds b = cs[0].bounds;
            foreach (Collider c in cs) b.Encapsulate(c.bounds);
            Height(ship.SpaceId, b.center, out pp, out pid, out e);
            up = pp.Magnitude > 1 ? (Vector3)(pp / pp.Magnitude) : Vector3.up;
            float ext = Mathf.Abs(b.extents.x * up.x) + Mathf.Abs(b.extents.y * up.y) + Mathf.Abs(b.extents.z * up.z);
            pos = b.center + up * (ext + 0.4f);
            return true;
        }

#if DSTEST

        /// <summary>Puts a player on the ground at a point given in their planet's own coordinates (for tests).</summary>
        public static string MoveTo(TrackedPlayerServer who, double x, double y, double z)
        {
            Vector3Double pp; ulong pid; float e;
            if (float.IsNaN(Height(who.SpaceId, who.Position, out pp, out pid, out e))) return who.Name + " is not on a planet's surface";
            Vector3Double target = new Vector3Double(x, y, z);
            double r = target.Magnitude;
            float elev = _planets.GetPlanetTerrainElevationAtPosition(pid, (Vector3)(target / r));
            target = target / r * (elev + 1.5);
            _spaces.MoveWorldObject(who, new PlanetSurfaceSpace { PlanetId = pid, Center = target });
            PlanetSurfaceSpace s = (PlanetSurfaceSpace)_spaces.GetSpace(who.SpaceId);
            Place(who, who.SpaceId, (Vector3)(target - s.Center), who.Rotation, CameraRotation(who));
            return "moved " + who.Name + " to " + target + " (space " + who.SpaceId + ")";
        }

        /// <summary>Moves a player along the planet's surface (metres north/east of where they are), for tests and admins.</summary>
        public static string Move(TrackedPlayerServer who, float forward, float right)
        {
            Vector3Double pp; ulong pid; float e;
            float h = Height(who.SpaceId, who.Position, out pp, out pid, out e);
            if (float.IsNaN(h)) return who.Name + " is not on a planet's surface";
            Vector3 up = (Vector3)(pp / pp.Magnitude);
            Vector3 fwd = Vector3.ProjectOnPlane(who.Rotation * Vector3.forward, up).normalized;
            Vector3 side = Vector3.Cross(up, fwd);
            Vector3Double target = pp + (Vector3Double)(fwd * forward + side * right);
            double r = target.Magnitude;
            float elev = _planets.GetPlanetTerrainElevationAtPosition(pid, (Vector3)(target / r));
            target = target / r * (elev + 1.0);
            PlanetSurfaceSpace s = (PlanetSurfaceSpace)_spaces.GetSpace(who.SpaceId);
            Vector3 local = (Vector3)(target - s.Center);
            Place(who, who.SpaceId, local, who.Rotation, CameraRotation(who));
            return "moved " + who.Name + " " + Math.Sqrt(forward * forward + right * right).ToString("0", CultureInfo.InvariantCulture) + " m";
        }

#endif
        public static IEnumerable<string> Report()
        {
            yield return "ground: " + PlayerEvents + " time(s) a player was under the ground, " + ShipEvents + " time(s) a ship; " + Rescues + " player(s) and " + ShipRescues + " ship(s) put back, ground remade " + (Repairs + DeadDropped) + " time(s), crashes kept out of the ground " + RestoresClamped + " (GroundRescue " + (Server.Cfg.GroundRescue.Value ? "on" : "off") + (Server.NecessaryFixes ? "; NecessaryFixes is loaded and does all of this itself" : "") + ")";
            yield return "  " + Audits + " hole check(s), " + Holes + " time(s) something near the ground had no terrain piece under it in its own physics scene";
            if (LastEvent.Length > 0) yield return "  last: " + LastEvent;
            foreach (TrackedPlayerServer p in Server.ActivePlayers())
            {
                float h = Height(p.SpaceId, p.Position);
                yield return "  " + p.Name + ": " + (float.IsNaN(h) ? "not on a planet's surface" : h.ToString("0.00", CultureInfo.InvariantCulture) + " m above the ground, lowest " + MinHeight(p.Id).ToString("0.00", CultureInfo.InvariantCulture) + " m") + ", space " + p.SpaceId;
            }
        }
    }
}
