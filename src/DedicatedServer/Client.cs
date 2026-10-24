// The client half, in every game with this mod (not only servers).
//
// Join by address: the game's Join menu only lists Steam friends. A box on that menu takes
// an address - an IP, a host name (playit.gg, a tunnel, a DNS name) - with an optional
// :port, and joins it over the game's own IP transport (Tugboat), the join path the game's
// own Test_Ip mode uses. The game's code for a client (player, HUD, chat) is unchanged:
// only how the connection is made differs from a Steam join.
//
// On connecting it tells the server which mods it has (DsHello). If the server turns it
// away, the reason (and the mods it lacks) is shown on the main menu.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FishNet;
using FishNet.Managing;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.Tugboat;
using HarmonyLib;
using Managers.Interface;
using MelonLoader;
using UI.MainMenu;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DedicatedServer
{
    internal static class Client
    {
        public const int DefaultPort = 7777;

        public static MelonPreferences_Entry<string> LastAddress, OfflineId, PlayerName, IdentityKey, JoinPassword, DebugLog;
        public static MelonPreferences_Entry<bool> PingSound;
        public const int MaxName = 24;

        // the join in progress
        private static string _host, _ip;
        private static int _port;
        public static bool JoiningByAddress;

        private static bool _helloSent;
        public static DsVerdict? LastVerdict;
        public static string JoinError;           // shown under the box
        public static int VerdictsSeen;


        public static void Init(HarmonyLib.Harmony h, MelonPreferences_Category cat)
        {
            LastAddress = cat.CreateEntry("LastAddress", "127.0.0.1:" + DefaultPort, description: "The last address typed in the Join menu's 'Join by address' box.");
            OfflineId = cat.CreateEntry("NoSteamId", "", description: "Only used when Steam isn't running and you join by address: the id servers know you by (made up once, kept here).");
            PlayerName = cat.CreateEntry("PlayerName", "", description: "Your name on servers you join by address (the Join page's Name box). Empty = your Steam name, or your Windows user name without Steam.");
            IdentityKey = cat.CreateEntry("IdentityKey", "", description: "This game's own secret: servers tie your character to it, so nobody else can join as you. Keep it private. A new game version on this PC takes it (and your name) from %APPDATA%\\StellarDrive Dedicated Server\\player.json; copy that file to a new PC to stay yourself there (or ask the server's admin to unlink you).");
            PingSound = cat.CreateEntry("PingSound", true, description: "Play a short sound when another player @mentions you or whispers to you.");
            DebugLog = cat.CreateEntry("DebugLog", "off", description: "Extra detail in this game's log (MelonLoader/Latest.log) for tracking down a problem on a server: off, all, or a list of: net, chat, respawn, markers, query, install, ui.");
            JoinPassword = cat.CreateEntry("JoinPassword", "", description: "(Before 1.3: one password for every server. Each saved server keeps its own now, in DedicatedServer-servers.json.)");
            if (!DedicatedServerMod.IsServer) try { Identity.Sync(); } catch (Exception e) { Log.Warn("your player file: " + e.Message); }
            Net.OnVerdict = OnVerdict;
            Net.OnChallenge = OnChallenge;
            Rules.InitClient(h);
            if (!Server.NecessaryFixes) Signals.InitClient(h);     // (NecessaryFixes guards the room grid in every game itself)
            ServerInfo.InitClient(h);
            try
            {
                h.Patch(AccessTools.Method(typeof(Managers.Common.MainGameSceneInitializer), "Start"),
                    postfix: new HarmonyMethod(typeof(Client).GetMethod("InitializerStartPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Managers.Client.Services.SteamLocalPlayerInfoProvider), "GetLocalPlayerSteamInfo"),
                    postfix: new HarmonyMethod(typeof(Client).GetMethod("SteamInfoPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
#if DSTEST
                if (Environment.GetEnvironmentVariable("SDD_DS_NO_AVATAR_FIX") != "1")   // test-only: see the old failure
#endif
                h.Patch(AccessTools.Method(typeof(UI.Common.AvatarTextureLoader), "TryCreateAvatarTexture"),
                    prefix: new HarmonyMethod(typeof(Client).GetMethod("AvatarPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    finalizer: new HarmonyMethod(typeof(Client).GetMethod("AvatarFinalizer", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Error("could not patch the game's scene start (joining by address won't work): " + e.Message); }
        }

        // ---- joining -------------------------------------------------------------------------

        /// <summary>
        /// What the Join button does: resolve the address, then load the game scene as the game's
        /// own Join does, but in IP mode. Returns an error, or null when the join has begun.
        /// </summary>
        public static string Join(string address, string typedPassword = null)
        {
            if (typedPassword != null) SavedServers.Typed = typedPassword;
            string host; int port; string err;
            if (!ParseAddress(address, out host, out port, out err)) return err;
            string ip;
            try { ip = Resolve(host); }
            catch (Exception e) { return "Can't find '" + host + "': " + e.Message; }
            _host = host; _ip = ip; _port = port;
            LastAddress.Value = address.Trim();
            try { MelonPreferences.Save(); } catch { }
            LastVerdict = null;
            RejectionShown = false;
            JoinError = null;
            JoiningByAddress = true;
            _helloSent = false;
            MainGameSceneLoadData.ConnectionError = null;
            MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.JoinGameIp;
            Log.Msg("joining " + host + (ip != host ? " (" + ip + ")" : "") + " port " + port);
            SceneManager.LoadScene("MainGame", LoadSceneMode.Single);
            return null;
        }

        public static bool ParseAddress(string address, out string host, out int port, out string error)
        {
            host = null; port = DefaultPort; error = null;
            string a = (address ?? "").Trim();
            if (a.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)) a = a.Substring(6);
            a = a.TrimEnd('/');
            if (a.Length == 0) { error = "Type an address first, like 123.45.67.89:7777 or a playit.gg address."; return false; }
            string portText = null;
            if (a.StartsWith("["))                                   // [ipv6]:port
            {
                int close = a.IndexOf(']');
                if (close < 0) { error = "Missing ] in the address."; return false; }
                host = a.Substring(1, close - 1);
                if (close + 1 < a.Length && a[close + 1] == ':') portText = a.Substring(close + 2);
            }
            else if (a.Count(ch => ch == ':') == 1)                 // host:port
            {
                int colon = a.IndexOf(':');
                host = a.Substring(0, colon);
                portText = a.Substring(colon + 1);
            }
            else host = a;                                           // host, or a bare IPv6 address
            if (portText != null && (!int.TryParse(portText, out port) || port < 1 || port > 65535))
            { error = "'" + portText + "' isn't a port number (1-65535)."; return false; }
            if (string.IsNullOrEmpty(host)) { error = "No host in the address."; return false; }
            return true;
        }

        private static string Resolve(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip.ToString();
            IPAddress[] all = Dns.GetHostAddresses(host);
            IPAddress pick = all.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            if (pick == null) throw new Exception("no address for that name");
            return pick.ToString();
        }

        private static void InitializerStartPostfix(Managers.Common.MainGameSceneInitializer __instance)
        {
            if (MainGameSceneLoadData.Mode != MainGameSceneLoadData.GameMode.JoinGameIp || !JoiningByAddress) return;
            JoiningByAddress = false;
            try
            {
                Multipass mp = (Multipass)InstanceFinder.NetworkManager.TransportManager.Transport;
                Tugboat tug = mp.Transports.OfType<Tugboat>().FirstOrDefault();
                if (tug == null) throw new Exception("the game has no Tugboat (IP) transport");
                tug.SetClientAddress(_ip);
                tug.SetPort((ushort)_port);
                // the game's own IP client start (hooks its connection-error handling, then connects)
                AccessTools.Method(typeof(Managers.Common.MainGameSceneInitializer), "StartClientIp").Invoke(__instance, null);
                Log.Msg("connecting to " + _ip + ":" + _port + " ...");
            }
            catch (Exception e)
            {
                Log.Error("could not start joining: " + e);
                JoinError = "Couldn't start joining: " + e.Message;
                SceneManager.LoadScene(0, LoadSceneMode.Single);
            }
        }

        /// <summary>The name to join with: the Name box, else Steam's, else Windows'.</summary>
        public static string DefaultName()
        {
            string n = CleanName(PlayerName.Value);
            if (n.Length > 0) return n;
            try { if (SteamManager.Initialized) n = CleanName(Steamworks.SteamFriends.GetPersonaName()); } catch { }
            if (n.Length == 0) n = CleanName(Environment.UserName);
            return n.Length > 0 ? n : "Player";
        }

        /// <summary>No rich-text tags or control characters, at most MaxName long. The server does the same.</summary>
        public static string CleanName(string n)
        {
            n = new string((n ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            return n.Length > MaxName ? n.Substring(0, MaxName).Trim() : n;
        }

        // The player list asks Steam for each player's avatar before it writes their name. With
        // Steam running but the game not connected to it (a copy started outside Steam), that call
        // threw: the row kept its placeholder text ("Language", "Normal") and, never recorded,
        // was made again every frame. Ids made up for no-Steam players have no avatar either.
        private static bool AvatarPrefix(Steamworks.CSteamID steamID, ref Texture2D texture, ref bool __result)
        {
            if (SteamManager.Initialized && (steamID.m_SteamID >> 56) != 0xD5) return true;
            texture = null;
            __result = false;
            return false;
        }

        private static Exception AvatarFinalizer(Exception __exception, ref Texture2D texture, ref bool __result)
        {
            if (__exception != null) { texture = null; __result = false; Log.Once("avatar", "no Steam avatar: " + __exception.Message); }
            return null;
        }

        // Joining by address, the player's name is theirs to choose (Steam's by default). Without
        // Steam the game calls every player "NoSteam" with id 0, so two of them would be the same
        // player to a server: such a player gets an id of their own, kept in MelonPreferences.
        private static void SteamInfoPostfix(ref Managers.Interface.Model.LocalPlayerSteamInfo __result)
        {
            if (MainGameSceneLoadData.Mode != MainGameSceneLoadData.GameMode.JoinGameIp) return;
            __result.PlayerName = DefaultName();
            if (__result.SteamId != 0) return;
            ulong id;
            if (!ulong.TryParse(OfflineId.Value, out id) || id == 0)
            {
                byte[] b = Guid.NewGuid().ToByteArray();
                id = (0xD5UL << 56) | (BitConverter.ToUInt64(b, 0) & 0x00FFFFFFFFFFFFFFUL);   // never a real SteamID64 (those start 0x011)
                OfflineId.Value = id.ToString();
                try { MelonPreferences.Save(); } catch { }
                try { Identity.Made(); } catch { }
            }
            __result.SteamId = id;
            Log.Msg("no Steam: joining as " + __result.PlayerName + " (id " + id + ")");
        }

        // ---- the handshake -------------------------------------------------------------------

        public static void Update()
        {
            try { Overlay.Update(); } catch (Exception e) { Log.Once("overlay", "announcement: " + e.Message); }
            try { JoinWatch.Update(); } catch (Exception e) { Log.Once("joinwatch", "join watch: " + e.Message); }
            try { ModsOff.Update(); } catch (Exception e) { Log.Once("modsoff", "mods off: " + e.Message); }
            try { ChatComplete.Update(); } catch (Exception e) { Log.Once("chatcomplete", "name completion: " + e.Message); }
            try { MenuFix.Update(); } catch (Exception e) { Log.Once("menufix", "menu highlights: " + e.Message); }
            NetworkManager m = InstanceFinder.NetworkManager;
            bool client = m != null && m.ClientManager != null && m.ClientManager.Started;
            bool server = m != null && m.ServerManager != null && m.ServerManager.Started;
            Query.UpdateClient();                       // server queries and file downloads, in a world too
            if (!client) { _helloSent = false; MenuUpdate(); return; }
            if (server || _helloSent) return;           // a host checks nobody through this
            if (m.ClientManager.Connection == null || !m.ClientManager.Connection.IsAuthenticated) return;
            _helloSent = Net.SendHello(new DsHello { ModVersion = DedicatedServerMod.Version, Mods = MyMods() });
            if (_helloSent) Log.Msg("told the server which mods this game has");
        }

        /// <summary>The mod list, plus the game's own version as a line "@game" (older servers read it as a mod nobody requires).</summary>
#if DSTEST
        private static bool Shown(string mod) { return !HideForTest.Contains(mod); }
        public static HashSet<string> HideForTest = new HashSet<string>();     // the probe leaves these out, to be offered them
        public static List<string> ExtraForTest = new List<string>();          // and says it has these too ("name\tversion")
#else
        private static bool Shown(string mod) { return true; }
#endif

        public static string MyMods()
        {
            string list = string.Join("\n", MelonMod.RegisteredMelons.Where(x => x != null && x.Info != null && Shown(x.Info.Name))
                .Select(x => x.Info.Name + "\t" + x.Info.Version).ToArray()) + "\n" + Gate.GameLine + "\t" + Application.version;
#if DSTEST
            foreach (string extra in ExtraForTest) list += "\n" + extra;
#endif
            return list;
        }

        private static void OnVerdict(DsVerdict v)
        {
            LastVerdict = v;
            VerdictsSeen++;
            if (MainGameSceneLoadData.Mode == MainGameSceneLoadData.GameMode.JoinGameIp) try { SavedServers.OnVerdict(LastAddress.Value, v); } catch (Exception e) { Log.Warn("saved servers: " + e.Message); }
            if (v.Accepted) Log.Msg("the server let us in: " + v.Message);
            else Log.Warn("the server turned us away: " + v.Message);
        }

        // ---- the main menu -------------------------------------------------------------------

        public static bool RejectionShown;
        public static string RejectionPopupText;

        /// <summary>
        /// The server asks who we are: HMAC(our key, its id) is the same every time for this game and
        /// that server and useless anywhere else; the password goes as HMAC(password, nonce), never itself.
        /// </summary>
        private static void OnChallenge(DsChallenge c)
        {
            string key = IdentityKey.Value;
            if (string.IsNullOrEmpty(key) || key.Length < 32)
            {
                byte[] b = new byte[32];
                using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
                key = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
                IdentityKey.Value = key;
                try { MelonPreferences.Save(); } catch { }
                try { Identity.Made(); } catch { }
            }
            string pass = SavedServers.PasswordFor(LastAddress.Value) ?? "";
            DsProof proof = new DsProof
            {
                Token = Gate.Hmac(System.Text.Encoding.UTF8.GetBytes(key), c.ServerId ?? ""),
                Password = c.NeedPassword && pass.Length > 0 ? Gate.Hmac(System.Text.Encoding.UTF8.GetBytes(pass), c.Nonce ?? "") : ""
            };
            Net.ToServer(proof);
        }

        public static void OnSceneLoaded(string name)
        {
            Rules.ClientReset();
            Markers.ClientReset();
            Fonts.RegisterAll();
            AdminItems.Register();
            if (name != "MainGame") ServerInfo.ClientReset();
            InputRelay.ClientReset();
            if (name == "MainGame") RejectionShown = false;
        }

        /// <summary>Every frame, not in a world: the Join page's address box (ClientUi).</summary>
        public static void MenuUpdate()
        {
            if (SceneManager.GetActiveScene().name != "MainGame") ClientUi.Update();
        }
    }
}
