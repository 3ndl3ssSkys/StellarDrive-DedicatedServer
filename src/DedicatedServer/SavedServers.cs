// The servers a player has saved (pinned) on the Join page, and the passwords of servers they have
// joined, in UserData/DedicatedServer-servers.json.
//
//   * Pinned servers are listed at the top of the Join page, above Steam friends, with their name,
//     description, players and whether they answered. Each is asked once when the page opens; the
//     Refresh button asks them all again (joining one also counts as an answer from it).
//   * A password is never asked for up front. A server that has one turns the join away with a
//     code; the game then asks for it, joins again, and keeps it once the server lets the player
//     in. A ban (the server says so with a code) erases it, and the server is marked banned.
//
// The passwords are scrambled with this game's own identity key, so the file alone doesn't show
// them; it is not encryption against someone who has this game's settings too.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace DedicatedServer
{
    internal class SavedServer
    {
        public string Address = "";
        public string Name = "", Desc = "", Game = "";
        public int Players = -1, Max, PingMs;
        public string[] Names = new string[0];
        public bool Pinned, Banned, HasPassword;
        public bool AutoModsOff;                 // mods this server doesn't allow are turned off for it by themselves
        public string Pass = "";                 // scrambled
        public DateTime PinnedAt, LastAnswer, LastJoined;

        public string Icon = "";                 // the icon's hash; the file is UserData/DedicatedServer-icons/<hash>.png
        [JsonIgnore] public string State = "";   // "asking", "online", "offline" (this session only)
        [JsonIgnore] public Texture2D IconTexture;
    }

    internal static class SavedServers
    {
        private static List<SavedServer> _all;
        public static int Version;               // bumps on every change, so the page knows to redraw

        private static string FilePath { get { return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-servers.json"); } }

        public static List<SavedServer> All
        {
            get
            {
                if (_all != null) return _all;
                _all = new List<SavedServer>();
                try { if (File.Exists(FilePath)) _all = JsonConvert.DeserializeObject<List<SavedServer>>(File.ReadAllText(FilePath)) ?? new List<SavedServer>(); }
                catch (Exception e) { Log.Warn("could not read " + FilePath + ": " + e.Message); }
                // 1.2 kept one password for every server: it belonged to the last address
                try
                {
                    string old = Client.JoinPassword != null ? Client.JoinPassword.Value : "";
                    if (!string.IsNullOrEmpty(old) && !string.IsNullOrEmpty(Client.LastAddress.Value))
                    {
                        SavedServer s = Get(Client.LastAddress.Value, true);
                        if (s.Pass.Length == 0) s.Pass = Scramble(old);
                        Client.JoinPassword.Value = "";
                        MelonPreferences.Save();
                        Save();
                    }
                }
                catch { }
                return _all;
            }
        }

        public static IEnumerable<SavedServer> Pinned { get { return All.Where(s => s.Pinned).OrderBy(s => s.PinnedAt); } }

        /// <summary>One spelling per server: host lower-case, with the port.</summary>
        public static string Key(string address)
        {
            string host, err; int port;
            if (!Client.ParseAddress(address, out host, out port, out err)) return (address ?? "").Trim().ToLowerInvariant();
            return host.ToLowerInvariant() + ":" + port;
        }

        public static SavedServer Get(string address, bool make)
        {
            string k = Key(address);
            SavedServer s = All.FirstOrDefault(x => Key(x.Address) == k);
            if (s == null && make) { s = new SavedServer { Address = k }; All.Add(s); }
            return s;
        }

        public static void Save()
        {
            Version++;
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(_all ?? new List<SavedServer>(), Formatting.Indented)); }
            catch (Exception e) { Log.Warn("could not save " + FilePath + ": " + e.Message); }
        }

        public static SavedServer Pin(string address)
        {
            SavedServer s = Get(address, true);
            if (!s.Pinned) { s.Pinned = true; s.PinnedAt = DateTime.UtcNow; }
            Save();
            Ask(s);
            return s;
        }

        /// <summary>A saved server moved (a new playit.gg address, say): same entry, kept password, asked again. Null when done, else why not.</summary>
        public static string ChangeAddress(SavedServer s, string address)
        {
            string host, err; int port;
            if (!Client.ParseAddress(address, out host, out port, out err)) return err;
            string k = Key(address);
            if (k == Key(s.Address)) return null;
            SavedServer other = Get(k, false);
            if (other != null && other != s && other.Pinned) return "You already have " + k + " saved.";
            if (other != null && other != s) All.Remove(other);
            s.Address = k;
            s.State = ""; s.PingMs = 0; s.Players = 0;
            Save();
            Ask(s);
            return null;
        }

        public static void Unpin(SavedServer s)
        {
            s.Pinned = false;
            if (s.Pass.Length == 0 && !s.Banned) All.Remove(s);     // nothing left worth keeping
            Save();
        }

        // ---- asking servers how they are ---------------------------------------------------

        public static void Ask(SavedServer s)
        {
            if (s.State == "asking") return;
            s.State = "asking";
            Version++;
            Query.AskServer(s.Address, (info, ms) =>
            {
                if (info != null)
                {
                    s.State = "online";
                    s.Name = info.name ?? ""; s.Desc = info.desc ?? ""; s.Game = info.game ?? "";
                    s.Players = info.players; s.Max = info.max; s.Names = info.names ?? new string[0];
                    s.HasPassword = info.password; s.PingMs = ms;
                    s.LastAnswer = DateTime.UtcNow;
                    if (string.IsNullOrEmpty(info.icon)) { s.Icon = ""; s.IconTexture = null; }
                    else if (info.icon != s.Icon || !File.Exists(IconFile(info.icon))) FetchIcon(s, info.icon);
                    else s.Icon = info.icon;
                }
                else s.State = "offline";
                Save();
            });
        }

        public static void AskAll() { foreach (SavedServer s in Pinned) Ask(s); }

        // ---- icons ----------------------------------------------------------------------------

        public static string IconFile(string hash) { return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-icons", hash + ".png"); }

        private static void FetchIcon(SavedServer s, string hash)
        {
            Query.GetFile(s.Address, "icon", (bytes, err) =>
            {
                if (bytes == null || bytes.Length > ServerInfo.MaxIcon || !ServerInfo.Hash(bytes).StartsWith(hash)) { Log.Msg("icon of " + s.Address + ": " + (err ?? "didn't match")); return; }
                KeepIcon(s.Address, hash, bytes);
            });
        }

        /// <summary>A server's icon, kept on disk by its hash and shown on its row.</summary>
        public static void KeepIcon(string address, string hash, byte[] png)
        {
            try
            {
                if (string.IsNullOrEmpty(hash) || png == null || png.Length == 0) return;
                string f = IconFile(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                if (!File.Exists(f)) File.WriteAllBytes(f, png);
                SavedServer s = Get(address, false);
                if (s == null) return;
                s.Icon = hash;
                s.IconTexture = null;
                Save();
            }
            catch (Exception e) { Log.Once("keep-icon", "could not keep a server's icon: " + e.Message); }
        }

        public static Texture2D IconOf(SavedServer s)
        {
            if (s.IconTexture != null || string.IsNullOrEmpty(s.Icon)) return s.IconTexture;
            try { if (File.Exists(IconFile(s.Icon))) s.IconTexture = ServerInfo.LoadIcon(File.ReadAllBytes(IconFile(s.Icon))); } catch { }
            return s.IconTexture;
        }

        /// <summary>Joined: what the server said about itself counts as an answer too.</summary>
        public static void OnInfo(string address, DsServerInfo i)
        {
            SavedServer s = Get(address, false);
            if (s == null) return;
            s.Name = i.Name ?? ""; s.Desc = i.Desc ?? ""; s.Players = i.Players; s.Max = i.Max;
            s.State = "online"; s.LastAnswer = DateTime.UtcNow;
            Save();
        }

        // ---- passwords and bans --------------------------------------------------------------

        /// <summary>The password typed for the join in progress (kept once the server lets us in).</summary>
        public static string Typed;

        public static string PasswordFor(string address)
        {
            if (!string.IsNullOrEmpty(Typed)) return Typed;
            SavedServer s = Get(address, false);
            return s != null ? Unscramble(s.Pass) : "";
        }

        /// <summary>The server's verdict on our join: keep or forget the password, note a ban.</summary>
        public static void OnVerdict(string address, DsVerdict v)
        {
            if (string.IsNullOrEmpty(address)) return;
            string codes = v.Missing ?? "";
            SavedServer s = Get(address, v.Accepted || codes.Contains(Gate.CodeBanned) || Typed != null);
            if (s == null) return;
            s.State = "online";
            s.LastAnswer = DateTime.UtcNow;
            if (v.Accepted)
            {
                s.LastJoined = DateTime.UtcNow;
                s.Banned = false;
                if (!string.IsNullOrEmpty(Typed)) s.Pass = Scramble(Typed);
            }
            else if (codes.Contains(Gate.CodeBanned)) { s.Banned = true; s.Pass = ""; }
            else if (codes.Contains(Gate.CodeWrongPassword)) s.Pass = "";
            Typed = null;
            Save();
        }

        public static bool NeedsPassword(DsVerdict v) { string c = v.Missing ?? ""; return !v.Accepted && (c.Contains(Gate.CodePassword) || c.Contains(Gate.CodeWrongPassword)); }
        public static bool WrongPassword(DsVerdict v) { return !v.Accepted && (v.Missing ?? "").Contains(Gate.CodeWrongPassword); }

        private static byte[] Pad()
        {
            string k = Client.IdentityKey != null && !string.IsNullOrEmpty(Client.IdentityKey.Value) ? Client.IdentityKey.Value : "StellarDrive";
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("saved-servers|" + k));
        }

        private static string Scramble(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            byte[] b = Encoding.UTF8.GetBytes(p), k = Pad();
            for (int i = 0; i < b.Length; i++) b[i] ^= k[i % k.Length];
            return Convert.ToBase64String(b);
        }

        private static string Unscramble(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                byte[] b = Convert.FromBase64String(s), k = Pad();
                for (int i = 0; i < b.Length; i++) b[i] ^= k[i % k.Length];
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }
}
