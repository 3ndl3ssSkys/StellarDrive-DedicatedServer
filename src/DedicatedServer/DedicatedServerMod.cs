// DedicatedServer - run a StellarDrive Demo world as a dedicated server, and join servers by address.
//
// One DLL for both sides:
//   * Started with -dedicated (see README), the game becomes a server: no menu, no player, no
//     rendering, a console for commands, operators, bans/whitelist, activity and uptime
//     tracking, autosave, and a mod check on everyone who joins.
//   * In an ordinary game it adds "Join by address" to the Join menu and answers the
//     server's mod check. A player needs it to join a dedicated server.
using System;
using System.Collections;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DedicatedServer.DedicatedServerMod), "DedicatedServer", DedicatedServer.DedicatedServerMod.Version, "EndlessSkys")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace DedicatedServer
{
    public class DedicatedServerMod : MelonMod
    {
        public const string Version = "1.4.2";

        public static bool IsServer { get; private set; }

        public override void OnInitializeMelon()
        {
            Log.Logger = LoggerInstance;
            string[] args = Environment.GetCommandLineArgs();
            IsServer = Array.Exists(args, a => string.Equals(a, "-dedicated", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--dedicated", StringComparison.OrdinalIgnoreCase));
            MelonPreferences_Category cat = MelonPreferences.CreateCategory("DedicatedServer", "Dedicated Server (joining)");
            Client.Init(HarmonyInstance, cat);
            if (IsServer)
            {
                Server.Init(HarmonyInstance);
                InputRelay.InitServer();
#if DSTEST
                Test.InitServer();
#endif
            }
            else
            {
                ClientUi.Init(HarmonyInstance);
                Log.SetDebug(Client.DebugLog.Value);
                Markers.InitClient(HarmonyInstance);
                NetHealth.InitClient(HarmonyInstance);
                Overlay.InitClient(HarmonyInstance);
                JoinWatch.InitClient(HarmonyInstance);
                MenuFix.InitClient(HarmonyInstance);
                AdminItems.InitClient(HarmonyInstance);
                Net.OnEvent = OnServerEvent;
#if DSTEST
                Probe.InitClient(HarmonyInstance);
#endif
                InputRelay.InitClient(HarmonyInstance);
            }
            LoggerInstance.Msg(IsServer ? "ready - this game is a DEDICATED SERVER" : "ready - 'Join by address' is on the Join menu");
        }

        /// <summary>What the server tells this player (DsEvent), by kind.</summary>
        private static void OnServerEvent(DsEvent e)
        {
            switch (e.Kind)
            {
                case "respawn": RespawnVisuals.OnEvent(e); break;
                case "announce": Overlay.OnEvent(e); break;
                case "ping": Overlay.OnPing(e); break;
                case "markers": Markers.OnEvent(e); break;
                default: Log.Debug("net", "unknown server message '" + e.Kind + "' (a newer server?)"); break;
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (IsServer) Server.OnSceneLoaded(sceneName);
            else Client.OnSceneLoaded(sceneName);
        }

        public override void OnUpdate()
        {
            Net.Update();
            if (IsServer) Server.Update();
            else Client.Update();
        }

        public override void OnLateUpdate()
        {
            if (!IsServer) { try { JoinWatch.LateUpdate(); } catch { } }
        }

        public override void OnApplicationQuit()
        {
            if (IsServer) Server.OnQuit();
        }

#if DSTEST
        // ---- the verify harness's entry points (test builds only) ---------------------------

        /// <summary>DemoVerifyHarness, SDD_VERIFY_SAVE=join:&lt;address&gt;: start the test server if asked, then join it.</summary>
        public static IEnumerator VerifyJoin(string address, Action<string> note)
        {
            return Probe.Join(address, note);
        }

        public static IEnumerator VerifyProbe(string probe, Action<string, bool, string> check, Action<string> note)
        {
            return Probe.Run(probe, check, note);
        }
#endif
    }
}
