// Server settings. They live in their own file, <data>/dedicatedserver.conf (MelonPreferences
// format), so a server's settings never mix with a player's MelonPreferences.cfg, and a second
// server on the same install can have its own (-dsdata <folder>). The Server Manager writes this
// file too. (Before 1.2 it was server.cfg; that is carried over once.)
//
// Every setting can be changed while the server runs: from its console with `set`, in game by an
// admin with `/set`, or from the Server Manager. Port, BindAddress, World, UseSteam and ConsoleInput
// only take effect on the next start (the server says so).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;

namespace DedicatedServer
{
    internal class ServerConfig
    {
        public const string FileName = "dedicatedserver.conf";

        public MelonPreferences_Category Cat;
        public MelonPreferences_Entry<string> ServerName, Description, World, BindAddress, Motd, RequiredMods, OptionalMods,
                                              DevTools, Research, Password, Admins,
                                              StyleServer, StyleAnnounce, StyleAlert, StyleWelcome, StyleJoin, StyleLeave, StyleGameNotice,
                                              StylePlayerName, StylePlayerText, MessagePrefix,
                                              StyleWhisper, StyleMention, PlayerMarkers, AllowedExtraMods, DebugLog,
                                              RestartTimes, RestartWarnings, RestartReason, RestartMessages;
        public MelonPreferences_Entry<int> Port, MaxPlayers, AutoSaveMinutes, FrameRate, BackupsToKeep, DeleteBackupsAfterDays, ShipBinDays,
                                           RespawnTreesMinutes, RespawnOresMinutes, RespawnClearance, OnScreenSeconds, WatchdogSeconds, SpreadSpawnKm,
                                           ResourceLogMinutes, MemoryLimitPercent, SleepMaxStepMs;
        public MelonPreferences_Entry<bool> SaveWhenPlayerLeaves, PauseWhenEmpty, RequireMods, MatchModVersions,
                                            Whitelist, ErrorsToChat, UseSteam, ConsoleInput, NetGuard, ProtectNames, TrackBandwidth, GroundRescue, SleepAirFlow, LimitPartInputs,
                                            PlayerChatFormatting, ShareMods, FixBadSignals, RejectExtraMods, AlertsOnScreen,
                                            AdminsSeeAllMarkers, LogWhispers, ColoredMarkers, SpreadSpawns;

        /// <summary>Settings that need a restart, by name.</summary>
        public static readonly HashSet<string> RestartOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Port", "BindAddress", "World", "UseSteam", "ConsoleInput" };

        /// <summary>Settings with a fixed set of values.</summary>
        public static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DevTools", new[] { "admins", "everyone", "nobody" } },
            { "Research", new[] { "shared", "individual" } },
            { "PlayerMarkers", new[] { "everyone", "friends", "nobody" } },
        };

        public const string DefaultOptional =
            "Demo Verify Harness, Demo Zoom, Demo Build Pick, Demo Mod List, Demo Ship Tools, ExtraCameras, Performance+";

