// Who you are on servers, kept outside the game's folder so it carries over to a new game version.
//
// A new StellarDrive build usually means a new game folder, and with it a fresh MelonPreferences.cfg:
// the Name box empty, and (worse) a new identity key and no-Steam id. Servers tie a character to the
// key, so the new install was turned away as "a player from another computer's game". This file -
// %APPDATA%\StellarDrive Dedicated Server\player.json - holds the name, the no-Steam id and the key
// for every install of the mod on this Windows account:
//   - the name follows the last change made in any install;
//   - an install with no id or key yet takes the shared ones (so a new version is still you);
//   - an install that already has its own id or key keeps it (a character tied to it stays reachable),
//     and the shared file only learns one when it has none.
// Test builds keep it in the test game's own UserData (or SDD_DS_PLAYER_FILE), never the real one.
using System;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;

namespace DedicatedServer
{
    internal static class Identity
    {
        public class Shared
        {
            public string Name = "";
            public string NoSteamId = "";
            public string IdentityKey = "";
            public DateTime NameChanged;
        }

        public static string FilePath
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("SDD_DS_PLAYER_FILE");
                if (!string.IsNullOrEmpty(env)) return env;
#if DSTEST
                return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-player-shared.json");
#else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StellarDrive Dedicated Server", "player.json");
#endif
            }
        }

        public static string LastSync = "";        // what Sync did (for the log and the probe)

        private static Shared Load()
        {
            try { if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<Shared>(File.ReadAllText(FilePath)) ?? new Shared(); }
            catch (Exception e) { Log.Warn("your player file " + FilePath + " couldn't be read (" + e.Message + "); starting a new one"); }
            return new Shared();
        }

        private static void Save(Shared s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(s, Formatting.Indented));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Log.Warn("your player file " + FilePath + " couldn't be written: " + e.Message); }
        }

        /// <summary>At start: this install and the shared file agree on who you are.</summary>
        public static void Sync()
        {
            Shared s = Load();
            bool sharedChanged = false, localChanged = false;
            string did = "";

            string local = Client.CleanName(Client.PlayerName.Value);
            if (s.Name.Length > 0 && local != s.Name) { Client.PlayerName.Value = s.Name; localChanged = true; did += "name '" + s.Name + "' from the shared file; "; }
            else if (s.Name.Length == 0 && local.Length > 0) { s.Name = local; s.NameChanged = DateTime.UtcNow; sharedChanged = true; did += "name '" + local + "' kept for other installs; "; }

            if (string.IsNullOrEmpty(Client.OfflineId.Value) && s.NoSteamId.Length > 0) { Client.OfflineId.Value = s.NoSteamId; localChanged = true; did += "no-Steam id from the shared file; "; }
            else if (s.NoSteamId.Length == 0 && !string.IsNullOrEmpty(Client.OfflineId.Value)) { s.NoSteamId = Client.OfflineId.Value; sharedChanged = true; did += "no-Steam id kept for other installs; "; }

            if (string.IsNullOrEmpty(Client.IdentityKey.Value) && s.IdentityKey.Length >= 32) { Client.IdentityKey.Value = s.IdentityKey; localChanged = true; did += "identity key from the shared file; "; }
            else if (s.IdentityKey.Length < 32 && !string.IsNullOrEmpty(Client.IdentityKey.Value) && Client.IdentityKey.Value.Length >= 32) { s.IdentityKey = Client.IdentityKey.Value; sharedChanged = true; did += "identity key kept for other installs; "; }

            if (sharedChanged) Save(s);
            if (localChanged) try { MelonPreferences.Save(); } catch { }
            LastSync = did.Length > 0 ? did.TrimEnd(' ', ';') : "already the same";
            Log.Msg("your player file (" + FilePath + "): " + LastSync);
        }

        /// <summary>The Name box changed: every install gets it.</summary>
        public static void NameChanged(string name)
        {
            Shared s = Load();
            if (s.Name == (name ?? "")) return;
            s.Name = name ?? ""; s.NameChanged = DateTime.UtcNow;
            Save(s);
        }

        /// <summary>A no-Steam id or key was just made here: the shared file keeps it when it has none.</summary>
        public static void Made()
        {
            Shared s = Load();
            bool changed = false;
            if (s.NoSteamId.Length == 0 && !string.IsNullOrEmpty(Client.OfflineId.Value)) { s.NoSteamId = Client.OfflineId.Value; changed = true; }
            if (s.IdentityKey.Length < 32 && !string.IsNullOrEmpty(Client.IdentityKey.Value) && Client.IdentityKey.Value.Length >= 32) { s.IdentityKey = Client.IdentityKey.Value; changed = true; }
            if (changed) Save(s);
        }
    }
}
