// A player's profile, whether they are on or not: put them back at their starting spot, onto a ship,
// or delete their character and what this server knows about them.
//
// The game keeps players who aren't on as saved snapshots (PlayersServerTracker._inactivePlayers),
// each also counted as an object in its space. Moving one goes through the spaces service the same way
// moving a player who is on does, so that bookkeeping stays right; when they next join they are there.
//
// Deleting: the world is saved first (keeping the previous save as a backup, as autosaves do), a player
// who is on is disconnected, then their character (inventory, place, bed) leaves the world and their
// record leaves server-data.json: name, play time, permissions, friends, marker colour, spawn spot and the
// game they were tied to. Bans, the whitelist and admin status stay (they are the server's rules about
// that person, not their profile). The visit history stays in the stats. Next time they join they start new.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Values;
using HarmonyLib;
using Players;
using Players.Interface.Model;
using Players.Services;
using Players.Utils;
using Ships;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static class Profiles
    {
        private static readonly Dictionary<ulong, string> _deleteWhenGone = new Dictionary<ulong, string>();   // id -> who asked
        private static float _giveUpAt;
        public static int Deleted, Moved;

        private static PlayersServerTracker Tracker() { return Core.Services.ServiceLocator.GetService<PlayersServerTracker>(); }
        private static ISpacesServerProvider Spaces() { return Core.Services.ServiceLocator.GetService<ISpacesServerProvider>(); }

        private static List<PlayerStateSnapshot> Inactive()
        {
            PlayersServerTracker t = Tracker();
            return t != null ? AccessTools.Field(typeof(PlayersServerTracker), "_inactivePlayers").GetValue(t) as List<PlayerStateSnapshot> : null;
        }

        public static TrackedPlayerServer OnNow(ulong id) { return Server.ActivePlayers().FirstOrDefault(p => p.SteamUserId == id); }
        public static PlayerStateSnapshot Away(ulong id) { List<PlayerStateSnapshot> l = Inactive(); return l != null ? l.FirstOrDefault(p => p.SteamUserId == id) : null; }

        /// <summary>Does this world have a character for the player (on now or not)?</summary>
        public static bool InWorld(ulong id) { return OnNow(id) != null || Away(id) != null; }

        // ---- back to the start ---------------------------------------------------------------------

        /// <summary>Their own spawn spot when they have one (SpreadSpawns, /setspawn), else the world's starting point.</summary>
        public static string Reset(ulong id, string name)
        {
            PlayerRecord rec = Server.Data.Player(id);
            bool own = rec != null && rec.SpawnPlanet != 0;
            string where = own ? "their own starting spot" : "the world's starting point";
            TrackedPlayerServer on = OnNow(id);
            if (on != null)
            {
                bool told;
                if (own) told = Spawns.Send(on, rec);
                else
                {
                    Vector3 local; Quaternion rot;
                    uint space = Signals.StartSpot(0f, out local, out rot);
                    told = Ground.Place(on, space, local, PlayerDefaultState.DefaultRotation, PlayerDefaultState.DefaultCameraRot);
                }
                Moved++;
                Server.SayTo(Server.Connection(on.ConnectionId), Chat.Server("An admin moved you back to " + where + "."));
                return "moved " + on.Name + " to " + where + (told ? "" : " (their game wasn't told; they'll see it when they rejoin)");
            }
            PlayerStateSnapshot away = Away(id);
            if (away == null) return (name ?? id.ToString()) + " has no character in this world.";
            ISpacesServerProvider spaces = Spaces();
            if (own)
            {
                Vector3Double spot = new Vector3Double(rec.SpawnX, rec.SpawnY, rec.SpawnZ);
                Quaternion turn = Turn((Vector3)spot.Normalized);
                spaces.MoveWorldObject(away, new PlanetSurfaceSpace { PlanetId = rec.SpawnPlanet, Center = spot });
                PlanetSurfaceSpace s = spaces.GetSpace(away.SpaceId) as PlanetSurfaceSpace;
                if (s == null) return "could not move " + away.Name + ": their spot's space wasn't made";
                Put(away, away.SpaceId, (Vector3)(spot - s.Center), turn);
            }
            else
            {
                Vector3 local; Quaternion rot;
                uint space = Signals.StartSpot(0f, out local, out rot);
                Put(away, space, local, Quaternion.identity);
            }
            Moved++;
            Server.RequestSave(2f, true);
            return "moved " + away.Name + " (not on now) to " + where + "; they'll be there when they join";
        }

        /// <summary>The turn from the starting point's up to `up`: a player stands upright anywhere on the planet.</summary>
        private static Quaternion Turn(Vector3 up)
        {
            PlanetSurfaceSpace start = PlayerDefaultState.GetPlayerStartSpaceAsPosition();
            Vector3 upStart = start != null ? (Vector3)start.Center.Normalized : Vector3.up;
            return Quaternion.FromToRotation(upStart, up);
        }

        /// <summary>A saved (offline) character to a place, standing still, the way the game loads players.</summary>
        private static void Put(PlayerStateSnapshot p, uint space, Vector3 pos, Quaternion turn)
        {
            Spaces().SetWorldObjectPhysics(p, space, new LocalPhysicsState { Position = pos, Rotation = turn * PlayerDefaultState.DefaultRotation, Velocity = Vector3.zero });
            p.Position = pos;
            p.Velocity = Vector3.zero;
            p.Rotation = turn * PlayerDefaultState.DefaultRotation;
            p.CameraRotation = turn * PlayerDefaultState.DefaultCameraRot;
        }

        // ---- onto a ship -----------------------------------------------------------------------------

        public static string ToShip(ulong id, string name, uint shipId)
        {
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            TrackedShipServer ship;
            if (t == null || !t.TryGetTrackedShip(shipId, out ship)) return "There's no ship #" + shipId + ".";
            TrackedPlayerServer on = OnNow(id);
            if (on != null)
            {
                string said = Ground.OntoShip(on, ship);
                Moved++;
                Server.SayTo(Server.Connection(on.ConnectionId), Chat.Server("An admin moved you to ship #" + shipId + "."));
                return said;
            }
            PlayerStateSnapshot away = Away(id);
            if (away == null) return (name ?? id.ToString()) + " has no character in this world.";
            Vector3 pos, up;
            Ground.ShipTop(ship, out pos, out up);
            Put(away, ship.SpaceId, pos, Turn(up));
            Moved++;
            Server.RequestSave(2f, true);
            return "put " + away.Name + " (not on now) on ship #" + shipId + "; they'll be there when they join";
        }

        // ---- deleting ----------------------------------------------------------------------------------

        public static string Delete(ulong id, string name, string by)
        {
            PlayerRecord rec = Server.Data.Player(id);
            string who = name ?? (rec != null ? rec.Name : id.ToString());
            if (rec == null && !InWorld(id)) return "There's no profile for " + who + ".";
            string err = Server.SaveNow("before deleting " + who + "'s profile", true);
            if (err != null && !err.Contains("already running")) return "Not deleted: the world couldn't be saved first (" + err + ").";
            TrackedPlayerServer on = OnNow(id);
            if (on != null)
            {
                _deleteWhenGone[id] = by;
                _giveUpAt = Time.realtimeSinceStartup + 30f;
                Server.Kick(Server.Connection(on.ConnectionId), "Your character on this server was deleted by an admin. Join again to start fresh.");
                return "Saved a backup; disconnecting " + who + ", then deleting their profile.";
            }
            return Finish(id, who, by);
        }

        private static string Finish(ulong id, string who, string by)
        {
            bool character = false;
            List<PlayerStateSnapshot> list = Inactive();
            PlayerStateSnapshot away = Away(id);
            if (away != null && list != null)
            {
                list.Remove(away);
                PlayersServerTracker t = Tracker();
                try { t.OnPlayerObjectUnloaded?.Invoke(away); } catch (Exception e) { Log.Warn("profiles: " + e.Message); }
                try { Core.Services.ServiceLocator.GetService<WorldTracking.Spaces.SpacesServer>()?.UnassignObjectFromItsSpace(away); } catch { }
                character = true;
            }
            PlayerRecord rec = Server.Data.Player(id);
            bool admin = rec != null && rec.Op;
            if (rec != null)
            {
                Server.Data.D.Players.Remove(rec);
                if (admin) Server.Data.D.Players.Add(new PlayerRecord { Id = id, Name = rec.Name, Op = true, FirstSeen = rec.FirstSeen, LastSeen = rec.LastSeen });
            }
            foreach (PlayerRecord other in Server.Data.D.Players) other.Friends.Remove(id);
            Server.Data.MarkDirty();
            Server.Data.Flush();
            Server.RequestSave(1f, false);
            Deleted++;
            Log.Msg("profiles: deleted " + who + " (" + id + ") - " + (character ? "their character and " : "") + "their record; by " + by);
            return "Deleted " + who + "'s profile: " + (character ? "their character (inventory, place, bed) and " : "") + "what the server knew about them"
                + (admin ? " (they stay an admin)" : "") + ". The save before it was kept as a backup.";
        }

        /// <summary>Every frame: a deleted player who was on is deleted once their game has gone.</summary>
        public static void Update()
        {
            if (_deleteWhenGone.Count == 0) return;
            foreach (KeyValuePair<ulong, string> kv in _deleteWhenGone.ToList())
            {
                if (OnNow(kv.Key) != null)
                {
                    if (Time.realtimeSinceStartup > _giveUpAt) { _deleteWhenGone.Remove(kv.Key); Log.Warn("profiles: " + kv.Key + " didn't leave; their profile wasn't deleted"); }
                    continue;
                }
                _deleteWhenGone.Remove(kv.Key);
                PlayerRecord rec = Server.Data.Player(kv.Key);
                Log.Msg(Finish(kv.Key, rec != null ? rec.Name : kv.Key.ToString(), kv.Value));
            }
        }

        /// <summary>For `player info`: where they are, whether they're on, and how far from the start and the nearest ship.</summary>
        public static string Where(ulong id)
        {
            TrackedPlayerServer on = OnNow(id);
            PlayerStateSnapshot away = on == null ? Away(id) : null;
            if (on == null && away == null) return "no character in this world";
            uint space = on != null ? on.SpaceId : away.SpaceId;
            Vector3 pos = on != null ? on.Position : away.Position;
            return (on != null ? "on now, in space " : "not on; their character is in space ") + space + " at " + V(pos) + Near(space, pos);
        }

        private static string Near(uint space, Vector3 pos)
        {
            string s = "";
            try
            {
                ShipRelativeSpace aboard = Spaces().GetSpace(space) as ShipRelativeSpace;
                if (aboard != null) return "; aboard ship #" + aboard.ShipId;
                PlanetSurfaceSpace here = Spaces().GetSpace(space) as PlanetSurfaceSpace;
                PlanetSurfaceSpace start = PlayerDefaultState.GetPlayerStartSpaceAsPosition();
                if (here != null && start != null && here.PlanetId == start.PlanetId)
                    s += "; " + ((here.Center + (Vector3Double)pos) - start.Center).Magnitude.ToString("0", CultureInfo.InvariantCulture) + " m from the world's starting point";
                ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
                TrackedShipServer best = null; float bestD = float.MaxValue;
                if (t != null)
                    foreach (TrackedShipServer ship in t.Ships)
                    {
                        if (ship.SpaceId != space) continue;
                        float d = (ship.Position - pos).magnitude;
                        if (d < bestD) { bestD = d; best = ship; }
                    }
                if (best != null) s += "; nearest ship #" + best.Id + " at " + bestD.ToString("0", CultureInfo.InvariantCulture) + " m";
            }
            catch (Exception e) { s += " (" + e.Message + ")"; }
            return s;
        }

        private static string V(Vector3 v) { return string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", v.x, v.y, v.z); }
    }
}
