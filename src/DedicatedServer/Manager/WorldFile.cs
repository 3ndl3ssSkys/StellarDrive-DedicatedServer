// A world save (world.json) as the Saves page sees it: who and what is in it, and copying players
// and ships from one save into another.
//
// Copying works on the save's own JSON. Ids are made new in the target (ships, players, spaces,
// cables), and everything that points at a ship is pointed at its copy: the ship's own space, ships
// docked to it or riding on it (they come along), cables between copied ships, targeting computers,
// docking doors and a player's respawn point. A reference to a ship that isn't copied is cleared.
// The target is backed up first (world-before-copy-<time>.json).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDServerManager
{
    public class WorldFile
    {
        public class PlayerRow { public long Id; public ulong SteamId; public string Name = ""; public string Where = ""; public double Health, Oxygen; public int ItemsUsed, Slots; public string Items = ""; }
        public class ShipRow { public long Id; public string Name = ""; public int Parts; public string Where = ""; public double Speed; public long DockedTo; public bool Riding; }

        public string Path;
        public JsonObject Root;
        public int Version;
        public double WorldTime;
        public List<PlayerRow> Players = new List<PlayerRow>();
        public List<ShipRow> Ships = new List<ShipRow>();
        public int Cables, Objects;

        public static WorldFile Read(string path, Catalog cat)
        {
            WorldFile w = new WorldFile { Path = path };
            w.Root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
            w.Version = (int)Num(w.Root, "version");
            w.WorldTime = Num(w.Root, "worldTime");
            Dictionary<long, long> spacePlanet = new Dictionary<long, long>();          // planet surface space -> planet
            Dictionary<long, long> relShip = new Dictionary<long, long>();              // ship relative space -> ship
            foreach (JsonNode s in Arr(w.Root["spaces"]?["planetSurfaceSpaces"])) spacePlanet[(long)Num(s, "id")] = (long)Num(s, "planetId");
            foreach (JsonNode s in Arr(w.Root["spaces"]?["planetRingSpaces"])) spacePlanet[(long)Num(s, "id")] = (long)Num(s, "planetId");
            foreach (JsonNode s in Arr(w.Root["spaces"]?["shipRelativeSpaces"])) relShip[(long)Num(s, "id")] = (long)Num(s, "shipId");
            Dictionary<long, JsonNode> ships = Arr(w.Root["ships"]).ToDictionary(s => (long)Num(s, "id"), s => s);
            Func<long, int, string> where = null;
            where = (space, depth) =>
            {
                long p, sh;
                if (spacePlanet.TryGetValue(space, out p)) return cat.Planet(p);
                if (relShip.TryGetValue(space, out sh))
                {
                    JsonNode ship;
                    string n = ShipName(ships.TryGetValue(sh, out ship) ? ship : null, sh);
                    return depth < 5 && ship != null ? "on " + n + " (" + where((long)Num(ship, "spaceId"), depth + 1) + ")" : "on " + n;
                }
                return space == 0 ? "" : "in space";
            };
            foreach (JsonNode p in Arr(w.Root["players"]))
            {
                JsonArray inv = p["inventory"] as JsonArray;
                List<JsonNode> held = Arr(p["inventory"]).Concat(Arr(p["toolbar"])).Where(x => Bool(x, "hasItem")).ToList();
                string items = string.Join(", ", held.GroupBy(x => (long)Num(x, "itemId")).Select(g => cat.Item(g.Key) + " x" + g.Sum(x => (long)Num(x, "quantity"))).Take(12));
                w.Players.Add(new PlayerRow
                {
                    Id = (long)Num(p, "id"), SteamId = (ulong)Num(p, "steamUserId"), Name = Str(p, "name"), Where = where((long)Num(p, "spaceId"), 0),
                    Health = Num(p, "health"), Oxygen = Num(p, "oxygen"), ItemsUsed = held.Count, Slots = Arr(p["inventory"]).Count + Arr(p["toolbar"]).Count, Items = items
                });
            }
            foreach (JsonNode s in ships.Values)
            {
                long id = (long)Num(s, "id");
                long docked = Arr(s["dockingDoorStates"]).Where(d => Bool(d, "isDocked")).Select(d => (long)Num(d, "dockedShipId")).FirstOrDefault();
                JsonNode v = s["velocity"];
                w.Ships.Add(new ShipRow
                {
                    Id = id, Name = ShipName(s, id),
                    Parts = Arr(s["hullParts"]).Count + Arr(s["statefulFloatingParts"]).Count + Arr(s["statefulHullParts"]).Count,
                    Where = where((long)Num(s, "spaceId"), 0), DockedTo = docked, Riding = relShip.ContainsKey((long)Num(s, "spaceId")),
                    Speed = v != null ? Math.Sqrt(Num(v, "x") * Num(v, "x") + Num(v, "y") * Num(v, "y") + Num(v, "z") * Num(v, "z")) : 0
                });
            }
            w.Cables = Arr(w.Root["cables"]).Count;
            w.Objects = Arr(w.Root["worldObjects"]).Count;
            return w;
        }

        public static string ShipName(JsonNode s, long id)
        {
            string beacon = s != null ? Arr(s["beaconStates"]).Select(b => Str(b, "name").Trim()).FirstOrDefault(n => n.Length > 0) : null;
            return !string.IsNullOrEmpty(beacon) ? beacon + " (#" + id + ")" : "Ship #" + id;
        }

        // ---- copying between saves --------------------------------------------------------------------

        public class CopyRequest
        {
            public List<ulong> Players = new List<ulong>();     // by Steam id
            public bool Location = true, Inventory = true;
            public List<long> Ships = new List<long>();
        }

        /// <summary>The ships that must come along: docked to, riding on, or carrying a chosen one.</summary>
        public static HashSet<long> ShipsWith(JsonObject src, IEnumerable<long> chosen)
        {
            Dictionary<long, long> relShip = Arr(src["spaces"]?["shipRelativeSpaces"]).ToDictionary(s => (long)Num(s, "id"), s => (long)Num(s, "shipId"));
            Dictionary<long, JsonNode> ships = Arr(src["ships"]).ToDictionary(s => (long)Num(s, "id"), s => s);
            HashSet<long> set = new HashSet<long>(chosen.Where(ships.ContainsKey));
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (long id in set.ToList())
                {
                    JsonNode s = ships[id];
                    List<long> links = new List<long>();
                    long parent;
                    if (relShip.TryGetValue((long)Num(s, "spaceId"), out parent)) links.Add(parent);
                    links.AddRange(Arr(s["dockingDoorStates"]).Where(d => Bool(d, "isDocked")).Select(d => (long)Num(d, "dockedShipId")));
                    long myRel = (long)Num(s, "relativeSpaceId");
                    links.AddRange(ships.Values.Where(o => (long)Num(o, "spaceId") == myRel).Select(o => (long)Num(o, "id")));
                    foreach (long l in links) if (l != 0 && ships.ContainsKey(l) && set.Add(l)) grew = true;
                }
            }
            return set;
        }

        /// <summary>Copies from source into target (a file). Returns what was done, line by line.</summary>
        public static List<string> Copy(string sourcePath, string targetPath, CopyRequest req)
        {
            List<string> said = new List<string>();
            JsonObject src = JsonNode.Parse(File.ReadAllText(sourcePath)).AsObject();
            JsonObject dst = JsonNode.Parse(File.ReadAllText(targetPath)).AsObject();
            if ((int)Num(src, "version") != (int)Num(dst, "version"))
                throw new InvalidOperationException("The saves are from different versions of the game (" + Num(src, "version") + " and " + Num(dst, "version") + "). Load the older one in the game and save it once, then try again.");
            string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(targetPath), "world-before-copy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Copy(targetPath, backup);
            said.Add("Kept the target as it was: " + System.IO.Path.GetFileName(backup));

            JsonObject dSpaces = dst["spaces"] as JsonObject ?? (JsonObject)(dst["spaces"] = new JsonObject());
            long nextObj = new[] { "players", "ships", "worldObjects" }.SelectMany(k => Arr(dst[k])).Select(x => (long)Num(x, "id")).DefaultIfEmpty(0).Max() + 1;
            long nextSpace = dSpaces.SelectMany(kv => Arr(kv.Value)).Select(x => (long)Num(x, "id")).DefaultIfEmpty(0).Max() + 1;
            long nextCable = Arr(dst["cables"]).Select(x => (long)Num(x, "id")).DefaultIfEmpty(0).Max() + 1;
            // keep clear of the source's ids too (some of a ship's own data names its id)
            nextObj = Math.Max(nextObj, new[] { "players", "ships", "worldObjects" }.SelectMany(k => Arr(src[k])).Select(x => (long)Num(x, "id")).DefaultIfEmpty(0).Max() + 1);

            Dictionary<long, Tuple<string, JsonNode>> sSpaces = new Dictionary<long, Tuple<string, JsonNode>>();
            foreach (KeyValuePair<string, JsonNode> kv in (src["spaces"] as JsonObject ?? new JsonObject()))
                foreach (JsonNode s in Arr(kv.Value)) sSpaces[(long)Num(s, "id")] = Tuple.Create(kv.Key, s);
            Dictionary<long, JsonNode> sShips = Arr(src["ships"]).ToDictionary(s => (long)Num(s, "id"), s => s);
            Dictionary<long, long> shipMap = new Dictionary<long, long>();
            Dictionary<long, long> spaceMap = new Dictionary<long, long>();

            // ships: ids first (spaces may point at each other's)
            HashSet<long> ships = ShipsWith(src, req.Ships);
            foreach (long id in ships.OrderBy(x => x)) shipMap[id] = nextObj++;
            foreach (long id in ships)
            {
                long rel = (long)Num(sShips[id], "relativeSpaceId");
                long nrel = nextSpace++;
                spaceMap[rel] = nrel;
                Add(dSpaces, "shipRelativeSpaces", new JsonObject { ["id"] = nrel, ["shipId"] = shipMap[id] });
            }
            Func<long, long> mapSpace = null;
            mapSpace = old =>
            {
                long n;
                if (spaceMap.TryGetValue(old, out n)) return n;
                Tuple<string, JsonNode> t;
                if (!sSpaces.TryGetValue(old, out t)) return old;
                JsonNode copy = t.Item2.DeepClone();
                n = nextSpace++;
                copy["id"] = n;
                spaceMap[old] = n;
                RemapShips(copy, shipMap);
                Add(dSpaces, t.Item1, copy);
                return n;
            };
            JsonArray dShips = dst["ships"] as JsonArray ?? (JsonArray)(dst["ships"] = new JsonArray());
            foreach (long id in ships.OrderBy(x => x))
            {
                JsonNode copy = sShips[id].DeepClone();
                copy["id"] = shipMap[id];
                copy["relativeSpaceId"] = spaceMap[(long)Num(sShips[id], "relativeSpaceId")];
                copy["spaceId"] = mapSpace((long)Num(sShips[id], "spaceId"));
                RemapShips(copy, shipMap);
                dShips.Add(copy);
                said.Add("Ship " + ShipName(sShips[id], id) + " copied as #" + shipMap[id] + (req.Ships.Contains(id) ? "" : " (it is docked to, or riding on, one that was picked)"));
            }
            int cables = 0;
            JsonArray dCables = dst["cables"] as JsonArray ?? (JsonArray)(dst["cables"] = new JsonArray());
            foreach (JsonNode c in Arr(src["cables"]))
            {
                long a = (long)Num(c["plugA"], "shipId"), b = (long)Num(c["plugB"], "shipId");
                if (!shipMap.ContainsKey(a) || !shipMap.ContainsKey(b)) continue;
                JsonNode copy = c.DeepClone();
                copy["id"] = nextCable++;
                RemapShips(copy, shipMap);
                dCables.Add(copy);
                cables++;
            }
            if (cables > 0) said.Add(cables + " cable(s) between them copied");
            CopyMeta(src, dst, "ships", shipMap);

            // players
            JsonArray dPlayers = dst["players"] as JsonArray ?? (JsonArray)(dst["players"] = new JsonArray());
            Dictionary<long, long> playerMap = new Dictionary<long, long>();
            foreach (ulong steam in req.Players)
            {
                JsonNode sp = Arr(src["players"]).FirstOrDefault(p => (ulong)Num(p, "steamUserId") == steam);
                if (sp == null) continue;
                JsonNode tp = Arr(dst["players"]).FirstOrDefault(p => (ulong)Num(p, "steamUserId") == steam);
                string name = Str(sp, "name");
                if (tp == null)
                {
                    JsonNode copy = sp.DeepClone();
                    long nid = nextObj++;
                    playerMap[(long)Num(sp, "id")] = nid;
                    copy["id"] = nid;
                    Place(copy, sp, sSpaces, sShips, mapSpace, shipMap);
                    FixRespawn(copy, shipMap);
                    dPlayers.Add(copy);
                    said.Add(name + " copied (everything: where they are, what they carry, health)");
                    continue;
                }
                if (req.Location) { Place(tp, sp, sSpaces, sShips, mapSpace, shipMap); tp["respawnPoint"] = sp["respawnPoint"]?.DeepClone(); FixRespawn(tp, shipMap); }
                if (req.Inventory) { tp["inventory"] = sp["inventory"]?.DeepClone(); tp["toolbar"] = sp["toolbar"]?.DeepClone(); }
                said.Add(name + ": " + (req.Location && req.Inventory ? "location and inventory" : req.Location ? "location" : req.Inventory ? "inventory" : "nothing") + " copied");
            }
            CopyMeta(src, dst, "players", playerMap);

            string tmp = targetPath + ".tmp";
            File.WriteAllText(tmp, dst.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, targetPath, true);
            File.Delete(tmp);
            return said;
        }

        /// <summary>A player's place from the source: their space (copied), position and facing.</summary>
        private static void Place(JsonNode to, JsonNode from, Dictionary<long, Tuple<string, JsonNode>> sSpaces, Dictionary<long, JsonNode> sShips, Func<long, long> mapSpace, Dictionary<long, long> shipMap)
        {
            long space = (long)Num(from, "spaceId");
            Tuple<string, JsonNode> t;
            JsonNode pos = from["position"]?.DeepClone();
            if (sSpaces.TryGetValue(space, out t) && t.Item1 == "shipRelativeSpaces" && !shipMap.ContainsKey((long)Num(t.Item2, "shipId")))
            {
                // they were on a ship that isn't copied: put them where that ship was, a little above it
                JsonNode ship;
                if (sShips.TryGetValue((long)Num(t.Item2, "shipId"), out ship))
                {
                    space = (long)Num(ship, "spaceId");
                    pos = ship["position"]?.DeepClone();
                    if (pos != null) { JsonNode up = Up(sSpaces, space, pos); pos["x"] = Num(pos, "x") + Num(up, "x") * 4; pos["y"] = Num(pos, "y") + Num(up, "y") * 4; pos["z"] = Num(pos, "z") + Num(up, "z") * 4; }
                }
            }
            to["spaceId"] = mapSpace(space);
            to["position"] = pos;
            to["rotation"] = from["rotation"]?.DeepClone();
            to["cameraRotation"] = from["cameraRotation"]?.DeepClone();
            to["velocity"] = new JsonObject { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 0.0 };
        }

        /// <summary>Away from the planet's centre at a point of a planet surface space.</summary>
        private static JsonNode Up(Dictionary<long, Tuple<string, JsonNode>> sSpaces, long space, JsonNode pos)
        {
            Tuple<string, JsonNode> t;
            double x = Num(pos, "x"), y = Num(pos, "y"), z = Num(pos, "z");
            if (sSpaces.TryGetValue(space, out t) && t.Item2["center"] != null) { x += Num(t.Item2["center"], "x"); y += Num(t.Item2["center"], "y"); z += Num(t.Item2["center"], "z"); }
            double m = Math.Sqrt(x * x + y * y + z * z);
            return m < 1 ? new JsonObject { ["x"] = 0.0, ["y"] = 1.0, ["z"] = 0.0 } : new JsonObject { ["x"] = x / m, ["y"] = y / m, ["z"] = z / m };
        }

        private static void FixRespawn(JsonNode p, Dictionary<long, long> shipMap)
        {
            JsonNode r = p["respawnPoint"];
            if (r == null) return;
            long ship = (long)Num(r, "shipId");
            if (Bool(r, "useDefaultSpawn") || ship == 0) return;
            long n;
            if (shipMap.TryGetValue(ship, out n)) r["shipId"] = n;
            else { r["useDefaultSpawn"] = true; r["shipId"] = 0; r["partId"] = 0; }
        }

        /// <summary>Every ship id inside, pointed at the copy (or cleared, with what depended on it).</summary>
        private static void RemapShips(JsonNode n, Dictionary<long, long> shipMap)
        {
            if (n is JsonObject o)
            {
                foreach (string k in o.Select(kv => kv.Key).ToList())
                {
                    JsonNode v = o[k];
                    if ((k == "shipId" || k == "dockedShipId" || k == "targetShipId") && v is JsonValue)
                    {
                        long id = (long)Num(o, k);
                        if (id == 0) continue;
                        long nid;
                        if (shipMap.TryGetValue(id, out nid)) o[k] = nid;
                        else
                        {
                            o[k] = 0;
                            if (k == "dockedShipId") { o["isDocked"] = false; o["dockedPartId"] = 0; }
                            if (k == "targetShipId") { o["hasTarget"] = false; o["targetPartId"] = 0; }
                        }
                    }
                    else if (v is JsonObject || v is JsonArray) RemapShips(v, shipMap);
                }
            }
            else if (n is JsonArray a) foreach (JsonNode x in a) RemapShips(x, shipMap);
        }

        private static void CopyMeta(JsonObject src, JsonObject dst, string key, Dictionary<long, long> map)
        {
            JsonObject sm = src["migrationMetadata"] as JsonObject, dm = dst["migrationMetadata"] as JsonObject;
            if (sm == null || dm == null || map.Count == 0) return;
            JsonArray to = dm[key] as JsonArray ?? (JsonArray)(dm[key] = new JsonArray());
            foreach (JsonNode e in Arr(sm[key]))
            {
                long n;
                if (!map.TryGetValue((long)Num(e, "id"), out n)) continue;
                JsonNode c = e.DeepClone();
                c["id"] = n;
                to.Add(c);
            }
        }

        private static void Add(JsonObject spaces, string list, JsonNode entry)
        {
            JsonArray a = spaces[list] as JsonArray ?? (JsonArray)(spaces[list] = new JsonArray());
            a.Add(entry);
        }

        // ---- json helpers ----------------------------------------------------------------------------

        public static List<JsonNode> Arr(JsonNode n) { return n is JsonArray a ? a.Where(x => x != null).ToList() : new List<JsonNode>(); }
        public static double Num(JsonNode n, string k)
        {
            try { JsonNode v = n?[k]; if (v == null) return 0; return v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.Parse(v.ToString(), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
        public static string Str(JsonNode n, string k) { try { return n?[k]?.GetValue<string>() ?? ""; } catch { return n?[k]?.ToString() ?? ""; } }
        public static bool Bool(JsonNode n, string k) { try { return n?[k] != null && n[k].GetValue<bool>(); } catch { return false; } }
    }

    /// <summary>The names behind ids, from the server's catalog.json.</summary>
    public class Catalog
    {
        private readonly Dictionary<string, string> _planets = new Dictionary<string, string>(), _items = new Dictionary<string, string>(), _parts = new Dictionary<string, string>();

        public static Catalog Load(Server s)
        {
            Catalog c = new Catalog();
            try
            {
                string p = System.IO.Path.Combine(s.DataDir, "catalog.json");
                if (!File.Exists(p)) return c;
                JsonNode n = JsonNode.Parse(File.ReadAllText(p));
                foreach (var kv in (n?["planets"] as JsonObject ?? new JsonObject())) c._planets[kv.Key] = kv.Value?.ToString() ?? "";
                foreach (var kv in (n?["items"] as JsonObject ?? new JsonObject())) c._items[kv.Key] = kv.Value?.ToString() ?? "";
                foreach (var kv in (n?["parts"] as JsonObject ?? new JsonObject())) c._parts[kv.Key] = kv.Value?.ToString() ?? "";
            }
            catch { }
            return c;
        }

        public string Planet(long id) { string n; return _planets.TryGetValue(id.ToString(), out n) && n.Length > 0 ? n : "planet " + id; }
        public string Item(long id) { string n; return _items.TryGetValue(id.ToString(), out n) && n.Length > 0 ? n : "item " + id; }
        public bool Known { get { return _planets.Count > 0; } }
    }
}
