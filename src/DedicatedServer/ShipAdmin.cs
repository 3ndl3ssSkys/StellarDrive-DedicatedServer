// Ships for admins: names (from a ship's beacon), where they are, taking players to them, and
// deleting them into a bin they can be restored from.
//
// A deleted ship is written to <data>/deleted-ships/<id>.json before anything is removed: the whole
// assembly (every docked or rotor-attached section, the cables between them) in the game's own
// ship export format, where it stood, and what it was made of. Restoring loads it back the way the
// game's own ship import does, at the spot it stood in (or next to a player). The bin keeps
// records for ShipBinDays.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Core.Values;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Players;
using Players.Services;
using Saving.Services.Converters;
using Saving.Services.Model.State;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Services;
using WorldTracking.Spaces;

namespace DedicatedServer
{
    internal static class ShipAdmin
    {
        private const ushort BeaconPartId = 38;
        private const int PartsPerFrame = 150;

        public static int Deleted, Restored;
        private static readonly HashSet<uint> _deleting = new HashSet<uint>();

        public static string BinDir { get { return Path.Combine(Server.DataDir, "deleted-ships"); } }

        private static ShipsServerTracker Ships() { return Core.Services.ServiceLocator.GetService<ShipsServerTracker>(); }
        private static SpacesServer Spaces() { return Core.Services.ServiceLocator.GetService<SpacesServer>(); }

        // ---- names and places ----------------------------------------------------------------------

        /// <summary>The name on a ship's first beacon ("" if it has none). Chess tables and graves keep data in beacon state too, so only real beacons count.</summary>
        public static string BeaconName(TrackedShip ship)
        {
            if (ship == null) return "";
            string best = ""; ushort bestPart = ushort.MaxValue;
            try
            {
                foreach (StatefulPart p in ship.GetAllStatefulParts<BeaconState>())
                {
                    if (p == null || p.Settings == null || p.Settings.id != BeaconPartId || p.Id >= bestPart) continue;
                    BeaconState s = p.State as BeaconState;
                    if (s == null || string.IsNullOrEmpty(s.Name) || s.Name.Trim().Length == 0) continue;
                    bestPart = p.Id; best = s.Name.Trim();
                }
            }
            catch { }
            return best;
        }

        /// <summary>The assembly's name: its root's beacon, else the first beacon anywhere in it.</summary>
        public static string GroupName(uint rootId)
        {
            ShipsServerTracker t = Ships();
            TrackedShipServer root;
            if (t == null || !t.TryGetTrackedShip(rootId, out root)) return "";
            string n = BeaconName(root);
            if (n.Length > 0) return n;
            foreach (RootRelativeShipData d in t.GetRootConnectedShips(rootId))
            {
                n = BeaconName(d.Ship as TrackedShip);
                if (n.Length > 0) return n;
            }
            return "";
        }

        public static uint RootOf(uint shipId)
        {
            ShipsServerTracker t = Ships();
            TrackedShipServer root;
            return t != null && t.TryGetTrackedRootShip(shipId, out root) ? root.Id : shipId;
        }

        /// <summary>Where a ship is: planet, its point in the planet's own coordinates (metres from the centre) and its height above the ground.</summary>
        public static JObject Where(TrackedShipServer s)
        {
            JObject o = new JObject();
            try
            {
                Vector3Double pp; ulong pid; float elev;
                float h = Ground.Height(s.SpaceId, s.Position, out pp, out pid, out elev);
                if (!float.IsNaN(h))
                {
                    o["planetId"] = pid.ToString();
                    o["planet"] = PlanetName(pid);
                    o["x"] = Math.Round(pp.x, 1); o["y"] = Math.Round(pp.y, 1); o["z"] = Math.Round(pp.z, 1);
                    o["height"] = Math.Round(h, 1);
                }
                else
                {
                    ISpace sp; SpacesServer ss = Spaces();
                    o["space"] = ss != null && ss.TryGetSpace(s.SpaceId, out sp) && sp != null ? sp.GetType().Name.Replace("Space", "").ToLowerInvariant() : "space";
                    o["x"] = Math.Round(s.Position.x, 1); o["y"] = Math.Round(s.Position.y, 1); o["z"] = Math.Round(s.Position.z, 1);
                }
            }
            catch { }
            return o;
        }

