// Signal values that are not numbers (NaN) or are infinite are set to 0 on the server.
//
// A math gate can make them (0 x infinity, infinity - infinity, a huge multiply, tan near 90 degrees),
// and once one is in a circuit it spreads to everything wired after it and is saved with the ship.
// Every value a cable carries comes from SignalConnectionGroupValues, so the value of each cable group
// and each socket is checked as it is worked out. Part states that hold a value (gate outputs, a
// delta's previous value, memory registers, constants) are also swept once a second, so a bad value
// already saved in a world is cleared as soon as its ship is loaded. Setting: FixBadSignals.
//
// Every part's state is cleaned, not only signal parts: a NaN signal driving a rotor saved the rotor's
// angle as NaN, and on load the ship it carries was placed at a NaN position. The game's room grid then
// grew forever trying to reach that point (ShipRoomsVoxelsTree.AddWall doubles its size until a wall
// coordinate fits; a coordinate made from NaN never does), using hundreds of MB a second until the
// machine ran out. So: the whole world is cleaned before anyone can join; rotors never take a NaN; and
// a wall coordinate that far out is skipped (and logged) instead of grown towards.
//
// From NecessaryFixes (tested there; with that mod loaded it does all of this and the server leaves it):
//   - "bad" includes numbers past +/-float.MaxValue: every part reads its signal as a float, where those ARE
//     infinity (a rotor turned by one gets a NaN angle);
//   - signal parts', rotors' and displays' states are cleaned as the server writes them (UpdatePartState), so
//     a bad value never exists even for a tick and a Delta loop can't remember one;
//   - pumps, valves, docking-door magnets and warp drives get at most +/-1 from a signal (LimitPartInputs);
//   - a ship is never given a NaN place, turn or speed (the game only logs "Attempting to set NaN to ship
//     physics state" and stores it - which is how NaN ships get saved): it keeps its last real one;
//   - saved ships and players are checked BEFORE the game loads them: one saved in a space the save doesn't
//     have (a space re-centred on a NaN ship) made LoadShip throw and the world never loaded. Such a ship is
//     given a real place to load into and then goes to the ship bin at READY like any other NaN ship.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Services;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Signals
    {
        public static long GroupsFixed;
        public static int PartsFixed;
        private static float _next;
        private static AccessTools.FieldRef<SignalConnectionGroupValues, Dictionary<uint, double>> _values;
        private static readonly Dictionary<Type, PropertyInfo[]> _props = new Dictionary<Type, PropertyInfo[]>();
        private static readonly Dictionary<Type, MethodInfo> _clone = new Dictionary<Type, MethodInfo>();

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                _values = AccessTools.FieldRefAccess<SignalConnectionGroupValues, Dictionary<uint, double>>("_groupSignalValues");
                h.Patch(AccessTools.Method(typeof(SignalConnectionGroupValues), "UpdateGroupSignalValue"),
                    postfix: new HarmonyMethod(typeof(Signals).GetMethod("GroupPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(SignalConnectionGroupValues), "GetSocketSignalValue"),
                    postfix: new HarmonyMethod(typeof(Signals).GetMethod("SocketPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                // what every part reads its input through (rotors, gyroscopes, docking doors, memory, reaction wheels)
                h.Patch(AccessTools.Method(typeof(ConnectionsTracker), "GetSignalValue", new[] { typeof(EntityPartSocket) }),
                    postfix: new HarmonyMethod(typeof(Signals).GetMethod("ReadPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("signals: could not hook signal values: " + e.Message); }
            try
            {
                // before and after the game turns its rotors: a NaN input makes a NaN angle within the update, so
                // the angle is cleaned again straight after (a rotor fed NaN holds at 0 until its input is a number)
                h.Patch(AccessTools.Method(typeof(Ships.Parts.Rotor.RotorsUpdater), "Update"),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("RotorsPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(Signals).GetMethod("RotorsPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Ships.Rooms.ShipRoomsVoxelsTree), "AddWall", new[] { typeof(Vector3Int) }),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("AddWallPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("signals: could not guard rotors and ship rooms: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(TrackedShipServer), "UpdatePartState", new[] { typeof(ushort), typeof(IStatefulPartState) }),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("StatePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Ships.Interface.Model.ShipState), "ServerSetPhysicsState"),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("PhysicsStatePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Players.Services.PlayersServerTracker), "LoadState"),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("PlayersLoadPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(ShipsServerTracker), "LoadShip"),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("ShipLoadPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("signals: could not guard part states, ship places and loading: " + e.Message); }
        }

        // ---- as the server writes a part's state, and gives a ship its place ---------------------------

        public static long StatesFixed, InputsLimited;
        public static int PhysicsRefused, LoadPartsFixed, PlayersMoved;

        private static void StatePrefix(TrackedShipServer __instance, ushort partId, ref IStatefulPartState state)
        {
            if (!On || state == null) return;
            if (!(state is ISignalEmitterState) && !(state is ISignalProcessorState) && !(state is Ships.Interface.Model.Parts.State.RotorState) && !(state is Ships.Interface.Model.Parts.State.SignalDisplayState)) return;
            IStatefulPartState clean = Clean(state);
            if (clean == null) return;
            state = clean;
            StatesFixed++;
            // (a circuit that keeps making it is cleaned every tick: said once per part)
            Log.Once("state-nan-" + __instance.Id + "-" + partId, "signals: ship " + __instance.Id + " part " + partId + " (" + clean.GetType().Name + ") was given NaN, infinity or a number too big for a float; it holds 0 until its inputs give a number again");
        }

        private static bool PhysicsStatePrefix(Ships.Interface.Model.State.ShipPhysicsState physicsState)
        {
            if (!On) return true;
            if (!BadV(physicsState.Position) && !BadQ(physicsState.Rotation) && !BadV(physicsState.Velocity)) return true;
            PhysicsRefused++;
            Log.Once("ship-nan-physics", "ships: a ship was about to be given a NaN place or speed; it keeps its last real one");
            return false;
        }

        // ---- the save, before the game loads it ------------------------------------------------------

        /// <summary>Ships given a real place as they loaded because theirs wasn't one: they go to the bin at READY.</summary>
        private static readonly HashSet<uint> _nanAtLoad = new HashSet<uint>();
        private static int _placedAtStart;

        private static bool SpaceExists(uint id)
        {
            WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
            WorldTracking.Interface.Model.Spaces.Common.ISpace sp;
            return spaces != null && spaces.TryGetSpace(id, out sp) && sp != null && !HasBadNumber(sp);
        }

        /// <summary>The world's starting point as a space (made if need be), and a spot there: 20 m up and this far to the side for a ship, 1.5 m up for a player.</summary>
        internal static uint StartSpot(float sideways, out Vector3 local, out Quaternion rot)
        {
            WorldTracking.Spaces.SpacesServer ss = Core.Services.ServiceLocator.GetService<WorldTracking.Spaces.SpacesServer>();
            WorldTracking.Interface.Model.Spaces.PlanetSurfaceSpace start = Players.Utils.PlayerDefaultState.GetPlayerStartSpaceAsPosition();
            var made = ss.GetOrCreateSpaceForPosition(start);
            WorldTracking.Interface.Model.Spaces.PlanetSurfaceSpace now = (WorldTracking.Interface.Model.Spaces.PlanetSurfaceSpace)made.Item2;
            Vector3 up = (Vector3)(start.Center / start.Center.Magnitude);
            Vector3 side = Vector3.Cross(up, Vector3.forward).normalized;
            local = (Vector3)(start.Center - now.Center) + up * (sideways > 0f ? 20f : 1.5f) + side * sideways;
            rot = Quaternion.LookRotation(Vector3.Cross(side, up), up);
            return made.Item1;
        }

        private static void PlayersLoadPrefix(IEnumerable<Players.Interface.Model.PlayerStateSnapshot> playerStates)
        {
            if (!On || playerStates == null) return;
            try
            {
                foreach (Players.Interface.Model.PlayerStateSnapshot p in playerStates)
                {
                    if (p == null || (SpaceExists(p.SpaceId) && !BadV(p.Position) && !BadV(p.Velocity))) continue;
                    Vector3 local; Quaternion rot;
                    p.SpaceId = StartSpot(0f, out local, out rot);
                    p.Position = local;
                    p.Velocity = Vector3.zero;
                    p.Rotation = Players.Utils.PlayerDefaultState.DefaultRotation;
                    p.CameraRotation = Players.Utils.PlayerDefaultState.DefaultCameraRot;
                    PlayersMoved++;
                    Log.Warn("players: " + p.Name + " was saved with no real place (NaN, or a space that isn't there); they start at the starting point");
                }
            }
            catch (Exception e) { Log.Warn("players: could not check saved players: " + e.Message); }
        }

        private static void ShipLoadPrefix(Ships.Interface.Model.State.IShipState shipToLoad)
        {
            if (!On || shipToLoad == null) return;
            try
            {
                if (shipToLoad.StatefulParts != null)
                    foreach (StatefulPart part in shipToLoad.StatefulParts)
                    {
                        if (part == null || part.State == null) continue;
                        IStatefulPartState clean = Clean(part.State);
                        if (clean != null) { part.State = clean; LoadPartsFixed++; }
                    }
                bool spaceOk = SpaceExists(shipToLoad.SpaceId);
                if (spaceOk && !BadV(shipToLoad.Position) && !BadQ(shipToLoad.Rotation) && !BadV(shipToLoad.Velocity)) return;
                WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
                WorldTracking.Interface.Model.Spaces.Common.ISpace sp = null;
                if (spaceOk) spaces.TryGetSpace(shipToLoad.SpaceId, out sp);
                if (spaceOk && sp is WorldTracking.Interface.Model.Spaces.ShipRelativeSpace)
                {
                    // hangs on another ship (a rotor, a dock): any real place will do, its joint sets it on the first tick
                    SetProp(shipToLoad, "Position", Vector3.zero);
                    SetProp(shipToLoad, "Rotation", Quaternion.identity);
                    SetProp(shipToLoad, "Velocity", Vector3.zero);
                }
                else
                {
                    Vector3 local; Quaternion rot;
                    SetProp(shipToLoad, "SpaceId", StartSpot(40f + 15f * _placedAtStart++, out local, out rot));
                    SetProp(shipToLoad, "Position", local);
                    SetProp(shipToLoad, "Rotation", rot);
                    SetProp(shipToLoad, "Velocity", Vector3.zero);
                }
                _nanAtLoad.Add(shipToLoad.Id);
                Log.Warn("ships: ship #" + shipToLoad.Id + " was saved with no real place (NaN" + (spaceOk ? "" : ", in a space the save doesn't have") + "); given one to load into, and it goes to the ship bin once the world is up");
            }
            catch (Exception e) { Log.Warn("ships: could not check a saved ship: " + e.Message); }
        }

        /// <summary>Sets a property of a saved record (its setter may be non-public, or only a backing field).</summary>
        private static void SetProp(object rec, string prop, object value)
        {
            for (Type t = rec.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                MethodInfo setter = p != null ? p.GetSetMethod(true) : null;
                if (setter != null) { setter.Invoke(rec, new[] { value }); return; }
                FieldInfo f = t.GetField("<" + prop + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(rec, value); return; }
            }
            throw new MissingMemberException(rec.GetType().Name, prop);
        }

        /// <summary>Any NaN or infinite float/double anywhere in an object's fields (a space's middle, say).</summary>
        public static bool HasBadNumber(object o, int depth = 0)
        {
            if (o == null || depth > 4) return false;
            if (o is float f) return BadF(f);
            if (o is double d) return double.IsNaN(d) || double.IsInfinity(d);
            Type t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is UnityEngine.Object) return false;
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type ft = fi.FieldType;
                if (ft == typeof(float) || ft == typeof(double) || (ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum))
                    if (HasBadNumber(fi.GetValue(o), depth + 1)) return true;
            }
            return false;
        }

        public static int RotorsFixed, WallsSkipped, ShipsMoved;

        /// <summary>In every game (players' and hosts' too): the room grid never grows towards a NaN position.</summary>
        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Ships.Rooms.ShipRoomsVoxelsTree), "AddWall", new[] { typeof(Vector3Int) }),
                    prefix: new HarmonyMethod(typeof(Signals).GetMethod("AddWallPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("ship rooms: could not guard the room grid: " + e.Message); }
        }

        private static bool BadV(Vector3 v) { return Bad(v.x) || Bad(v.y) || Bad(v.z); }
        private static bool BadQ(Quaternion q) { return Bad(q.x) || Bad(q.y) || Bad(q.z) || Bad(q.w) || (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0); }

        /// <summary>
        /// Ships whose saved position, rotation or speed is NaN (from a NaN rotor, say) can't be placed
        /// anywhere real: each such assembly goes into the ship bin (its numbers cleaned, so `ship restore
        /// <id> here <player>` brings it back sound), and anyone who logged off standing in it starts at
        /// the world's starting point.
        /// </summary>
        public static readonly HashSet<uint> BinnedSpaces = new HashSet<uint>();

        private static void BinNaNShips()
        {
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
            Players.Services.PlayersServerTracker players = Core.Services.ServiceLocator.GetService<Players.Services.PlayersServerTracker>();
            if (t == null || spaces == null) return;
            HashSet<uint> roots = new HashSet<uint>();
            foreach (TrackedShipServer s in t.Ships)
            {
                Ships.Interface.Model.State.ShipPhysicsState ps = s.PhysicsState;
                if (BadV(ps.Position) || BadQ(ps.Rotation) || BadV(ps.Velocity) || _nanAtLoad.Contains(s.Id)) roots.Add(ShipAdmin.RootOf(s.Id));
            }
            _nanAtLoad.Clear();
            _placedAtStart = 0;
            if (roots.Count == 0) return;
            foreach (uint root in roots)
                foreach (Ships.Interface.Model.Joints.RootRelativeShipData d in t.GetRootConnectedShips(root))
                    if (d.Ship is TrackedShipServer sec) BinnedSpaces.Add(sec.RelativeSpaceId);
            // offline players standing in them start at the starting point instead
            if (players != null)
                foreach (Players.Interface.Model.PlayerStateSnapshot p in players.InactivePlayers.ToList())
                {
                    if (!BinnedSpaces.Contains(p.SpaceId) && !BadV(p.Position)) continue;
                    if (ToStart(spaces, p)) Log.Warn("ships: " + p.Name + " was saved standing on a ship with no real position; they start at the starting point");
                }
            foreach (uint root in roots)
            {
                string name = ShipAdmin.GroupName(root);
                string said = ShipAdmin.Delete(root, "the server (the ship's position was NaN)");
                ShipsMoved++;
                Log.Warn("ships: ship #" + root + (name.Length > 0 ? " \"" + name + "\"" : "") + " had no real position (NaN) in the save, so it can't be placed: " + said);
            }
        }

        /// <summary>A player (online or saved) to the world's starting point, as the game's own respawn does.</summary>
        public static bool ToStart(WorldTracking.Interface.Services.ISpacesServerProvider spaces, WorldTracking.Interface.Model.ITrackedWorldObjectServer who)
        {
            try
            {
                spaces.MoveWorldObject(who, Players.Utils.PlayerDefaultState.GetPlayerStartSpaceAsPosition());
                if (who is Players.Interface.Model.PlayerStateSnapshot snap)
                {
                    snap.Rotation = Players.Utils.PlayerDefaultState.DefaultRotation;
                    snap.CameraRotation = Players.Utils.PlayerDefaultState.DefaultCameraRot;
                    snap.Velocity = Vector3.zero;
                }
                return true;
            }
            catch (Exception e) { Log.Warn("ships: could not move a player to the starting point: " + e.Message); return false; }
        }

        /// <summary>As a player joins: one whose place is gone or not a number goes to the starting point (the game's own respawn).</summary>
        public static void RescueIfLost(Players.TrackedPlayerServer p)
        {
            try
            {
                WorldTracking.Interface.Services.ISpacesServerProvider spaces = Core.Services.ServiceLocator.GetService<WorldTracking.Interface.Services.ISpacesServerProvider>();
                WorldTracking.Interface.Model.Spaces.Common.ISpace sp;
                bool lost = BadV(p.Position) || BinnedSpaces.Contains(p.SpaceId) || spaces == null || !spaces.TryGetSpace(p.SpaceId, out sp);
                if (!lost) return;
                Players.Network.Services.PlayerRespawnService rs = UnityEngine.Object.FindFirstObjectByType<Players.Network.Services.PlayerRespawnService>();
                FishNet.Connection.NetworkConnection conn = Server.Connection(p.ConnectionId);
                if (rs == null || conn == null) return;
                AccessTools.Method(typeof(Players.Network.Services.PlayerRespawnService), "RespawnAtSurface").Invoke(rs, new object[] { conn });
                Log.Warn("ships: " + p.Name + " joined somewhere that doesn't exist (a ship with no real position); moved to the starting point");
            }
            catch (Exception e) { Log.Once("rescue", "ships: could not move a lost player: " + e.Message); }
        }

        /// <summary>Every NaN or infinite number in an object (a saved ship, say) set to 0; a rotation left all zeros becomes "no turn". Returns how many.</summary>
        public static int CleanFields(object o, int depth = 0)
        {
            if (o == null || depth > 8) return 0;
            Type t = o.GetType();
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum || o is UnityEngine.Object) return 0;
            int n = 0;
            if (o is Array arr)
            {
                Type et = t.GetElementType();
                if (et == typeof(float)) { for (int i = 0; i < arr.Length; i++) if (BadF((float)arr.GetValue(i))) { arr.SetValue(0f, i); n++; } return n; }
                if (et == typeof(double)) { for (int i = 0; i < arr.Length; i++) if (Bad((double)arr.GetValue(i))) { arr.SetValue(0.0, i); n++; } return n; }
                if (et.IsPrimitive || et == typeof(string) || et.IsEnum) return 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    object e = arr.GetValue(i);
                    int k = CleanFields(e, depth + 1);
                    if (k > 0 && et.IsValueType) arr.SetValue(e, i);
                    n += k;
                }
                return n;
            }
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                Type ft = f.FieldType;
                if (ft == typeof(float)) { if (BadF((float)f.GetValue(o))) { f.SetValue(o, 0f); n++; } continue; }
                if (ft == typeof(double)) { if (Bad((double)f.GetValue(o))) { f.SetValue(o, 0.0); n++; } continue; }
                if (ft.IsPrimitive || ft == typeof(string) || ft.IsEnum || typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;
                object v = f.GetValue(o);
                int k = CleanFields(v, depth + 1);
                if (k > 0 && ft.IsValueType) f.SetValue(o, v);
                n += k;
            }
            // x, y, z, w all 0 after cleaning: a rotation that was NaN
            if (n > 0 && t.IsValueType)
            {
                FieldInfo x = t.GetField("x"), y = t.GetField("y"), z = t.GetField("z"), w = t.GetField("w");
                if (x != null && y != null && z != null && w != null && x.FieldType == typeof(float)
                    && (float)x.GetValue(o) == 0 && (float)y.GetValue(o) == 0 && (float)z.GetValue(o) == 0 && (float)w.GetValue(o) == 0) w.SetValue(o, 1f);
            }
            return n;
        }

        private static bool BadF(float f) { return float.IsNaN(f) || float.IsInfinity(f); }

        // a rotor with a NaN angle is set to 0 before the game turns it (its target is a signal, already cleaned)
        private static void RotorsPrefix(object __instance)
        {
            if (!On) return;
            try
            {
                Ships.Interface.Model.State.IShipStateWrite ship = Traverse.Create(__instance).Field("_ship").GetValue<Ships.Interface.Model.State.IShipStateWrite>();
                if (ship == null) return;
                List<KeyValuePair<ushort, IStatefulPartState>> fixes = null;
                foreach (StatefulPart part in ship.GetAllStatefulParts<Ships.Interface.Model.Parts.State.RotorState>())
                {
                    IStatefulPartState clean = Clean(part.State);
                    if (clean != null) (fixes = fixes ?? new List<KeyValuePair<ushort, IStatefulPartState>>()).Add(new KeyValuePair<ushort, IStatefulPartState>(part.Id, clean));
                }
                if (fixes == null) return;
                foreach (KeyValuePair<ushort, IStatefulPartState> f in fixes)
                {
                    ship.UpdatePartState(f.Key, f.Value);
                    RotorsFixed++;
                    Log.Once("rotor-nan-" + ship.Id + "-" + f.Key, "signals: ship " + ship.Id + " rotor " + f.Key + " is fed NaN or infinity; it holds at 0 until its input is a number again");
                }
            }
            catch (Exception e) { Log.Once("rotor-guard", "signals: rotor guard: " + e.Message); }
        }

        // the room grid grows until a wall fits; a coordinate this far out never would (it came from NaN)
        private const int FarWall = 1 << 20;

        private static bool AddWallPrefix(Vector3Int wallCoord)
        {
            if (Mathf.Abs((long)wallCoord.x) < FarWall && Mathf.Abs((long)wallCoord.y) < FarWall && Mathf.Abs((long)wallCoord.z) < FarWall) return true;
            WallsSkipped++;
            if (WallsSkipped <= 5 || WallsSkipped % 1000 == 0)
                Log.Warn("ship rooms: skipped a wall at " + wallCoord + " (" + WallsSkipped + " so far): a ship part is somewhere impossible (a NaN position), which would make the game's room grid grow without end");
            return false;
        }

        /// <summary>The whole world at once (as it loads, before anyone joins): every part of every ship.</summary>
        public static void CleanWorld()
        {
            if (!On) return;
            int before = PartsFixed;
            Sweep(true);
            BinNaNShips();
            if (PartsFixed > before) Log.Warn("signals: " + (PartsFixed - before) + " part(s) in this world held NaN or infinite numbers; set to 0 before anyone joined");
        }

        private static bool On { get { return Server.Cfg.FixBadSignals.Value; } }

        /// <summary>Not a number, infinite, or too big for a float (every part turns its signal into a float, where that IS infinity).</summary>
        private static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v) || v > float.MaxValue || v < -float.MaxValue; }

        private static void GroupPostfix(SignalConnectionGroupValues __instance, uint groupId)
        {
            if (!On || _values == null) return;
            Dictionary<uint, double> d = _values(__instance);
            double v;
            if (d != null && d.TryGetValue(groupId, out v) && Bad(v))
            {
                d[groupId] = 0.0;
                GroupsFixed++;
                Log.Debug("signals", "cable group " + groupId + " was " + v + ", set to 0");
            }
        }

        private static void SocketPostfix(ref double __result)
        {
            if (On && Bad(__result)) __result = 0.0;
        }

        public static long ReadsFixed;

        private static void ReadPostfix(EntityPartSocket socket, ref double __result)
        {
            if (On && Bad(__result)) { __result = 0.0; ReadsFixed++; }
            LimitInput(socket, ref __result);
        }

        /// <summary>
        /// A pump, valve, docking door's magnet or warp drive multiplies by its signal with no upper limit (a pump at
        /// 1e30 moves its whole tank in a tick, a magnet at 1e30 flings ships, a warp drive past ~1e7 goes infinite).
        /// The game's own controls (lever, seats) give at most 1, so that is the most these parts get. Cables, displays
        /// and math parts still carry the full number. Setting: LimitPartInputs.
        /// </summary>
        private static void LimitInput(EntityPartSocket socket, ref double v)
        {
            if (!Server.Cfg.LimitPartInputs.Value || (v <= 1.0 && v >= -1.0)) return;
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            TrackedShipServer ship = t != null ? t.GetShip(socket.EntityId) : null;
            StatefulPart part;
            if (ship == null || !ship.TryGetStatefulPart(socket.Socket.PartId, out part)) return;
            IStatefulPartState st = part.State;
            if (st is Ships.Interface.Model.Parts.State.FluidPumpState || st is Ships.Interface.Model.Parts.State.FluidValveState
                || st is Ships.Interface.Model.Parts.State.DockingDoorState || st is Ships.Interface.Model.Parts.State.WarpDriveState)
            {
                v = v > 1.0 ? 1.0 : -1.0;
                InputsLimited++;
            }
        }

        /// <summary>Every frame on the server: once a second, every part of every ship.</summary>
        public static void Update()
        {
            if (!On || !Server.Ready || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            Sweep(false);
        }

        private static void Sweep(bool all)
        {
            ShipsServerTracker t = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (t == null) return;
            foreach (TrackedShipServer ship in t.Ships)
            {
                if (ship == null) continue;
                List<KeyValuePair<ushort, IStatefulPartState>> fixes = null;
                try
                {
                    foreach (StatefulPart part in ship.StatefulParts)
                    {
                        IStatefulPartState st = part.State;
                        if (st == null) continue;
                        IStatefulPartState clean = Clean(st);
                        if (clean != null) (fixes = fixes ?? new List<KeyValuePair<ushort, IStatefulPartState>>()).Add(new KeyValuePair<ushort, IStatefulPartState>(part.Id, clean));
                    }
                    if (fixes == null) continue;
                    foreach (KeyValuePair<ushort, IStatefulPartState> f in fixes)
                    {
                        ship.UpdatePartState(f.Key, f.Value);
                        PartsFixed++;
                        Log.Msg("signals: ship " + ship.Id + " part " + f.Key + " (" + f.Value.GetType().Name + ") held NaN or infinity; set to 0");
                    }
                }
                catch (Exception e) { Log.Once("signals-sweep", "signals: sweep failed on ship " + ship.Id + ": " + e.Message); }
            }
        }

        /// <summary>A copy of the state with every NaN or infinite number set to 0, or null if it had none.</summary>
        public static IStatefulPartState Clean(IStatefulPartState st)
        {
            Type type = st.GetType();
            PropertyInfo[] props;
            if (!_props.TryGetValue(type, out props))
            {
                List<PropertyInfo> l = new List<PropertyInfo>();
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                    Type pt = p.PropertyType;
                    if (pt == typeof(double) || pt == typeof(float) || pt == typeof(Vector3) || pt == typeof(Vector2) || pt == typeof(Core.Values.Vector3Double)) l.Add(p);
                }
                _props[type] = props = l.ToArray();
                _clone[type] = type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance);
            }
            if (props.Length == 0) return null;
            object copy = null;
            foreach (PropertyInfo p in props)
            {
                object v = p.GetValue(st, null);
                object fixedV = Fix(v);
                if (fixedV == null) continue;
                if (copy == null)
                {
                    MethodInfo clone = _clone[type];
                    if (clone == null) return null;
                    copy = clone.Invoke(st, null);
                }
                p.SetValue(copy, fixedV, null);
            }
            return copy as IStatefulPartState;
        }

        /// <summary>The value with bad numbers set to 0, or null if it was fine.</summary>
        private static object Fix(object v)
        {
            if (v is double d) return Bad(d) ? (object)0.0 : null;
            if (v is float f) return float.IsNaN(f) || float.IsInfinity(f) ? (object)0f : null;
            if (v is Vector3 a)
            {
                if (!Bad(a.x) && !Bad(a.y) && !Bad(a.z)) return null;
                return new Vector3(Bad(a.x) ? 0f : a.x, Bad(a.y) ? 0f : a.y, Bad(a.z) ? 0f : a.z);
            }
            if (v is Vector2 b)
            {
                if (!Bad(b.x) && !Bad(b.y)) return null;
                return new Vector2(Bad(b.x) ? 0f : b.x, Bad(b.y) ? 0f : b.y);
            }
            if (v is Core.Values.Vector3Double c)
            {
                if (!Bad(c.x) && !Bad(c.y) && !Bad(c.z)) return null;
                return new Core.Values.Vector3Double(Bad(c.x) ? 0.0 : c.x, Bad(c.y) ? 0.0 : c.y, Bad(c.z) ? 0.0 : c.z);
            }
            return null;
        }
    }
}
