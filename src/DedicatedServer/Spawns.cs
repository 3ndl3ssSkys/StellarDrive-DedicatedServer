// Players spread over the planet instead of all starting on one spot, each with a spawn point of
// their own.
//
// With SpreadSpawns on, a player who arrives at the world's starting point for the first time (a new
// player in this world) is moved to a spot of their own: on dry, fairly flat ground, clear of ships,
// and away from other players' spots, within SpreadSpawnKm of the start (0 = anywhere on the planet).
// That spot is kept (server-data.json) and is where they respawn when they die without a bed on a
// ship (the game would otherwise send them back to the one starting point).
// Admins: /setspawn [player] puts a player's spot where they stand now; /clearspawn <player> removes it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Values;
using FishNet.Connection;
using HarmonyLib;
using Planets.Services;
using Players;
using Players.Network.Services;
using Players.Services;
using Players.Utils;
using UnityEngine;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static class Spawns
    {
        private static readonly Dictionary<int, float> _checkAt = new Dictionary<int, float>();   // connection -> when to look at a new arrival
        private static readonly System.Random _rng = new System.Random();
        public static int Spread;

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PlayerRespawnService), "RespawnAtSurface"),
                    prefix: new HarmonyMethod(typeof(Spawns).GetMethod("RespawnPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("spawn points: could not hook respawning: " + e.Message); }
        }

        /// <summary>A player has joined: look at where they are once their game has placed them.</summary>
        public static void OnJoined(TrackedPlayerServer p)
        {
            if (Server.Cfg.SpreadSpawns.Value) _checkAt[p.ConnectionId] = Time.realtimeSinceStartup + 3f;
        }

        public static void Update()
        {
            if (_checkAt.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            foreach (KeyValuePair<int, float> kv in _checkAt.ToList())
            {
                if (now < kv.Value) continue;
                _checkAt.Remove(kv.Key);
                TrackedPlayerServer p = Server.PlayerByConnection(kv.Key);
                if (p == null) continue;
                try { MaybeSpread(p); } catch (Exception e) { Log.Once("spread", "spawn points: " + e.Message); }
            }
        }

        private static void MaybeSpread(TrackedPlayerServer p)
        {
            PlayerRecord rec = Server.Data.Player(p.SteamUserId);
            if (rec == null || rec.SpawnPlanet != 0) return;
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            PlanetSurfaceSpace here = spaces.GetSpace(p.SpaceId) as PlanetSurfaceSpace;
            PlanetSurfaceSpace start = PlayerDefaultState.GetPlayerStartSpaceAsPosition();
            if (here == null || start == null || here.PlanetId != start.PlanetId) return;
            double fromStart = ((here.Center + (Vector3Double)p.Position) - start.Center).Magnitude;
            if (fromStart > 80.0) { Log.Debug("ground", p.Name + " is not at the starting point (" + fromStart.ToString("0") + " m away): no spread spot"); return; }
            Vector3Double spot;
            if (!Pick(start, p.Name, out spot)) { Log.Warn("spawn points: no good spot found for " + p.Name + "; they stay at the start"); return; }
            rec.SpawnPlanet = start.PlanetId; rec.SpawnX = spot.x; rec.SpawnY = spot.y; rec.SpawnZ = spot.z;
            Server.Data.MarkDirty();
            Server.Data.Flush();
            Send(p, rec);
            Spread++;
            Log.Msg("spawn points: " + p.Name + " starts (and respawns) " + ((spot - start.Center).Magnitude / 1000.0).ToString("0.0") + " km from the starting point");
            NetworkConnection conn = Server.Connection(p.ConnectionId);
            if (conn != null) Server.SayTo(conn, Chat.Server("Welcome! This is your own starting spot; you come back here if you die without a bed."));
        }

        /// <summary>Puts a player at their spot (also used on respawn).</summary>
        public static bool Send(TrackedPlayerServer p, PlayerRecord rec)
        {
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            Vector3Double spot = new Vector3Double(rec.SpawnX, rec.SpawnY, rec.SpawnZ);
            PlanetSurfaceSpace start = PlayerDefaultState.GetPlayerStartSpaceAsPosition();
            Vector3 upStart = start != null ? (Vector3)start.Center.Normalized : Vector3.up;
            Vector3 up = (Vector3)spot.Normalized;
            Quaternion turn = Quaternion.FromToRotation(upStart, up);
            spaces.MoveWorldObject(p, new PlanetSurfaceSpace { PlanetId = rec.SpawnPlanet, Center = spot });
            PlanetSurfaceSpace s = spaces.GetSpace(p.SpaceId) as PlanetSurfaceSpace;
            if (s == null) return false;
            return Ground.Place(p, p.SpaceId, (Vector3)(spot - s.Center), turn * PlayerDefaultState.DefaultRotation, turn * PlayerDefaultState.DefaultCameraRot);
        }

        // the game respawns at its one starting point; a player with a spot of their own goes there
        private static bool RespawnPrefix(NetworkConnection conn)
        {
            try
            {
                TrackedPlayerServer p = conn != null ? Server.PlayerByConnection(conn.ClientId) : null;
                PlayerRecord rec = p != null ? Server.Data.Player(p.SteamUserId) : null;
                if (rec == null || rec.SpawnPlanet == 0) return true;
                if (!Send(p, rec)) return true;
                Log.Debug("ground", p.Name + " respawned at their own spot");
                return false;
            }
            catch (Exception e) { Log.Once("respawn-spot", "spawn points: " + e.Message); return true; }
        }

        /// <summary>A spot: dry, fairly flat, clear of ships, as far as can be from other players' spots.</summary>
        private static bool Pick(PlanetSurfaceSpace start, string who, out Vector3Double best)
        {
            best = Vector3Double.Zero;
            PlanetsServerTracker planets = Core.Services.ServiceLocator.GetService<PlanetsServerTracker>();
            Planets.Planet planet = Resources.FindObjectsOfTypeAll<Planets.Planet>().FirstOrDefault(x => x.Id == start.PlanetId && x.gameObject.scene.IsValid());
            float radius = planet != null ? planet.Radius : (float)start.Center.Magnitude;
            Vector3 home = (Vector3)start.Center.Normalized;
            double km = Server.Cfg.SpreadSpawnKm.Value;
            double maxAngle = km <= 0 ? Math.PI : Math.Min(Math.PI, km * 1000.0 / radius);
            List<Vector3Double> taken = Server.Data.D.Players.Where(r => r.SpawnPlanet == start.PlanetId).Select(r => new Vector3Double(r.SpawnX, r.SpawnY, r.SpawnZ)).ToList();
            taken.Add(start.Center);
            double bestScore = -1;
            for (int i = 0; i < 60; i++)
            {
                // an even spread over the cap around the start
                double cos = 1 - _rng.NextDouble() * (1 - Math.Cos(maxAngle));
                double ang = Math.Acos(cos), turn = _rng.NextDouble() * Math.PI * 2;
                Vector3 side = Vector3.Cross(home, Mathf.Abs(home.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 dir = Quaternion.AngleAxis((float)(turn * 180 / Math.PI), home) * (Quaternion.AngleAxis((float)(ang * 180 / Math.PI), side) * home);
                float e = planets.GetPlanetTerrainElevationAtPosition(start.PlanetId, dir);
                if (e < radius + 2f) continue;                                   // under water level
                // fairly flat: the ground 4 m around within 2.5 m of here
                Vector3 t1 = Vector3.Cross(dir, side).normalized, t2 = Vector3.Cross(dir, t1).normalized;
                float step = 4f / e, worst = 0f;
                foreach (Vector3 d in new[] { t1, -t1, t2, -t2 })
                    worst = Mathf.Max(worst, Mathf.Abs(planets.GetPlanetTerrainElevationAtPosition(start.PlanetId, (dir + d * step).normalized) - e));
                if (worst > 2.5f) continue;
                Vector3Double at = (Vector3Double)dir * (e + 1.5);
                double nearest = taken.Select(t => (t - at).Magnitude).DefaultIfEmpty(1e9).Min();
                if (nearest < 30) continue;
                // far enough from the others (up to 600 m counts), flat, and otherwise anywhere
                double score = Math.Min(nearest, 600) - worst * 50 + _rng.NextDouble() * 200;
                if (score > bestScore) { bestScore = score; best = at; }
            }
            return bestScore >= 0;
        }

#if DSTEST
        public static string TestSpread(TrackedPlayerServer p)
        {
            PlayerRecord rec = Server.Data.Player(p.SteamUserId);
            PlanetSurfaceSpace start = PlayerDefaultState.GetPlayerStartSpaceAsPosition();
            Vector3Double spot;
            if (rec == null || start == null || !Pick(start, p.Name, out spot)) return "testspread: no spot";
            rec.SpawnPlanet = start.PlanetId; rec.SpawnX = spot.x; rec.SpawnY = spot.y; rec.SpawnZ = spot.z;
            Server.Data.MarkDirty();
            Server.Data.Flush();
            Send(p, rec);
            return "testspread " + p.Name + " spot " + spot.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + spot.y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + spot.z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " km " + ((spot - start.Center).Magnitude / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
#endif

        // ---- admin commands ----------------------------------------------------------------------

        public static string SetHere(TrackedPlayerServer p)
        {
            PlayerRecord rec = Server.Data.Player(p.SteamUserId);
            ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<ISpacesServerProvider>();
            PlanetSurfaceSpace s = spaces.GetSpace(p.SpaceId) as PlanetSurfaceSpace;
            if (rec == null || s == null) return p.Name + " isn't standing on a planet.";
            Vector3Double at = s.Center + (Vector3Double)p.Position;
            rec.SpawnPlanet = s.PlanetId; rec.SpawnX = at.x; rec.SpawnY = at.y; rec.SpawnZ = at.z;
            Server.Data.MarkDirty();
            Server.Data.Flush();
            return p.Name + " respawns here now (when they have no bed).";
        }
    }
}
