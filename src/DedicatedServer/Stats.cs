// What happens on the server, for the `stats` command and the Server Manager's pages:
//   * exploration: every 2 s each player's spot is marked on a grid - per planet, 6 x 24 x 24 cells
//     on its sphere; for space, 32 x 32 x 32 cells over the box around all the planets (and their
//     spheres of influence). Kept next to the world (exploration.json), so it belongs to that world.
//   * ships built (the game's own ShipsServerTracker.OnShipCreated), chat messages, bans, and -
//     with TrackBandwidth on - bytes in and out, counted per day in server-data.json.
//   * ships.json: every ship, for the Ships page (id, parts, where, speed), rewritten every 15 s.
//   * backups: the game keeps BackupsToKeep of them (its own limit is hard-coded to 10), and
//     DeleteBackupsAfterDays removes older ones after each save.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Values;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Planets.Interface.Model;
using Planets.Services;
using Players;
using Ships;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    public class ExplorationFile
    {
        public Dictionary<string, List<int>> Planets = new Dictionary<string, List<int>>();   // planet id -> cells
        public Dictionary<string, string> PlanetNames = new Dictionary<string, string>();
        public List<int> Space = new List<int>();
        public long[] Min, Max;                                                               // system-cell box (km)
    }

    internal static class Stats
    {
        public const int FaceCells = 24, PlanetCells = 6 * FaceCells * FaceCells, SpaceCells = 32 * 32 * 32;

        private static ExplorationFile _ex;
        private static readonly Dictionary<string, HashSet<int>> _planetSets = new Dictionary<string, HashSet<int>>();
        private static HashSet<int> _spaceSet = new HashSet<int>();
        private static float _nextSample, _nextShips, _nextBytes;
        private static bool _exDirty;
        private static ShipsServerTracker _hookedShips;
        public static long BytesIn, BytesOut, _reportedIn, _reportedOut;
        public static double InRate, OutRate;     // bytes/s over the last 5 s
        private static float _nextLive;
        private static bool _liveStarted;
        private static long _liveIn, _liveOut;
        private static readonly Queue<double[]> _live = new Queue<double[]>();     // [unix seconds, in/s, out/s]
        private static long _lastIn, _lastOut;
        private static float _lastRateAt;

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(Saving.Settings.SaveSettings), "MaxBackupCount"),
                    prefix: new HarmonyMethod(typeof(Stats).GetMethod("BackupCountPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("backups: " + e.Message); }
            try
            {
                Type tug = typeof(FishNet.Transporting.Tugboat.Tugboat);
                h.Patch(AccessTools.Method(tug, "SendToClient"), postfix: new HarmonyMethod(typeof(Stats).GetMethod("SentPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(tug, "HandleServerReceivedDataArgs"), postfix: new HarmonyMethod(typeof(Stats).GetMethod("ReceivedPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("bandwidth counting unavailable: " + e.Message); }
        }

        private static bool BackupCountPrefix(ref int __result) { __result = Mathf.Max(0, Server.Cfg.BackupsToKeep.Value); return false; }

        private static void SentPostfix(ArraySegment<byte> segment)
        {
            if (Server.Cfg.TrackBandwidth.Value) BytesOut += segment.Count;
        }

        private static void ReceivedPostfix(FishNet.Transporting.ServerReceivedDataArgs receivedDataArgs)
        {
            if (Server.Cfg.TrackBandwidth.Value) BytesIn += receivedDataArgs.Data.Count;
        }

        // ---- every frame, once the world is up ---------------------------------------------------

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            HookShips();
            if (now >= _nextSample) { _nextSample = now + 2f; Sample(); }
            if (now >= _nextShips) { _nextShips = now + 15f; WriteShips(); if (_exDirty) SaveExploration(); }
            if (now >= _nextLive)
            {
                _nextLive = now + 1f;
                if (Server.Cfg.TrackBandwidth.Value) WriteLive();
                else if (_live.Count > 0) { _live.Clear(); WriteLive(); }
            }
            if (now >= _nextBytes)
            {
                _nextBytes = now + 5f;
                float dt = _lastRateAt > 0 ? now - _lastRateAt : 5f;
                InRate = (BytesIn - _lastIn) / Math.Max(0.1, dt);
                OutRate = (BytesOut - _lastOut) / Math.Max(0.1, dt);
                _lastIn = BytesIn; _lastOut = BytesOut; _lastRateAt = now;
                if (BytesIn != _reportedIn || BytesOut != _reportedOut)
                {
                    Server.Data.CountBytes(BytesIn - _reportedIn, BytesOut - _reportedOut);
                    _reportedIn = BytesIn; _reportedOut = BytesOut;
                }
            }
        }

        // The Manager's live network graph: bytes in and out each second, for the last two minutes.
        private static void WriteLive()
        {
            try
            {
                if (Server.Cfg.TrackBandwidth.Value && !_liveStarted) _liveStarted = true;       // the first second starts the count
                else if (Server.Cfg.TrackBandwidth.Value)
                {
                    _live.Enqueue(new[] { Math.Round((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds, 1), (double)(BytesIn - _liveIn), (double)(BytesOut - _liveOut) });
                    while (_live.Count > 120) _live.Dequeue();
                }
                _liveIn = BytesIn; _liveOut = BytesOut;
                string path = Path.Combine(Server.DataDir, "bandwidth-live.json");
                JArray a = new JArray();
                foreach (double[] s in _live) a.Add(new JArray(s[0], s[1], s[2]));
                File.WriteAllText(path + ".tmp", new JObject { ["on"] = Server.Cfg.TrackBandwidth.Value, ["samples"] = a }.ToString(Formatting.None));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Log.Once("bw-live", "bandwidth-live.json: " + e.Message); }
        }

        /// <summary>The ship list is written again on the next frame (after a delete or a restore).</summary>
        public static void ShipsChanged() { _nextShips = 0f; }

        private static void HookShips()
        {
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (t == null || ReferenceEquals(t, _hookedShips)) return;
            _hookedShips = t;
            t.OnShipCreated = (Action<TrackedShipServer>)Delegate.Combine(t.OnShipCreated, new Action<TrackedShipServer>(s => { if (Server.Ready) Server.Data.CountShip(); }));
        }

        // ---- exploration ---------------------------------------------------------------------------

        private static void LoadExploration()
        {
            if (_ex != null) return;
            _ex = new ExplorationFile();
            string path = ExplorationPath();
            try { if (File.Exists(path)) _ex = JsonConvert.DeserializeObject<ExplorationFile>(File.ReadAllText(path)) ?? new ExplorationFile(); }
            catch (Exception e) { Log.Warn("could not read exploration.json: " + e.Message); }
            foreach (KeyValuePair<string, List<int>> kv in _ex.Planets) _planetSets[kv.Key] = new HashSet<int>(kv.Value);
            _spaceSet = new HashSet<int>(_ex.Space);
        }

        private static string ExplorationPath() { return Path.Combine(Path.GetDirectoryName(Server.WorldPath) ?? Server.DataDir, "exploration.json"); }

        public static void SaveExploration()
        {
            if (_ex == null) return;
            try
            {
                _ex.Planets = _planetSets.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToList());
                _ex.Space = _spaceSet.OrderBy(x => x).ToList();
                File.WriteAllText(ExplorationPath(), JsonConvert.SerializeObject(_ex));
                _exDirty = false;
            }
            catch (Exception e) { Log.Once("ex-save", "could not write exploration.json: " + e.Message); }
        }

        private static void Sample()
        {
            if (!Server.Ready) return;
            IReadOnlyList<TrackedPlayerServer> players = Server.ActivePlayers();
            if (players.Count == 0) return;
            LoadExploration();
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            IWorldTransposeServer tr = Core.Services.ServiceLocator.GetService<IWorldTransposeServer>();
            PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            if (spaces == null || tr == null || planets == null) return;
            EnsureBox(planets);
            foreach (TrackedPlayerServer p in players)
            {
                try
                {
                    ISpace space;
                    if (!spaces.TryGetSpace(p.SpaceId, out space)) continue;
                    Vector3 pos = p.PhysicsState.Position;
                    PlanetSurfaceSpace surface = space as PlanetSurfaceSpace;
                    if (surface != null)
                    {
                        Vector3Double c = surface.Center;
                        Vector3 dir = new Vector3((float)(c.x + pos.x), (float)(c.y + pos.y), (float)(c.z + pos.z));
                        string key = surface.PlanetId.ToString();
                        HashSet<int> set;
                        if (!_planetSets.TryGetValue(key, out set)) { set = new HashSet<int>(); _planetSets[key] = set; }
                        if (set.Add(SphereCell(dir))) _exDirty = true;
                        if (!_ex.PlanetNames.ContainsKey(key))
                        {
                            IPlanetProperties props = planets.GetPlanetProps(surface.PlanetId);
                            _ex.PlanetNames[key] = props != null ? props.Name : key;
                            _exDirty = true;
                        }
                    }
                    PreciseWorldPosition w = tr.ToPreciseWorldPos(space) + new Vector3Double(pos.x, pos.y, pos.z);
                    int cell = SpaceCell(w.worldCell.systemCell);
                    if (cell >= 0 && _spaceSet.Add(cell)) _exDirty = true;
                }
                catch (Exception e) { Log.Once("explore", "exploration sample: " + e.Message); }
            }
        }

        /// <summary>A cube-sphere cell: which face the direction points through, then a grid on that face.</summary>
        public static int SphereCell(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            int face; float u, v, m;
            if (ax >= ay && ax >= az) { face = d.x > 0 ? 0 : 1; m = ax; u = d.y; v = d.z; }
            else if (ay >= az) { face = d.y > 0 ? 2 : 3; m = ay; u = d.x; v = d.z; }
            else { face = d.z > 0 ? 4 : 5; m = az; u = d.x; v = d.y; }
            if (m <= 0) return 0;
            int iu = Mathf.Clamp((int)((u / m * 0.5f + 0.5f) * FaceCells), 0, FaceCells - 1);
            int iv = Mathf.Clamp((int)((v / m * 0.5f + 0.5f) * FaceCells), 0, FaceCells - 1);
            return face * FaceCells * FaceCells + iu * FaceCells + iv;
        }

        private static void EnsureBox(PlanetsServerTracker planets)
        {
            if (_ex.Min != null && _ex.Max != null) return;
            long[] min = null, max = null;
            foreach (IPlanetProperties pl in planets.Planets)
            {
                Vector3Int c = pl.WorldCell.systemCell;
                long r = (long)Math.Ceiling(Math.Max(pl.SphereOfInfluence, pl.Radius * 3.0) / 1000.0);
                long[] lo = { c.x - r, c.y - r, c.z - r }, hi = { c.x + r, c.y + r, c.z + r };
                if (min == null) { min = lo; max = hi; continue; }
                for (int i = 0; i < 3; i++) { min[i] = Math.Min(min[i], lo[i]); max[i] = Math.Max(max[i], hi[i]); }
            }
            if (min == null) return;
            _ex.Min = min; _ex.Max = max;
            _exDirty = true;
        }

        private static int SpaceCell(Vector3Int c)
        {
            if (_ex.Min == null) return -1;
            long[] v = { c.x, c.y, c.z };
            int[] i3 = new int[3];
            for (int i = 0; i < 3; i++)
            {
                double span = Math.Max(1, _ex.Max[i] - _ex.Min[i]);
                double f = (v[i] - _ex.Min[i]) / span;
                if (f < 0 || f > 1) return -1;       // outside the planets' box: not counted
                i3[i] = Mathf.Clamp((int)(f * 32), 0, 31);
            }
            return (i3[0] * 32 + i3[1]) * 32 + i3[2];
        }

        /// <summary>Planet name -> % of its surface grid visited; and space %.</summary>
        public static List<KeyValuePair<string, double>> PlanetPercents()
        {
            LoadExploration();
            PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            List<KeyValuePair<string, double>> r = new List<KeyValuePair<string, double>>();
            if (planets != null)
                foreach (IPlanetProperties pl in planets.Planets)
                {
                    HashSet<int> set;
                    int n = _planetSets.TryGetValue(pl.Id.ToString(), out set) ? set.Count : 0;
                    r.Add(new KeyValuePair<string, double>(pl.Name, 100.0 * n / PlanetCells));
                }
            return r;
        }

        public static double SpacePercent { get { LoadExploration(); return 100.0 * _spaceSet.Count / SpaceCells; } }

        public static double WorldPercent
        {
            get
            {
                List<KeyValuePair<string, double>> p = PlanetPercents();
                return p.Count == 0 ? 0 : p.Average(x => x.Value);
            }
        }

        // ---- ships.json ----------------------------------------------------------------------------

        public static int ShipCount;

        private static void WriteShips()
        {
            if (!Server.Ready) return;
            try
            {
                ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
                PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
                if (t == null) return;
                JArray arr = new JArray();
                foreach (TrackedShipServer s in t.Ships)
                {
                    JObject o = new JObject();
                    o["id"] = s.Id;
                    try { o["parts"] = s.State.PartCount; } catch { o["parts"] = 0; }
                    try
                    {
                        IPlanetProperties near = planets != null ? planets.GetNearestPlanet(s.SpaceId) : null;
                        o["near"] = near != null ? near.Name : "";
                    }
                    catch { o["near"] = ""; }
                    try
                    {
                        object ph = t.GetShipPhysicsState(s.Id);
                        object vel = ph.GetType().GetField("Velocity") != null ? ph.GetType().GetField("Velocity").GetValue(ph)
                                   : ph.GetType().GetProperty("Velocity") != null ? ph.GetType().GetProperty("Velocity").GetValue(ph, null) : null;
                        o["speed"] = vel is Vector3 ? Math.Round(((Vector3)vel).magnitude, 1) : 0.0;
                    }
                    catch { o["speed"] = 0.0; }
                    o["loaded"] = s.IsRigidbodyLoaded;
                    uint root = ShipAdmin.RootOf(s.Id);
                    o["root"] = root;
                    string own = ShipAdmin.BeaconName(s);
                    o["name"] = own.Length > 0 ? own : root != s.Id ? ShipAdmin.GroupName(root) : "";
                    o["beacon"] = own.Length > 0;
                    o["where"] = ShipAdmin.Where(s);
                    o["protected"] = Protect.IsProtected(s.Id);
                    arr.Add(o);
                }
                ShipCount = arr.Count;
                string path = Path.Combine(Server.DataDir, "ships.json");
                File.WriteAllText(path + ".tmp", new JObject { ["writtenUtc"] = DateTime.UtcNow.ToString("o"), ["ships"] = arr }.ToString(Formatting.None));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Log.Once("ships", "ships.json: " + e.Message); }
        }

        // ---- backups older than DeleteBackupsAfterDays -----------------------------------------------

        public static void PruneBackups()
        {
            int days = Server.Cfg.DeleteBackupsAfterDays.Value;
            if (days <= 0 || Server.WorldPath == null) return;
            try
            {
                DateTime cut = DateTime.Now.AddDays(-days);
                string dir = Path.GetDirectoryName(Server.WorldPath);
                int n = 0;
                foreach (string f in Directory.GetFiles(dir, "backup*.*"))
                    if (File.GetLastWriteTime(f) < cut) { File.Delete(f); n++; }
                if (n > 0) Log.Msg("deleted " + n + " backup file(s) older than " + days + " day(s)");
            }
            catch (Exception e) { Log.Warn("backup cleanup: " + e.Message); }
        }
    }
}
