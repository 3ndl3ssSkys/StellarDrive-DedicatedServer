// Sleeping: oxygen through pipes and vents (from NecessaryFixes, which found and tested it).
//
// When every player is in a bed, the server's physics step (WorldPhysicsServerUpdater.PrePhysics) speeds
// the world clock up to 50x and skips everything but the players: no ship parts, no cables, no physics.
// The players are still updated with the sped-up time, so a sleeper keeps breathing their room's air - but
// nothing refills it: vents, pipes, tanks, pumps, electrolyzers and generators are all in the skipped
// UpdateShipParts. The room runs dry and they wake up choking.
//
// This runs that skipped part step while everyone sleeps, with the same sped-up time the sleepers get, cut
// into pieces no longer than SleepMaxStep. Physics stays off (the game's choice: ships don't fly about while
// you sleep). An empty server counts as "all asleep" to the game too; it is left as the game (and
// PauseWhenEmpty) has it. Setting: SleepAirFlow. With NecessaryFixes loaded, it does this instead.
using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Managers.Server;
using Players.Interface.Services;
using Players.Services;
using UnityEngine;
using WorldTracking.Services;

namespace DedicatedServer
{
    internal static class Sleep
    {
        private static AccessTools.FieldRef<WorldPhysicsServerUpdater, IPlayersWaitingServerProvider> _waiting;
        private static AccessTools.FieldRef<WorldPhysicsServerUpdater, WorldTimeServerTracker> _time;
        private static AccessTools.FieldRef<WorldPhysicsServerUpdater, PlayersServerTracker> _players;
        private static Action<WorldPhysicsServerUpdater, float> _updateShipParts;

        public static long Ticks, Steps;
        public static double SimulatedSeconds, LastSpeed, CostMs, WorstTickMs;
        private static bool _logged;

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                _waiting = AccessTools.FieldRefAccess<WorldPhysicsServerUpdater, IPlayersWaitingServerProvider>("_playersWaitingProvider");
                _time = AccessTools.FieldRefAccess<WorldPhysicsServerUpdater, WorldTimeServerTracker>("_worldTime");
                _players = AccessTools.FieldRefAccess<WorldPhysicsServerUpdater, PlayersServerTracker>("_playersServerTracker");
                _updateShipParts = AccessTools.MethodDelegate<Action<WorldPhysicsServerUpdater, float>>(AccessTools.Method(typeof(WorldPhysicsServerUpdater), "UpdateShipParts"));
                h.Patch(AccessTools.Method(typeof(WorldPhysicsServerUpdater), "PrePhysics"),
                    postfix: new HarmonyMethod(typeof(Sleep).GetMethod("PrePhysicsPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("sleeping: could not keep ship parts running while everyone sleeps: " + e.Message); }
        }

        private static void PrePhysicsPostfix(WorldPhysicsServerUpdater __instance, float deltaTime)
        {
            if (!Server.Cfg.SleepAirFlow.Value) return;
            IPlayersWaitingServerProvider waiting = _waiting(__instance);
            PlayersServerTracker players = _players(__instance);
            if (waiting == null || players == null || players.ActivePlayers.Count == 0 || !waiting.AreAllPlayersWaiting()) return;
            WorldTimeServerTracker time = _time(__instance);
            double speed = time != null ? time.CurrentTimeSpeed : 1.0;
            if (double.IsNaN(speed) || speed < 1.0) speed = 1.0;
            float total = (float)(deltaTime * speed);
            float max = Mathf.Clamp(Server.Cfg.SleepMaxStepMs.Value / 1000f, 0.02f, 1f);
            int n = Mathf.Max(1, Mathf.CeilToInt(total / max - 1e-4f));
            float step = total / n;
            long t0 = Stopwatch.GetTimestamp();
            Server.Step = "sleeping: ship parts";
            for (int i = 0; i < n; i++) _updateShipParts(__instance, step);
            Server.Step = "frame";
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Ticks++; Steps += n; SimulatedSeconds += total; LastSpeed = speed; CostMs += ms;
            if (ms > WorstTickMs) WorstTickMs = ms;
            if (!_logged) { _logged = true; Log.Msg("sleeping: pipes, vents and ship parts keep running while everyone sleeps (at " + speed.ToString("0.0") + "x now)"); }
        }
    }
}
