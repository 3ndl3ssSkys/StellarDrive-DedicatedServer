// Who may join. The game authenticates a player with one server RPC,
// PlayerNetworkNameAuth.AuthenticatePlayer_ServerRpc (Steam id, name, game version); its body
// checks "already connected" and "game version" and then spawns the player. The gate is a
// prefix on that body, so a player turned away never gets a player object at all.
//
// Checked in order: game version, banned (player or address), identity (this player's character
// belongs to the game that first joined with it), protected name, password, whitelist, server full
// (admins may still join), and mods.
//
// Identity: every copy of the client mod has a random key of its own (MelonPreferences). On
// hello the server sends its id and a fresh nonce; the client answers HMAC(key, server id) - the
// same every time for that game and this server, useless on any other server - and, if the server
// has a password, HMAC(password, nonce). The first proof a player id arrives with is kept (hashed);
// after that only that game can be that player. An admin can `unlink` a player who moved PC.
// The mod list comes from the client half of this mod (DsHello), which it sends as soon
// as it connects - before the game's own auth RPC, which waits for the scene to load.
// A client with no DedicatedServer mod never says hello, and is turned away after a short wait.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet.Connection;
using FishNet.Transporting;
using HarmonyLib;
using Managers.Interface.Model;
using MelonLoader;
using Players.Network;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Gate
    {
        public const float HelloWait = 8f;

        private class Pending
        {
            public object Instance;
            public object[] Args;
            public NetworkConnection Conn;
            public float Since;
        }

        private static MethodInfo _authBody;
        private static bool _bypass;
        private static readonly Dictionary<int, DsHello> _hellos = new Dictionary<int, DsHello>();
        private static readonly Dictionary<int, DsProof> _proofs = new Dictionary<int, DsProof>();
        private static readonly Dictionary<int, string> _nonces = new Dictionary<int, string>();
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly List<KeyValuePair<NetworkConnection, float>> _drop = new List<KeyValuePair<NetworkConnection, float>>();

        public const string GameLine = "@game";
        public static int Accepted, Rejected;
        public static string LastRejection = "";

        public static void Init(HarmonyLib.Harmony h)
        {
            _authBody = typeof(PlayerNetworkNameAuth).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name.StartsWith("RpcLogic___AuthenticatePlayer_ServerRpc"));
            if (_authBody == null) { Log.Error("PATCH MISSING: the game's player authentication (mods, bans and slots can't be checked)"); return; }
            h.Patch(_authBody, prefix: new HarmonyMethod(typeof(Gate).GetMethod("AuthPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
            Net.OnHello = OnHello;
            Net.OnProof = (conn, proof) => { _proofs[conn.ClientId] = proof; };
        }

        private static void OnHello(NetworkConnection conn, DsHello hello)
        {
            _hellos[conn.ClientId] = hello;
            int n = string.IsNullOrEmpty(hello.Mods) ? 0 : hello.Mods.Split('\n').Length;
            Log.Msg("client " + conn.ClientId + " has DedicatedServer " + hello.ModVersion + " and " + n + " mod(s)");
            string nonce = Guid.NewGuid().ToString("N");
            _nonces[conn.ClientId] = nonce;
            Net.ToClient(conn, new DsChallenge { ServerId = Server.Data.D.ServerId, Nonce = nonce, NeedPassword = (Server.Cfg.Password.Value ?? "").Length > 0 });
        }

        public static string Hmac(byte[] key, string text)
        {
            using (System.Security.Cryptography.HMACSHA256 h = new System.Security.Cryptography.HMACSHA256(key))
                return BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha(string text)
        {
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        public static bool ListedAdmin(ulong id, string name)
        {
            foreach (string a in (Server.Cfg.Admins.Value ?? "").Split(','))
            {
                string t = a.Trim();
                if (t.Length == 0) continue;
                if (t == id.ToString() || string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string AddressOf(NetworkConnection conn)
        {
            try { string a = conn.GetAddress(); return a ?? ""; } catch { return ""; }
        }

        // ---- the gate ------------------------------------------------------------------------

        private static bool AuthPrefix(object __instance, object[] __args)
        {
            if (_bypass) return true;
            NetworkConnection conn = __args.Length > 3 ? __args[3] as NetworkConnection : null;
            if (conn == null) return true;
            _pending.RemoveAll(p => p.Conn == conn);
            _pending.Add(new Pending { Instance = __instance, Args = (object[])__args.Clone(), Conn = conn, Since = Time.realtimeSinceStartup });
            return false;   // decided in Update, once the hello is in (normally the same frame)
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (!p.Conn.IsActive) { _pending.RemoveAt(i); continue; }
                DsHello hello;
                DsProof proof;
                bool has = _hellos.TryGetValue(p.Conn.ClientId, out hello);
                bool proven = _proofs.TryGetValue(p.Conn.ClientId, out proof);
                if (has && !proven && EarlyModVerdict(p, hello)) { _pending.RemoveAt(i); continue; }   // turned away for mods: at once
                if ((!has || !proven) && now - p.Since < HelloWait) continue;
                _pending.RemoveAt(i);
                Decide(p, has ? (DsHello?)hello : null, proven ? (DsProof?)proof : null);
            }
            for (int i = _drop.Count - 1; i >= 0; i--)
            {
                if (now < _drop[i].Value) continue;
                NetworkConnection c = _drop[i].Key;
                _drop.RemoveAt(i);
                try { if (c.IsActive) c.Disconnect(false); } catch { }
            }
            // forget hellos of connections that have gone
            if (_hellos.Count > 0 && Time.frameCount % 120 == 0)
                foreach (int id in _hellos.Keys.ToList())
                    if (Server.Connection(id) == null) { _hellos.Remove(id); _proofs.Remove(id); _nonces.Remove(id); }
        }

        /// <summary>
        /// As soon as a player's mod list is in: turned away straight away when their game version or mods mean no (it no longer
        /// waits up to HelloWait for the identity proof first - 8 s of loading screen for nothing). True when turned away.
        /// </summary>
        private static bool EarlyModVerdict(Pending p, DsHello hello)
        {
            try
            {
                LocalPlayerSteamInfo info = (LocalPlayerSteamInfo)p.Args[0];
                string who = (Client.CleanName(info.PlayerName).Length > 0 ? Client.CleanName(info.PlayerName) : "Player") + " (" + info.SteamId + ")";
                string theirGame = GameVersionOf(hello);
                if (theirGame != null && theirGame != Application.version)
                {
                    Reject(p.Conn, who, "Your game is version " + theirGame + ", this server runs " + Application.version + ". Both need the same StellarDrive version.", "");
                    return true;
                }
                if (Server.Cfg.RequireMods.Value)
                {
                    List<KeyValuePair<string, string>> missing = Missing(hello);
                    if (missing.Count > 0)
                    {
                        Reject(p.Conn, who, "You need these mods to join: " + string.Join(", ", missing.Select(m => m.Key + " " + m.Value).ToArray()), Lines(missing) + ServerInfo.FileLines(missing));
                        return true;
                    }
                }
                bool op = Server.Data.IsOp(info.SteamId);
                List<KeyValuePair<string, string>> extra = Extra(hello);
                if (extra.Count > 0 && Server.Cfg.RejectExtraMods.Value && !op)
                {
                    NoteExtra(extra, Client.CleanName(info.PlayerName));
                    Reject(p.Conn, who, "This server doesn't allow these mods: " + string.Join(", ", extra.Select(m => m.Key + (m.Value.Length > 0 ? " " + m.Value : "")).ToArray()),
                        string.Join("\n", extra.Select(m => CodeExtraMods + "\t" + m.Key + "\t" + m.Value).ToArray()));
                    return true;
                }
            }
            catch (Exception e) { Log.Once("early-verdict", "gate: early mod check: " + e.Message); }
            return false;
        }

        private static void Decide(Pending p, DsHello? hello, DsProof? proof)
        {
            LocalPlayerSteamInfo info = (LocalPlayerSteamInfo)p.Args[0];
            string cleanName = Client.CleanName(info.PlayerName);
            if (cleanName.Length == 0) cleanName = "Player";
            string who = cleanName + " (" + info.SteamId + ")";
            string ip = AddressOf(p.Conn);

            // The game's own check refuses another game version without a word; say why instead.
            string theirGame = hello.HasValue ? GameVersionOf(hello.Value) : null;
            string clientGame = p.Args.Length > 1 ? p.Args[1] as string : theirGame;
            if (clientGame != null && clientGame != Application.version)
            {
                Reject(p.Conn, who, "Your game is version " + clientGame + ", this server runs " + Application.version + ". Both need the same StellarDrive version.", "");
                return;
            }
            ServerData d = Server.Data;
            bool op = d.IsOp(info.SteamId);

            BanRecord ban = d.Ban(info.SteamId) ?? d.IpBan(ip);
            if (ban != null) { Reject(p.Conn, who, "You are banned from this server" + (ban.Reason.Length > 0 ? ": " + ban.Reason : "."), CodeBanned); return; }

            // this player's character belongs to the game that first joined with it
            string token = proof.HasValue && !string.IsNullOrEmpty(proof.Value.Token) ? Sha(proof.Value.Token) : "";
            PlayerRecord rec = d.Player(info.SteamId);
            if (rec != null && rec.Token.Length > 0 && rec.Token != token)
            {
                Reject(p.Conn, who, "This player belongs to another computer's game. If it's really you (a new PC or a reinstall), ask an admin to unlink " + (rec.Name.Length > 0 ? rec.Name : info.SteamId.ToString()) + ".", "");
                return;
            }

            // a protected name belongs to the first player who joined with it
            if (Server.Cfg.ProtectNames.Value)
            {
                PlayerRecord owner = d.D.Players.FirstOrDefault(x => x.Id != info.SteamId && x.Visits > 0 && string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
                if (owner != null)
                {
                    Reject(p.Conn, who, "The name " + cleanName + " belongs to another player on this server. Choose another name on the Join page.", "");
                    return;
                }
            }
            info.PlayerName = UniqueName(cleanName, info.SteamId);
            p.Args[0] = info;
            who = info.PlayerName + " (" + info.SteamId + ")";

            string password = Server.Cfg.Password.Value ?? "";
            if (password.Length > 0 && !op)
            {
                string nonce;
                _nonces.TryGetValue(p.Conn.ClientId, out nonce);
                string want = Hmac(System.Text.Encoding.UTF8.GetBytes(password), nonce ?? "");
                if (!proof.HasValue || proof.Value.Password != want)
                {
                    bool gave = proof.HasValue && !string.IsNullOrEmpty(proof.Value.Password);
                    Reject(p.Conn, who, gave ? "Wrong password." : "This server needs a password.", gave ? CodeWrongPassword : CodePassword);
                    return;
                }
            }

            if (Server.Cfg.Whitelist.Value && !op && !d.IsWhitelisted(info.SteamId))
            { d.Touch(info.SteamId, info.PlayerName); d.Flush(); Reject(p.Conn, who, "This server is whitelisted, and you're not on the list.", ""); return; }

            if (!op && Server.ActivePlayers().Count >= Server.MaxPlayers)
            { Reject(p.Conn, who, "The server is full (" + Server.MaxPlayers + " players).", ""); return; }

            if (Server.Cfg.RequireMods.Value)
            {
                if (hello == null)
                {
                    Reject(p.Conn, who, "This server needs the DedicatedServer mod and: " + string.Join(", ", Required().Select(m => m.Key + " " + m.Value).ToArray()),
                        Lines(Required()));
                    return;
                }
                List<KeyValuePair<string, string>> missing = Missing(hello.Value);
                if (missing.Count > 0)
                {
                    Reject(p.Conn, who, "You need these mods to join: " + string.Join(", ", missing.Select(m => m.Key + " " + m.Value).ToArray()), Lines(missing) + ServerInfo.FileLines(missing));
                    return;
                }
            }

            // mods the player has that this server doesn't run: noted (the Server Manager lists them), and
            // with RejectExtraMods on, turned away unless allowed
            if (hello != null)
            {
                List<KeyValuePair<string, string>> extra = Extra(hello.Value);
                NoteExtra(extra, info.PlayerName);
                if (extra.Count > 0 && Server.Cfg.RejectExtraMods.Value && !op)
                {
                    Reject(p.Conn, who, "This server doesn't allow these mods: " + string.Join(", ", extra.Select(m => m.Key + (m.Value.Length > 0 ? " " + m.Value : "")).ToArray()),
                        string.Join("\n", extra.Select(m => CodeExtraMods + "\t" + m.Key + "\t" + m.Value).ToArray()));
                    return;
                }
            }

            // remember this game and address for this player
            rec = d.Touch(info.SteamId, info.PlayerName);
            if (rec.Token.Length == 0 && token.Length > 0) rec.Token = token;
            if (!rec.Op && ListedAdmin(info.SteamId, info.PlayerName)) { rec.Op = true; Log.Msg(info.PlayerName + " is an admin (listed in Admins)"); }
            if (ip.Length > 0)
            {
                rec.LastIp = ip;
                if (!rec.Ips.Contains(ip)) { rec.Ips.Add(ip); if (rec.Ips.Count > 20) rec.Ips.RemoveAt(0); }
            }
            d.Flush();

            Accepted++;
            string welcome = "Welcome to " + Server.Cfg.ServerName.Value + ".";
            Net.SendVerdict(p.Conn, new DsVerdict { Accepted = true, Message = welcome, Missing = "" });
            ServerInfo.SendTo(p.Conn);
            Log.Msg("ACCEPTED " + who + (hello != null ? " (mods checked)" : " (mods not required)"));
            try
            {
                _bypass = true;
                _authBody.Invoke(p.Instance, p.Args);
            }
            catch (Exception e) { Log.Error("the game's join for " + who + " failed: " + (e.InnerException ?? e)); }
            finally { _bypass = false; }
        }

        // Codes go in a verdict's Missing field (a line starting with #), so a client of this
        // version can act on them: ask for the password, or forget a saved one when banned.
        public const string CodePassword = "#password", CodeWrongPassword = "#wrongpassword", CodeBanned = "#banned";

        private static void Reject(NetworkConnection conn, string who, string why, string missing)
        {
            Rejected++;
            LastRejection = why;
            Log.Msg("TURNED AWAY " + who + ": " + why);
            Net.SendVerdict(conn, new DsVerdict { Accepted = false, Message = why, Missing = missing });
            DisconnectSoon(conn);
        }

        /// <summary>After the verdict has had time to leave (it's on the same reliable channel).</summary>
        public static void DisconnectSoon(NetworkConnection conn)
        {
            _drop.Add(new KeyValuePair<NetworkConnection, float>(conn, Time.realtimeSinceStartup + 0.5f));
        }

        // ---- which mods ----------------------------------------------------------------------

        /// <summary>Required mod name -> version (the server's).</summary>
        public static List<KeyValuePair<string, string>> Required()
        {
            Dictionary<string, string> mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (MelonMod m in MelonMod.RegisteredMelons)
                if (m != null && m.Info != null && Side(m) == "both") mine[m.Info.Name] = m.Info.Version;
            List<KeyValuePair<string, string>> req = new List<KeyValuePair<string, string>>();
            req.Add(new KeyValuePair<string, string>("DedicatedServer", DedicatedServerMod.Version));
            string spec = (Server.Cfg.RequiredMods.Value ?? "auto").Trim();
            if (spec.Length == 0 || spec.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                HashSet<string> optional = Split(Server.Cfg.OptionalMods.Value);
                foreach (KeyValuePair<string, string> kv in mine.OrderBy(k => k.Key))
                    if (!optional.Contains(kv.Key) && kv.Key != "DedicatedServer") req.Add(kv);
            }
            else
            {
                foreach (string name in Split(spec).OrderBy(s => s))
                {
                    if (name.Equals("DedicatedServer", StringComparison.OrdinalIgnoreCase)) continue;
                    string v;
                    // a named mod the server doesn't have itself: any version will do
                    req.Add(new KeyValuePair<string, string>(name, mine.TryGetValue(name, out v) ? v : ""));
                }
            }
            return req;
        }

        /// <summary>
        /// Where a mod says it belongs, from an optional `public static string DedicatedServerSide` on its
        /// mod class: "server" (only the server needs it: players are never asked for it), "client" (only
        /// players' own games: a server ignores it), or "both" (the default: players need the same mod).
        /// </summary>
        public static string Side(MelonMod m)
        {
            try
            {
                System.Reflection.FieldInfo f = m.GetType().GetField("DedicatedServerSide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                System.Reflection.PropertyInfo p = f == null ? m.GetType().GetProperty("DedicatedServerSide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                string v = (f != null ? f.GetValue(null) : p != null ? p.GetValue(null, null) : null) as string;
                v = (v ?? "").Trim().ToLowerInvariant();
                return v == "server" || v == "client" ? v : "both";
            }
            catch { return "both"; }
        }

        public static string GameVersionOf(DsHello hello)
        {
            string v;
            return ParseMods(hello.Mods).TryGetValue(GameLine, out v) ? v : null;
        }

        /// <summary>Never two players on at once with the same name: the later one gets " (2)".</summary>
        private static string UniqueName(string name, ulong id)
        {
            if (name.Length == 0) name = "Player";
            string n = name;
            for (int i = 2; Server.ActivePlayers().Any(x => x.SteamUserId != id && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)); i++)
                n = name + " (" + i + ")";
            return n;
        }

        public static List<KeyValuePair<string, string>> Missing(DsHello hello)
        {
            Dictionary<string, string> theirs = ParseMods(hello.Mods);
            theirs["DedicatedServer"] = hello.ModVersion ?? "";
            bool versions = Server.Cfg.MatchModVersions.Value;
            List<KeyValuePair<string, string>> missing = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> r in Required())
            {
                string v;
                if (!theirs.TryGetValue(r.Key, out v)) missing.Add(r);
                else if (versions && r.Value.Length > 0 && v != r.Value) missing.Add(r);
            }
            return missing;
        }

        public const string CodeExtraMods = "#extra";

        /// <summary>The player's mods that this server neither runs nor allows (AllowedExtraMods).</summary>
        public static List<KeyValuePair<string, string>> Extra(DsHello hello)
        {
            HashSet<string> mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DedicatedServer" };
            foreach (MelonMod m in MelonMod.RegisteredMelons) if (m != null && m.Info != null) mine.Add(m.Info.Name);
            HashSet<string> allowed = Split(Server.Cfg.AllowedExtraMods.Value);
            return ParseMods(hello.Mods).Where(kv => !kv.Key.StartsWith("@") && !mine.Contains(kv.Key) && !allowed.Contains(kv.Key))
                .OrderBy(kv => kv.Key).ToList();
        }

        private static void NoteExtra(List<KeyValuePair<string, string>> extra, string player)
        {
            if (extra.Count == 0) return;
            foreach (KeyValuePair<string, string> m in extra)
            {
                FlaggedMod f = Server.Data.D.FlaggedMods.FirstOrDefault(x => string.Equals(x.Name, m.Key, StringComparison.OrdinalIgnoreCase));
                if (f == null) { f = new FlaggedMod { Name = m.Key }; Server.Data.D.FlaggedMods.Add(f); }
                f.Version = m.Value;
                f.Times++;
                f.LastSeen = DateTime.UtcNow;
                f.LastPlayer = player ?? "";
            }
            Server.Data.MarkDirty();
            Log.Debug("gate", player + " has mods this server doesn't run: " + string.Join(", ", extra.Select(m => m.Key).ToArray()));
        }

        public static Dictionary<string, string> ParseMods(string mods)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (mods ?? "").Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) d[line.Trim()] = "";
                else d[line.Substring(0, tab).Trim()] = line.Substring(tab + 1).Trim();
            }
            return d;
        }

        public static string Lines(IEnumerable<KeyValuePair<string, string>> mods)
        {
            return string.Join("\n", mods.Select(m => m.Key + "\t" + m.Value).ToArray());
        }

        private static HashSet<string> Split(string s)
        {
            return new HashSet<string>((s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        }
    }
}