        private static string PlanetName(ulong id)
        {
            try
            {
                foreach (Planets.Planet p in Resources.FindObjectsOfTypeAll<Planets.Planet>())
                    if (p != null && p.Id == id) return p.Name;
            }
            catch { }
            return "planet " + id;
        }

        public static string WhereText(TrackedShipServer s)
        {
            JObject w = Where(s);
            string at = Num(w, "x") + ", " + Num(w, "y") + ", " + Num(w, "z");
            return w["planet"] != null ? (string)w["planet"] + " at " + at + " (" + Num(w, "height") + " m above the ground)" : "in " + (string)w["space"] + " at " + at;
        }

        private static string Num(JObject o, string k) { return o[k] != null ? ((double)o[k]).ToString("0.0", CultureInfo.InvariantCulture) : "?"; }

        // ---- taking players there ------------------------------------------------------------------

        public static string TakeTo(TrackedPlayerServer who, uint shipId)
        {
            ShipsServerTracker t = Ships();
            TrackedShipServer ship;
            if (t == null || !t.TryGetTrackedShip(shipId, out ship)) return "There's no ship #" + shipId + ".";
            return Ground.OntoShip(who, ship);
        }

        // ---- what a ship is made of ---------------------------------------------------------------

        /// <summary>Item id -> count: every part's build cost (what taking it apart gives back), and what is stored in it.</summary>
        public static Dictionary<uint, int> Materials(IEnumerable<TrackedShipServer> sections, out Dictionary<uint, int> stored)
        {
            Dictionary<uint, int> m = new Dictionary<uint, int>();
            stored = new Dictionary<uint, int>();
            foreach (TrackedShipServer s in sections)
            {
                ShipStateSnapshot snap = (ShipStateSnapshot)s.GetStateSnapshot();
                if (snap.HullParts != null)
                    foreach (HullPart h in snap.HullParts)
                        try { foreach (var c in h.Settings.BuildingCost) Add(m, c.ItemId, c.Quantity); } catch { }
                if (snap.StatefulParts != null)
                    foreach (StatefulPart p in snap.StatefulParts)
                    {
                        try { foreach (var c in p.Settings.BuildingCost) Add(m, c.ItemId, c.Quantity); } catch { }
                        try
                        {
                            IStoragePart st = p.State as IStoragePart;
                            if (st != null)
                                for (byte b = 0; b < st.SlotCount; b++)
                                {
                                    var slot = st.GetSlot(b);
                                    if (slot.HasItem) Add(stored, slot.ItemId, slot.Quantity);
                                }
                        }
                        catch { }
                    }
            }
            return m;
        }

        private static void Add(Dictionary<uint, int> d, uint id, int n) { int v; d.TryGetValue(id, out v); d[id] = v + n; }

        private static JObject Named(Dictionary<uint, int> d)
        {
            Dictionary<uint, string> names = AdminItems.All().ToDictionary(i => i.Id, i => i.Name);
            JObject o = new JObject();
            foreach (KeyValuePair<uint, int> kv in d.OrderByDescending(x => x.Value))
            {
                string n; if (!names.TryGetValue(kv.Key, out n)) n = "item " + kv.Key;
                o[n] = (o[n] != null ? (int)o[n] : 0) + kv.Value;
            }
            return o;
        }

        public static string ListText(JObject named, int max = 8)
        {
            List<string> l = named.Properties().Select(p => (int)p.Value + " " + p.Name).ToList();
            return l.Count == 0 ? "nothing" : string.Join(", ", l.Take(max).ToArray()) + (l.Count > max ? " and " + (l.Count - max) + " more" : "");
        }

        // ---- deleting -------------------------------------------------------------------------------

