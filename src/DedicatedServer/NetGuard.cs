// Keeps the game port listening for days, and says when players can't reach the server.
//
// The game's network library (LiteNetLib, in FishNet's Tugboat) reads the UDP port on a thread of its
// own. Some socket errors end that thread for good - SocketError.NotConnected (the network adapter
// reset or changed, as on a PC sharing a hotspot), OperationAborted, Interrupted - and nothing restarts
// it: the server runs on, saves, and looks fine, but no packet reaches it again. Nobody can join and
// nothing is logged ("running, but not connecting").
//
// So, while NetGuard is on:
//   - every 10 s the main loop looks at that thread; if it has ended, the port is opened again at once
//     (same port, the players still connected keep their connections);
//   - every 10 s a background thread asks the server, on 127.0.0.1, the same status question players'
//     Join pages ask. That proves the whole way in works (socket, thread, the main loop answering).
//     Three misses in a row: the port is opened again. Still no answer after five more: the server
//     saves and restarts (exit code 3; the Server Manager or StartDedicatedServer.bat starts it again);
//   - every 2 minutes, when the Server Manager has the address players use (Launch page, "Address
//     players use"), the same question goes through it - a playit.gg tunnel or a forwarded port - from
//     one socket kept open for it (one flow at the tunnel, not a new one each time). When that stops
//     answering while the server answers on 127.0.0.1, the way in from the internet is down (the
//     tunnel program, its service, or the router): it is logged once, with when it went down and came
//     back, and shown in status.json for the Server Manager. That part can't be fixed from here.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json.Linq;

namespace DedicatedServer
{
    internal static class NetGuard
    {
        public const float CheckSeconds = 10f;
        public const int MissesBeforeReopen = 3, MissesBeforeRestart = 8;
        public const int PublicEverySeconds = 120;

        private static Thread _thread;
        private static volatile bool _armed;
        private static float _nextLook;

        // what the background checks found (written there, read on the main thread)
        private static volatile int _localMisses;
        private static long _localOkTicks, _publicOkTicks, _publicDownTicks;
        private static volatile int _publicState;          // 0 unknown / not set, 1 reachable, 2 not reachable
        private static volatile string _publicAddress = "", _publicError = "";

        public static int Reopened, LocalChecks, LocalMissesTotal, PublicChecks, PublicMissesTotal;
        public static string LastReopen = "";
        private static bool _reopenedForMisses, _restartAsked;

#if DSTEST
        public static volatile bool TestDeaf;              // Query ignores status questions (tests a deaf port)
        public static volatile int TestPublicPort;         // the "public" address for tests: 127.0.0.1:<this>
        public static volatile bool TestPublicNow;          // check the public address at the next round
#endif

        private static NetManager Net { get { return Query.ServerNet; } }
        private static FieldInfo F(string name) { return AccessTools.Field(typeof(NetManager), name); }

        /// <summary>Once the world is up and the port open.</summary>
        public static void Arm()
        {
            if (!Server.Cfg.NetGuard.Value) { Log.Msg("network guard: off (NetGuard = false)"); return; }
            _armed = true;
            _localOkTicks = DateTime.UtcNow.Ticks;
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "DedicatedServer network guard" };
            _thread.Start();
            Log.Msg("network guard: watching the game port" + (PublicAddress().Length > 0 ? " and the address players use (" + PublicAddress() + ")" : ""));
        }

        // ---- the main loop's part ------------------------------------------------------------------

        public static void Update()
        {
            if (!_armed || Server.Stopping) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextLook) return;
            _nextLook = now + CheckSeconds;
            NetManager nm = Net;
            if (nm == null) return;

            string dead = Dead(nm);
            if (dead != null) { Reopen(nm, dead); return; }

