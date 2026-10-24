// Trees and ores growing back.
//
// The game keeps, per planet, the sets of things that are gone: trees, bushes and rocks ("dense"
// objects), ore deposits ("hotspot" objects), a few fixed resources, and ores on asteroids. Only the
// ore deposits are saved with the world, so on a vanilla server every tree that was ever cut comes back
// at the next restart, wherever it is, including inside ships and builds parked there since.
//
// This keeps its own record of when each thing went (worlds/<name>/respawn.json) and:
//   * at load, puts back everything that has not reached its time yet (so a restart changes nothing);
//   * once a minute, brings back what has (RespawnTreesMinutes, RespawnOresMinutes), except anything
//     that would come up inside or right next to a ship, a build or a player (RespawnClearance). Those
//     wait and are tried again later, so things grow back around them;
//   * tells every player, whose game then grows them back in place (RespawnVisuals.cs).
//
// To see where the waiting things are, the game's own placement code is asked what would be at each
// ship and player, with the waiting things counted as present for that one question.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Values;
using HarmonyLib;
using Newtonsoft.Json;
using Planets;
using Planets.EnvironmentGeneration.ToSpawn;
using Planets.Interface.Model;
using Planets.Network;
using Planets.Network.Model;
using Planets.Objects.Common.Model;
using Planets.Rings.Asteroid;
using Planets.Rings.Model;
using Planets.Services;
using Players;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static class Respawn
    {
        public const string Tree = "tree", Ore = "ore", Fixed = "fixed", Asteroid = "asteroid";

        public class Rec
        {
            public ulong Planet;
            public string Kind = "";
            public string Id = "";
            public DateTime At;
        }

        private static Dictionary<string, Rec> _recs = new Dictionary<string, Rec>();
        private static bool _quiet, _loaded;
        private static float _next;
        private static HashSet<string> _probe;                // while asking "what would be here": these count as present
        public static int Respawned, Blocked;
        public static DateTime LastSweep;
        private static readonly Dictionary<ulong, PlanetNetworkedState> _states = new Dictionary<ulong, PlanetNetworkedState>();

        private static string Key(ulong planet, string kind, string id) { return planet + "|" + kind + "|" + id; }
        private static string FilePath { get { return Server.WorldPath == null ? null : Path.Combine(Path.GetDirectoryName(Server.WorldPath), "respawn.json"); } }

        public static int Pending { get { return _recs.Count; } }

        // ---- ids as text ---------------------------------------------------------------------------

        public static string IdOf(ChunkObjectId c) { return c.ChunkHash.ToString(CultureInfo.InvariantCulture) + ":" + c.IdWithinChunk.ToString(CultureInfo.InvariantCulture); }

        public static bool ParseChunk(string s, out ChunkObjectId c)
        {
            c = default(ChunkObjectId);
            string[] p = (s ?? "").Split(':');
            ulong h; uint i;
            if (p.Length != 2 || !ulong.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) || !uint.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return false;
            c = new ChunkObjectId(h, i);
            return true;
        }

        public static string IdOf(AsteroidResourceObjectId a)
        {
            AsteroidId x = a.AsteroidId;
            return x.RingSegmentId + ":" + x.ChunkId + ":" + x.PatternIndex + ":" + a.ResourceId;
        }

        private static string AsteroidPrefix(AsteroidId x) { return x.RingSegmentId + ":" + x.ChunkId + ":" + x.PatternIndex + ":"; }

        public static bool ParseAsteroid(ulong planet, string s, out AsteroidResourceObjectId a)
        {
            a = default(AsteroidResourceObjectId);
            string[] p = (s ?? "").Split(':');
            ushort seg, chunk; uint pat; byte res;
            if (p.Length != 4 || !ushort.TryParse(p[0], out seg) || !ushort.TryParse(p[1], out chunk) || !uint.TryParse(p[2], out pat) || !byte.TryParse(p[3], out res)) return false;
            a = new AsteroidResourceObjectId { AsteroidId = new AsteroidId { PlanetId = planet, RingSegmentId = seg, ChunkId = chunk, PatternIndex = pat }, ResourceId = res };
            return true;
        }

        // ---- hooks -----------------------------------------------------------------------------------

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                Type t = typeof(PlanetNetworkedState);
                Post(h, t, "ServerMineDenseObject", "DensePostfix");
                Post(h, t, "ServerCollisionDestroyDenseObject", "DensePostfix");
                Post(h, t, "ServerDestroyHotspotObject", "HotspotPostfix");
                Post(h, t, "ServerDestroyStaticResource", "FixedPostfix");
                Post(h, t, "ServerDestroyAsteroidResource", "AsteroidPostfix");
                Pre(h, t, "IsDenseObjectDestroyed", "IsDensePrefix");
                Pre(h, t, "IsHotspotObjectDestroyed", "IsHotspotPrefix");
                Pre(h, t, "IsStaticResourceDestroyed", "IsFixedPrefix");
                Pre(h, t, "IsAsteroidResourceDestroyed", "IsAsteroidPrefix");
            }
            catch (Exception e) { Log.Warn("respawn: could not hook the planets' destroyed lists: " + e.Message); }
        }

        private static void Post(HarmonyLib.Harmony h, Type t, string m, string fix) { h.Patch(AccessTools.Method(t, m), postfix: new HarmonyMethod(typeof(Respawn).GetMethod(fix, BindingFlags.NonPublic | BindingFlags.Static))); }
        private static void Pre(HarmonyLib.Harmony h, Type t, string m, string fix) { h.Patch(AccessTools.Method(t, m), prefix: new HarmonyMethod(typeof(Respawn).GetMethod(fix, BindingFlags.NonPublic | BindingFlags.Static))); }

        private static ulong PlanetOf(PlanetNetworkedState s)
        {
            try { Planet p = Traverse.Create(s).Field("_planet").GetValue<Planet>(); return p != null ? p.Id : 0; } catch { return 0; }
        }

        private static void Note(PlanetNetworkedState s, string kind, string id)
        {
            if (_quiet || !DedicatedServerMod.IsServer) return;
            ulong planet = PlanetOf(s);
            if (planet == 0) return;
            string k = Key(planet, kind, id);
            if (!_recs.ContainsKey(k)) _recs[k] = new Rec { Planet = planet, Kind = kind, Id = id, At = DateTime.UtcNow };
            Log.Debug("respawn", kind + " " + id + " on planet " + planet + " is gone; noted");
        }

        private static void DensePostfix(PlanetNetworkedState __instance, ChunkObjectId objectId) { Note(__instance, Tree, IdOf(objectId)); }
        private static void HotspotPostfix(PlanetNetworkedState __instance, ChunkObjectId objectId) { Note(__instance, Ore, IdOf(objectId)); }
        private static void FixedPostfix(PlanetNetworkedState __instance, int objectId) { Note(__instance, Fixed, objectId.ToString(CultureInfo.InvariantCulture)); }
        private static void AsteroidPostfix(PlanetNetworkedState __instance, AsteroidResourceObjectId objectId) { Note(__instance, Asteroid, IdOf(objectId)); }

        // while probing, the things being checked count as still there
        private static bool IsDensePrefix(PlanetNetworkedState __instance, ChunkObjectId objectId, ref bool __result) { return Probe(__instance, Tree, IdOf(objectId), ref __result); }
        private static bool IsHotspotPrefix(PlanetNetworkedState __instance, ChunkObjectId objectId, ref bool __result) { return Probe(__instance, Ore, IdOf(objectId), ref __result); }
        private static bool IsFixedPrefix(PlanetNetworkedState __instance, int id, ref bool __result) { return Probe(__instance, Fixed, id.ToString(CultureInfo.InvariantCulture), ref __result); }
        private static bool IsAsteroidPrefix(PlanetNetworkedState __instance, AsteroidResourceObjectId resourceId, ref bool __result) { return Probe(__instance, Asteroid, IdOf(resourceId), ref __result); }

        private static bool Probe(PlanetNetworkedState s, string kind, string id, ref bool result)
        {
            if (_probe == null) return true;
            if (!_probe.Contains(Key(PlanetOf(s), kind, id))) return true;
            result = false;
            return false;
        }

        // ---- the planets' own sets ---------------------------------------------------------------

        private static PlanetNetworkedState StateOf(ulong planet)
        {
            PlanetNetworkedState s;
            if (_states.TryGetValue(planet, out s) && s != null) return s;
            _states.Clear();
            foreach (PlanetNetworkedState x in UnityEngine.Object.FindObjectsByType<PlanetNetworkedState>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ulong id = PlanetOf(x);
                if (id != 0) _states[id] = x;
            }
            return _states.TryGetValue(planet, out s) ? s : null;
        }

        private static Dictionary<ulong, HashSet<uint>> ServerDict(PlanetNetworkedState s, string kind)
        {
            DestroyedChunkObjectsSync sync = kind == Tree ? s._destroyedDenseObjects : s._destroyedHotspotsObjects;
            return Traverse.Create(sync).Field("_destroyedDenseObjectsServer").GetValue<Dictionary<ulong, HashSet<uint>>>();
        }

        /// <summary>Gone again (at load), without a new time.</summary>
        private static void Redestroy(Rec r)
        {
            PlanetNetworkedState s = StateOf(r.Planet);
            if (s == null) return;
            ChunkObjectId c; AsteroidResourceObjectId a; int n;
            _quiet = true;
            try
            {
                if (r.Kind == Tree && ParseChunk(r.Id, out c) && !s.IsDenseObjectDestroyed(c, true)) s.ServerMineDenseObject(c);
                else if (r.Kind == Ore && ParseChunk(r.Id, out c) && !s.IsHotspotObjectDestroyed(c, true)) s.ServerDestroyHotspotObject(c);
                else if (r.Kind == Fixed && int.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && !s.IsStaticResourceDestroyed(n, true)) s.ServerDestroyStaticResource(n);
                else if (r.Kind == Asteroid && ParseAsteroid(r.Planet, r.Id, out a) && !s.IsAsteroidResourceDestroyed(a, true)) s.ServerDestroyAsteroidResource(a);
            }
            finally { _quiet = false; }
        }

        /// <summary>Back on the server. The players are told separately.</summary>
        private static bool Restore(Rec r)
        {
            PlanetNetworkedState s = StateOf(r.Planet);
            if (s == null) return false;
            ChunkObjectId c; AsteroidResourceObjectId a; int n;
            if ((r.Kind == Tree || r.Kind == Ore) && ParseChunk(r.Id, out c))
            {
                Dictionary<ulong, HashSet<uint>> d = ServerDict(s, r.Kind);
                HashSet<uint> set;
                if (d == null || !d.TryGetValue(c.ChunkHash, out set)) return true;
                set.Remove(c.IdWithinChunk);
                if (set.Count == 0) d.Remove(c.ChunkHash);
                return true;
            }
            if (r.Kind == Fixed && int.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { s._destroyedStaticResources.Remove(n); return true; }
            if (r.Kind == Asteroid && ParseAsteroid(r.Planet, r.Id, out a)) { s._destroyedAsteroidResources.Remove(a); return true; }
            return false;
        }

        // ---- load and save -----------------------------------------------------------------------

        /// <summary>The world is loaded: read the record and put back what isn't due.</summary>
        public static void OnWorldReady()
        {
            _recs.Clear();
            _states.Clear();
            string f = FilePath;
            try
            {
                if (f != null && File.Exists(f))
                    foreach (Rec r in JsonConvert.DeserializeObject<List<Rec>>(File.ReadAllText(f)) ?? new List<Rec>())
                        if (r != null && r.Planet != 0 && r.Kind.Length > 0) _recs[Key(r.Planet, r.Kind, r.Id)] = r;
            }
            catch (Exception e) { Log.Warn("respawn: could not read " + f + ": " + e.Message); }
            int put = 0;
            foreach (Rec r in _recs.Values.ToList()) { Redestroy(r); put++; }
            // ore deposits the world already had (saved by the game, from before this record): from now
            int adopted = 0;
            foreach (KeyValuePair<ulong, PlanetNetworkedState> kv in AllStates())
            {
                Dictionary<ulong, HashSet<uint>> d = ServerDict(kv.Value, Ore);
                if (d == null) continue;
                foreach (KeyValuePair<ulong, HashSet<uint>> chunk in d)
                    foreach (uint id in chunk.Value)
                    {
                        string k = Key(kv.Key, Ore, IdOf(new ChunkObjectId(chunk.Key, id)));
                        if (!_recs.ContainsKey(k)) { _recs[k] = new Rec { Planet = kv.Key, Kind = Ore, Id = IdOf(new ChunkObjectId(chunk.Key, id)), At = DateTime.UtcNow }; adopted++; }
                    }
            }
            _loaded = true;
            _next = Time.realtimeSinceStartup + 20f;
            Log.Msg("respawn: " + put + " record(s) loaded" + (adopted > 0 ? ", " + adopted + " mined ore deposit(s) added" : "")
                + "; trees " + Minutes(Server.Cfg.RespawnTreesMinutes.Value) + ", ores " + Minutes(Server.Cfg.RespawnOresMinutes.Value));
        }

        private static string Minutes(int m) { return m <= 0 ? "off" : m + " min"; }

        private static IEnumerable<KeyValuePair<ulong, PlanetNetworkedState>> AllStates()
        {
            StateOf(0);
            return _states.ToList();
        }

        public static void Save()
        {
            if (!_loaded) return;
            string f = FilePath;
            if (f == null) return;
            try
            {
                string tmp = f + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_recs.Values.OrderBy(r => r.At).ToList(), Formatting.Indented));
                if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
            }
            catch (Exception e) { Log.Warn("respawn: could not save " + f + ": " + e.Message); }
        }

        // ---- growing back -----------------------------------------------------------------------

        private static int MinutesFor(string kind)
        {
            return kind == Tree ? Server.Cfg.RespawnTreesMinutes.Value : Server.Cfg.RespawnOresMinutes.Value;
        }

        public static void Update()
        {
            if (!_loaded || !Server.Ready || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 60f;
            try { Sweep(false); }
            catch (Exception e) { Log.Once("respawn-sweep", "respawn: " + e); }
        }

        /// <summary>Brings back what is due (or everything, with all) and clear of ships and players. A line for the console.</summary>
        private static readonly Dictionary<string, DateTime> _waitUntil = new Dictionary<string, DateTime>();

        public static string Sweep(bool all)
        {
            LastSweep = DateTime.UtcNow;
            DateTime now = DateTime.UtcNow;
            // (finding what is in the way asks the planet's generator around every ship and player - a quarter of a second on
            // Ethan's server - so something found blocked is only looked at again 10 minutes later, not every minute)
            List<Rec> due = _recs.Values.Where(r =>
            {
                int m = MinutesFor(r.Kind);
                DateTime until;
                if (!all && _waitUntil.TryGetValue(Key(r.Planet, r.Kind, r.Id), out until) && now < until) return false;
                return all || (m > 0 && (now - r.At).TotalMinutes >= m);
            }).ToList();
            if (due.Count == 0) return "Nothing is due to grow back.";
            int back = 0, waiting = 0;
            foreach (IGrouping<ulong, Rec> planet in due.GroupBy(r => r.Planet))
            {
                HashSet<string> blocked = InTheWay(planet.Key, planet.ToList());
                foreach (string k in blocked) _waitUntil[k] = now.AddMinutes(10);
                List<Rec> go = planet.Where(r => !blocked.Contains(Key(r.Planet, r.Kind, r.Id))).ToList();
                waiting += planet.Count() - go.Count;
                List<string> lines = new List<string>();
                foreach (Rec r in go)
                {
                    if (!Restore(r)) continue;
                    _recs.Remove(Key(r.Planet, r.Kind, r.Id));
                    _waitUntil.Remove(Key(r.Planet, r.Kind, r.Id));
                    lines.Add(r.Kind + "\t" + r.Id);
                    back++;
                }
                if (lines.Count == 0) continue;
                RefreshColliders();
                // the players' games: in pieces, so no one message gets too big
                for (int i = 0; i < lines.Count; i += 400)
                    Net.EventAll("respawn", planet.Key.ToString(CultureInfo.InvariantCulture), string.Join("\n", lines.Skip(i).Take(400).ToArray()));
            }
            Respawned += back; Blocked = _waitUntil.Count(kv => kv.Value > now);
            if (back > 0 || waiting > 0) Log.Msg("respawn: " + back + " restored" + (waiting > 0 ? ", " + waiting + " blocked by a ship, build or player" : ""));
            if (back > 0) Save();
            return back + " grew back" + (waiting > 0 ? ", " + waiting + " wait for a ship, build or player to move" : "") + ".";
        }

        /// <summary>The server's planet colliders are worked out again around everything (the restored things get theirs).</summary>
        private static void RefreshColliders()
        {
            try
            {
                PlanetCollidersServer pcs = Core.Services.ServiceLocator.GetService<PlanetCollidersServer>();
                object pc = AccessTools.Field(typeof(PlanetCollidersServer), "_planetColliders").GetValue(pcs);
                IDictionary passes = AccessTools.Field(typeof(PlanetColliders), "_generationPasses").GetValue(pc) as IDictionary;
                if (passes != null) passes.Clear();
            }
            catch (Exception e) { Log.Once("respawn-colliders", "respawn: could not refresh colliders: " + e.Message); }
        }

        // ---- what is in the way ------------------------------------------------------------------

        private class Obstacle
        {
            public bool IsShip;
            public Vector3Double Pos;          // planet-centred
            public Quaternion Rot;
            public Bounds Local;               // ship: parts' bounds in the ship's own frame
            public ISpace Space;               // where to ask (ring ships ask in their ring space)
            public Vector3 SpacePos;
            public List<Vector3Double> Points = new List<Vector3Double>();
        }

        private static HashSet<string> InTheWay(ulong planet, List<Rec> due)
        {
            HashSet<string> blocked = new HashSet<string>();
            HashSet<string> keys = new HashSet<string>(due.Select(r => Key(r.Planet, r.Kind, r.Id)));
            PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            IWorldTranspose transpose = Core.Services.ServiceLocator.GetService<IWorldTranspose>();
            if (planets == null || spaces == null || transpose == null) { foreach (string k in keys) blocked.Add(k); return blocked; }
            IPlanetColliderGenerator gen = planets.GetPlanetColliderGenerator(planet);
            Planet p = (gen as TrackedPlanet) != null ? ((TrackedPlanet)gen).Planet : null;
            if (gen == null || p == null) { foreach (string k in keys) blocked.Add(k); return blocked; }
            PlanetSurfaceSpace centre = new PlanetSurfaceSpace { Center = Vector3Double.Zero, PlanetId = planet };
            List<Obstacle> obstacles = Obstacles(planet, centre, spaces, transpose);
            float clear = Server.Cfg.RespawnClearance.Value;
            _probe = keys;
            try
            {
                foreach (Obstacle o in obstacles)
                {
                    List<IPlanetObjectSpawnData> near = new List<IPlanetObjectSpawnData>();
                    if (o.Space is PlanetRingSpace) near.AddRange(gen.GetCollidersToSpawnAtPosition((IPlanetSpace)o.Space, o.SpacePos));
                    else foreach (Vector3Double q in o.Points) near.AddRange(gen.GetCollidersToSpawnAtPosition(new PlanetSurfaceSpace { Center = q, PlanetId = planet }, Vector3.zero));
                    foreach (IPlanetObjectSpawnData d in near)
                    {
                        string kind; string id; Vector3Double at; float height;
                        if (d is DensePlanetObjectSpawnData dense) { kind = Tree; id = IdOf(dense.ObjectId.PlacedObjectId); at = dense.Transform.SurfacePosition; height = 12f; }
                        else if (d is HotspotObjectToSpawn hot) { kind = Ore; id = IdOf(hot.ObjectId.PlacedObjectId); at = (Vector3Double)hot.Transform.Position; height = 3f; }
                        else if (d is StaticObjectToSpawn st) { kind = Fixed; id = st.StaticObjectId.Index.ToString(CultureInfo.InvariantCulture); at = st.Transform.SurfacePosition; height = 6f; }
                        else if (d is AsteroidObjectToSpawn ast)
                        {
                            // anything on an asteroid a ship is at waits
                            string pre = Key(planet, Asteroid, AsteroidPrefix(ast.AsteroidId));
                            foreach (string k in keys) if (k.StartsWith(pre, StringComparison.Ordinal)) blocked.Add(k);
                            continue;
                        }
                        else continue;
                        string key = Key(planet, kind, id);
                        if (!keys.Contains(key) || blocked.Contains(key)) continue;
                        if (Hits(o, at, height, clear)) { blocked.Add(key); Log.Debug("respawn", kind + " " + id + " waits: " + (o.IsShip ? "a ship" : "a player") + " is in the way"); }
                    }
                }
            }
            finally { _probe = null; }
            return blocked;
        }

        /// <summary>Ships and players on (or in the ring of) this planet.</summary>
        private static List<Obstacle> Obstacles(ulong planet, ISpace centre, ISpacesServerProvider spaces, IWorldTranspose transpose)
        {
            List<Obstacle> l = new List<Obstacle>();
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships != null)
                foreach (TrackedShipServer s in ships.Ships)
                {
                    try
                    {
                        if (s == null || s.IsShipDestroyed) continue;
                        ShipPhysicsState ps = s.PhysicsState;
                        ISpace sp = spaces.GetSpace(ps.SpaceId);
                        if (!OnPlanet(sp, planet, spaces)) continue;
                        Obstacle o = new Obstacle { IsShip = true, Space = sp, SpacePos = ps.Position };
                        o.Pos = transpose.Position(sp, centre, (Vector3Double)ps.Position);
                        o.Rot = transpose.Rotation(sp, centre, ps.Rotation);
                        o.Local = ShipBounds(s);
                        // ask at the middle and the corners, so a long ship is covered end to end
                        o.Points.Add(o.Pos + (Vector3Double)(o.Rot * o.Local.center));
                        Vector3 e = o.Local.extents;
                        if (e.magnitude > 15f)
                            for (int i = 0; i < 8; i++)
                            {
                                Vector3 corner = o.Local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                                o.Points.Add(o.Pos + (Vector3Double)(o.Rot * corner));
                            }
                        l.Add(o);
                    }
                    catch (Exception ex) { Log.Debug("respawn", "ship " + (s != null ? s.Id : 0) + " skipped: " + ex.Message); }
                }
            foreach (TrackedPlayerServer p in Server.ActivePlayers())
            {
                try
                {
                    ISpace sp = spaces.GetSpace(p.SpaceId);
                    if (!OnPlanet(sp, planet, spaces)) continue;
                    Obstacle o = new Obstacle { IsShip = false, Space = sp, SpacePos = p.Position };
                    o.Pos = transpose.Position(sp, centre, (Vector3Double)p.Position);
                    o.Points.Add(o.Pos);
                    l.Add(o);
                }
                catch { }
            }
            return l;
        }

        private static bool OnPlanet(ISpace sp, ulong planet, ISpacesServerProvider spaces)
        {
            if (sp is IPlanetSpace ps) return ps.PlanetId == planet;
            return false;
        }

        /// <summary>The ship's parts' extent in its own frame (hull voxels and placed parts), a little padded.</summary>
        private static Bounds ShipBounds(TrackedShipServer s)
        {
            bool any = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.one);
            foreach (KeyValuePair<HullPartPosition, Ships.Interface.Model.Parts.HullPartNetworkedState> kv in s.GetHullParts())
            {
                Vector3 v = kv.Key.VoxelCoord;
                if (!any) { b = new Bounds(v, Vector3.one); any = true; } else b.Encapsulate(new Bounds(v, Vector3.one));
            }
            foreach (StatefulPart part in s.StatefulParts)
            {
                IPartPlacement pl;
                if (!s.TryGetPartPlacement(part.Id, out pl)) continue;
                if (!any) { b = new Bounds(pl.LocalPosition, Vector3.one); any = true; } else b.Encapsulate(new Bounds(pl.LocalPosition, Vector3.one));
            }
            b.Expand(1f);
            return b;
        }

        /// <summary>Would a thing standing at 'at' (planet-centred), this tall, be inside or next to the obstacle?</summary>
        private static bool Hits(Obstacle o, Vector3Double at, float height, float clear)
        {
            Vector3 up = (Vector3)(at / at.Magnitude);
            for (float h = 0f; h <= height; h += Mathf.Max(1f, height / 6f))
            {
                Vector3 pt = (Vector3)(at - o.Pos) + up * h;
                if (o.IsShip)
                {
                    Vector3 local = Quaternion.Inverse(o.Rot) * pt;
                    Bounds b = o.Local;
                    b.Expand(clear * 2f);
                    if (b.Contains(local)) return true;
                }
                else if (pt.magnitude < clear + 1.5f) return true;
            }
            return false;
        }