        /// <summary>
        /// Players on these ship sections: inside them (in a ship's own space), or standing on or right by their parts outside
        /// (an open deck has no inside: someone on it is in the planet's space, a step above the ship's colliders).
        /// </summary>
        public static List<string> PlayersOn(List<TrackedShipServer> sections)
        {
            HashSet<uint> inside = new HashSet<uint>(sections.Select(s => s.RelativeSpaceId));
            List<string> on = new List<string>();
            WorldTracking.Interface.Services.IWorldTransposeServer tr = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.IWorldTransposeServer>();
            foreach (TrackedPlayerServer p in Server.ActivePlayers())
            {
                if (inside.Contains(p.SpaceId)) { on.Add(p.Name); continue; }
                foreach (TrackedShipServer s in sections)
                {
                    if (s.RigidbodyObject == null) continue;
                    try
                    {
                        Vector3 at = p.SpaceId == s.SpaceId ? p.Position : tr.Position(p.SpaceId, s.SpaceId, p.Position);
                        Collider[] cs = s.RigidbodyObject.GetComponentsInChildren<Collider>();
                        if (cs.Length == 0) continue;
                        Bounds b = cs[0].bounds;
                        foreach (Collider c in cs) b.Encapsulate(c.bounds);
                        b.Expand(4f);                                   // standing on it (a body is ~1 m above its feet) or right beside it
                        if (b.Contains(at)) { on.Add(p.Name); break; }
                    }
                    catch { }
                }
            }
            return on;
        }

        /// <summary>Deletes a ship and everything docked to it, keeping it in the bin. Returns the reply.</summary>
        public static string Delete(uint shipId, string by)
        {
            ShipsServerTracker t = Ships();
            SpacesServer spaces = Spaces();
            if (t == null || spaces == null) return "The world isn't loaded.";
            uint rootId = RootOf(shipId);
            TrackedShipServer root;
            if (!t.TryGetTrackedShip(rootId, out root)) return "There's no ship #" + shipId + ".";
            if (_deleting.Contains(rootId)) return "Ship #" + rootId + " is being deleted already.";
            List<TrackedShipServer> sections = t.GetRootConnectedShips(rootId).Select(d => d.Ship as TrackedShipServer).Where(x => x != null).ToList();
            if (sections.Count == 0) sections.Add(root);
            foreach (TrackedShipServer s in sections)
                if (Protect.IsProtected(s.Id)) return "Ship #" + s.Id + " is protected: unprotect it first (unprotect " + s.Id + ").";
            List<string> aboard = PlayersOn(sections);
            if (aboard.Count > 0) return "Not deleted: " + string.Join(", ", aboard.ToArray()) + (aboard.Count == 1 ? " is" : " are") + " aboard. Everyone has to be off it first.";

            JObject rec;
            try { rec = Record(t, spaces, root, sections, by); }
            catch (Exception e) { Log.Error("ship delete: could not copy #" + rootId + " first: " + e); return "Not deleted: it could not be copied first (" + e.Message + ")."; }
            Directory.CreateDirectory(BinDir);
            string file = Path.Combine(BinDir, (string)rec["id"] + ".json");
            File.WriteAllText(file, rec.ToString(Formatting.None));

            _deleting.Add(rootId);
            MelonCoroutines.Start(Remove(sections.Select(s => s.Id).ToList(), rootId));
            Deleted++;
            string name = (string)rec["name"];
            string reply = "Deleted " + (name.Length > 0 ? "\"" + name + "\" " : "") + "(#" + rootId + ", " + (int)rec["parts"] + " parts" + (sections.Count > 1 ? ", " + sections.Count + " ships" : "") + "). "
                + "Recovered: " + ListText((JObject)rec["materials"]) + (((JObject)rec["stored"]).Count > 0 ? "; it held " + ListText((JObject)rec["stored"]) : "") + ". "
                + "Restore it with: ship restore " + (string)rec["id"];
            Log.Msg("ship: " + by + " deleted #" + rootId + (name.Length > 0 ? " \"" + name + "\"" : "") + " (" + (int)rec["parts"] + " parts) into the bin as " + (string)rec["id"]);
            return reply;
        }

