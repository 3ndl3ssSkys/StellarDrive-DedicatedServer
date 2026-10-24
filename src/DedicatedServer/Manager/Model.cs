// The Server Manager's data: its own settings (which servers it knows), each server's folder, the
// server's settings file (dedicatedserver.conf - the same file the DedicatedServer mod reads), and
// talking to a running server. A server is a folder of its own: a copy of the game made of hard
// links (no extra disk) with its own Mods, UserData (settings, worlds, player data) and logs. The
// running server is driven only through files the mod reads and writes, so it keeps running when
// the Manager is closed and the Manager picks it up again when reopened:
//   UserData/DedicatedServer/status.json          live state (every 5 s)
//   UserData/DedicatedServer/commands.txt         lines added here are run as console commands
//   UserData/DedicatedServer/console-output.txt   their replies
//   UserData/DedicatedServer/server-data.json     players, bans, visits, runs, daily stats
//   UserData/DedicatedServer/ships.json           ships (every 15 s)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDServerManager
{
    public class ServerEntry
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
    }

    public class ManagerSettings
    {
        public List<ServerEntry> Servers { get; set; } = new List<ServerEntry>();
        public string LastGameFolder { get; set; } = "";
        public string LastRoot { get; set; } = "";
        public List<string> ConsoleHidden { get; set; } = new List<string>();     // kinds of line the Launch console leaves out
        public Dictionary<string, int> Sizes { get; set; } = new Dictionary<string, int>();   // boxes and dividers the person has dragged to a size
        public bool ChartsHourly { get; set; } = true;

        public static string Dir
        {
            get
            {
#if SDSMTEST
                string over = Environment.GetEnvironmentVariable("SDSM_HOME");     // the self test keeps its own
                if (!string.IsNullOrEmpty(over)) return over;
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StellarDrive Server Manager");
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "manager.json"); } }

        public static ManagerSettings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(FilePath)) ?? new ManagerSettings(); }
            catch { }
            return new ManagerSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Indented));
        }
    }

    /// <summary>What the Manager knows about how a server folder was made (dedicatedserver.manager.json).</summary>
    public class ServerInfo
    {
        public string SourceGame { get; set; } = "";
        public string PublicAddress { get; set; } = "";     // what players type to join from the internet (a tunnel, or a public IP:port)
        public List<string> Mods { get; set; } = new List<string>();
        public DateTime Created { get; set; }
        public string ManagerVersion { get; set; } = "";
    }

    // ---- dedicatedserver.conf -------------------------------------------------------------------

    /// <summary>
    /// The MelonPreferences file the mod reads ([DedicatedServerHost] with key = value lines and #
    /// comments above them). Values are kept as they are written: true/false, numbers, "strings".
    /// </summary>
    public class Conf
    {
        public const string Section = "DedicatedServerHost";
        public readonly List<ConfItem> Items = new List<ConfItem>();

        public class ConfItem
        {
            public string Key, Raw, Comment = "";
            public bool IsBool { get { return Raw == "true" || Raw == "false"; } }
            public bool IsInt { get { long n; return long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n); } }
            public string Text { get { return Conf.Unquote(Raw); } }
        }

        /// <summary>The settings the wizard and the Settings page know about, with their defaults.</summary>
        public static readonly (string Key, string Raw, string Comment)[] Known =
        {
            ("ServerName", "\"StellarDrive Dedicated Server\"", "Shown to players when they join."),
            ("Description", "\"\"", "A line or two about the server, shown under its name in players' saved servers on the Join page."),
            ("World", "\"default\"", "A world name (kept in worlds/<name>/world.json, created new if missing) or the full path of a world.json. Restart to change."),
            ("Port", "7777", "UDP port players connect to (forward it, or point playit.gg / a tunnel at it). Restart to change."),
            ("BindAddress", "\"\"", "IPv4 address to listen on. Empty = every network interface. Restart to change."),
            ("MaxPlayers", "8", "Players allowed on at once. Admins may join when it is full."),
            ("Motd", "\"\"", "Message of the day, sent to each player as they join. Empty = none."),
            ("Password", "\"\"", "A password players must type on the Join page. Empty = none."),
            ("Admins", "\"\"", "Comma-separated player names or Steam ids who are admins."),
            ("ProtectNames", "true", "A name belongs to the first player who joins with it."),
            ("DevTools", "\"admins\"", "Who may use developer tools (cheat flying, free build) and Ship Tools: admins, everyone or nobody."),
            ("Research", "\"shared\"", "shared = one research tree for the server. individual = each player researches (and pays) for themselves."),
            ("AutoSaveMinutes", "10", "Save the world this often while anyone is on. 0 = never."),
            ("BackupsToKeep", "10", "Each autosave keeps the previous world as a backup; this many are kept."),
            ("DeleteBackupsAfterDays", "0", "Also delete backups older than this many days. 0 = never."),
            ("SaveWhenPlayerLeaves", "true", "Save as soon as a player leaves."),
            ("PauseWhenEmpty", "true", "With nobody on, the world stands still."),
            ("RequireMods", "true", "Turn away players who don't have the mods this server needs."),
            ("RequiredMods", "\"auto\"", "Mod names a player must have, or auto = every mod on this server except OptionalMods."),
            ("OptionalMods", "\"Demo Verify Harness, Demo Zoom, Demo Build Pick, Demo Mod List, Demo Ship Tools, ExtraCameras, Performance+\"", "With RequiredMods = auto: mods a player may leave out."),
            ("MatchModVersions", "true", "A required mod must be the same version as the server's."),
            ("Whitelist", "false", "Only players on the whitelist (and admins) may join."),
            ("FrameRate", "120", "Server loop rate (at most ~8 ms of delay at 120)."),
            ("TrackBandwidth", "false", "Count the bytes sent and received."),
            ("ShareMods", "true", "A player missing mods this server needs can download them from it (asked first, shown what and how big, and warned)."),
            ("GroundRescue", "true", "Ships and players never end up in the ground: holes are made good, crashes can't push a ship in, and anything sinking in is put back on top."),
            ("SleepAirFlow", "true", "While everyone is asleep, pipes, vents and other ship parts keep working (the game stops them, so sleepers wake up choking)."),
            ("SleepMaxStepMs", "100", "While everyone sleeps: the longest piece of sped-up time ship parts work out at once, in ms (20-1000)."),
            ("MessagePrefix", "\"[Server]\"", "Put before announcements and alerts. Empty = nothing."),
            ("StyleAnnounce", "\"color=#4FC3F7 bold size=115\"", "How announcements (say) look in chat."),
            ("StyleAlert", "\"color=#FFB84D bold size=120\"", "How restart and shutdown warnings and alerts look."),
            ("StyleServer", "\"color=#8FD3FF\"", "How replies to commands look."),
            ("StyleWelcome", "\"color=#9BE7A0 bold\"", "How the welcome and message of the day look."),
            ("StyleJoin", "\"color=#9BE7A0\"", "How \"<name> has joined the game.\" looks."),
            ("StyleLeave", "\"color=#B8C2CC italic\"", "How \"<name> has left the game.\" looks."),
            ("StyleGameNotice", "\"\"", "How the game's other notices look (sleeping, errors). Empty = the game's own look."),
            ("StylePlayerName", "\"color=#FFE16B bold\"", "How a player's name looks before what they say."),
            ("StylePlayerText", "\"\"", "How what players say looks. Empty = the game's own look."),
            ("PlayerChatFormatting", "false", "Let players use colour/size tags in their own chat. Off: shown exactly as typed, so nobody can pass for the server."),
            ("StyleWhisper", "\"color=#C79BFF italic\"", "How whispers (/w) look."),
            ("StyleMention", "\"color=#FFD54F bold\"", "How @name looks in chat (the player named also hears a ping)."),
            ("AlertsOnScreen", "true", "Restart warnings and alerts also show in the middle of every player's screen (Enter closes them)."),
            ("OnScreenSeconds", "5", "How long an on-screen announcement stays up."),
            ("RespawnTreesMinutes", "120", "Trees, bushes and rocks grow back this many minutes after they go. 0 = never. Never inside ships, builds or players."),
            ("RespawnOresMinutes", "0", "Ore deposits come back this many minutes after they are mined. 0 = never."),
            ("RespawnClearance", "4", "Metres kept clear around ships, builds and players when things grow back."),
            ("FixBadSignals", "true", "Signal values that are NaN, infinite or too big are set to 0 (and stay 0 until the maths is a number again); NaN ships go to the bin."),
            ("LimitPartInputs", "true", "Pumps, valves, docking-door magnets and warp drives get at most 1 from a signal, like the game's own levers."),
            ("PlayerMarkers", "\"everyone\"", "Whose markers (dots) players see: everyone, friends (added both ways with /friend) or nobody."),
            ("AdminsSeeAllMarkers", "true", "Admins see every player's marker."),
            ("ColoredMarkers", "true", "Each player's marker in their own colour (/color); looking at one shows the name."),
            ("SpreadSpawns", "false", "New players start at spots of their own across the planet, and respawn there."),
            ("SpreadSpawnKm", "3", "How far from the starting point those spots can be (km). 0 = anywhere on the planet."),
            ("RejectExtraMods", "false", "Turn away players with mods the server doesn't run, unless listed in AllowedExtraMods (see the Mods page)."),
            ("AllowedExtraMods", "\"Demo Zoom, Demo Build Pick, Demo Mod List, ExtraCameras, Performance+, Demo Verify Harness\"", "Mods players may have that the server doesn't run."),
            ("LogWhispers", "true", "Whispers are written to the server log."),
            ("WatchdogSeconds", "120", "A server stuck this long restarts itself. 0 = off."),
            ("RestartTimes", "\"\"", "Restart every day at these times, in the server machine's time: like 04:00, 16:30 (a day first for once a week: Sun 04:00). Empty = none."),
            ("RestartWarnings", "\"10m, 5m, 2m, 1m, 30s, 10s\"", "When players are warned before a restart or stop (planned or timed): like 10m, 5m, 1m, 30s."),
            ("RestartReason", "\"\"", "What players are told a planned restart is for. Empty = 'planned restart'."),
            ("MemoryLimitPercent", "60", "If the server uses more than this share of the machine's memory for a minute, it warns everyone and restarts (saving first). 0 = off."),
            ("ResourceLogMinutes", "5", "Write the server's memory and CPU, and the machine's, to the log this often (resources.csv gets a row every minute). 0 = only resources.csv."),
            ("ShipBinDays", "30", "Deleted ships can be restored for this many days. 0 = keep them all."),
            ("DebugLog", "\"off\"", "Extra detail in the log: off, all, or areas (net, gate, chat, ground, respawn, signals, markers, query, install, saves)."),
            ("ErrorsToChat", "false", "Post server errors into the players' chat."),
            ("UseSteam", "false", "Start Steam in the server process. Not needed. Restart to change."),
            ("ConsoleInput", "true", "Read commands typed into the server's console window. Restart to change."),
        };

        public static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DevTools", new[] { "admins", "everyone", "nobody" } },
            { "Research", new[] { "shared", "individual" } },
            { "PlayerMarkers", new[] { "everyone", "friends", "nobody" } },
        };

        public static readonly HashSet<string> RestartOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Port", "BindAddress", "World", "UseSteam", "ConsoleInput" };

        public static Conf Load(string path)
        {
            Conf c = new Conf();
            if (File.Exists(path))
            {
                string comment = "";
                bool inSection = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.StartsWith("[")) { inSection = t.Trim('[', ']').Trim() == Section; comment = ""; continue; }
                    if (!inSection) continue;
                    if (t.StartsWith("#")) { comment = (comment.Length > 0 ? comment + " " : "") + t.TrimStart('#').Trim(); continue; }
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    c.Items.Add(new ConfItem { Key = t.Substring(0, eq).Trim(), Raw = t.Substring(eq + 1).Trim(), Comment = comment });
                    comment = "";
                }
            }
            foreach (var k in Known)
                if (c.Find(k.Key) == null) c.Items.Add(new ConfItem { Key = k.Key, Raw = k.Raw, Comment = k.Comment });
            return c;
        }

        public ConfItem Find(string key) { return Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)); }
        public string Get(string key) { ConfItem i = Find(key); return i != null ? i.Text : ""; }
        public bool GetBool(string key) { return Get(key) == "true"; }
        public int GetInt(string key) { int n; return int.TryParse(Get(key), out n) ? n : 0; }

        public void Set(string key, string text)
        {
            ConfItem i = Find(key);
            if (i == null) { i = new ConfItem { Key = key, Raw = Quote(text) }; Items.Add(i); return; }
            i.Raw = i.IsBool || i.IsInt ? text.Trim().ToLowerInvariant() : Quote(text);
        }

        public void SetBool(string key, bool v) { ConfItem i = Find(key); if (i == null) Items.Add(new ConfItem { Key = key, Raw = v ? "true" : "false" }); else i.Raw = v ? "true" : "false"; }
        public void SetInt(string key, int v) { ConfItem i = Find(key); string r = v.ToString(CultureInfo.InvariantCulture); if (i == null) Items.Add(new ConfItem { Key = key, Raw = r }); else i.Raw = r; }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[').Append(Section).Append(']').Append('\n');
            foreach (ConfItem i in Items)
            {
                if (i.Comment.Length > 0) sb.Append("# ").Append(i.Comment).Append('\n');
                sb.Append(i.Key).Append(" = ").Append(i.Raw).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static string Quote(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        public static string Unquote(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return raw.Substring(1, raw.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return raw;
        }
    }

    // ---- one server ------------------------------------------------------------------------------

    public class Server
    {
        public ServerEntry Entry;
        public Server(ServerEntry e) { Entry = e; }

        public string Folder { get { return Entry.Folder; } }
        public string DataDir { get { return Path.Combine(Folder, "UserData", "DedicatedServer"); } }
        public string ConfPath { get { return Path.Combine(DataDir, "dedicatedserver.conf"); } }
        public string StatusPath { get { return Path.Combine(DataDir, "status.json"); } }
        public string DataPath { get { return Path.Combine(DataDir, "server-data.json"); } }
        public string ShipsPath { get { return Path.Combine(DataDir, "ships.json"); } }
        public string CommandsPath { get { return Path.Combine(DataDir, "commands.txt"); } }
        public string OutputPath { get { return Path.Combine(DataDir, "console-output.txt"); } }
        public string LogPath { get { return Path.Combine(Folder, "MelonLoader", "Latest.log"); } }
        public string UnityLogPath { get { return Path.Combine(Folder, "UserData", "DedicatedServer-Player.log"); } }
        public string LogArchiveDir { get { return Path.Combine(DataDir, "logs"); } }
        public string InfoPath { get { return Path.Combine(Folder, "dedicatedserver.manager.json"); } }
        public string Exe { get { return Path.Combine(Folder, "StellarDrive.exe"); } }
        public string WorldsDir { get { return Path.Combine(DataDir, "worlds"); } }
        public string IconPath { get { return Path.Combine(DataDir, "server-icon.png"); } }
        public string ModSettingsPath { get { return Path.Combine(DataDir, "modsettings.json"); } }
        public string BandwidthLivePath { get { return Path.Combine(DataDir, "bandwidth-live.json"); } }
        public string BinDir { get { return Path.Combine(DataDir, "deleted-ships"); } }
        public string PrefsPath { get { return Path.Combine(Folder, "UserData", "MelonPreferences.cfg"); } }
        public string ResourcesPath { get { return Path.Combine(DataDir, "resources.csv"); } }

        public Conf LoadConf() { return Conf.Load(ConfPath); }

        // A file is read again only when it has changed: the pages ask for the same files several
        // times a second, and server-data.json grows with every visit.
        private readonly Dictionary<string, KeyValuePair<long, JsonNode>> _files = new Dictionary<string, KeyValuePair<long, JsonNode>>();

        public JsonNode Cached(string path)
        {
            long stamp = 0;
            try { FileInfo f = new FileInfo(path); if (f.Exists) stamp = f.LastWriteTimeUtc.Ticks ^ (f.Length << 20); } catch { }
            KeyValuePair<long, JsonNode> c;
            if (stamp != 0 && _files.TryGetValue(path, out c) && c.Key == stamp) return c.Value;
            JsonNode n = stamp == 0 ? null : Json.ReadFile(path);
            if (n != null) _files[path] = new KeyValuePair<long, JsonNode>(stamp, n); else _files.Remove(path);
            return n;
        }

        /// <summary>Forget what was read (after this program changed a file itself).</summary>
        public void Forget(string path) { _files.Remove(path); }

        public ServerInfo LoadInfo()
        {
            try { if (File.Exists(InfoPath)) return JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(InfoPath)) ?? new ServerInfo(); } catch { }
            return new ServerInfo();
        }

        public void SaveInfo(ServerInfo i) { File.WriteAllText(InfoPath, JsonSerializer.Serialize(i, Json.Indented)); }

        // ---- state --------------------------------------------------------------------------------

        private JsonNode _status;
        private DateTime _statusRead;
        public Process Launched;          // when this Manager started it
        public string LedgerNote = "";

        public JsonNode Status
        {
            get
            {
                if ((DateTime.UtcNow - _statusRead).TotalSeconds < 0.8) return _status;
                _statusRead = DateTime.UtcNow;
                _status = Json.ReadFile(StatusPath);
                return _status;
            }
        }

        /// <summary>The server's process, if it is running (the pid in status.json, still alive, from this folder).</summary>
        private Process _proc;
        private DateTime _procAt;

        public Process Process()
        {
            // asked many times a second (every page, every server in the list): looked up twice a second at most
            if ((DateTime.UtcNow - _procAt).TotalSeconds < 0.5) { try { return _proc != null && !_proc.HasExited ? _proc : null; } catch { return null; } }
            _procAt = DateTime.UtcNow;
            _proc = FindProcess();
            return _proc;
        }

        private Process FindProcess()
        {
            try
            {
                if (Launched != null && !Launched.HasExited) return Launched;
                JsonNode st = Status;
                if (st == null) return null;
                int pid = Json.Int(st, "pid");
                if (pid <= 0) return null;
                Process p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.HasExited || !string.Equals(p.ProcessName, "StellarDrive", StringComparison.OrdinalIgnoreCase)) return null;
                string path = "";
                try { path = p.MainModule.FileName; } catch { }
                if (path.Length > 0 && !path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return null;
                return p;
            }
            catch { return null; }
        }

        public bool Running { get { return Process() != null; } }

        /// <summary>Seconds since the server last wrote its status (0 if unknown).</summary>
        public double QuietSeconds
        {
            get
            {
                DateTime w = Json.Date(Status, "writtenUtc");
                return w == DateTime.MinValue ? 0 : (DateTime.UtcNow - w).TotalSeconds;
            }
        }

        public string State
        {
            get
            {
                if (!Running) return Json.Str(Status, "state") == "failed" ? "failed" : "stopped";
                string s = Json.Str(Status, "state");
                // the server writes its status every 5 s from its main loop: a long silence is a hang
                if (s == "ready" && QuietSeconds > 30) return "not responding";
                return s == "ready" ? "running" : s == "" ? "starting" : s;
            }
        }

        public string Start()
        {
            if (Running) return "It is already running.";
            if (!File.Exists(Exe)) return "The server's game copy is missing: " + Exe;
            try { if (File.Exists(StatusPath)) File.Delete(StatusPath); } catch { }
            ProcessStartInfo psi = new ProcessStartInfo(Exe,
                "-batchmode -nographics -dedicated --melonloader.hideconsole -logFile \"" + UnityLogPath + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder };
            foreach (string k in psi.Environment.Keys.Where(k => k.StartsWith("SDD_VERIFY", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
#if SDSMTEST
            string parent = Environment.GetEnvironmentVariable("SDSM_TEST_PARENT");   // the self test's safety line
            if (!string.IsNullOrEmpty(parent)) { psi.Environment["SDD_DS_PARENT_PID"] = parent; psi.Environment["SDD_DS_QUIT_AFTER"] = "900"; }
#endif
            Launched = System.Diagnostics.Process.Start(psi);
            _procAt = DateTime.MinValue;
#if SDSMTEST
            if (!string.IsNullOrEmpty(parent)) LedgerNote = TestLedger.Register(Launched.Id, System.Diagnostics.Process.GetCurrentProcess().Id, "DedicatedServer test server (started by the Server Manager self test)");
#endif
            return null;
        }

        public void Command(string line)
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(CommandsPath, line + Environment.NewLine);
        }

        public void Stop() { Command("stop"); }

        public void Kill() { try { Process p = Process(); if (p != null) p.Kill(); } catch { } _procAt = DateTime.MinValue; }

        /// <summary>server-data.json (shared with other readers: change a copy, see EditData).</summary>
        public JsonNode Data { get { return Cached(DataPath); } }
        public JsonNode Ships { get { return Cached(ShipsPath); } }
        public JsonNode ModSettings { get { return Cached(ModSettingsPath); } }

        public string CurrentWorldFile()
        {
            string w = LoadConf().Get("World");
            if (w.Length == 0) w = "default";
            if (w.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || w.Contains("\\") || w.Contains("/"))
                return Directory.Exists(w) ? Path.Combine(w, "world.json") : w;
            return Path.Combine(WorldsDir, w, "world.json");
        }
    }

    // ---- JSON helpers --------------------------------------------------------------------------------

    public static class Json
    {
        /// <summary>Indented, with the default type resolver set (without one, writing some nodes throws).</summary>
        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };

        public static JsonNode ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs)) return JsonNode.Parse(r.ReadToEnd());
            }
            catch { return null; }
        }

        public static string Str(JsonNode n, string key) { try { JsonNode v = n?[key]; return v == null ? "" : v.ToString(); } catch { return ""; } }
        public static int Int(JsonNode n, string key) { try { return n?[key] == null ? 0 : (int)n[key].GetValue<double>(); } catch { int v; return int.TryParse(Str(n, key), out v) ? v : 0; } }
        public static double Num(JsonNode n, string key) { try { return n?[key] == null ? 0 : n[key].GetValue<double>(); } catch { double v; return double.TryParse(Str(n, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; } }
        public static bool Bool(JsonNode n, string key) { try { return n?[key] != null && n[key].GetValue<bool>(); } catch { return false; } }
        public static DateTime Date(JsonNode n, string key) { DateTime d; return DateTime.TryParse(Str(n, key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d) ? d : DateTime.MinValue; }

        public static IEnumerable<JsonNode> Arr(JsonNode n, string key)
        {
            JsonArray a = null;
            try { a = n?[key] as JsonArray; } catch { }
            return a != null ? a.Where(x => x != null) : Enumerable.Empty<JsonNode>();
        }

        public static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs)) return r.ReadToEnd();
            }
            catch { return ""; }
        }
    }

    // ---- asking a server how it is, as the Join page does ------------------------------------------

    /// <summary>
    /// The server's status question ("SDDSQ1" + nonce, padded to 64 bytes, as a LiteNetLib unconnected message) over UDP to an
    /// address, as players' Join pages ask it: through a tunnel or a forwarded port it shows whether players can reach the server.
    /// </summary>
    public static class ServerQuery
    {
        public class Result { public bool Ok; public int Ms; public string Json = "", Error = ""; }

        public static Result Ask(string host, int port, int timeoutMs = 3000, int tries = 3)
        {
            Result r = new Result();
            try
            {
                System.Net.IPAddress ip;
                if (!System.Net.IPAddress.TryParse(host, out ip))
                    ip = System.Net.Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ip == null) { r.Error = "can't find " + host; return r; }
                byte[] nonce = new byte[8]; new Random().NextBytes(nonce);
                byte[] q = new byte[65];
                q[0] = 8;                                                  // LiteNetLib: an unconnected message
                System.Text.Encoding.ASCII.GetBytes("SDDSQ1").CopyTo(q, 1);
                nonce.CopyTo(q, 7);
                using (System.Net.Sockets.UdpClient u = new System.Net.Sockets.UdpClient())
                {
                    u.Client.ReceiveTimeout = timeoutMs;
                    System.Net.IPEndPoint to = new System.Net.IPEndPoint(ip, port);
                    for (int i = 0; i < tries; i++)
                    {
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        u.Send(q, q.Length, to);
                        try
                        {
                            System.Net.IPEndPoint from = null;
                            byte[] a = u.Receive(ref from);
                            if (a.Length > 15 && System.Text.Encoding.ASCII.GetString(a, 1, 6) == "SDDSR1")
                            {
                                r.Ok = true; r.Ms = (int)sw.ElapsedMilliseconds;
                                r.Json = System.Text.Encoding.UTF8.GetString(a, 15, a.Length - 15);
                                return r;
                            }
                        }
                        catch (System.Net.Sockets.SocketException) { }
                    }
                }
                r.Error = "no answer";
            }
            catch (Exception e) { r.Error = e.Message; }
            return r;
        }

        /// <summary>"host:port" (port defaults to the server's own).</summary>
        public static bool Parse(string address, int defaultPort, out string host, out int port)
        {
            host = (address ?? "").Trim(); port = defaultPort;
            int c = host.LastIndexOf(':');
            if (c > 0 && int.TryParse(host.Substring(c + 1), out int p)) { port = p; host = host.Substring(0, c); }
            return host.Length > 0 && port > 0 && port < 65536;
        }
    }

    // ---- making a server folder --------------------------------------------------------------------

    public static class GameCopy
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        public static readonly string[] LinkedDirs = { "StellarDrive_Data", "MonoBleedingEdge", "D3D12" };

        public static bool IsGameFolder(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "StellarDrive.exe")) && Directory.Exists(Path.Combine(dir, "StellarDrive_Data"));
        }

        public static bool HasMelonLoader(string dir) { return Directory.Exists(Path.Combine(dir, "MelonLoader")) && File.Exists(Path.Combine(dir, "version.dll")); }

        /// <summary>Where the game probably is: beside or above this program, then Steam's usual places.</summary>
        public static string Find()
        {
            List<string> tries = new List<string>();
            string here = AppContext.BaseDirectory;
            for (DirectoryInfo d = new DirectoryInfo(here); d != null; d = d.Parent) tries.Add(d.FullName);
            foreach (string lib in new[] { @"C:\Program Files (x86)\Steam\steamapps\common", @"D:\SteamLibrary\steamapps\common", @"E:\SteamLibrary\steamapps\common" })
                if (Directory.Exists(lib)) tries.AddRange(Directory.GetDirectories(lib, "StellarDrive*"));
            return tries.FirstOrDefault(IsGameFolder) ?? "";
        }

        public const string ServerMod = "DedicatedServer";

        /// <summary>The DLLs in the game's Mods folder, with one DedicatedServer: the newest of the game's and the one beside this program.</summary>
        public static List<string> ModFiles(string game)
        {
            List<string> mods = new List<string>();
            string dir = Path.Combine(game, "Mods");
            if (Directory.Exists(dir)) mods.AddRange(Directory.GetFiles(dir, "*.dll"));
            // DedicatedServer is found by its mod name: its file is DedicatedServer-<version>.dll (or an older DedicatedServer.dll)
            List<string> copies = mods.Where(m => Path.GetFileName(m).StartsWith(ServerMod, StringComparison.OrdinalIgnoreCase) && ModMeta.Read(m).Name == ServerMod).ToList();
            foreach (string mine in Directory.GetFiles(AppContext.BaseDirectory, ServerMod + "*.dll"))
                if (ModMeta.Read(mine).Name == ServerMod) copies.Add(mine);
            if (copies.Count > 1)
            {
                string best = copies.OrderByDescending(f => VersionOf(ModMeta.Read(f).Version)).ThenByDescending(File.GetLastWriteTimeUtc).First();
                mods.RemoveAll(m => copies.Contains(m));
                mods.Add(best);
            }
            else if (copies.Count == 1 && !mods.Contains(copies[0])) mods.Add(copies[0]);
            return mods.OrderBy(Path.GetFileName).ToList();
        }

        private static Version VersionOf(string s) { Version v; return Version.TryParse(s, out v) ? v : new Version(0, 0); }

        private static bool Newer(string a, string b)
        {
            Version x, y;
            return Version.TryParse(a, out x) && (!Version.TryParse(b, out y) || x > y);
        }

        /// <summary>
        /// A server folder from the game: its big folders are hard links (same drive - no extra disk)
        /// or copies; MelonLoader is copied (it keeps logs and caches there); UserData starts with the
        /// game's mod settings; only the chosen mods go in Mods.
        /// </summary>
        public static void Create(string game, string target, IEnumerable<string> modFiles, Action<string> progress)
        {
            Directory.CreateDirectory(target);
            int linked = 0, copied = 0;
            foreach (string d in LinkedDirs)
            {
                string src = Path.Combine(game, d);
                if (!Directory.Exists(src)) continue;
                progress("Linking " + d + " ...");
                foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(target, Path.GetRelativePath(game, f));
                    if (LinkOrCopy(f, dst)) linked++; else copied++;
                }
            }
            progress("Linking the game's own files ...");
            foreach (string f in Directory.GetFiles(game))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                if (LinkOrCopy(f, Path.Combine(target, name))) linked++; else copied++;
            }
            progress("Copying MelonLoader ...");
            CopyDir(Path.Combine(game, "MelonLoader"), Path.Combine(target, "MelonLoader"), p => p.Contains(@"\Logs\") || p.EndsWith("Latest.log"));
            CopyDir(Path.Combine(game, "UserLibs"), Path.Combine(target, "UserLibs"), p => false);
            Directory.CreateDirectory(Path.Combine(target, "Plugins"));
            Directory.CreateDirectory(Path.Combine(target, "UserData"));
            foreach (string f in new[] { "Loader.cfg", "MelonPreferences.cfg" })
            {
                string s = Path.Combine(game, "UserData", f), d = Path.Combine(target, "UserData", f);
                if (File.Exists(s) && !File.Exists(d)) File.Copy(s, d);
            }
            SetMods(target, modFiles, progress);
            progress("Game copy ready (" + linked + " files linked, " + copied + " copied).");
        }

        /// <summary>Beside the server's Mods: mods it has but doesn't load (turned off), and mods taken away (removed).</summary>
        public const string OffFolder = "Mods-off", RemovedFolder = "Mods-removed";

        /// <summary>
        /// Mods/ holds exactly these DLLs. Nothing is ever deleted: a DLL that shouldn't run goes to Mods-off (where the Mods
        /// page still lists it), one coming back from there is moved back, and one from elsewhere (the game folder) is copied.
        /// </summary>
        public static void SetMods(string target, IEnumerable<string> modFiles, Action<string> progress)
        {
            string mods = Path.Combine(target, "Mods"), off = Path.Combine(target, OffFolder);
            Directory.CreateDirectory(mods);
            List<string> list = modFiles.ToList();
            HashSet<string> want = new HashSet<string>(list.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(mods, "*.dll"))
                if (!want.Contains(Path.GetFileName(f)))
                {
                    Directory.CreateDirectory(off);
                    string to = Path.Combine(off, Path.GetFileName(f));
                    if (File.Exists(to)) File.Delete(to);           // an older turned-off copy of the same file
                    File.Move(f, to);
                }
            foreach (string m in list)
            {
                string dst = Path.Combine(mods, Path.GetFileName(m));
                if (string.Equals(Path.GetFullPath(m), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) continue;
                progress("Mod " + Path.GetFileName(m));
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(m)), Path.GetFullPath(off), StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(m, dst);
                }
                else File.Copy(m, dst, true);
            }
        }

        /// <summary>Every mod this server knows of, once each by mod name: on it (Mods), turned off (Mods-off), or in the game folder.</summary>
        public static List<ModMeta> AllMods(string serverFolder, string game, out Dictionary<string, string> newerInGame)
        {
            newerInGame = new Dictionary<string, string>();
            Dictionary<string, ModMeta> by = new Dictionary<string, ModMeta>();
            foreach (string dir in new[] { Path.Combine(serverFolder, "Mods"), Path.Combine(serverFolder, OffFolder) })
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.dll"))
                    {
                        ModMeta m = ModMeta.Read(f);
                        ModMeta have;
                        if (!by.TryGetValue(m.Name, out have)) by[m.Name] = m;      // Mods comes first: what is on the server wins
                    }
            if (IsGameFolder(game))
                foreach (string f in ModFiles(game))
                {
                    ModMeta m = ModMeta.Read(f);
                    ModMeta have;
                    if (!by.TryGetValue(m.Name, out have)) by[m.Name] = m;
                    else if (Newer(m.Version, have.Version)) newerInGame[m.Name] = m.Version;
                }
            return by.Values.ToList();
        }

        /// <summary>The server's copy of each mod replaced by the game folder's where that one is a newer version. What was updated.</summary>
        public static List<string> UpdateNewer(string serverFolder, string game)
        {
            List<string> done = new List<string>();
            if (!IsGameFolder(game)) return done;
            List<ModMeta> fromGame = ModFiles(game).Select(ModMeta.Read).ToList();
            foreach (string dir in new[] { Path.Combine(serverFolder, "Mods"), Path.Combine(serverFolder, OffFolder) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "*.dll"))
                {
                    ModMeta have = ModMeta.Read(f);
                    ModMeta g = fromGame.FirstOrDefault(x => x.Name == have.Name && Newer(x.Version, have.Version));
                    if (g == null) continue;
                    Keep(serverFolder, f, "replaced by " + g.Version);
                    File.Copy(g.File, Path.Combine(dir, Path.GetFileName(g.File)), true);
                    done.Add(have.Name + " " + have.Version + " -> " + g.Version);
                }
            }
            return done;
        }

        /// <summary>A mod file moved to Mods-removed (named with the time), never deleted.</summary>
        public static string Keep(string serverFolder, string file, string why)
        {
            string dir = Path.Combine(serverFolder, RemovedFolder);
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string to = stem + ".dll.old";
            for (int n = 2; File.Exists(to); n++) to = stem + "-" + n + ".dll.old";     // two kept in the same second (say, Mods and Mods-off)
            File.Move(file, to);
            return to;
        }

        /// <summary>
        /// DLLs put on the server (the Mods page's Add, or dropped on it): each into Mods, or Mods-off for a mod that only changes a
        /// player's own screen. A copy of the same mod already there is kept in Mods-removed. Returns what happened, one line each.
        /// </summary>
        public static List<string> AddMods(string serverFolder, IEnumerable<string> files, bool running)
        {
            List<string> said = new List<string>();
            string mods = Path.Combine(serverFolder, "Mods"), off = Path.Combine(serverFolder, OffFolder);
            Directory.CreateDirectory(mods);
            foreach (string f in files)
            {
                if (!f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) { said.Add(Path.GetFileName(f) + ": not a .dll file"); continue; }
                ModMeta m = ModMeta.Read(f);
                if (m.Version == "?") { said.Add(Path.GetFileName(f) + ": not a MelonLoader mod (no MelonInfo in it)"); continue; }
                string old = null;
                foreach (string dir in new[] { mods, off })
                    if (Directory.Exists(dir))
                        foreach (string e in Directory.GetFiles(dir, "*.dll"))
                            if (ModMeta.Read(e).Name == m.Name) old = e;
                bool oldOn = old != null && string.Equals(Path.GetDirectoryName(old), mods, StringComparison.OrdinalIgnoreCase);
                if (old != null && string.Equals(Path.GetFullPath(old), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)) { said.Add(m.Name + " is already there"); continue; }
                if (old != null && oldOn && running) { said.Add(m.Name + ": the running server has " + Path.GetFileName(old) + " loaded; stop it to replace it"); continue; }
                bool on = old != null ? oldOn : ModUse.Default(m.Name) != ModUse.PlayersOnly;
                if (old != null) Keep(serverFolder, old, "replaced");
                string dst = Path.Combine(on ? mods : off, Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
                said.Add(m.Name + " " + m.Version + (old != null ? " replaced " + Path.GetFileName(old) : " added") + (on ? " (on the server" + (running ? ", from its next start)" : ")") : " (not on the server: it only changes a player's own screen)"));
            }
            return said;
        }

        /// <summary>A mod taken off this server altogether (to Mods-removed). The game folder's own copy is left alone.</summary>
        public static string RemoveMod(string serverFolder, ModMeta m, bool running)
        {
            string mods = Path.Combine(serverFolder, "Mods"), off = Path.Combine(serverFolder, OffFolder);
            List<string> mine = new List<string>();
            foreach (string dir in new[] { mods, off })
                if (Directory.Exists(dir))
                    foreach (string e in Directory.GetFiles(dir, "*.dll"))
                        if (ModMeta.Read(e).Name == m.Name) mine.Add(e);
            if (mine.Count == 0) return m.Name + " isn't in this server's folder (only in the game folder)";
            if (running && mine.Any(e => string.Equals(Path.GetDirectoryName(e), mods, StringComparison.OrdinalIgnoreCase))) return "Stop the server first: it has " + m.Name + " loaded.";
            foreach (string e in mine) Keep(serverFolder, e, "removed");
            return m.Name + " removed (kept in " + RemovedFolder + ", in the server's folder)";
        }

        /// <summary>
        /// Has the game in its folder been updated since this server's copy was made? (A Steam update writes new files; the server's
        /// links still point at the old ones, so it would keep running the old build that players no longer have.)
        /// </summary>
        public static bool Outdated(string game, string target)
        {
            if (!IsGameFolder(game) || !Directory.Exists(target)) return false;
            foreach (string rel in new[] { @"StellarDrive_Data\Managed\Assembly-CSharp.dll", @"StellarDrive_Data\globalgamemanagers", "UnityPlayer.dll", "StellarDrive.exe" })
            {
                string a = Path.Combine(game, rel), b = Path.Combine(target, rel);
                if (File.Exists(a) && (!File.Exists(b) || !SameFile(a, b))) return true;
            }
            return false;
        }

        /// <summary>After the game is updated: re-link its files (a link made before an update still points at the old file).</summary>
        public static void Relink(string game, string target, Action<string> progress)
        {
            foreach (string d in LinkedDirs)
            {
                string src = Path.Combine(game, d);
                if (!Directory.Exists(src)) continue;
                progress("Relinking " + d + " ...");
                foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(target, Path.GetRelativePath(game, f));
                    if (File.Exists(dst) && SameFile(f, dst)) continue;
                    if (File.Exists(dst)) File.Delete(dst);
                    LinkOrCopy(f, dst);
                }
            }
            foreach (string f in Directory.GetFiles(game))
            {
                string dst = Path.Combine(target, Path.GetFileName(f));
                if (f.EndsWith(".bat") || f.EndsWith(".log")) continue;
                if (File.Exists(dst) && SameFile(f, dst)) continue;
                if (File.Exists(dst)) File.Delete(dst);
                LinkOrCopy(f, dst);
            }
        }

        private static bool SameFile(string a, string b)
        {
            FileInfo x = new FileInfo(a), y = new FileInfo(b);
            return x.Length == y.Length && x.LastWriteTimeUtc == y.LastWriteTimeUtc;
        }

        private static bool LinkOrCopy(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(dst)) return true;
            if (CreateHardLink(dst, src, IntPtr.Zero)) return true;
            File.Copy(src, dst);
            return false;
        }

        private static void CopyDir(string src, string dst, Func<string, bool> skip)
        {
            if (!Directory.Exists(src)) return;
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (skip(f)) continue;
                string d = Path.Combine(dst, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(d));
                if (!File.Exists(d)) File.Copy(f, d);
            }
        }

        /// <summary>The player's own worlds (to start a server from one).</summary>
        public static List<string> PlayerWorlds()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "CuriousOwlGames", "StellarDrive");
            List<string> r = new List<string>();
            if (!Directory.Exists(root)) return r;
            foreach (string dir in new[] { "WorldSaves" }.Concat(Directory.GetDirectories(root, "saves*").Select(Path.GetFileName)))
            {
                string d = Path.Combine(root, dir);
                if (!Directory.Exists(d)) continue;
                foreach (string w in Directory.GetDirectories(d))
                    if (File.Exists(Path.Combine(w, "world.json"))) r.Add(Path.Combine(w, "world.json"));
            }
            return r;
        }
    }

    public static class Fmt
    {
        public static string Span(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalDays >= 1) return ((int)t.TotalDays) + "d " + t.Hours + "h";
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + "m " + t.Seconds + "s";
            return t.Seconds + "s";
        }

        public static string Bytes(double b)
        {
            if (b >= 1 << 30) return (b / (1 << 30)).ToString("0.0") + " GB";
            if (b >= 1 << 20) return (b / (1 << 20)).ToString("0.0") + " MB";
            if (b >= 1024) return (b / 1024).ToString("0.0") + " KB";
            return b.ToString("0") + " B";
        }
    }
}
