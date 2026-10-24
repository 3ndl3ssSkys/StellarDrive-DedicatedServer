// The server half. Only ever active in a process started with -dedicated.
//
// Boot: the main menu is skipped. The world is loaded the way the game's own Host button
// loads one (MainGameSceneLoadData + the MainGame scene), and the server is started on the
// game's own IP transport (FishNet's Tugboat, the one the developers' Test_Ip mode uses) with
// NO local client: nobody plays in this process, so there is no player, camera or HUD here.
// Players join with the normal client path. The game already runs every server system
// (physics ticks, ships, research, chat, autosave) on the server side, so the server here is
// the same code a player's own hosted game runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.Tugboat;
using HarmonyLib;
using Managers.Interface;
using Managers.Interface.Model;
using Managers.Server.Services;
using MelonLoader;
using Newtonsoft.Json.Linq;
using Players;
using Players.Services;
using Saving.Interface.Model;
using Saving.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using WorldTracking.Services;
using WorldTracking.Services.Model;

namespace DedicatedServer
{
    internal static class Server
    {
        public static string DataDir, WorldPath;
        public static ServerConfig Cfg;
        public static ServerData Data;
        public static int? PortOverride, MaxPlayersOverride;
        public static string WorldOverride;

        public static bool WorldLoaded, Ready, Stopping;
        public static DateTime? LastSaveUtc;
        public static int Saves;
        public static string LastSaveNote = "";

        private static int _boot;                 // 0 menu, 1 MainGame requested, 2 server started
        private static float _nextBeat, _nextStatus, _nextSweep, _nextInbox, _nextAutoSave;
        private static float _saveAt = -1f;       // a save asked for, done at this time (debounces leaves)
        private static bool _saveAuto;
        private static int _camerasOff;
        private static readonly StringBuilder _consoleOut = new StringBuilder();

        // frame-time statistics over the last status interval
        private static int _frames;
        private static double _frameSum, _frameMax;
        public static double AvgFrameMs, MaxFrameMs;
        public static int Stalls { get { return ServerPerf.Stalls; } }
        public static double LongestStallMs { get { return ServerPerf.LongestStallMs; } }

        // ---- setup ---------------------------------------------------------------------------

        public static int Port { get { return PortOverride ?? Cfg.Port.Value; } }
        public static int MaxPlayers { get { return MaxPlayersOverride ?? Cfg.MaxPlayers.Value; } }