        public static ServerConfig Load(string dataDir)
        {
            ServerConfig c = new ServerConfig();
            string path = Path.Combine(dataDir, FileName);
            string old = Path.Combine(dataDir, "server.cfg");
            if (!File.Exists(path) && File.Exists(old))
            {
                try { File.Copy(old, path); Log.Msg("settings carried over from server.cfg to " + FileName); } catch { }
            }
            c.Cat = MelonPreferences.CreateCategory("DedicatedServerHost", "Dedicated Server (server settings)");
            try { c.Cat.SetFilePath(path, true, false); }
            catch (Exception e) { Log.Warn(FileName + " could not be used (" + e.Message + "); settings go to MelonPreferences.cfg"); }
            MelonPreferences_Category k = c.Cat;
            c.ServerName = k.CreateEntry("ServerName", "StellarDrive Dedicated Server", description: "Shown to players when they join.");
            c.Description = k.CreateEntry("Description", "", description: "A line or two about the server, shown under its name in players' saved servers on the Join page.");
            c.World = k.CreateEntry("World", "default", description: "A world name (kept in <data>/worlds/<name>/world.json, created new if missing) or the full path of a world.json. Restart to change.");
            c.Port = k.CreateEntry("Port", 7777, description: "UDP port players connect to (forward it, or point playit.gg / a tunnel at it). Restart to change.");
            c.BindAddress = k.CreateEntry("BindAddress", "", description: "IPv4 address to listen on. Empty = every network interface. Restart to change.");
            c.MaxPlayers = k.CreateEntry("MaxPlayers", 8, description: "Players allowed on at once. Admins may join when it is full.");
            c.Motd = k.CreateEntry("Motd", "", description: "Message of the day, sent to each player as they join. Empty = none.");
            c.Password = k.CreateEntry("Password", "", description: "A password players must type on the Join page. Empty = none.");
            c.Admins = k.CreateEntry("Admins", "", description: "Comma-separated player names or Steam ids who are admins. A listed name becomes admin when that player first joins (with ProtectNames on, nobody else can take the name first). Admins made in game with op are kept too.");
            c.ProtectNames = k.CreateEntry("ProtectNames", true, description: "A name belongs to the first player who joins with it: nobody else can join with it. (A player's own character is always tied to their game, never to their name.)");
            c.DevTools = k.CreateEntry("DevTools", "admins", description: "Who may use the game's developer tools (cheat flying, free build) and Ship Tools: admins, everyone or nobody.");
            c.Research = k.CreateEntry("Research", "shared", description: "shared = one research tree for the whole server (the game's own way). individual = each player researches (and pays) for themselves.");
            c.AutoSaveMinutes = k.CreateEntry("AutoSaveMinutes", 10, description: "Save the world this often while anyone is on. 0 = never.");
            c.BackupsToKeep = k.CreateEntry("BackupsToKeep", 10, description: "Each autosave first keeps the previous world as backup_<date>.json. This many are kept (the oldest go first).");
            c.DeleteBackupsAfterDays = k.CreateEntry("DeleteBackupsAfterDays", 0, description: "Also delete backups older than this many days. 0 = only BackupsToKeep counts.");
            c.ShipBinDays = k.CreateEntry("ShipBinDays", 30, description: "Deleted ships can be restored for this many days (ship bin). 0 = keep them all.");
            c.SaveWhenPlayerLeaves = k.CreateEntry("SaveWhenPlayerLeaves", true, description: "Save as soon as a player leaves.");
            c.PauseWhenEmpty = k.CreateEntry("PauseWhenEmpty", true, description: "With nobody on, the world stands still (the game would otherwise run its clock at up to 50x, as when everyone sleeps).");
            c.RequireMods = k.CreateEntry("RequireMods", true, description: "Turn away players who don't have the mods this server needs.");
            c.RequiredMods = k.CreateEntry("RequiredMods", "auto", description: "Comma-separated mod names a player must have, or 'auto' = every mod on this server except OptionalMods. The DedicatedServer mod itself is always required.");
            c.OptionalMods = k.CreateEntry("OptionalMods", DefaultOptional, description: "With RequiredMods = auto: mods on this server a player may leave out (ones that change only their own screen).");
            c.MatchModVersions = k.CreateEntry("MatchModVersions", true, description: "A required mod must be the same version as the server's, not just present.");
            c.Whitelist = k.CreateEntry("Whitelist", false, description: "Only players on the whitelist (and admins) may join.");
            c.FrameRate = k.CreateEntry("FrameRate", 120, description: "Server loop rate. Network messages are read once per loop, so this bounds the delay added on the server (120 = at most ~8 ms). The world itself steps at the game's tick rate either way.");
            c.TrackBandwidth = k.CreateEntry("TrackBandwidth", false, description: "Count the bytes sent and received (shown on the Uptime page / in stats).");
            c.GroundRescue = k.CreateEntry("GroundRescue", true, description: "Ships and players never end up inside the ground: a hole in the ground under anything is made again, a crash can't push a ship on into the ground, and a ship sinking into it or a player whose feet are in it is put back on top. (Done by NecessaryFixes instead when that mod is loaded.)");
            c.SleepAirFlow = k.CreateEntry("SleepAirFlow", true, description: "While everyone on is asleep, pipes, vents, tanks and every other ship part keep working at the sped-up time (the game stops them all but keeps the sleepers breathing, so they wake up choking). (Done by NecessaryFixes instead when that mod is loaded.)");
            c.SleepMaxStepMs = k.CreateEntry("SleepMaxStepMs", 100, description: "While everyone sleeps: the longest piece of sped-up time ship parts work out in one go, in milliseconds (20-1000). Smaller is closer to awake, larger is cheaper.");
            const string styleHelp = " A list of: color=<#rrggbb or red/orange/yellow/green/teal/blue/lightblue/purple/pink/white/grey>, bold, italic, underline, strike, caps, smallcaps, size=<percent>, highlight=<colour>. The Server Manager has a picker for it.";
            c.StyleServer = k.CreateEntry("StyleServer", "color=#8FD3FF", description: "How replies to commands look in chat." + styleHelp);
            c.StyleAnnounce = k.CreateEntry("StyleAnnounce", "color=#4FC3F7 bold size=115", description: "How `say` and announcements look." + styleHelp);
            c.StyleAlert = k.CreateEntry("StyleAlert", "color=#FFB84D bold size=120", description: "How restart and shutdown warnings and `alert` look." + styleHelp);
            c.StyleWelcome = k.CreateEntry("StyleWelcome", "color=#9BE7A0 bold", description: "How the message of the day looks." + styleHelp);
            c.StyleJoin = k.CreateEntry("StyleJoin", "color=#9BE7A0", description: "How \"<name> has joined the game.\" looks." + styleHelp);
            c.StyleLeave = k.CreateEntry("StyleLeave", "color=#B8C2CC italic", description: "How \"<name> has left the game.\" looks." + styleHelp);
            c.StyleGameNotice = k.CreateEntry("StyleGameNotice", "", description: "How the game's other notices look (sleeping/waiting, errors). Empty = the game's own look." + styleHelp);
            c.StylePlayerName = k.CreateEntry("StylePlayerName", "color=#FFE16B bold", description: "How a player's name looks before what they say." + styleHelp);
            c.StylePlayerText = k.CreateEntry("StylePlayerText", "", description: "How what players say looks. Empty = the game's own look." + styleHelp);
            c.MessagePrefix = k.CreateEntry("MessagePrefix", "[Server]", description: "Put before announcements and alerts. Empty = nothing.");
            c.PlayerChatFormatting = k.CreateEntry("PlayerChatFormatting", false, description: "Let players use colour/size tags in their own chat. Off: what they type is shown exactly as typed, so nobody can pass for the server.");
            c.StyleWhisper = k.CreateEntry("StyleWhisper", "color=#C79BFF italic", description: "How whispers (/w) look, to both players." + styleHelp);
            c.StyleMention = k.CreateEntry("StyleMention", "color=#FFD54F bold", description: "How @name looks in chat. The player named also hears a ping and sees the line highlighted." + styleHelp);
            c.FixBadSignals = k.CreateEntry("FixBadSignals", true, description: "Signal values that are not a number (NaN), infinite or too big for a float are set to 0 and stay 0 until the maths gives a number again, in cables and in the parts that hold them; ships saved with no real place go to the ship bin. (Done by NecessaryFixes instead when that mod is loaded.)");
            c.LimitPartInputs = k.CreateEntry("LimitPartInputs", true, description: "Pumps, valves, docking-door magnets and warp drives get at most 1 (and -1) from a signal, the most the game's own levers and seats give: a huge number from a circuit can't run them thousands of times faster than they were made to (or send a warp drive to infinity). Cables, displays and math parts still carry the full number.");
            c.RespawnTreesMinutes = k.CreateEntry("RespawnTreesMinutes", 120, description: "Trees, bushes and rocks grow back this many minutes after they are cut or knocked down. 0 = never. Anything in the way (a ship, a build, a player) is left alone; they grow back around it.");
            c.RespawnOresMinutes = k.CreateEntry("RespawnOresMinutes", 0, description: "Ore deposits (on planets and asteroids) come back this many minutes after they are mined. 0 = never.");
            c.RespawnClearance = k.CreateEntry("RespawnClearance", 4, description: "Metres kept clear around ships, builds and players when trees and ores grow back.");
            c.AlertsOnScreen = k.CreateEntry("AlertsOnScreen", true, description: "Restart and shutdown warnings and `alert` also show in the middle of every player's screen, over menus. Enter closes it.");
            c.OnScreenSeconds = k.CreateEntry("OnScreenSeconds", 5, description: "How long an on-screen announcement stays up (it fades in and out as well).");
            c.PlayerMarkers = k.CreateEntry("PlayerMarkers", "everyone", description: "Whose markers (the dots and arrows that show where other players are) a player sees: everyone, friends (players they have added with /friend, both ways) or nobody.");
            c.AdminsSeeAllMarkers = k.CreateEntry("AdminsSeeAllMarkers", true, description: "Admins see every player's marker whatever PlayerMarkers says.");
            c.ColoredMarkers = k.CreateEntry("ColoredMarkers", true, description: "Each player's marker has its own colour (players pick one with /color). Looking straight at a marker shows the player's name.");
            c.RejectExtraMods = k.CreateEntry("RejectExtraMods", false, description: "Turn away players who have mods this server doesn't run, unless they are listed in AllowedExtraMods. They are told which mods it was, and the Server Manager lists them so they can be allowed.");
            c.AllowedExtraMods = k.CreateEntry("AllowedExtraMods", "Demo Zoom, Demo Build Pick, Demo Mod List, ExtraCameras, Performance+, Demo Verify Harness", description: "With RejectExtraMods on: mods players may have that this server doesn't run (comma separated names).");
            c.LogWhispers = k.CreateEntry("LogWhispers", true, description: "Whispers between players are written to the server log (for moderation).");
            c.SpreadSpawns = k.CreateEntry("SpreadSpawns", false, description: "A player new to this world starts at a spot of their own (dry, flat, away from others) instead of everyone at one place, and respawns there when they die without a bed.");
            c.SpreadSpawnKm = k.CreateEntry("SpreadSpawnKm", 3, description: "With SpreadSpawns: how far from the world's starting point the spots can be, in km. 0 = anywhere on the planet.");
            c.NetGuard = k.CreateEntry("NetGuard", true, description: "Keep the game port listening: every 10 s the server checks it still receives (and asks itself the players' status question on 127.0.0.1). A port Windows stopped (a network change can do that) is opened again; if that doesn't bring it back, the server saves and restarts. Also checks the Server Manager's 'Address players use' every 2 minutes and logs when players can't reach the server through it.");
            c.WatchdogSeconds = k.CreateEntry("WatchdogSeconds", 120, description: "If the server's main loop stops for this long (a hang), the server logs where it was stuck and exits with code 3 so the Server Manager or StartDedicatedServer.bat starts it again. 0 = off.");
            c.RestartTimes = k.CreateEntry("RestartTimes", "", description: "Restart every day at these times, in this machine's time: like 04:00, 16:30 (a day first for once a week: Sun 04:00). Empty = none.");
            c.RestartWarnings = k.CreateEntry("RestartWarnings", "10m, 5m, 2m, 1m, 30s, 10s", description: "When players are warned before a restart or stop (planned or timed): like 10m, 5m, 1m, 30s.");
            c.RestartMessages = k.CreateEntry("RestartMessages", "", description: "Your own words for each restart/stop warning, by how long before: like 10m: The server restarts in [time] for a quick tidy-up || 1m!: Last call - [time] left! (|| between them; ! after the time = also in the middle of everyone's screen, else chat only; * instead of a time = every other warning). [time] is the real time left when it is sent, [reason] the reason, [action] restart or shut down. A warning with no words here says 'The server will restart in [time]: [reason]'.");
            c.RestartReason = k.CreateEntry("RestartReason", "", description: "What players are told a planned restart is for. Empty = 'planned restart'.");
            c.ResourceLogMinutes = k.CreateEntry("ResourceLogMinutes", 5, description: "Write the server's memory and CPU, and the machine's, to the log this often. 0 = only in resources.csv (every minute).");
            c.MemoryLimitPercent = k.CreateEntry("MemoryLimitPercent", 60, description: "If the server uses more than this share of the machine's memory for a minute, it warns everyone and restarts (saving first). 0 = off.");
            c.DebugLog = k.CreateEntry("DebugLog", "off", description: "Extra detail in the log for tracking down problems: off, all, or a list of: net, gate, chat, ground, respawn, signals, markers, query, install, saves.");
            c.ShareMods = k.CreateEntry("ShareMods", true, description: "A player missing mods this server needs can download them from it (they're asked first, shown what and how big, and warned). Off: they're only told what to install.");
            c.ErrorsToChat = k.CreateEntry("ErrorsToChat", false, description: "The game posts every server error into the players' chat. Off: they go to the server log only.");
            c.UseSteam = k.CreateEntry("UseSteam", false, description: "Start Steam in the server process. Not needed: players join by address and bring their own Steam identity. Restart to change.");
            c.ConsoleInput = k.CreateEntry("ConsoleInput", true, description: "Read commands typed into the server's console window. Restart to change.");
            try { c.Cat.SaveToFile(false); } catch { }
            return c;
        }