        private static JObject Record(ShipsServerTracker t, SpacesServer spaces, TrackedShipServer root, List<TrackedShipServer> sections, string by)
        {
            // the game's own export, for this root (Managers.Client.Utils.ShipImportExportUtils.Export)
            Dictionary<uint, ShipStateSnapshot> snaps = new Dictionary<uint, ShipStateSnapshot>();
            foreach (TrackedShipServer s in sections)
            {
                ShipStateSnapshot snap = (ShipStateSnapshot)s.GetStateSnapshot();
                if (snap.Id == root.Id) snap.SpaceId = 0u;
                else
                {
                    ISpace space;
                    if (!spaces.TryGetSpace(snap.SpaceId, out space) || !(space is ShipRelativeSpace)) throw new Exception("section #" + snap.Id + " isn't inside another ship");
                    snap.SpaceId = ((ShipRelativeSpace)space).ShipId;
                }
                snaps[snap.Id] = snap;
            }
            Managers.Client.Utils.ShipImportExportUtils.ShipImportExportData data = new Managers.Client.Utils.ShipImportExportUtils.ShipImportExportData();
            data.rootShipId = root.Id;
            data.ships = new ShipsConverter().ConvertToSerializable(new SavedShipStates(snaps.Values.Cast<IShipState>().ToArray()));
            CablesServerTracker cables = Core.Services.ServiceLocator.GetService<CablesServerTracker>();
            Dictionary<uint, CableData> keep = new Dictionary<uint, CableData>();
            if (cables != null)
                foreach (uint id in snaps.Keys)
                    foreach (CableData c in cables.GetShipCables(id))
                    {
                        CablePlugSocket a = c.State.PlugA, b = c.State.PlugB;
                        if (!a.IsHeldByPlayer && !b.IsHeldByPlayer && snaps.ContainsKey(a.EntityId) && snaps.ContainsKey(b.EntityId)) keep[c.Id] = c;
                    }
            data.cables = CablesConverter.ConvertToSerializable(keep.Values.ToArray());
            int cleaned = Signals.CleanFields(data);       // a ship with a NaN rotor or position comes back sound
            if (cleaned > 0) Log.Msg("ship: " + cleaned + " NaN or infinite number(s) in ship #" + root.Id + " set to 0 in its bin copy");

            Dictionary<uint, int> stored;
            Dictionary<uint, int> mats = Materials(sections, out stored);
            int parts = snaps.Values.Sum(s => (s.HullParts != null ? s.HullParts.Length : 0) + (s.StatefulParts != null ? s.StatefulParts.Length : 0));
            JObject where = Where(root);
            Quaternion r = root.PhysicsState.Rotation;
            return new JObject
            {
                ["id"] = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + root.Id,
                ["shipId"] = root.Id,
                ["name"] = GroupName(root.Id),
                ["parts"] = parts,
                ["ships"] = sections.Count,
                ["deletedUtc"] = DateTime.UtcNow.ToString("o"),
                ["by"] = by,
                ["spaceId"] = root.SpaceId,
                ["px"] = root.Position.x, ["py"] = root.Position.y, ["pz"] = root.Position.z,
                ["rot"] = new JArray(r.x, r.y, r.z, r.w),
                ["where"] = where,
                ["whereText"] = WhereText(root),
                ["materials"] = Named(mats),
                ["stored"] = Named(stored),
                ["data"] = JsonUtility.ToJson(data),
            };
        }

        // The game's own way of taking a ship apart without refunds or debris (ShipImportExportUtils.Destroy), spread over frames.
        private static IEnumerator Remove(List<uint> ids, uint rootId)
        {
            ShipsServerTracker t = Ships();
            try
            {
                int budget = 0;
                foreach (uint id in ids)
                {
                    TrackedShipServer s;
                    if (t == null || !t.TryGetTrackedShip(id, out s)) continue;
                    foreach (ushort pid in s.StatefulParts.Where(p => p != null).Select(p => p.Id).ToList())
                    {
                        if (!t.TryGetTrackedShip(id, out s)) break;
                        s.RemoveStatefulPart(pid);
                        if (++budget >= PartsPerFrame) { budget = 0; yield return null; }
                    }
                    if (!t.TryGetTrackedShip(id, out s)) continue;
                    foreach (HullPartPosition hp in s.GetHullParts().Keys.ToList())
                    {
                        if (!t.TryGetTrackedShip(id, out s)) break;
                        s.RemoveHullPart(hp);
                        if (++budget >= PartsPerFrame) { budget = 0; yield return null; }
                    }
                }
            }
            finally { _deleting.Remove(rootId); }
            Stats.ShipsChanged();
        }