        public static void Init(HarmonyLib.Harmony h)
        {
            string[] args = Environment.GetCommandLineArgs();
            DataDir = Arg(args, "-dsdata") ?? Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer");
            Directory.CreateDirectory(DataDir);
            Cfg = ServerConfig.Load(DataDir);
            int n;
            if (int.TryParse(Arg(args, "-port") ?? "", out n) && n > 0 && n < 65536) PortOverride = n;
            if (int.TryParse(Arg(args, "-maxplayers") ?? "", out n) && n > 0) MaxPlayersOverride = n;
            WorldOverride = Arg(args, "-world");
            Data = new ServerData(DataDir);

            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Cfg.FrameRate.Value;

            _harmony = h;
            Patch(h, typeof(SteamManager), "Awake", prefix: "SteamAwakePrefix");
            Patch(h, typeof(Managers.Common.MainGameSceneInitializer), "Start", postfix: "InitializerStartPostfix");
            Patch(h, typeof(Managers.Server.Saving.ServerStateLoader), "LoadWorldFromSavedGame", postfix: "WorldLoadedPostfix");
            Patch(h, typeof(GameAutoSaver), "UpdateAutoSave", prefix: "GameAutoSavePrefix");
            Patch(h, typeof(GameSaver), "SaveScreenshot", prefix: "SaveScreenshotPrefix");
            Patch(h, typeof(WorldTimeServerTracker), "UpdateFixedTime", prefix: "TimePrefix", postfix: "TimePostfix");
            Patch(h, typeof(ServerErrorLogsChatNotifier), "HandleLog", prefix: "ErrorsToChatPrefix");
            Patch(h, typeof(ChatMessageBroadcaster), "ServerReceiveChatMessage", prefix: "ChatPrefix");
            Patch(h, typeof(ChatMessageBroadcaster), "ServerSendChatMessage", prefix: "GameNoticePrefix");
            Patch(h, typeof(PlayersServerTracker), "ServerRegisterPlayer", postfix: "PlayerJoinedPostfix");
            Patch(h, typeof(PlayersServerTracker), "ServerUnregisterPlayer", postfix: "PlayerLeftPostfix");
            Patch(h, typeof(Ships.Services.ShipPartsThumbnailRuntimeCache), "Update", prefix: "SkipOnServer");
            Patch(h, typeof(Managers.Client.Services.MainMenuCameraUpdater), "Update", prefix: "SkipOnServer");       // the menu's galaxy view
            // MelonLoader reloads MelonPreferences.cfg whenever the file changes - on a background
            // thread, pushing new values into every mod while the game runs. A server started from
            // the same folder as a player's game would reload every time that game saves its
            // settings. The server's own settings are in server.cfg; MelonPreferences.cfg is read once.
            Patch(h, AccessTools.TypeByName("MelonLoader.Preferences.IO.Watcher"), "OnFileWatcherTriggered", prefix: "SkipOnServer");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                Log.Warn("this server has a graphics device: start it with -nographics (StartDedicatedServer.bat does) so it never waits on the GPU a game on this PC is using");
            Patch(h, typeof(Managers.Client.Services.ClientWorldVisualsUpdater), "UpdateWorldVisuals", prefix: "SkipOnServer");
            Gate.Init(h);
            Rules.Init(h);
            Stats.Init(h);
            Log.SetDebug(Cfg.DebugLog.Value);
            if (NecessaryFixes)
                Log.Msg("NecessaryFixes is loaded: it fixes bad signals, ships and players in the ground and air while sleeping, so this server's own copies of those stay off");
            else
            {
                Signals.Init(h);
                Sleep.Init(h);
                Ground.Init(h);
            }
            Respawn.Init(h);
            NetHealth.InitServer(h);
            Protect.Init(h);
            Spawns.Init(h);
            // the game's recording helper is gone before the last player is despawned when a server stops
            foreach (string m in new[] { "OnServerPlayerLoaded", "OnServerPlayerUnloaded" })
                if (typeof(Managers.Client.Utils.RecordingUtils).GetMethod(m, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                    Patch(h, typeof(Managers.Client.Utils.RecordingUtils), m, prefix: "AliveOnly");
            Query.InitServer(h);
            ArchiveLogs();
            ServerPerf.InstallLoopTimer();

            ServerConsole.Start(Cfg.ConsoleInput.Value);
            WriteStatus("starting");
            Log.Msg("DEDICATED SERVER mode. Data folder: " + DataDir);
        }

        public static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Patch(HarmonyLib.Harmony h, Type t, string method, string prefix = null, string postfix = null)
        {
            try
            {
                MethodInfo m = AccessTools.Method(t, method);
                if (m == null) { Log.Error("PATCH MISSING: " + t.Name + "." + method); return; }
                h.Patch(m,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Server).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Server).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Error("could not patch " + t.Name + "." + method + ": " + e.Message); }
        }

        // ---- boot ----------------------------------------------------------------------------

        public static void OnSceneLoaded(string name)
        {
            if (name == "MainGame")
            {
                SweepRendering();
                return;
            }
            if (_boot == 0)
            {
                _boot = 1;
                MelonCoroutines.Start(LoadWorldNextFrame());
            }
            else if (!Stopping)
            {
                // The game sends a failed host back to the main menu. On a server there is
                // nobody to read that menu: say why and quit, so a supervisor can restart it.
                string why = MainGameSceneLoadData.ConnectionError != null ? " (the game reported a connection error)" : "";
                Log.Error("the server stopped and the game went back to its menu" + why + " - shutting down. Is UDP port " + Port + " already in use?");
                WriteStatus("failed");
                Quit(1);
            }
        }

        private static IEnumerator LoadWorldNextFrame()
        {
            yield return null;
            string err;
            WorldPath = ResolveWorld(WorldOverride ?? Cfg.World.Value, out err);
            if (WorldPath == null)
            {
                Log.Error("no world to load: " + err);
                WriteStatus("failed");
                Quit(1);
                yield break;
            }
            Log.Msg("loading world " + WorldPath);
            MainGameSceneLoadData.FakeLocalSteamUserId = null;
            MainGameSceneLoadData.ConnectionError = null;
            MainGameSceneLoadData.SaveFileFullName = WorldPath;
            MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.HostGameIp;
            SceneManager.LoadScene("MainGame", LoadSceneMode.Single);
        }

        /// <summary>A world name (kept under the data folder, created if missing) or a world.json path.</summary>
        public static string ResolveWorld(string world, out string error)
        {
            error = null;
            world = (world ?? "").Trim().Trim('"');
            if (world.Length == 0) world = "default";
            if (world.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || world.IndexOf('/') >= 0 || world.IndexOf('\\') >= 0)
            {
                string p = Environment.ExpandEnvironmentVariables(world);
                if (Directory.Exists(p)) p = Path.Combine(p, "world.json");
                if (File.Exists(p)) return Path.GetFullPath(p);
                error = "world file not found: " + p;
                return null;
            }
            foreach (char c in Path.GetInvalidFileNameChars())
                if (world.IndexOf(c) >= 0) { error = "'" + world + "' can't be a folder name"; return null; }
            string dir = Path.Combine(DataDir, "worlds", world);
            string file = Path.Combine(dir, "world.json");
            if (File.Exists(file)) return file;
            string fresh = Path.Combine(Application.streamingAssetsPath, "NewWorldSave.json");
            if (!File.Exists(fresh)) { error = "no world '" + world + "' and the game's NewWorldSave.json is missing"; return null; }
            Directory.CreateDirectory(dir);
            File.Copy(fresh, file);
            Log.Msg("created a new world '" + world + "' at " + file);
            return file;
        }

        // SteamManager.Awake: a dedicated server doesn't need Steam (players bring their own
        // identity) and shouldn't show its host's account as in-game twice.
        private static bool SteamAwakePrefix(SteamManager __instance)
        {
            if (Cfg.UseSteam.Value) return true;
            try
            {
                Traverse.Create(typeof(SteamManager)).Field("s_instance").SetValue(__instance);
                UnityEngine.Object.DontDestroyOnLoad(__instance.gameObject);
            }
            catch { }
            Log.Msg("Steam is not started in the server (UseSteam = off)");
            return false;
        }

        private static void InitializerStartPostfix(Managers.Common.MainGameSceneInitializer __instance)
        {
            if (MainGameSceneLoadData.Mode != MainGameSceneLoadData.GameMode.HostGameIp || _boot != 1) return;
            _boot = 2;
            try
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                Multipass mp = (Multipass)nm.TransportManager.Transport;
                int idx = -1;
                for (int i = 0; i < mp.Transports.Count; i++) if (mp.Transports[i] is Tugboat) idx = i;
                if (idx < 0) throw new Exception("the game has no Tugboat (IP) transport");
                Tugboat tug = (Tugboat)mp.Transports[idx];
                tug.SetPort((ushort)Port);
                string bind = (Cfg.BindAddress.Value ?? "").Trim();
                if (bind.Length > 0) tug.SetServerBindAddress(bind, IPAddressType.IPv4);
                nm.ServerManager.SetFrameRate((ushort)Mathf.Clamp(Cfg.FrameRate.Value, 15, 500));
                // the game's own handler: a server that fails to start goes back to the menu (see OnSceneLoaded)
                object handler = Traverse.Create(__instance).Field("_errorHandler").GetValue();
                if (handler != null) handler.GetType().GetMethod("HookErrorHandling").Invoke(handler, null);
                bool ok = mp.StartConnection(true, idx);
                Log.Msg((ok ? "starting server on UDP port " : "server FAILED to start on UDP port ") + Port + (bind.Length > 0 ? " (" + bind + ")" : "") + " - no local player");
            }
            catch (Exception e)
            {
                Log.Error("could not start the server: " + e);
                WriteStatus("failed");
                Quit(1);
            }
        }