#if DSTEST
        // ---- for the tests ---------------------------------------------------------------------

        /// <summary>
        /// Cuts down a tree (near: 8-40 m from the first player; here: within 4 m of them; ship: under the
        /// test ship) and makes it due at once. A line for the console: "testtree tree <id> ...".
        /// </summary>
        public static string TestTree(string where, TrackedShipServer testShip)
        {
            TrackedPlayerServer me = Server.ActivePlayers().FirstOrDefault();
            if (me == null) return "testtree: nobody is on";
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            IWorldTranspose transpose = Core.Services.ServiceLocator.GetService<IWorldTranspose>();
            PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            PlanetSurfaceSpace sp = spaces.GetSpace(me.SpaceId) as PlanetSurfaceSpace;
            if (sp == null) return "testtree: the player isn't on a planet";
            ulong planet = sp.PlanetId;
            IPlanetColliderGenerator gen = planets.GetPlanetColliderGenerator(planet);
            PlanetSurfaceSpace centre = new PlanetSurfaceSpace { Center = Vector3Double.Zero, PlanetId = planet };
            Vector3Double mePos = transpose.Position(sp, centre, (Vector3Double)me.Position);
            List<Obstacle> obstacles = Obstacles(planet, centre, spaces, transpose);
            Obstacle ship = testShip != null ? obstacles.FirstOrDefault(o => o.IsShip && (o.Pos - transpose.Position(spaces.GetSpace(testShip.PhysicsState.SpaceId), centre, (Vector3Double)testShip.PhysicsState.Position)).Magnitude < 1.0) : null;
            if (where == "ship" && ship == null) return "testtree: no test ship (testship park 6 first)";
            List<IPlanetObjectSpawnData> near = new List<IPlanetObjectSpawnData>();
            if (where == "ship") foreach (Vector3Double q in ship.Points) near.AddRange(gen.GetCollidersToSpawnAtPosition(new PlanetSurfaceSpace { Center = q, PlanetId = planet }, Vector3.zero));
            else near.AddRange(gen.GetCollidersToSpawnAtPosition(sp, me.Position));
            float clear = Server.Cfg.RespawnClearance.Value;
            foreach (DensePlanetObjectSpawnData d in near.OfType<DensePlanetObjectSpawnData>().OrderBy(x => (x.Transform.SurfacePosition - mePos).Magnitude))
            {
                Vector3Double at = d.Transform.SurfacePosition;
                double dist = (at - mePos).Magnitude;
                bool ok;
                if (where == "ship") ok = Hits(ship, at, 12f, clear);
                else if (where == "front")
                {
                    // in front of the player's camera, 10-35 m away, so a screenshot catches it growing
                    Vector3 fwd = Ground.CameraRotation(me) * Vector3.forward;
                    Vector3 to = (Vector3)(at - mePos);
                    ok = dist > 10.0 && dist < 35.0 && Vector3.Angle(Vector3.ProjectOnPlane(fwd, (Vector3)mePos.Normalized), Vector3.ProjectOnPlane(to, (Vector3)mePos.Normalized)) < 25f
                         && !obstacles.Any(o => o.IsShip && Hits(o, at, 12f, clear + 4f));
                }
                else ok = (where == "here" || dist > 3.0) && !obstacles.Any(o => o.IsShip && Hits(o, at, 12f, clear + 4f));
                if (!ok) continue;
                if (where == "here") Ground.MoveTo(me, at.x, at.y, at.z);      // the player stands where it will grow
                ChunkObjectId id = d.ObjectId.PlacedObjectId;
                TrackedPlanetServer tp = planets.GetPlanetServer(planet);
                tp.MineDenseObject(id);
                string k = Key(planet, Tree, IdOf(id));
                Rec r;
                if (_recs.TryGetValue(k, out r)) r.At = DateTime.UtcNow.AddMinutes(-(Server.Cfg.RespawnTreesMinutes.Value + 1));
                Vector3 rel = (Vector3)(at - mePos);
                return "testtree tree " + IdOf(id) + " planet " + planet + " dist " + dist.ToString("0.0", CultureInfo.InvariantCulture) + " m" + (r != null ? " (due)" : " (NOT NOTED)");
            }
            return "testtree: no tree found (" + where + ", " + near.OfType<DensePlanetObjectSpawnData>().Count() + " looked at)";
        }
#endif

        // ---- for the console -------------------------------------------------------------------

        public static IEnumerable<string> Report()
        {
            DateTime now = DateTime.UtcNow;
            foreach (IGrouping<string, Rec> g in _recs.Values.GroupBy(r => r.Kind).OrderBy(g => g.Key))
            {
                int m = MinutesFor(g.Key);
                int dueNow = m > 0 ? g.Count(r => (now - r.At).TotalMinutes >= m) : 0;
                yield return "  " + g.Key + ": " + g.Count() + " gone" + (m > 0 ? ", grow back after " + m + " min, " + dueNow + " due now" : ", never grow back (setting is 0)");
            }
            yield return "  grown back since the server started: " + Respawned + (Blocked > 0 ? "; waiting for something to move: " + Blocked : "");
        }
    }
}
