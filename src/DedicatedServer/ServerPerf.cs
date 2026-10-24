// How busy the server is.
//   * Work per loop: the time from the start of the frame's work (after Unity's wait for the
//     frame-rate cap) to the end of the frame. The loop *interval* includes that wait; the work
//     doesn't, so this is what's left before the server can't keep up.
//   * `profile [seconds]`: times every script's Update / LateUpdate / FixedUpdate for a few
//     seconds (Harmony, removed again afterwards) and lists where the time goes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace DedicatedServer
{
    internal static class ServerPerf
    {
        private struct DsFrameStart { }
        private struct DsFrameEnd { }

        private static long _start;
        private static int _n;
        private static double _sum, _max;
        public static double AvgWorkMs, MaxWorkMs;
        public static bool LoopTimerOn;

        // One timestamp at the start of each of Unity's top-level frame phases, one at the very end.
        // A stall (a frame over 250 ms) is then reported by where its time went.
        private static Type[] _phaseTypes = new Type[0];
        private static long[] _phaseAt = new long[0];
        private static long _prevEnd;
        public static int Stalls;
        public static double LongestStallMs;
        public static string LastStall = "";

        private class PhaseMark { public int I; public void Mark() { _phaseAt[I] = Stopwatch.GetTimestamp(); CurrentPhase = I; if (I == _workPhase) _start = _phaseAt[I]; } }

        // where the main thread is, for the watchdog (another thread) when the loop stops
        public static volatile int CurrentPhase = -1;
        private static readonly string[] _inside = new string[24];      // the watched methods the main thread is in, outermost first
        private static volatile int _depth;

        public static string Where()
        {
            int p = CurrentPhase;
            string phase = p >= 0 && p < _phaseTypes.Length ? _phaseTypes[p].Name : "?";
            int d = Math.Min(_depth, _inside.Length);
            string inside = d > 0 ? string.Join(" > ", _inside.Take(d).ToArray()) : "";
            return "frame phase " + phase + (inside.Length > 0 ? ", inside " + inside : "") + ", last server step: " + Server.Step;
        }
        private static int _workPhase = -1;

        public static void InstallLoopTimer()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                _phaseTypes = root.subSystemList.Select(x => x.type).ToArray();
                _phaseAt = new long[_phaseTypes.Length];
                for (int i = 0; i < root.subSystemList.Length; i++)
                {
                    PlayerLoopSystem s = root.subSystemList[i];
                    List<PlayerLoopSystem> subs = (s.subSystemList ?? new PlayerLoopSystem[0]).ToList();
                    PhaseMark m = new PhaseMark { I = i };
                    subs.Insert(0, new PlayerLoopSystem { type = typeof(DsFrameStart), updateDelegate = m.Mark });
                    if (s.type == typeof(EarlyUpdate)) _workPhase = i;
                    if (s.type == typeof(PostLateUpdate)) subs.Add(new PlayerLoopSystem { type = typeof(DsFrameEnd), updateDelegate = FrameEnd });
                    s.subSystemList = subs.ToArray();
                    root.subSystemList[i] = s;
                }
                PlayerLoop.SetPlayerLoop(root);
                LoopTimerOn = true;
            }
            catch (Exception e) { Log.Warn("could not time the server loop: " + e.Message); }
        }

        private static void FrameEnd()
        {
            long end = Stopwatch.GetTimestamp();
            double f = 1000.0 / Stopwatch.Frequency;
            if (_start != 0)
            {
                double ms = (end - _start) * f;
                _n++; _sum += ms; if (ms > _max) _max = ms;
            }
            if (_prevEnd != 0 && Server.Ready)
            {
                double frame = (end - _prevEnd) * f;
                if (frame > 250)
                {
                    List<KeyValuePair<string, double>> parts = new List<KeyValuePair<string, double>>();
                    long first = _phaseAt.Length > 0 ? _phaseAt[0] : end;
                    parts.Add(new KeyValuePair<string, double>("between frames (frame-rate wait / graphics)", (first - _prevEnd) * f));
                    for (int i = 0; i < _phaseAt.Length; i++)
                    {
                        long next = i + 1 < _phaseAt.Length ? _phaseAt[i + 1] : end;
                        if (_phaseAt[i] == 0 || next < _phaseAt[i]) continue;
                        parts.Add(new KeyValuePair<string, double>(_phaseTypes[i].Name, (next - _phaseAt[i]) * f));
                    }
                    string top = string.Join(", ", parts.Where(x => x.Value >= 5).OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value.ToString("0") + " ms").ToArray());
                    string who = string.Join(", ", _frameCalls.Where(x => x.Value >= 5).OrderByDescending(x => x.Value).Take(4).Select(x => Name(x.Key) + " " + x.Value.ToString("0") + " ms").ToArray());
                    Stalls++;
                    if (frame > LongestStallMs) LongestStallMs = frame;
                    LastStall = frame.ToString("0") + " ms: " + top + (who.Length > 0 ? "; in: " + who : "");
                    Log.Warn("the server stalled for " + LastStall);
                    if (frame >= 500) try { Server.Data.Freeze(frame); } catch { }
                }
            }
            _prevEnd = end;
            _frameCalls.Clear();
        }

        // ---- always-on timing of the likely culprits: each mod's updates and the server tick ----

        private static readonly Dictionary<MethodBase, double> _frameCalls = new Dictionary<MethodBase, double>();

        public static void WatchCulprits(HarmonyLib.Harmony h)
        {
            HarmonyMethod pre = new HarmonyMethod(typeof(ServerPerf).GetMethod("WPre", BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod post = new HarmonyMethod(typeof(ServerPerf).GetMethod("WPost", BindingFlags.NonPublic | BindingFlags.Static));
            int n = 0;
            List<MethodInfo> ms = new List<MethodInfo>();
            foreach (MelonMod mod in MelonMod.RegisteredMelons)
            {
                if (mod == null || mod.GetType().Assembly == typeof(ServerPerf).Assembly) continue;
                foreach (string name in new[] { "OnUpdate", "OnLateUpdate", "OnFixedUpdate", "OnGUI" })
                {
                    MethodInfo m = mod.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    if (m != null) ms.Add(m);
                }
            }
            ms.Add(AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PrePhysics"));
            ms.Add(AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PostPhysics"));
            // each step of the server's physics tick, so a hang in it names the step
            Type wp = typeof(Managers.Server.WorldPhysicsServerUpdater);
            foreach (string step in new[] { "UpdatePlayers", "UpdateLoadedShips", "UpdateLoadedWorldObjects", "UpdateLoadedPlanetColliders", "UpdateShipParts",
                                            "UpdateShipsPhysics", "UpdateWorldObjectsPhysics", "UpdatePlayersPhysics", "ShipPostPhysics", "ObjectsPostPhysics" })
                ms.Add(AccessTools.Method(wp, step));
            foreach (string[] fm in new[] { new[] { "_shipsJoints", "UpdateJoints" }, new[] { "_spacesUpdater", "UpdateSpaces" }, new[] { "_scenes", "PreparePhysicsScenes" }, new[] { "_scenes", "SimulatePhysics" } })
            {
                FieldInfo f = AccessTools.Field(wp, fm[0]);
                if (f != null) ms.Add(AccessTools.Method(f.FieldType, fm[1]));
            }
            ms.Add(AccessTools.Method(AccessTools.TypeByName("FishNet.Transporting.NetworkReaderLoop"), "Update"));
            ms.Add(AccessTools.Method(AccessTools.TypeByName("FishNet.Transporting.NetworkWriterLoop"), "LateUpdate"));
            ms.Add(AccessTools.Method(typeof(Saving.Services.GameSaver), "SaveGame", new[] { typeof(Saving.Interface.Model.SaveGameParams) }));
            foreach (MethodInfo m in ms)
            {
                if (m == null) continue;
                try { h.Patch(m, pre, post); n++; } catch (Exception e) { Log.Warn("could not watch " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
            }
            Log.Msg("watching " + n + " update methods for stalls");
        }

        private static void WPre(out long __state, MethodBase __originalMethod)
        {
            __state = Stopwatch.GetTimestamp();
            int d = _depth;
            if (d < _inside.Length) _inside[d] = Name(__originalMethod);
            _depth = d + 1;
        }

        private static void WPost(long __state, MethodBase __originalMethod)
        {
            if (_depth > 0) _depth--;
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            double v; _frameCalls.TryGetValue(__originalMethod, out v); _frameCalls[__originalMethod] = v + ms;
        }

        private static string Name(MethodBase m) { return m.DeclaringType.Name + "." + m.Name; }

        /// <summary>Every status interval.</summary>
        public static void Roll()
        {
            AvgWorkMs = _n > 0 ? _sum / _n : 0;
            MaxWorkMs = _max;
            _n = 0; _sum = 0; _max = 0;
        }

        // ---- profile -------------------------------------------------------------------------

        private static bool _profiling;
        private static readonly Dictionary<MethodBase, double> _ms = new Dictionary<MethodBase, double>();
        private static readonly Dictionary<MethodBase, int> _calls = new Dictionary<MethodBase, int>();

        public static void Profile(float seconds, Caller c)
        {
            if (_profiling) { c.Reply("A profile is already running."); return; }
            MelonCoroutines.Start(ProfileRun(Mathf.Clamp(seconds, 1f, 60f), c));
        }

        private static IEnumerator ProfileRun(float seconds, Caller c)
        {
            _profiling = true;
            _ms.Clear(); _calls.Clear();
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("DedicatedServer.profile");
            List<MethodInfo> patched = new List<MethodInfo>();
            HarmonyMethod pre = new HarmonyMethod(typeof(ServerPerf).GetMethod("Pre", BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod post = new HarmonyMethod(typeof(ServerPerf).GetMethod("Post", BindingFlags.NonPublic | BindingFlags.Static));
            HashSet<Type> types = new HashSet<Type>();
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                if (b != null && b.isActiveAndEnabled) types.Add(b.GetType());
            foreach (Type t in types)
                for (Type k = t; k != null && k != typeof(MonoBehaviour); k = k.BaseType)
                    foreach (string name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    {
                        MethodInfo m = k.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m == null || m.IsAbstract || patched.Contains(m) || m.DeclaringType.Assembly == typeof(ServerPerf).Assembly) continue;
                        try { h.Patch(m, pre, post); patched.Add(m); } catch { }
                    }
            // also the server tick and the FishNet loop, which aren't MonoBehaviour updates
            foreach (MethodInfo m in new[] {
                AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PrePhysics"),
                AccessTools.Method(typeof(Managers.Server.WorldPhysicsServerUpdater), "PostPhysics") })
                if (m != null) { try { h.Patch(m, pre, post); patched.Add(m); } catch { } }
            int frames0 = Time.frameCount;
            float until = Time.realtimeSinceStartup + seconds;
            c.Reply("Profiling " + patched.Count + " update methods for " + seconds.ToString("0") + " s ...");
            while (Time.realtimeSinceStartup < until) yield return null;
            int frames = Math.Max(1, Time.frameCount - frames0);
            try { h.UnpatchSelf(); } catch (Exception e) { Log.Warn("profile unpatch: " + e.Message); }
            _profiling = false;
            double total = _ms.Values.Sum();
            c.Reply("Profile over " + frames + " frames: timed scripts took " + (total / frames).ToString("0.000") + " ms per frame; server work per frame "
                    + AvgWorkMs.ToString("0.00") + " ms avg. Top:");
            foreach (var kv in _ms.OrderByDescending(k => k.Value).Take(15))
                c.Reply("  " + (kv.Value / frames).ToString("0.000").PadLeft(8) + " ms  " + kv.Key.DeclaringType.FullName + "." + kv.Key.Name
                        + "  (" + (_calls[kv.Key] / (double)frames).ToString("0.#") + "/frame)");
        }

        // ---- trace: per frame, each player's queue of movement inputs waiting on the server ----
        // The server moves a player one input per tick from this queue (the game caps it at 50,
        // one second). A queue that stays long is delay the server adds to that player.

        private static float _traceUntil = -1f;
        private static System.IO.StreamWriter _trace;
        private static readonly Dictionary<string, int> _traceMax = new Dictionary<string, int>();
        private static readonly Dictionary<string, double> _traceSum = new Dictionary<string, double>();
        private static int _traceFrames;

        public static void Trace(float seconds, Caller c)
        {
            if (_trace != null) { c.Reply("A trace is already running."); return; }
            string path = System.IO.Path.Combine(Server.DataDir, "trace.csv");
            _trace = new System.IO.StreamWriter(path, false);
            _trace.WriteLine("time,tick,player,bufferedInputs,x,y,z");
            _traceUntil = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 1f, 600f);
            _traceMax.Clear(); _traceSum.Clear(); _traceFrames = 0;
            _traceCaller = c;
            c.Reply("Tracing players' input queues for " + seconds.ToString("0") + " s into " + path);
        }

        private static Caller _traceCaller;

        public static void TraceUpdate()
        {
            if (_trace == null) return;
            _traceFrames++;
            FishNet.Managing.NetworkManager nm = FishNet.InstanceFinder.NetworkManager;
            uint tick = nm != null ? nm.TimeManager.Tick : 0;
            foreach (Players.TrackedPlayerServer p in Server.ActivePlayers())
            {
                int n = BufferedInputs(p);
                Vector3 pos = p.PhysicsState.Position;
                _trace.WriteLine(Time.realtimeSinceStartup.ToString("0.000") + "," + tick + "," + p.Name + "," + n + "," + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00") + "," + pos.z.ToString("0.00"));
                int m; _traceMax.TryGetValue(p.Name, out m); if (n > m) _traceMax[p.Name] = n;
                double s; _traceSum.TryGetValue(p.Name, out s); _traceSum[p.Name] = s + n;
            }
            if (Time.realtimeSinceStartup < _traceUntil) return;
            _trace.Close(); _trace = null;
            foreach (string name in _traceSum.Keys)
                _traceCaller.Reply("Trace: " + name + " had " + (_traceSum[name] / Math.Max(1, _traceFrames)).ToString("0.0") + " inputs queued on average, " + _traceMax[name]
                                   + " at most (each is " + (nm != null ? (1000.0 / nm.TimeManager.TickRate).ToString("0") : "?") + " ms of delay).");
            if (_traceSum.Count == 0) _traceCaller.Reply("Trace: nobody was on.");
        }

        public static int BufferedInputs(Players.TrackedPlayerServer p)
        {
            try
            {
                Players.Network.PlayerNetworkState ns = Traverse.Create(p).Property("NetworkState").GetValue<Players.Network.PlayerNetworkState>();
                Players.Network.PlayerNetworkInputs inp = ns != null ? ns.GetComponent<Players.Network.PlayerNetworkInputs>() : null;
                return inp != null ? inp.CurrentBufferedInputCount : -1;
            }
            catch { return -1; }
        }

        private static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        private static void Post(long __state, MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            double v; _ms.TryGetValue(__originalMethod, out v); _ms[__originalMethod] = v + ms;
            int n; _calls.TryGetValue(__originalMethod, out n); _calls[__originalMethod] = n + 1;
        }
    }
}