        private static void WorldLoadedPostfix()
        {
            WorldLoaded = true;
            if (!_watching) { _watching = true; ServerPerf.WatchCulprits(_harmony); }
        }

        private static bool _watching;
        private static HarmonyLib.Harmony _harmony;

        // ---- the game's own systems, adjusted for a server nobody sits at --------------------

        // The game's autosave runs every 10 minutes (hard-coded) in the server tick. Ours does
        // the same through the same GameSaver, but with the interval from server.cfg, and
        // skips saving while nobody is on (nothing changes: the world is paused).
        private static bool GameAutoSavePrefix() { return false; }

        // Save screenshots come from the screen: a server has none, and the GPU readback it
        // waits for is what marks the save finished. The world file is already written by now.
        private static bool SaveScreenshotPrefix(GameSaver __instance, ref IEnumerator __result)
        {
            __result = Nothing();
            try { Traverse.Create(__instance).Field("_currentSavingParams").SetValue(null); } catch { }
            return false;
        }

        private static IEnumerator Nothing() { yield break; }

        private static void TimePrefix(WorldTimeServerTracker __instance, out double __state)
        {
            __state = __instance.CurrentWorldTime;
        }

        // With nobody on, the game treats "everyone" as asleep and runs its clock at up to 50x.
        // PauseWhenEmpty keeps the world where the last player left it.
        private static void TimePostfix(WorldTimeServerTracker __instance, uint tick, double __state)
        {
            if (!Cfg.PauseWhenEmpty.Value || ActivePlayers().Count > 0) return;
            try { __instance._serverTimeTick.Value = new WorldTimeTick { Tick = tick, WorldTime = __state, TimeSpeed = 1.0 }; }
            catch (Exception e) { Log.Once("pause", "could not hold the world clock: " + e.Message); }
        }

        private static bool ErrorsToChatPrefix() { return Cfg.ErrorsToChat.Value; }

        // Client-only work with nobody to show it to: build-menu part thumbnails are rendered
        // with a camera, one after another, for the first minutes of every world.
        private static bool SkipOnServer() { return false; }

        private static bool AliveOnly(UnityEngine.Object __instance) { return __instance != null; }

        // "/..." in chat is a command for the server, not a message for everyone.
        private static bool ChatPrefix(object[] __args)
        {
            try
            {
                NetworkConnection conn = __args[0] as NetworkConnection;
                ChatMessageBroadcaster.ClientMessageBroadcast msg = (ChatMessageBroadcaster.ClientMessageBroadcast)__args[1];
                string text = (msg.Message ?? "").Trim();
                if (conn == null) return true;
                if (text.Length < 2 || text[0] != '/')
                {
                    Data.CountMessage();
                    TrackedPlayerServer from = PlayerByConnection(conn.ClientId);
                    Log.Msg("[chat] " + (from != null ? from.Name : "client " + conn.ClientId) + ": " + text);
                    // what they typed is shown as typed (no passing for the server), in the players' chat style
                    // (sent from here: the game copies the message before a change could reach it, and
                    // its 255-letter cut would land after the tags and break them)
                    string said = msg.Message ?? "";
                    if (said.Length > 255) said = said.Substring(0, 255);
                    IReadOnlyList<TrackedPlayerServer> online = ActivePlayers();
                    List<string> mentioned = new List<string>();
                    string line = Chat.PlayerLine(said, online.Select(x => x.Name), mentioned);
                    // @everyone is for admins
                    bool everyone = mentioned.Remove("everyone") && from != null && Data.IsOp(from.SteamUserId);
                    HashSet<int> pinged = new HashSet<int>();
                    foreach (TrackedPlayerServer x in online)
                        if (x.ConnectionId != conn.ClientId && (everyone || mentioned.Any(n => string.Equals(n, x.Name, StringComparison.OrdinalIgnoreCase)))) pinged.Add(x.ConnectionId);
                    string marked = Chat.Format("highlight=#FFD54F", line);
                    foreach (NetworkConnection to in InstanceFinder.ServerManager.Clients.Values)
                    {
                        if (to == null || !to.IsAuthenticated) continue;
                        bool ping = pinged.Contains(to.ClientId);
                        InstanceFinder.ServerManager.Broadcast(to, new ChatMessageBroadcaster.ServerMessageBroadcast
                        {
                            Message = ping ? marked : line,
                            PlayerConnectionId = conn.ClientId
                        });
                        if (ping) Net.Event(to, "ping", from != null ? from.Name : "", "mention");
                    }
                    if (pinged.Count > 0) Log.Debug("chat", (from != null ? from.Name : "?") + " mentioned " + pinged.Count + " player(s)");
                    return false;
                }
                TrackedPlayerServer p = PlayerByConnection(conn.ClientId);
                Caller c = Caller.ForPlayer(conn, p);
                Log.Msg("[chat command] " + c.Name + ": " + text);
                Commands.Run(text.Substring(1), c);
                return false;
            }
            catch (Exception e) { Log.Once("chatcmd", "chat command failed: " + e); return true; }
        }