        // ---- the bin --------------------------------------------------------------------------------

        public static List<JObject> Bin()
        {
            List<JObject> l = new List<JObject>();
            if (!Directory.Exists(BinDir)) return l;
            foreach (string f in Directory.GetFiles(BinDir, "*.json"))
                try { JObject o = JObject.Parse(File.ReadAllText(f)); o.Remove("data"); l.Add(o); } catch { }
            return l.OrderByDescending(o => (string)o["id"]).ToList();
        }

        /// <summary>Drops bin records older than ShipBinDays (0 = keep them all).</summary>
        public static void Prune()
        {
            int days = Server.Cfg.ShipBinDays.Value;
            if (days <= 0 || !Directory.Exists(BinDir)) return;
            foreach (string f in Directory.GetFiles(BinDir, "*.json"))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-days)) File.Delete(f); } catch { }
        }

        /// <summary>Puts a deleted ship back where it stood, or next to a player.</summary>
        public static string Restore(string binId, TrackedPlayerServer near, string by)
        {
            string file = Directory.Exists(BinDir) ? Directory.GetFiles(BinDir, "*.json").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == binId
                || Path.GetFileNameWithoutExtension(f).EndsWith("-" + binId)) : null;
            if (file == null) return "There's nothing in the bin called " + binId + " (ship bin lists them).";
            ShipsServerTracker ships = Ships();
            SpacesServer spaces = Spaces();
            WorldObjectsIdsServerTracker ids = Core.Services.ServiceLocator.GetService<WorldObjectsIdsServerTracker>();
            CablesServerTracker cables = Core.Services.ServiceLocator.GetService<CablesServerTracker>();
            if (ships == null || spaces == null || ids == null || cables == null) return "The world isn't loaded.";
            JObject rec = JObject.Parse(File.ReadAllText(file));

            // where it goes
            uint spaceId; Vector3 pos; Quaternion rot;
            JArray r = (JArray)rec["rot"];
            rot = new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
            if (near != null)
            {
                spaceId = near.SpaceId;
                Vector3Double pp; ulong pid; float e;
                Ground.Height(near.SpaceId, near.Position, out pp, out pid, out e);
                Vector3 up = pp.Magnitude > 1 ? (Vector3)(pp / pp.Magnitude) : near.Rotation * Vector3.up;
                pos = near.Position + near.Rotation * Vector3.forward * 12f + up * 3f;
            }
            else if (!PlaceOf(rec, spaces, out spaceId, out pos)) return "The place it stood in isn't there any more. Restore it next to a player: ship restore " + binId + " here <player>";

            Managers.Client.Utils.ShipImportExportUtils.ShipImportExportData data = JsonUtility.FromJson<Managers.Client.Utils.ShipImportExportUtils.ShipImportExportData>((string)rec["data"]);
            uint newRoot;
            try { newRoot = Load(data, ships, spaces, ids, cables, spaceId, pos, rot); }
            catch (Exception e) { Log.Error("ship restore " + binId + ": " + e); return "Could not restore it: " + e.Message; }
            File.Move(file, file + ".restored");
            Restored++;
            Stats.ShipsChanged();
            string name = (string)rec["name"];
            Log.Msg("ship: " + by + " restored " + binId + (name.Length > 0 ? " \"" + name + "\"" : "") + " as #" + newRoot);
            return "Restored " + (name.Length > 0 ? "\"" + name + "\" " : "") + "as ship #" + newRoot + (near != null ? " next to " + near.Name : " where it stood") + ".";
        }

        private static bool PlaceOf(JObject rec, SpacesServer spaces, out uint spaceId, out Vector3 pos)
        {
            spaceId = (uint)rec["spaceId"];
            pos = new Vector3((float)rec["px"], (float)rec["py"], (float)rec["pz"]);
            JObject w = rec["where"] as JObject;
            ISpace sp;
            if (w != null && w["planetId"] != null)
            {
                // planet-surface spaces come and go: find (or make) the one for that point on the planet
                ulong pid = ulong.Parse((string)w["planetId"], CultureInfo.InvariantCulture);
                Vector3Double at = new Vector3Double((double)w["x"], (double)w["y"], (double)w["z"]);
                (uint id, ISpace space) found = spaces.GetOrCreateSpaceForPosition(new PlanetSurfaceSpace { PlanetId = pid, Center = at });
                PlanetSurfaceSpace ps = found.space as PlanetSurfaceSpace;
                if (ps == null) return false;
                spaceId = found.id;
                pos = (Vector3)(at - ps.Center);
                return true;
            }
            return spaces.TryGetSpace(spaceId, out sp);
        }

        // The game's own import (ShipImportExportUtils.Import), with the root put at a given place instead of on the local player.
        private static uint Load(Managers.Client.Utils.ShipImportExportUtils.ShipImportExportData data, ShipsServerTracker ships, SpacesServer spaces,
            WorldObjectsIdsServerTracker ids, CablesServerTracker cables, uint spaceId, Vector3 pos, Quaternion rot)
        {
            CableData[] cabs = CablesConverter.ConvertToState(data.cables);
            SavedShipState[] states = new ShipsConverter().ConvertToState(data.ships).States.Select(s => (SavedShipState)s).ToArray();
            uint rootOld = data.rootShipId;
            Dictionary<uint, uint> map = new Dictionary<uint, uint>(), back = new Dictionary<uint, uint>();
            foreach (SavedShipState s in states)
            {
                s.Velocity = Vector3.zero;
                uint n = ids.CreateUniqueIdForObject();
                map[s.Id] = n; back[n] = s.Id; s.Id = n;
            }
            for (int i = 0; i < cabs.Length; i++)
            {
                CableData c = cabs[i];
                CableState st = c.State;
                CablePlugSocket a = st.PlugA; EntityPartSocket ea = a.EntityPartSocket; ea.EntityId = map[ea.EntityId]; a.EntityPartSocket = ea; st.PlugA = a;
                CablePlugSocket b = st.PlugB; EntityPartSocket eb = b.EntityPartSocket; eb.EntityId = map[eb.EntityId]; b.EntityPartSocket = eb; st.PlugB = b;
                c.State = st; cabs[i] = c;
            }
            foreach (SavedShipState s in states)
            {
                if (back[s.Id] != rootOld) s.SpaceId = map[s.SpaceId];
                foreach (StatefulPart p in s.StatefulParts)
                {
                    uint v;
                    if (p.State is RotorState rs) p.State = rs with { AnchoredShipId = map.TryGetValue(rs.AnchoredShipId, out v) ? v : rs.AnchoredShipId };
                    else if (p.State is DockingDoorState ds && map.TryGetValue(ds.DockedConnectedPart.ShipId, out v))
                    {
                        var dc = ds.DockedConnectedPart; dc.ShipId = v;
                        p.State = ds with { DockedConnectedPart = dc };
                    }
                }
            }
            uint newRoot = 0;
            foreach (SavedShipState s in states)
                if (back[s.Id] == rootOld) { s.SpaceId = spaceId; s.Position = pos; s.Rotation = rot; newRoot = s.Id; }
            Dictionary<uint, uint> rel = new Dictionary<uint, uint>();
            foreach (SavedShipState s in states) { uint sp = spaces.GetOrCreateShipRelativeSpace(s.Id); rel[s.Id] = sp; s.RelativeSpaceId = sp; }
            foreach (SavedShipState s in states) if (back[s.Id] != rootOld) s.SpaceId = rel[s.SpaceId];
            foreach (SavedShipState s in states) ships.LoadShip(s);
            foreach (CableData c in cabs) cables.AddCable(c.State);
            return newRoot;
        }
    }
}