            int misses = _localMisses;
            if (misses == 0)
            {
                if (_reopenedForMisses) Log.Msg("network guard: the server answers on its own port again");
                _reopenedForMisses = false;
                return;
            }
            if (misses >= MissesBeforeReopen && !_reopenedForMisses)
            {
                _reopenedForMisses = true;
                Reopen(nm, "the server hasn't answered on its own port for " + misses + " checks in a row");
                return;
            }
            if (misses >= MissesBeforeRestart && !_restartAsked)
            {
                _restartAsked = true;
                Log.Error("network guard: the game port still doesn't answer after opening it again (" + misses + " checks); saving and restarting the server to recover");
                Server.Stop("the game port stopped answering (network guard)", true);
            }
        }

        /// <summary>Why the library's receiving thread isn't running, or null when it is.</summary>
        public static string Dead(NetManager nm)
        {
            if (!nm.IsRunning) return null;                // stopped on purpose
            try
            {
                if ((bool)F("NotConnected").GetValue(nm)) return "Windows reported the network gone (NotConnected)";
                if ((bool)F("_manualMode").GetValue(nm)) return null;
                Thread t = F("_receiveThread").GetValue(nm) as Thread;
                if (t == null || !t.IsAlive) return "the thread that reads the port had stopped";
            }
            catch (Exception e) { Log.Once("netguard-look", "network guard: can't look at the port: " + e.Message); }
            return null;
        }

        /// <summary>Opens the port again: new sockets on the same address and port, a new reading thread; peers stay.</summary>
        public static bool Reopen(NetManager nm, string why)
        {
            int port = nm.LocalPort > 0 ? nm.LocalPort : Server.Port;
            IPAddress v4 = IPAddress.Any, v6 = IPAddress.IPv6Any;
            bool manual = false;
            try
            {
                manual = (bool)F("_manualMode").GetValue(nm);
                Socket s4 = F("_udpSocketv4").GetValue(nm) as Socket, s6 = F("_udpSocketv6").GetValue(nm) as Socket;
                try { if (s4 != null && s4.LocalEndPoint is IPEndPoint e4) v4 = e4.Address; } catch { }
                try { if (s6 != null && s6.LocalEndPoint is IPEndPoint e6) v6 = e6.Address; } catch { }
                // closed and opened again straight away: the old reading thread (if alive) ends on its own, on the
                // old socket it holds; anything sent in between would hit a closed socket, so no waiting here
                F("NotConnected").SetValue(nm, true);
                try { s4?.Close(); } catch { }
                try { s6?.Close(); } catch { }       // what Start wants before it opens a running manager again
                bool ok = nm.Start(v4, v6, port, manual);
                Reopened++;
                LastReopen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ": " + why + (ok ? "" : " (could not open it again)");
                if (ok) Log.Warn("network guard: " + why + "; opened UDP port " + port + " again (time " + Reopened + ")");
                else Log.Error("network guard: " + why + "; UDP port " + port + " could NOT be opened again");
                if (!ok && !_restartAsked) { _restartAsked = true; Server.Stop("the game port could not be opened again (network guard)", true); }
                return ok;
            }
            catch (Exception e)
            {
                Log.Error("network guard: " + why + "; opening the port again failed: " + e);
                return false;
            }
        }

        // ---- the background checks ------------------------------------------------------------------

        private static void Run()
        {
            UdpClient pub = null;
            string pubFor = "";
            DateTime nextPublic = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                Thread.Sleep((int)(CheckSeconds * 1000));
                if (!_armed || Server.Stopping) continue;
                try
                {
                    // on this PC
                    LocalChecks++;
                    if (Ask(null, new IPEndPoint(IPAddress.Loopback, Server.Port), 2500, 2)) { _localMisses = 0; Interlocked.Exchange(ref _localOkTicks, DateTime.UtcNow.Ticks); }
                    else { _localMisses++; LocalMissesTotal++; Log.Warn("network guard: no answer on 127.0.0.1:" + Server.Port + " (" + _localMisses + " in a row)"); }

                    // through the address players use
#if DSTEST
                    if (TestPublicNow) { TestPublicNow = false; nextPublic = DateTime.UtcNow; }
#endif
                    if (DateTime.UtcNow < nextPublic) continue;
                    string addr = PublicAddress();
                    _publicAddress = addr;
                    if (addr.Length == 0) { _publicState = 0; continue; }
                    if (_localMisses > 0) continue;               // the server itself isn't answering: that's the problem, not the way in (asked again next round)
                    nextPublic = DateTime.UtcNow.AddSeconds(PublicEverySeconds);
                    IPEndPoint to = Resolve(addr);
                    if (to == null) { PublicResult(addr, false, "can't find " + addr); continue; }
                    if (pub == null || pubFor != addr) { try { pub?.Close(); } catch { } pub = new UdpClient(0, AddressFamily.InterNetwork); pubFor = addr; }
                    PublicChecks++;
                    bool ok = Ask(pub, to, 3000, 3);
                    PublicResult(addr, ok, ok ? "" : "no answer");
                }
                catch (Exception e) { Log.Once("netguard-run", "network guard: " + e.Message); }
            }
        }

        private static void PublicResult(string addr, bool ok, string error)
        {
            long now = DateTime.UtcNow.Ticks;
            int was = _publicState;
            _publicError = error;
            if (ok)
            {
                Interlocked.Exchange(ref _publicOkTicks, now);
                _publicState = 1;
                if (was == 2) Log.Warn("network guard: players can reach the server through " + addr + " again (it was down since " + Local(_publicDownTicks) + ")");
                else if (was == 0) Log.Msg("network guard: players can reach the server through " + addr);
            }
            else
            {
                PublicMissesTotal++;
                if (was != 2) { Interlocked.Exchange(ref _publicDownTicks, now); Log.Warn("network guard: players CAN'T reach the server through " + addr + " (" + error + "), though it answers on this PC: the tunnel (playit.gg: is its program running and connected?) or the port forward is down"); }
                _publicState = 2;
            }
        }

        private static string Local(long ticks) { return ticks == 0 ? "?" : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm"); }

        /// <summary>The status question to `to`; true when the server answers it. A null client: a fresh socket, closed after.</summary>
        private static bool Ask(UdpClient client, IPEndPoint to, int timeoutMs, int tries)
        {
            UdpClient u = client ?? new UdpClient(0, AddressFamily.InterNetwork);
            try
            {
                u.Client.ReceiveTimeout = timeoutMs;
                byte[] nonce = new byte[Query.NonceSize];
                new Random().NextBytes(nonce);
                byte[] q = new byte[1 + Query.QuestionSize];
                q[0] = 8;                                                          // LiteNetLib: an unconnected message
                Buffer.BlockCopy(Query.Ask, 0, q, 1, Query.Ask.Length);
                Buffer.BlockCopy(nonce, 0, q, 1 + Query.Ask.Length, nonce.Length);
                for (int i = 0; i < tries; i++)
                {
                    u.Send(q, q.Length, to);
                    Stopwatch sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        try
                        {
                            IPEndPoint from = null;
                            byte[] a = u.Receive(ref from);
                            if (a.Length > 15 && Encoding.ASCII.GetString(a, 1, 6) == "SDDSR1")
                            {
                                bool same = true;
                                for (int k = 0; k < nonce.Length && same; k++) same = a[7 + k] == nonce[k];
                                if (same) return true;                             // (an old answer to an earlier try is skipped)
                            }
                        }
                        catch (SocketException) { break; }
                    }
                }
                return false;
            }
            finally { if (client == null) try { u.Close(); } catch { } }
        }

        private static IPEndPoint Resolve(string address)
        {
            string host, err; int port;
            if (!Client.ParseAddress(address, out host, out port, out err)) return null;
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip))
                    foreach (IPAddress a in Dns.GetHostAddresses(host)) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                return ip != null ? new IPEndPoint(ip, port) : null;
            }
            catch { return null; }
        }

        /// <summary>The address players use, as the Server Manager keeps it (dedicatedserver.manager.json beside the game).</summary>
        public static string PublicAddress()
        {
#if DSTEST
            if (TestPublicPort > 0) return "127.0.0.1:" + TestPublicPort;
#endif
            try
            {
                string f = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "dedicatedserver.manager.json");
                if (!File.Exists(f)) return "";
                return ((string)JObject.Parse(File.ReadAllText(f))["PublicAddress"] ?? "").Trim();
            }
            catch { return ""; }
        }

        /// <summary>For status.json (the Server Manager shows it).</summary>
        public static JObject Status()
        {
            JObject o = new JObject();
            o["on"] = _armed;
            NetManager nm = Net;
            o["listening"] = nm != null && nm.IsRunning && Dead(nm) == null;
            o["localOkUtc"] = _localOkTicks == 0 ? "" : new DateTime(Interlocked.Read(ref _localOkTicks), DateTimeKind.Utc).ToString("o");
            o["localMisses"] = _localMisses;
            o["reopened"] = Reopened;
            o["lastReopen"] = LastReopen;
            o["publicAddress"] = _publicAddress;
            o["public"] = _publicState == 1 ? "reachable" : _publicState == 2 ? "unreachable" : "";
            o["publicOkUtc"] = _publicOkTicks == 0 ? "" : new DateTime(Interlocked.Read(ref _publicOkTicks), DateTimeKind.Utc).ToString("o");
            o["publicDownSinceUtc"] = _publicState == 2 && _publicDownTicks != 0 ? new DateTime(Interlocked.Read(ref _publicDownTicks), DateTimeKind.Utc).ToString("o") : "";
            o["publicError"] = _publicError;
            return o;
        }
    }
}