        /// <summary>The game's own notices (joined, left, waiting, errors), in the server's styles.</summary>
        private static void GameNoticePrefix(ref string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message)) return;
                if (message.EndsWith(" has joined the game.")) message = Chat.Format(Cfg.StyleJoin.Value, message);
                else if (message.EndsWith(" has left the game.")) message = Chat.Format(Cfg.StyleLeave.Value, message);
                else message = Chat.Format(Cfg.StyleGameNotice.Value, message);
            }
            catch { }
        }

        /// <summary>What the server's own code last started (the watchdog names it when the loop stops).</summary>
        public static volatile string Step = "";

        /// <summary>
        /// NecessaryFixes is loaded. It fixes bad signals, the ground and air while sleeping itself, for every kind of host,
        /// and the two mods agreed that the server's own copies stay off then (both patching the same methods twice).
        /// The ship bin for NaN ships, `ground`, `tp` and the rest of the server's own commands stay on.
        /// </summary>
        public static bool NecessaryFixes
        {
            get
            {
                if (_nf == null) _nf = AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetName().Name == "NecessaryFixes"; } catch { return false; } });
                return _nf.Value;
            }
        }
        private static bool? _nf;

        private static void PlayerJoinedPostfix(TrackedPlayerServer player)
        {
            try
            {
                Step = "join: " + player.Name;
                Data.Join(player.ConnectionId, player.SteamUserId, player.Name);
                Data.Flush();
                Log.Msg("JOINED " + player.Name + " (" + player.SteamUserId + ") - " + ActivePlayers().Count + "/" + MaxPlayers + " on");
                NetworkConnection conn = Connection(player.ConnectionId);
                string motd = (Cfg.Motd.Value ?? "").Trim();
                if (conn != null && motd.Length > 0) SayTo(conn, Chat.Welcome(motd));
                Rules.OnJoined(player);
                Rules.SendResearch(player);
                Spawns.OnJoined(player);
                Signals.RescueIfLost(player);
                WriteStatus(null);
                Step = "joined: " + player.Name;
            }
            catch (Exception e) { Log.Once("joined", "join bookkeeping: " + e); }
        }

        private static void PlayerLeftPostfix(TrackedPlayerServer player)
        {
            try
            {
                Data.Leave(player.ConnectionId);
                InputRelay.Forget(player.ConnectionId);
                Data.Flush();
                string why = NetHealth.WhyGone(player.ConnectionId);
                Log.Msg("LEFT " + player.Name + " - " + ActivePlayers().Count + "/" + MaxPlayers + " on" + (why.Length > 0 ? " (" + why + ")" : ""));
                if (Cfg.SaveWhenPlayerLeaves.Value && !Stopping) RequestSave(2f, false);
                WriteStatus(null);
            }
            catch (Exception e) { Log.Once("left", "leave bookkeeping: " + e); }
        }

        // ---- every frame ---------------------------------------------------------------------

        public static void Update()
        {
            Watchdog.Beat();
            try { Chat.Update(); } catch (Exception e) { Log.Once("chat-timer", "timed stop/restart: " + e.Message); }
            try { if (Ready) { Restarts.Update(); Usage.Update(); } } catch (Exception e) { Log.Once("usage-upd", "planned restarts / resources: " + e.Message); }
            try { if (Ready) ServerInfo.Update(); } catch (Exception e) { Log.Once("serverinfo-upd", "server info: " + e.Message); }
            float now = Time.realtimeSinceStartup;
            double dt = Time.unscaledDeltaTime * 1000.0;
            _frames++; _frameSum += dt; if (dt > _frameMax) _frameMax = dt;

            if (!Ready && WorldLoaded && Net.ServerStarted)
            {
                Ready = true;
                Data.StartRun();
                TimeSpan? down = Data.DowntimeBeforeThisRun;
                Log.Msg("READY - " + Cfg.ServerName.Value + " is listening on UDP port " + Port + ", world " + WorldPath
                        + (down.HasValue ? " (was down " + ServerData.Span(down.Value.TotalSeconds) + (Data.PreviousRun != null && !Data.PreviousRun.Clean ? ", after a crash" : "") + ")" : "")
                        + ". Type 'help' for commands.");
                _nextAutoSave = now + Cfg.AutoSaveMinutes.Value * 60f;
                SweepRendering();
                WriteStatus("ready");
                HookResearchProxy();
                try { Respawn.OnWorldReady(); } catch (Exception e) { Log.Warn("respawn: " + e); }
                try { Signals.CleanWorld(); } catch (Exception e) { Log.Warn("signals: " + e.Message); }
                Watchdog.Arm();
                try { NetGuard.Arm(); } catch (Exception e) { Log.Warn("network guard: " + e.Message); }
                AdminItems.Register();
                Catalog.Write();
                ModSettings.Write();
                Usage.LogSpecs();
                if (!string.IsNullOrWhiteSpace(Cfg.RestartTimes.Value)) Log.Msg(Restarts.Describe());
            }

            ServerConsole.Drain();
            if (now >= _nextInbox) { _nextInbox = now + 1f; ReadInbox(); }
            Gate.Update();
            if (!NecessaryFixes) { Step = "signals"; Signals.Update(); }
            Step = "world objects"; try { NetHealth.Update(); } catch (Exception e) { Log.Once("objects", "world objects: " + e.Message); }
            Step = "respawn"; Respawn.Update();
            Step = "markers"; Markers.Update();
            Step = "protect"; Protect.Update();
            Step = "network guard"; try { NetGuard.Update(); } catch (Exception e) { Log.Once("netguard", "network guard: " + e.Message); }
            Step = "spawns"; Spawns.Update();
            Step = "profiles"; try { Profiles.Update(); } catch (Exception e) { Log.Once("profiles", "profiles: " + e.Message); }
            Step = "frame";
#if DSTEST
            Test.Update();
#endif
            ServerPerf.TraceUpdate();

            if (!Ready) return;
            Step = "stats"; Stats.Update(); Step = "frame";
            if (now >= _nextSweep) { _nextSweep = now + 5f; SweepRendering(); }
            if (now >= _nextLogKeep) { _nextLogKeep = now + 120f; KeepThisLog(); }
            if (now >= _nextBeat) { _nextBeat = now + 30f; Data.Heartbeat(); Data.NoteOnline(ActivePlayers().Select(p => p.SteamUserId)); Data.Flush(); }

            int mins = Cfg.AutoSaveMinutes.Value;
            if (mins <= 0) _nextAutoSave = now + 60f;
            else if (now >= _nextAutoSave)
            {
                _nextAutoSave = now + mins * 60f;
                if (ActivePlayers().Count > 0) SaveNow("autosave", true);
            }
            if (_saveAt >= 0f && now >= _saveAt) { _saveAt = -1f; if (SaveNow("a player left", _saveAuto) == "a save is already running") RequestSave(2f, _saveAuto); }
            if (_writing != null) FinishSave(false);

            if (now >= _nextStatus)
            {
                _nextStatus = now + 5f;
                AvgFrameMs = _frames > 0 ? _frameSum / _frames : 0;
                MaxFrameMs = _frameMax;
                _frames = 0; _frameSum = 0; _frameMax = 0;
                ServerPerf.Roll();
                Data.Flush();          // only writes when something changed; keeps server-data.json within 5 s of the truth
                WriteStatus(null);
            }
        }

        /// <summary>
        /// The game's research questions (IResearchProvider: is this part / feature / research
        /// unlocked?) go through ResearchClientProxy, which gets the research state only when a
        /// client starts - never, in a server with no local player. Mods asking it on the server
        /// then got an exception. Hand it the server's research state instead.
        /// </summary>
        private static void HookResearchProxy()
        {
            try
            {
                Research.ResearchClientProxy proxy = Core.Services.ServiceLocator.GetService<Research.ResearchClientProxy>();
                Research.ResearchNetworkedState state = UnityEngine.Object.FindFirstObjectByType<Research.ResearchNetworkedState>();
                if (proxy == null || state == null) { Log.Warn("research: no proxy or state to join up"); return; }
                if (Traverse.Create(proxy).Field("_researchState").GetValue() != null) return;
                proxy.Setup(state);
                Log.Msg("research questions answered from the server's own research state (" + state.Researched.Length + " researched)");
            }
            catch (Exception e) { Log.Warn("research proxy: " + e.Message); }
        }

        /// <summary>No cameras, no sound: a server draws nothing.</summary>
        private static void SweepRendering()
        {
            try
            {
                foreach (Camera c in Camera.allCameras)
                    if (c != null && c.enabled) { c.enabled = false; _camerasOff++; }
                AudioListener.volume = 0f;
                AudioListener.pause = true;
                if (FMODUnity.RuntimeManager.IsInitialized)
                {
                    FMOD.ChannelGroup master;
                    if (FMODUnity.RuntimeManager.CoreSystem.getMasterChannelGroup(out master) == FMOD.RESULT.OK) master.setMute(true);
                }
            }
            catch (Exception e) { Log.Once("sweep", "rendering sweep: " + e.Message); }
        }

        public static int CamerasTurnedOff { get { return _camerasOff; } }

        /// <summary>A setting changed while running: apply what can be applied now.</summary>
        public static void ApplySetting(string name)
        {
            if (name == "FrameRate")
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                if (nm != null) nm.ServerManager.SetFrameRate((ushort)Cfg.FrameRate.Value);
                Application.targetFrameRate = Cfg.FrameRate.Value;
            }
            else if (name == "AutoSaveMinutes") _nextAutoSave = Time.realtimeSinceStartup + Cfg.AutoSaveMinutes.Value * 60f;
            else if (name == "Research") Rules.ResearchModeChanged();
            else if (name == "DebugLog") Log.SetDebug(Cfg.DebugLog.Value);
            else if (name == "PlayerMarkers" || name == "AdminsSeeAllMarkers" || name == "ColoredMarkers") Markers.Changed();
            else if (name == "DevTools") foreach (TrackedPlayerServer p in ActivePlayers()) Rules.OnJoined(p);
            WriteStatus(null);
        }

        // ---- saving --------------------------------------------------------------------------

        public static void RequestSave(float delay, bool automatic)
        {
            float at = Time.realtimeSinceStartup + delay;
            if (_saveAt < 0f || at < _saveAt) _saveAt = at;
            _saveAuto = automatic;
        }

        // A save another mod (Performance+) is still writing to disk on another thread.
        private class Writing
        {
            public string Why;
            public DateTime Before;
            public System.Diagnostics.Stopwatch Sw;
            public Action<string> Done;
            public float GiveUpAt;
        }
        private static Writing _writing;

        /// <summary>
        /// Saves through the game's own GameSaver. Returns an error, or null once the save is started.
        /// <paramref name="done"/> gets the outcome (null or an error) when the world file is on disk,
        /// which can be a few frames later when the file is written in the background.
        /// </summary>
        public static string SaveNow(string why, bool automatic, Action<string> done = null)
        {
            if (!WorldLoaded) return "the world isn't loaded yet";
            GameSaver saver = Core.Services.ServiceLocator.GetService<GameSaver>();
            if (saver == null) return "the game's saver isn't there";
            if (_writing != null || saver.IsCurrentlySaving()) return "a save is already running";
            DateTime before = File.Exists(WorldPath) ? File.GetLastWriteTimeUtc(WorldPath) : DateTime.MinValue;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try { saver.SaveGame(new SaveGameParams { IsAutomatic = automatic }); }
            catch (Exception e) { Log.Error("save failed: " + e); return "save failed: " + e.Message; }
            _writing = new Writing { Why = why, Before = before, Sw = sw, Done = done, GiveUpAt = Time.realtimeSinceStartup + 60f };
            FinishSave(false);
            return null;
        }

        /// <summary>Completes the save in progress once its file is written. <paramref name="wait"/> blocks until then.</summary>
        public static void FinishSave(bool wait)
        {
            Writing w = _writing;
            if (w == null) return;
            GameSaver saver = Core.Services.ServiceLocator.GetService<GameSaver>();
            if (wait)
            {
                System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
                while (saver != null && saver.IsCurrentlySaving() && waited.Elapsed.TotalSeconds < 60) System.Threading.Thread.Sleep(10);
            }
            else if (saver != null && saver.IsCurrentlySaving() && Time.realtimeSinceStartup < w.GiveUpAt) return;
            _writing = null;
            string err = Saved(w.Why, w.Before, w.Sw);
            if (w.Done != null) try { w.Done(err); } catch (Exception e) { Log.Error("after save: " + e); }
        }

        private static string Saved(string why, DateTime before, System.Diagnostics.Stopwatch sw)
        {
            sw.Stop();
            DateTime after = File.Exists(WorldPath) ? File.GetLastWriteTimeUtc(WorldPath) : DateTime.MinValue;
            if (after <= before) { Log.Error("save (" + why + ") did not write " + WorldPath); return "the world file was not written"; }
            LastSaveUtc = DateTime.UtcNow;
            Saves++;
            LastSaveNote = why;
            Stats.PruneBackups();
            Stats.SaveExploration();
            Respawn.Save();
            try { ShipAdmin.Prune(); } catch { }
            long kb = new FileInfo(WorldPath).Length / 1024;
            Log.Msg("saved (" + why + "): " + kb + " KB in " + sw.ElapsedMilliseconds + " ms");
            WriteStatus(null);
            return null;
        }

        // ---- stopping ------------------------------------------------------------------------

        public static bool Restarting;

        public static void Stop(string why, bool restart = false)
        {
            if (Stopping) return;
            Restarting = restart;
            Log.Msg((restart ? "restarting: " : "stopping: ") + why);
            if (Ready)
            {
                Chat.AlertAll(restart ? "The server is restarting now. Join again in a minute." : "The server is shutting down.");
                string err = SaveNow("server stopping", true);
                if (err != null) Log.Warn("final save: " + err);
            }
            Stopping = true;
            MelonCoroutines.Start(QuitSoon());
        }

        private static IEnumerator QuitSoon()
        {
            float until = Time.realtimeSinceStartup + 0.5f;     // let the goodbye messages go out
            while (Time.realtimeSinceStartup < until) yield return null;
            while (_writing != null) { FinishSave(false); yield return null; }   // the last save, if it is written in the background
            Quit(Restarting ? 3 : 0);
        }

        public static void Quit(int code)
        {
            Stopping = true;
            try { FinishSave(true); } catch { }
            try { KeepThisLog(); } catch { }
            try { Stats.SaveExploration(); } catch { }
            try { Data.StopRun(); } catch { }
            WriteStatus(code == 0 ? "stopped" : code == 3 ? "restarting" : "failed");
            try
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                if (nm != null && nm.ServerManager != null && nm.ServerManager.Started) nm.ServerManager.StopConnection(true);
            }
            catch { }
            Application.Quit(code);
        }

        public static void OnQuit()
        {
            if (!Stopping) { Stopping = true; try { Data.StopRun(); } catch { } WriteStatus("stopped"); }
        }

        // ---- players -------------------------------------------------------------------------

        public static IReadOnlyList<TrackedPlayerServer> ActivePlayers()
        {
            PlayersServerTracker t = Core.Services.ServiceLocator.GetService<PlayersServerTracker>();
            return t != null ? t.ActivePlayers : (IReadOnlyList<TrackedPlayerServer>)new List<TrackedPlayerServer>();
        }

        /// <summary>The player on a connection, by name, or "client N" before they are in.</summary>
        public static string NameOf(int clientId)
        {
            TrackedPlayerServer p = PlayerByConnection(clientId);
            return p != null ? p.Name : "client " + clientId;
        }

        public static TrackedPlayerServer PlayerByConnection(int clientId)
        {
            foreach (TrackedPlayerServer p in ActivePlayers()) if (p.ConnectionId == clientId) return p;
            return null;
        }

        public static NetworkConnection Connection(int clientId)
        {
            NetworkManager nm = InstanceFinder.NetworkManager;
            NetworkConnection c;
            if (nm != null && nm.ServerManager != null && nm.ServerManager.Clients.TryGetValue(clientId, out c)) return c;
            return null;
        }

        public static void SayTo(NetworkConnection conn, string line)
        {
            try
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                if (nm == null || conn == null || !nm.ServerManager.Started) return;
                nm.ServerManager.Broadcast(conn, new ChatMessageBroadcaster.ServerMessageBroadcast { IsSystem = true, Message = line });
            }
            catch (Exception e) { Log.Once("sayto", "could not message a player: " + e.Message); }
        }

        public static void SayAll(string line)
        {
            try
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                if (nm == null || !nm.ServerManager.Started) return;
                nm.ServerManager.Broadcast(new ChatMessageBroadcaster.ServerMessageBroadcast { IsSystem = true, Message = line });
            }
            catch (Exception e) { Log.Once("sayall", "could not message everyone: " + e.Message); }
        }

        /// <summary>Tells the client why (it shows on their main menu), then disconnects.</summary>
        public static void Kick(NetworkConnection conn, string why, string code = "")
        {
            if (conn == null) return;
            Net.SendVerdict(conn, new DsVerdict { Accepted = false, Message = why, Missing = code ?? "" });
            SayTo(conn, why);
            Gate.DisconnectSoon(conn);
        }

        // ---- commands file (for scripts, and the test harness) -------------------------------

        // ---- logs that stay ------------------------------------------------------------------
        // MelonLoader keeps only the last few runs' logs (MelonLoader/Logs) and the Unity log is
        // rewritten every start. At each start, copy whatever is there into <data>/logs, which is
        // never cleared, so the Server Manager can show and export any day's log.

        public static string LogArchiveDir { get { return Path.Combine(DataDir, "logs"); } }
        private static float _nextLogKeep = 120f;

        private static void ArchiveLogs()
        {
            try
            {
                Directory.CreateDirectory(LogArchiveDir);
                string game = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
                string ml = Path.Combine(game, "MelonLoader", "Logs");
                int n = 0;
                // MelonLoader writes each run's log here as the run goes: a copy made while a run was young
                // (a few lines) is brought up to date whenever the log has grown since
                if (Directory.Exists(ml))
                    foreach (string f in Directory.GetFiles(ml, "*.log"))
                    {
                        string to = Path.Combine(LogArchiveDir, "server-" + Path.GetFileName(f));
                        long have = File.Exists(to) ? new FileInfo(to).Length : -1;
                        if (have >= new FileInfo(f).Length) continue;
                        CopyShared(f, to);
                        if (have < 0) n++;
                    }
                // the Unity log of the previous run (StartDedicatedServer.bat and the Manager write it here)
                foreach (string name in new[] { "DedicatedServer-Player.log", "Player.log" })
                {
                    string f = Path.Combine(game, "UserData", name);
                    if (!File.Exists(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    if (DateTime.Now - fi.LastWriteTime < TimeSpan.FromSeconds(20)) continue;     // this run's own, already being written
                    string to = Path.Combine(LogArchiveDir, "unity-" + fi.LastWriteTime.ToString("yy-MM-dd_HH-mm-ss") + ".log");
                    if (!File.Exists(to)) { File.Copy(f, to); n++; }
                }
                if (n > 0) Log.Msg("kept " + n + " earlier log(s) in " + LogArchiveDir);
            }
            catch (Exception e) { Log.Warn("could not keep the earlier logs: " + e.Message); }
        }

        /// <summary>A copy of a file another program (MelonLoader) still has open for writing.</summary>
        private static void CopyShared(string from, string to)
        {
            using (FileStream i = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream o = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.Read))
                i.CopyTo(o);
        }

        /// <summary>This run's log, as far as it has got (when stopping, and every few minutes).</summary>
        public static void KeepThisLog()
        {
            try
            {
                string ml = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "MelonLoader", "Logs");
                if (!Directory.Exists(ml)) return;
                FileInfo newest = new DirectoryInfo(ml).GetFiles("*.log").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                if (newest == null) return;
                Directory.CreateDirectory(LogArchiveDir);
                CopyShared(newest.FullName, Path.Combine(LogArchiveDir, "server-" + newest.Name));
            }
            catch { }
        }

        public static string InboxPath { get { return Path.Combine(DataDir, "commands.txt"); } }
        public static string OutboxPath { get { return Path.Combine(DataDir, "console-output.txt"); } }

        private static void ReadInbox()
        {
            string inbox = InboxPath;
            if (!File.Exists(inbox)) return;
            string taken = inbox + ".taking";
            string[] lines;
            try
            {
                if (File.Exists(taken)) File.Delete(taken);
                File.Move(inbox, taken);          // atomic: a writer mid-append keeps the file and it is read next time
                lines = File.ReadAllLines(taken);
                File.Delete(taken);
            }
            catch { return; }
            foreach (string l in lines)
            {
                string line = l.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                Commands.Run(line, Caller.Console("commands.txt"));
            }
        }

        /// <summary>Console replies also go to console-output.txt, so a script can read them.</summary>
        public static void ConsoleOut(string line)
        {
            Log.Msg(line);
            try { File.AppendAllText(OutboxPath, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); } catch { }
        }

        // ---- status.json: what a monitor (or the harness) reads ------------------------------

        /// <summary>From the watchdog's thread (no Unity calls): just the state, in the file as it is.</summary>
        public static void WriteStatusFromWatchdog(string state)
        {
            string path = Path.Combine(DataDir, "status.json");
            JObject o;
            try { o = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); } catch { o = new JObject(); }
            o["state"] = state;
            o["writtenUtc"] = DateTime.UtcNow.ToString("o");
            o["watchdog"] = true;
            File.WriteAllText(path, o.ToString());
        }

        private static string _state = "starting";

        public static void WriteStatus(string state)
        {
            if (state != null) _state = state;
            try
            {
                JObject o = new JObject();
                o["state"] = _state;
                o["mod"] = DedicatedServerMod.Version;
                o["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
                o["serverName"] = Cfg.ServerName.Value;
                o["world"] = WorldPath ?? "";
                o["port"] = Port;
                o["maxPlayers"] = MaxPlayers;
                try { o["net"] = NetGuard.Status(); } catch { }
                o["writtenUtc"] = DateTime.UtcNow.ToString("o");
                o["startedUtc"] = Ready ? Data.RunStart.ToString("o") : "";
                o["uptimeSeconds"] = Ready ? (DateTime.UtcNow - Data.RunStart).TotalSeconds : 0;
                JArray ps = new JArray();
                foreach (TrackedPlayerServer p in ActivePlayers())
                {
                    JObject j = new JObject();
                    j["name"] = p.Name;
                    j["id"] = p.SteamUserId.ToString();
                    j["op"] = Data.IsOp(p.SteamUserId);
                    ps.Add(j);
                }
                o["players"] = ps;
                if (Chat.Pending)
                {
                    JObject sch = new JObject();
                    sch["restart"] = Chat.PendingRestart;
                    sch["secondsLeft"] = Math.Round(Chat.SecondsLeft);
                    sch["reason"] = Chat.PendingReason;
                    o["scheduled"] = sch;
                }
                o["worldTime"] = WorldTime();
                o["avgFrameMs"] = Math.Round(AvgFrameMs, 2);
                o["maxFrameMs"] = Math.Round(MaxFrameMs, 2);
                o["targetFrameRate"] = Application.targetFrameRate;
                o["avgWorkMs"] = Math.Round(ServerPerf.AvgWorkMs, 3);
                o["stalls"] = Stalls;
                o["longestStallMs"] = Math.Round(LongestStallMs, 0);
                o["lastStall"] = ServerPerf.LastStall;
                o["consoleQuickEditOff"] = ServerConsole.QuickEditOff;
                o["maxWorkMs"] = Math.Round(ServerPerf.MaxWorkMs, 3);
                NetworkManager nm = InstanceFinder.NetworkManager;
                o["tickRate"] = nm != null && nm.TimeManager != null ? nm.TimeManager.TickRate : 0;
                o["localClient"] = nm != null && nm.ClientManager != null && nm.ClientManager.Started;
                o["camerasTurnedOff"] = _camerasOff;
                o["enabledCameras"] = Camera.allCamerasCount;
                o["saves"] = Saves;
                o["lastSaveUtc"] = LastSaveUtc.HasValue ? LastSaveUtc.Value.ToString("o") : "";
                o["requiredMods"] = new JArray(Gate.Required().Select(m => m.Key + " " + m.Value).ToArray());
                o["rules"] = new JObject { ["devTools"] = Cfg.DevTools.Value, ["research"] = Cfg.Research.Value, ["password"] = (Cfg.Password.Value ?? "").Length > 0,
                                           ["whitelist"] = Cfg.Whitelist.Value, ["protectNames"] = Cfg.ProtectNames.Value };
                JObject perms = new JObject();
                foreach (KeyValuePair<string, string[]> kv in Rules.Declared()) perms[kv.Key] = new JArray(kv.Value);
                o["permissionLevels"] = perms;
                if (Ready)
                {
                    JObject ex = new JObject();
                    foreach (KeyValuePair<string, double> kv in Stats.PlanetPercents()) ex[kv.Key] = Math.Round(kv.Value, 2);
                    o["planetsExplored"] = ex;
                    o["worldExploredPercent"] = Math.Round(Stats.WorldPercent, 2);
                    o["spaceExploredPercent"] = Math.Round(Stats.SpacePercent, 3);
                    o["ships"] = Stats.ShipCount;
                }
                o["respawn"] = new JObject { ["pending"] = Respawn.Pending, ["grownBack"] = Respawn.Respawned, ["waiting"] = Respawn.Blocked };
                o["signalsFixed"] = Signals.GroupsFixed + Signals.PartsFixed + Signals.StatesFixed;
                o["wallsSkipped"] = Signals.WallsSkipped;
                o["fixes"] = NecessaryFixes ? (JToken)"NecessaryFixes" : new JObject
                {
                    ["statesCleaned"] = Signals.StatesFixed, ["inputsLimited"] = Signals.InputsLimited, ["nanPlacesRefused"] = Signals.PhysicsRefused,
                    ["sleepTicks"] = Sleep.Ticks, ["groundHoles"] = Ground.Holes, ["groundRepairs"] = Ground.Repairs + Ground.DeadDropped,
                    ["crashesKeptOut"] = Ground.RestoresClamped, ["liftedPlayers"] = Ground.Rescues, ["liftedShips"] = Ground.ShipRescues,
                };
                o["fonts"] = new JArray(Chat.Fonts.ToArray());
                o["resources"] = Usage.Status();
                DateTime nextPlanned = Restarts.NextPlanned();
                o["nextRestart"] = nextPlanned == DateTime.MinValue ? "" : nextPlanned.ToUniversalTime().ToString("o");
                o["bandwidth"] = new JObject { ["on"] = Cfg.TrackBandwidth.Value, ["inBytesPerSec"] = Math.Round(Stats.InRate), ["outBytesPerSec"] = Math.Round(Stats.OutRate),
                                               ["inTotal"] = Data.Total(d => d.BytesIn), ["outTotal"] = Data.Total(d => d.BytesOut) };
                o["totals"] = new JObject { ["messages"] = Data.Total(d => d.Messages), ["shipsBuilt"] = Data.Total(d => d.ShipsBuilt), ["bans"] = Data.Total(d => d.Bans),
                                            ["joins"] = Data.Total(d => d.Joins), ["playersEver"] = Data.D.Players.Count };
                string path = Path.Combine(DataDir, "status.json");
                File.WriteAllText(path + ".tmp", o.ToString());
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Log.Once("status", "could not write status.json: " + e.Message); }
        }

        public static double WorldTime()
        {
            try
            {
                WorldTimeServerTracker t = Core.Services.ServiceLocator.GetService<WorldTimeServerTracker>();
                return t != null ? t.CurrentWorldTime : 0;
            }
            catch { return 0; }
        }
    }
}
