// "Is this server up, and who's on?" without joining it - for the saved servers on the Join page.
//
// It goes through the game's own UDP port, so whatever already lets players in (port forwarding,
// playit.gg or another tunnel) works for it too. The game's network library (LiteNetLib, inside
// FishNet's Tugboat transport) can carry "unconnected" messages beside its connections; the server
// switches that on and answers one kind of message: a question that starts with "SDDSQ1". The
// answer is "SDDSR1", the question's nonce, then a little JSON (name, description, players, ...).
//
// A question is padded to 64 bytes and the answer stays under 1 KB, and each address gets at most
// a few answers a second, so the server can't be used to flood someone else.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using FishNet;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Query
    {
        public static readonly byte[] Ask = Encoding.ASCII.GetBytes("SDDSQ1");
        public static readonly byte[] Answer = Encoding.ASCII.GetBytes("SDDSR1");
        public const int QuestionSize = 64, NonceSize = 8;

        public class Info
        {
            public string name = "", desc = "", game = "", mod = "", world = "";
            public int players, max;
            public bool password, whitelist;
            public string[] names = new string[0];
            public string icon = "";         // the icon's hash (fetch "icon" when it's new to you)
            public int iconSize;
        }

        // Files (the icon, mods) go in pieces: ask "SDDSF1" + nonce + piece + name, padded to 512 bytes so
        // an answer is never bigger than its question; answer "SDDSG1" + nonce + piece + pieces + size + data.
        public static readonly byte[] FileAsk = Encoding.ASCII.GetBytes("SDDSF1");
        public static readonly byte[] FileAnswer = Encoding.ASCII.GetBytes("SDDSG1");
        public const int FileQuestionSize = 512, Piece = 420;

        // ---- the server's side -------------------------------------------------------------

        private static NetManager _server;
        public static NetManager ServerNet { get { return _server; } }
        private static readonly Dictionary<string, KeyValuePair<float, int>> _rate = new Dictionary<string, KeyValuePair<float, int>>();
        public static int Answered;

        public static void InitServer(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(FishNet.Transporting.Tugboat.Server.ServerSocket), "StartSocket"),
                    postfix: new HarmonyMethod(typeof(Query).GetMethod("StartSocketPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("server status questions won't be answered (the Join page shows this server as unknown): " + e.Message); }
        }

        private static void StartSocketPostfix(object __instance)
        {
            try
            {
                NetManager nm = AccessTools.Field(__instance.GetType().BaseType, "NetManager").GetValue(__instance) as NetManager
                             ?? AccessTools.Field(__instance.GetType(), "NetManager").GetValue(__instance) as NetManager;
                if (nm == null) return;
                EventBasedNetListener l = AccessTools.Field(typeof(NetManager), "_netEventListener").GetValue(nm) as EventBasedNetListener;
                if (l == null) { Log.Warn("server status: the game's listener isn't the expected kind"); return; }
                nm.UnconnectedMessagesEnabled = true;
                l.NetworkReceiveUnconnectedEvent += OnServerUnconnected;
                _server = nm;
                Log.Msg("answering server status questions on the game port (for players' saved servers)");
            }
            catch (Exception e) { Log.Warn("server status questions won't be answered: " + e.Message); }
        }

        private static void OnServerUnconnected(IPEndPoint from, NetPacketReader reader, UnconnectedMessageType type)
        {
            try
            {
                byte[] q = reader.GetRemainingBytes();
                if (q.Length >= FileQuestionSize && Starts(q, FileAsk)) { AnswerFile(from, q); return; }
                if (q.Length < QuestionSize || !Starts(q, Ask)) return;
#if DSTEST
                if (NetGuard.TestDeaf) return;
#endif
                string key = from.Address.ToString();
                float now = Time.realtimeSinceStartup;
                KeyValuePair<float, int> r;
                if (_rate.TryGetValue(key, out r) && now - r.Key < 1f) { if (r.Value >= 4) return; _rate[key] = new KeyValuePair<float, int>(r.Key, r.Value + 1); }
                else _rate[key] = new KeyValuePair<float, int>(now, 1);
                if (_rate.Count > 2000) _rate.Clear();
                byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Current()));
                byte[] a = new byte[Answer.Length + NonceSize + json.Length];
                Buffer.BlockCopy(Answer, 0, a, 0, Answer.Length);
                Buffer.BlockCopy(q, Ask.Length, a, Answer.Length, NonceSize);
                Buffer.BlockCopy(json, 0, a, Answer.Length + NonceSize, json.Length);
                _server.SendUnconnectedMessage(a, from);
                Answered++;
            }
            catch (Exception e) { Log.Once("query", "server status answer: " + e.Message); }
        }

        private static readonly Dictionary<string, KeyValuePair<float, int>> _fileRate = new Dictionary<string, KeyValuePair<float, int>>();

        private static void AnswerFile(IPEndPoint from, byte[] q)
        {
            string key = from.Address.ToString();
            float now = Time.realtimeSinceStartup;
            KeyValuePair<float, int> r;
            if (_fileRate.TryGetValue(key, out r) && now - r.Key < 1f) { if (r.Value >= 1500) return; _fileRate[key] = new KeyValuePair<float, int>(r.Key, r.Value + 1); }
            else _fileRate[key] = new KeyValuePair<float, int>(now, 1);
            if (_fileRate.Count > 2000) _fileRate.Clear();
            int at = FileAsk.Length + NonceSize;
            int piece = BitConverter.ToInt32(q, at);
            int nameLen = BitConverter.ToUInt16(q, at + 4);
            if (nameLen <= 0 || nameLen > 200 || at + 6 + nameLen > q.Length) return;
            string name = Encoding.UTF8.GetString(q, at + 6, nameLen);
            byte[] file = ServerInfo.FileBytes(name);
            if (file == null) return;
            int pieces = Math.Max(1, (file.Length + Piece - 1) / Piece);
            if (piece < 0 || piece >= pieces) return;
            int off = piece * Piece, len = Math.Min(Piece, file.Length - off);
            byte[] a = new byte[FileAnswer.Length + NonceSize + 12 + len];
            Buffer.BlockCopy(FileAnswer, 0, a, 0, FileAnswer.Length);
            Buffer.BlockCopy(q, FileAsk.Length, a, FileAnswer.Length, NonceSize);
            Buffer.BlockCopy(BitConverter.GetBytes(piece), 0, a, FileAnswer.Length + NonceSize, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(pieces), 0, a, FileAnswer.Length + NonceSize + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(file.Length), 0, a, FileAnswer.Length + NonceSize + 8, 4);
            Buffer.BlockCopy(file, off, a, FileAnswer.Length + NonceSize + 12, len);
            _server.SendUnconnectedMessage(a, from);
            FilePiecesSent++;
        }

        public static int FilePiecesSent;

        public static Info Current()
        {
            Info i = new Info
            {
                name = Cut(Server.Cfg.ServerName.Value, 60),
                desc = Cut(Server.Cfg.Description.Value, 300),
                game = Application.version,
                mod = DedicatedServerMod.Version,
                max = Server.MaxPlayers,
                password = (Server.Cfg.Password.Value ?? "").Length > 0,
                whitelist = Server.Cfg.Whitelist.Value,
                icon = ServerInfo.IconHash,
                iconSize = ServerInfo.Icon().Length,
            };
            List<string> names = new List<string>();
            foreach (Players.TrackedPlayerServer p in Server.ActivePlayers()) names.Add(Cut(p.Name, 24));
            i.players = names.Count;
            i.names = names.Count <= 12 ? names.ToArray() : names.GetRange(0, 12).ToArray();
            return i;
        }

        private static string Cut(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n); }

        private static bool Starts(byte[] b, byte[] prefix)
        {
            if (b.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (b[i] != prefix[i]) return false;
            return true;
        }

        // ---- the client's side -------------------------------------------------------------

        public class Pending
        {
            public string Address;
            public IPEndPoint To;
            public byte[] Nonce;
            public float SentAt, Deadline;
            public Action<Info, int> Done;      // (null = no answer, round trip ms)
            public bool Resolving, Failed;
        }

        private static NetManager _client;
        private static EventBasedNetListener _clientListener;
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly System.Random _rng = new System.Random();

        /// <summary>Asks a server how it is. `done` is called on the main thread: an Info, or null after `timeout` seconds.</summary>
        public static void AskServer(string address, Action<Info, int> done, float timeout = 3f)
        {
            string host; int port;
            string err;
            if (!Client.ParseAddress(address, out host, out port, out err)) { done(null, 0); return; }
            if (!EnsureClient()) { done(null, 0); return; }
            Pending p = new Pending { Address = address, Done = done, Nonce = new byte[NonceSize], Deadline = Time.realtimeSinceStartup + timeout + 2f };
            _rng.NextBytes(p.Nonce);
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) p.To = new IPEndPoint(ip, port);
            else
            {
                // a host name: look it up off the main thread
                p.Resolving = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (IPAddress a in Dns.GetHostAddresses(host))
                            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { p.To = new IPEndPoint(a, port); break; }
                    }
                    catch { }
                    if (p.To == null) p.Failed = true;
                    p.Resolving = false;
                });
            }
            lock (_pending) _pending.Add(p);
        }

        private static float _clientUsed;
        public static int ClientSocketsOpened, ClientSocketsClosed;      // for the probe
        public static bool ClientOpen { get { return _client != null; } }

        private static bool EnsureClient()
        {
            _clientUsed = Time.realtimeSinceStartup;
            if (_client != null) return true;
            try
            {
                _clientListener = new EventBasedNetListener();
                _clientListener.NetworkReceiveUnconnectedEvent += OnClientUnconnected;
                _client = new NetManager(_clientListener, null, false) { UnconnectedMessagesEnabled = true };
                if (!_client.Start()) { _client = null; return false; }
                ClientSocketsOpened++;
                return true;
            }
            catch (Exception e) { Log.Once("query-client", "could not ask servers how they are: " + e.Message); _client = null; return false; }
        }

        /// <summary>Every frame on a client: send what's ready, read answers, time out the rest.</summary>
        public static void UpdateClient()
        {
            if (_client == null) return;
            try { _client.PollEvents(); } catch { }
            try { UpdateFetches(); } catch (Exception e) { Log.Once("fetches", "downloads: " + e.Message); }
            List<Pending> finished = null;
            float now = Time.realtimeSinceStartup;
            lock (_pending)
            {
                foreach (Pending p in _pending)
                {
                    if (p.Failed || now > p.Deadline) { (finished = finished ?? new List<Pending>()).Add(p); continue; }
                    if (p.To != null && p.SentAt == 0f)
                    {
                        byte[] q = new byte[QuestionSize];
                        Buffer.BlockCopy(Ask, 0, q, 0, Ask.Length);
                        Buffer.BlockCopy(p.Nonce, 0, q, Ask.Length, NonceSize);
                        p.SentAt = now;
                        p.Deadline = now + 3f;
                        _client.SendUnconnectedMessage(q, p.To);
                    }
                    else if (p.SentAt > 0f && now - p.SentAt > 1f && now - p.SentAt < 1.05f)
                    {
                        // one more try for a packet lost on the way
                        byte[] q = new byte[QuestionSize];
                        Buffer.BlockCopy(Ask, 0, q, 0, Ask.Length);
                        Buffer.BlockCopy(p.Nonce, 0, q, Ask.Length, NonceSize);
                        _client.SendUnconnectedMessage(q, p.To);
                    }
                }
                if (finished != null) foreach (Pending p in finished) _pending.Remove(p);
            }
            if (finished != null) foreach (Pending p in finished) { try { p.Done(null, 0); } catch (Exception e) { Log.Once("query-done", e.Message); } }
            // nothing asked or downloading for a while: close the socket (it was left open for the whole session)
            bool busy;
            lock (_pending) busy = _pending.Count > 0;
            if (!busy) lock (_fetches) busy = _fetches.Count > 0;
            if (busy) _clientUsed = now;
            else if (now - _clientUsed > 10f)
            {
                try { _client.Stop(); } catch { }
                _client = null;
                ClientSocketsClosed++;
            }
        }

        // ---- fetching a file in pieces -----------------------------------------------------

        public class Fetch
        {
            public string Address, Name;
            public IPEndPoint To;
            public byte[] Nonce = new byte[NonceSize];
            public byte[][] Pieces;
            public int Size = -1, Got;
            public float LastProgress, StartedAt;
            public readonly Dictionary<int, float> Asked = new Dictionary<int, float>();
            public Action<byte[], string> Done;     // (file, null) or (null, why)
            public bool Finished;
            public float Progress { get { return Pieces == null ? 0f : (float)Got / Pieces.Length; } }
        }

        private static readonly List<Fetch> _fetches = new List<Fetch>();

        /// <summary>Downloads a file the server hands out ("icon", "mod/&lt;file&gt;"). `done` is called on the main thread.</summary>
        public static Fetch GetFile(string address, string name, Action<byte[], string> done)
        {
            Fetch f = new Fetch { Address = address, Name = name, Done = done, StartedAt = Time.realtimeSinceStartup, LastProgress = Time.realtimeSinceStartup };
            _rng.NextBytes(f.Nonce);
            string host, err; int port;
            if (!Client.ParseAddress(address, out host, out port, out err) || !EnsureClient()) { done(null, err ?? "can't reach the server"); f.Finished = true; return f; }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IPAddress ip;
                    if (IPAddress.TryParse(host, out ip)) f.To = new IPEndPoint(ip, port);
                    else foreach (IPAddress a in Dns.GetHostAddresses(host)) if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { f.To = new IPEndPoint(a, port); break; }
                }
                catch { }
            });
            lock (_fetches) _fetches.Add(f);
            return f;
        }

        private static void AskPiece(Fetch f, int piece)
        {
            byte[] q = new byte[FileQuestionSize];
            Buffer.BlockCopy(FileAsk, 0, q, 0, FileAsk.Length);
            Buffer.BlockCopy(f.Nonce, 0, q, FileAsk.Length, NonceSize);
            Buffer.BlockCopy(BitConverter.GetBytes(piece), 0, q, FileAsk.Length + NonceSize, 4);
            byte[] n = Encoding.UTF8.GetBytes(f.Name);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)n.Length), 0, q, FileAsk.Length + NonceSize + 4, 2);
            Buffer.BlockCopy(n, 0, q, FileAsk.Length + NonceSize + 6, n.Length);
            f.Asked[piece] = Time.realtimeSinceStartup;
            _client.SendUnconnectedMessage(q, f.To);
        }

        private static void UpdateFetches()
        {
            List<Fetch> done = null;
            float now = Time.realtimeSinceStartup;
            lock (_fetches)
                foreach (Fetch f in _fetches)
                {
                    if (f.Finished) { (done = done ?? new List<Fetch>()).Add(f); continue; }
                    if (f.To == null) { if (now - f.StartedAt > 8f) { f.Finished = true; f.Done(null, "can't find the server's address"); } continue; }
                    if (now - f.LastProgress > 12f) { f.Finished = true; f.Done(null, "the server stopped answering"); continue; }
                    if (f.Pieces == null) { if (!f.Asked.ContainsKey(0) || now - f.Asked[0] > 1f)AskPiece(f, 0); continue; }
                    // up to 64 pieces on the way at once; a piece unanswered for a second is asked again
                    int inFlight = 0;
                    foreach (KeyValuePair<int, float> kv in f.Asked.ToList())
                    {
                        if (f.Pieces[kv.Key] != null) { f.Asked.Remove(kv.Key); continue; }
                        if (now - kv.Value > 1f)AskPiece(f, kv.Key);
                        inFlight++;
                    }
                    for (int i = 0; i < f.Pieces.Length && inFlight < 64; i++)
                        if (f.Pieces[i] == null && !f.Asked.ContainsKey(i)) {AskPiece(f, i); inFlight++; }
                }
            if (done != null) lock (_fetches) foreach (Fetch f in done) _fetches.Remove(f);
        }

        private static void OnFilePiece(byte[] a)
        {
            if (a.Length < FileAnswer.Length + NonceSize + 12) return;
            Fetch f = null;
            lock (_fetches)
                foreach (Fetch x in _fetches)
                {
                    bool same = true;
                    for (int i = 0; i < NonceSize && same; i++) same = a[FileAnswer.Length + i] == x.Nonce[i];
                    if (same) { f = x; break; }
                }
            if (f == null || f.Finished) return;
            int at = FileAnswer.Length + NonceSize;
            int piece = BitConverter.ToInt32(a, at), pieces = BitConverter.ToInt32(a, at + 4), size = BitConverter.ToInt32(a, at + 8);
            if (pieces <= 0 || pieces > 20000 || size < 0 || size > 64 * 1024 * 1024 || piece < 0 || piece >= pieces) return;
            if (f.Pieces == null) { f.Pieces = new byte[pieces][]; f.Size = size; }
            if (pieces != f.Pieces.Length || f.Pieces[piece] != null) return;
            byte[] data = new byte[a.Length - at - 12];
            Buffer.BlockCopy(a, at + 12, data, 0, data.Length);
            f.Pieces[piece] = data;
            f.Got++;
            f.LastProgress = Time.realtimeSinceStartup;
            if (f.Got < pieces) return;
            byte[] file = new byte[f.Size];
            int off = 0;
            foreach (byte[] p in f.Pieces) { Buffer.BlockCopy(p, 0, file, off, Math.Min(p.Length, file.Length - off)); off += p.Length; }
            f.Finished = true;
            f.Done(off == f.Size ? file : null, off == f.Size ? null : "the pieces didn't add up");
        }

        private static void OnClientUnconnected(IPEndPoint from, NetPacketReader reader, UnconnectedMessageType type)
        {
            byte[] a = reader.GetRemainingBytes();
            if (Starts(a, FileAnswer)) { try { OnFilePiece(a); } catch (Exception e) { Log.Once("piece", "file piece: " + e.Message); } return; }
            if (a.Length < Answer.Length + NonceSize || !Starts(a, Answer)) return;
            Pending match = null;
            lock (_pending)
            {
                foreach (Pending p in _pending)
                {
                    bool same = true;
                    for (int i = 0; i < NonceSize && same; i++) same = a[Answer.Length + i] == p.Nonce[i];
                    if (same) { match = p; break; }
                }
                if (match != null) _pending.Remove(match);
            }
            if (match == null) return;
            Info info = null;
            try { info = JsonConvert.DeserializeObject<Info>(Encoding.UTF8.GetString(a, Answer.Length + NonceSize, a.Length - Answer.Length - NonceSize)); }
            catch { }
            int ms = (int)((Time.realtimeSinceStartup - match.SentAt) * 1000f);
            try { match.Done(info, ms); } catch (Exception e) { Log.Once("query-done", e.Message); }
        }
    }
}