        public void Save() { try { Cat.SaveToFile(false); } catch (Exception e) { Log.Warn("could not save " + FileName + ": " + e.Message); } }

        public IEnumerable<MelonPreferences_Entry> Entries { get { return Cat.Entries; } }

        public MelonPreferences_Entry Find(string name)
        {
            foreach (MelonPreferences_Entry e in Cat.Entries)
                if (string.Equals(e.Identifier, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static string Show(MelonPreferences_Entry e)
        {
            object v = e.BoxedValue;
            if (v is bool) return (bool)v ? "on" : "off";
            if (v is string)
            {
                if (e.Identifier == "Password") return ((string)v).Length == 0 ? "(none)" : "(set)";
                return "\"" + v + "\"";
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>Parses and sets; returns an error, or null when set.</summary>
        public string Set(MelonPreferences_Entry e, string text)
        {
            text = (text ?? "").Trim();
            object cur = e.BoxedValue;
            try
            {
                if (cur is bool)
                {
                    string t = text.ToLowerInvariant();
                    if (t == "on" || t == "true" || t == "yes" || t == "1") e.BoxedValue = true;
                    else if (t == "off" || t == "false" || t == "no" || t == "0") e.BoxedValue = false;
                    else return "expected on or off";
                }
                else if (cur is int)
                {
                    int n;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return "expected a whole number";
                    string err = CheckRange(e.Identifier, n);
                    if (err != null) return err;
                    e.BoxedValue = n;
                }
                else
                {
                    if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"') text = text.Substring(1, text.Length - 2);
                    string bad = null;
                    if (e.Identifier == "RestartTimes") Restarts.ParseTimes(text, out bad);
                    if (e.Identifier == "RestartWarnings") Restarts.ParseWarnings(text, out bad);
                    if (e.Identifier == "RestartMessages") Restarts.ParseMessages(text, out bad);
                    if (bad != null) return bad;
                    string[] allowed;
                    if (Choices.TryGetValue(e.Identifier, out allowed))
                    {
                        string t = text.ToLowerInvariant();
                        if (Array.IndexOf(allowed, t) < 0) return "expected one of: " + string.Join(", ", allowed);
                        text = t;
                    }
                    e.BoxedValue = text;
                }
            }
            catch (Exception ex) { return ex.Message; }
            Save();
            return null;
        }

        private static string CheckRange(string name, int n)
        {
            switch (name)
            {
                case "Port": return n >= 1 && n <= 65535 ? null : "1-65535";
                case "MaxPlayers": return n >= 1 && n <= 250 ? null : "1-250";
                case "AutoSaveMinutes": return n >= 0 && n <= 1440 ? null : "0-1440";
                case "FrameRate": return n >= 15 && n <= 500 ? null : "15-500";
                case "BackupsToKeep": return n >= 0 && n <= 1000 ? null : "0-1000";
                case "DeleteBackupsAfterDays": return n >= 0 && n <= 3650 ? null : "0-3650";
                case "ShipBinDays": return n >= 0 && n <= 3650 ? null : "0-3650";
                case "ResourceLogMinutes": return n >= 0 && n <= 1440 ? null : "0-1440";
                case "MemoryLimitPercent": return n == 0 || (n >= 20 && n <= 95) ? null : "0, or 20-95";
                case "SleepMaxStepMs": return n >= 20 && n <= 1000 ? null : "20-1000";
                case "RespawnTreesMinutes":
                case "RespawnOresMinutes": return n >= 0 && n <= 525600 ? null : "0-525600";
                case "RespawnClearance": return n >= 0 && n <= 100 ? null : "0-100";
                case "OnScreenSeconds": return n >= 1 && n <= 120 ? null : "1-120";
                case "WatchdogSeconds": return n == 0 || (n >= 20 && n <= 3600) ? null : "0, or 20-3600";
                case "SpreadSpawnKm": return n >= 0 && n <= 100000 ? null : "0-100000";
            }
            return null;
        }
    }
}
