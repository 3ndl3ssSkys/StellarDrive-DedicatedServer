// For the tests only (a server started by the verify harness, or the probe in a single-player
// game): `testship [speed]` builds a small deck ship 12 m in front of the first player, 6 m up, and
// flies it sideways at a steady speed, turning back every 6 s, by setting its rigidbody's velocity
// just before each physics step. Every server tick its true position is written with the
// machine-wide high-resolution clock (QueryPerformanceCounter, the same in every process on this
// PC) to <data>/shiptrace.csv, so a client on the same PC can measure how far behind, and how
// smoothly, it draws the ship.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Players;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static class ShipTest
    {
        public static TrackedShipServer Ship;
        private static Vector3 _dir, _up;
        private static float _speed, _t0;
        private static bool _onGround;      // `testship ground`: slide along the ground instead of hovering
        public static string Mode = "";     // "park": left alone; "ram": driven into the ground
        public static int _size = 3;         // floor tiles along each side
        private static float _ramAt;

        public static string Ram(float speed)
        {
            if (Ship == null) return "no test ship";
            Mode = "ram"; _speed = speed; _ramAt = Time.realtimeSinceStartup;
            return "ramming test ship #" + Ship.Id + " into the ground at " + speed + " m/s, then thrusting into it";
        }
        private static StreamWriter _trace;
        private static bool _patched;
        public static string TracePath;

        // Windows' own counter, the same in every process on the PC. (Mono's Stopwatch counts from
        // when the process started, so a server's and a client's times could not be compared.)
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long count);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long freq);
        private static long _qpf;

        public static double NowMs()
        {
            if (_qpf == 0) QueryPerformanceFrequency(out _qpf);
            long c; QueryPerformanceCounter(out c);
            return c * 1000.0 / _qpf;
        }

        public static string Spawn(float speed, string traceDir, bool onGround = false)
        {
            _onGround = onGround;
            TrackedPlayerServer me = Server.ActivePlayers().FirstOrDefault();
            if (me == null) return "nobody is on to put a ship next to";
            if (!_patched)
            {
                _patched = true;
                HarmonyLib.Harmony h = new HarmonyLib.Harmony("DedicatedServer.shiptest");
                h.Patch(AccessTools.Method(typeof(Managers.Server.Updaters.PhysicsScenesServerUpdater), "SimulatePhysics"),
                    prefix: new HarmonyMethod(typeof(ShipTest).GetMethod("DrivePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PostPhysics"),
                    postfix: new HarmonyMethod(typeof(ShipTest).GetMethod("TracePostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            ShipCreationService creator = Core.Services.ServiceLocator.GetService<ShipCreationService>();
            ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            HullPartSettings floor = new PartSettingsProvider().GetAllPartSettings().FirstOrDefault(p => p.name == "IronFloor") as HullPartSettings;
            if (creator == null || ships == null || floor == null) return "the game's ship services aren't there";
            uint space = me.SpaceId;
            Vector3 pos = me.Position;
            Quaternion rot = me.Rotation;
            Vector3 g = Vector3.zero;
            try { g = Core.Services.ServiceLocator.GetService<WorldProvision.Interface.Services.IWorldEnvironmentServerProvider>().GetGravityAtPosition(space, pos); } catch { }
            Vector3 up = g.sqrMagnitude > 0.01f ? -g.normalized : rot * Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(rot * Vector3.forward, up).normalized;
            if (fwd.sqrMagnitude < 0.5f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            Vector3 at = onGround ? pos + fwd * 6f + up * 2.5f : pos + fwd * 12f + up * 6f;
            HullPartPlacement first = new HullPartPlacement(new HullPartPosition { Type = VoxelPartType.Floor, VoxelCoord = Vector3Int.zero }, PartOrientation.Up, PartOrientation.Forward);
            uint id = creator.CreateShip(new HullPart(floor, first), new WorldTransform { SpaceId = space, Position = at, Rotation = Quaternion.LookRotation(fwd, up) });
            Ship = ships.GetShip(id);
            for (int x = 0; x < (_size); x++)
                for (int z = 0; z < (_size); z++)
                    if (x != 0 || z != 0)
                        Ship.AddHullPart(floor, new HullPartPlacement(new HullPartPosition { Type = VoxelPartType.Floor, VoxelCoord = new Vector3Int(x, 0, z) }, PartOrientation.Up, PartOrientation.Forward));
            _dir = onGround ? fwd : Vector3.Cross(up, fwd).normalized;
            _up = up;
            _speed = speed;
            _t0 = Time.realtimeSinceStartup;
            if (_trace != null) _trace.Close();
            TracePath = Path.Combine(traceDir, "shiptrace.csv");
            _trace = new StreamWriter(TracePath, false) { AutoFlush = true };
            _trace.WriteLine("ms,space,x,y,z");
            return "test ship #" + id + (onGround ? " sliding along the ground at " + speed + " m/s" : " flying at " + speed + " m/s, turning every 6 s");
        }

        public static void StopTest()
        {
            if (_trace != null) { _trace.Close(); _trace = null; }
            Ship = null;
        }

        private static void DrivePrefix()
        {
            if (Ship == null || !Ship.IsRigidbodyLoaded) return;
            try
            {
                Rigidbody rb = Ship.RigidbodyObject != null ? Ship.RigidbodyObject.GetComponent<Rigidbody>() : null;
                if (rb == null || rb.isKinematic) return;
                if (Mode == "park") return;                       // left alone: gravity, the ground and the players on it
                if (Mode == "ram")
                {
                    // flown into the ground: a hard dive for a second, then three seconds of full thrust into it
                    float r = Time.realtimeSinceStartup - _ramAt;
                    if (r < 1f) rb.linearVelocity = -_up * _speed + _dir * 4f;
                    else if (r < 4f) rb.AddForce(-_up * rb.mass * 60f, ForceMode.Force);
                    return;
                }
                rb.constraints = RigidbodyConstraints.FreezeRotation;
                float t = Time.realtimeSinceStartup - _t0;
                if (_onGround)
                {
                    // straight on, at a steady speed over the ground; gravity and the ground decide the height
                    Vector3 v = rb.linearVelocity;
                    rb.linearVelocity = _dir * _speed + _up * Vector3.Dot(v, _up);
                    rb.angularVelocity = Vector3.zero;
                    return;
                }
                float sign = ((int)(t / 6f)) % 2 == 0 ? 1f : -1f;
                rb.linearVelocity = _dir * _speed * sign;
                rb.angularVelocity = Vector3.zero;
            }
            catch (Exception e) { Log.Once("shiptest", "test ship: " + e.Message); }
        }

        private static void TracePostfix()
        {
            if (Ship == null || _trace == null) return;
            try
            {
                Vector3 p = Ship.Position;
                _trace.WriteLine(NowMs().ToString("0.0") + "," + Ship.SpaceId + "," + p.x.ToString("0.0000") + "," + p.y.ToString("0.0000") + "," + p.z.ToString("0.0000"));
            }
            catch { }
        }
    }
}
