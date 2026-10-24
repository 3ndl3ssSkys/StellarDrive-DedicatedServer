// What the server remembers between runs, in <data>/server-data.json:
//   players    everyone who has ever joined: names, first/last seen, play time, operator
//   bans, whitelist
//   sessions   each visit (who, from, to), kept 35 days, for recent/weekly/monthly activity
//   runs       each time the server ran (start, last heartbeat, how it stopped), kept 35 days,
//              for uptime and downtime
//
// A crash can't write "stopped", so a run's end is its last heartbeat (every 30 s);
// a run that never recorded a clean stop is counted as a crash.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace DedicatedServer
{
    public class PlayerRecord
    {
        public ulong Id;
        public string Name = "";
        public List<string> OtherNames = new List<string>();
        public DateTime FirstSeen, LastSeen;
        public double Seconds;
        public int Visits;
        public bool Op;                                  // admin
        public string Token = "";                        // proof of identity this player's game gave the first time (a hash)
        public string LastIp = "";
        public List<string> Ips = new List<string>();
        public Dictionary<string, int> Levels = new Dictionary<string, int>();   // mod name -> permission level
        public List<ulong> Friends = new List<ulong>();  // players this one added with /friend (friends when both have)
        public string MarkerColor = "";                  // their marker's colour (/color), "" = picked from their id
        public ulong SpawnPlanet;                        // their own spawn spot (SpreadSpawns, /setspawn); 0 = the world's start
        public double SpawnX, SpawnY, SpawnZ;            // planet-centred
    }

    public class FlaggedMod
    {
        public string Name = "";
        public string Version = "";
        public int Times;                                // joins turned away (or noted) for it
        public DateTime LastSeen;
        public string LastPlayer = "";
    }

    public class BanRecord
    {
        public ulong Id;                                 // 0 for an address-only ban
        public string Ip = "";                           // set for an address ban
        public string Name = "";
        public string Reason = "";
        public DateTime At;
    }

    public class SessionRecord
    {
        public ulong Id;
        public string Name = "";
        public DateTime Start;
        public DateTime? End;
    }

    public class RunRecord
    {
        public DateTime Start;
        public DateTime LastBeat;
        public bool Clean;
    }

    public class FreezeRecord
    {
        public DateTime At;                              // UTC
        public double Ms;
    }

    public class DayStats
    {
        public string Date = "";                         // yyyy-MM-dd, UTC
        public int Joins, Messages, Peak, ShipsBuilt, Bans;
        public List<ulong> Players = new List<ulong>();
        public long BytesIn, BytesOut;
    }

    /// <summary>One hour's numbers (UTC), for the Manager's hourly charts. Kept 35 days.</summary>
    public class HourStats
    {
        public string Hour = "";                         // yyyy-MM-ddTHH, UTC
        public int Joins, Messages, Peak, ShipsBuilt, Bans;
        public List<ulong> Players = new List<ulong>();
        public long BytesIn, BytesOut;
    }

    public class ServerDataFile
    {
        public int Format = 2;
        public string ServerId = "";                     // made once; identity proofs are tied to it
        public List<DayStats> Days = new List<DayStats>();   // kept 400 days
        public List<HourStats> Hours = new List<HourStats>();   // kept 35 days
        public List<PlayerRecord> Players = new List<PlayerRecord>();
        public List<BanRecord> Bans = new List<BanRecord>();
        public List<ulong> Whitelist = new List<ulong>();
        public List<SessionRecord> Sessions = new List<SessionRecord>();
        public List<RunRecord> Runs = new List<RunRecord>();
        public List<FreezeRecord> Freezes = new List<FreezeRecord>();   // the server loop stood still for half a second or more
        public List<FlaggedMod> FlaggedMods = new List<FlaggedMod>();  // mods players had that this server doesn't run
        public List<ProtectedShip> Protected = new List<ProtectedShip>();   // builds nothing breaks or changes (Protect.cs)
    }

    internal class ServerData
    {
        public const double KeepDays = 35;
        public ServerDataFile D = new ServerDataFile();
        private readonly string _path;
        private bool _dirty;
        private RunRecord _run;
        private readonly Dictionary<int, SessionRecord> _open = new Dictionary<int, SessionRecord>();   // by connection id

        public ServerData(string dataDir)
        {
            _path = Path.Combine(dataDir, "server-data.json");
            try
            {
                if (File.Exists(_path))
                    D = JsonConvert.DeserializeObject<ServerDataFile>(File.ReadAllText(_path)) ?? new ServerDataFile();
            }
            catch (Exception e)
            {
                string bad = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(_path, bad, true); } catch { }
                Log.Warn("server-data.json could not be read (" + e.Message + "); starting fresh, the old file is kept as " + Path.GetFileName(bad));
                D = new ServerDataFile();
            }
            if (string.IsNullOrEmpty(D.ServerId)) { D.ServerId = Guid.NewGuid().ToString("N"); _dirty = true; }
            // Sessions still open belong to a run that crashed: close them at that run's last heartbeat.
            RunRecord last = D.Runs.LastOrDefault();
            foreach (SessionRecord s in D.Sessions)
                if (s.End == null) s.End = last != null && last.LastBeat > s.Start ? last.LastBeat : s.Start;
        }

        // ---- the run -------------------------------------------------------------------------

        public RunRecord PreviousRun { get; private set; }

        public void StartRun()
        {
            PreviousRun = D.Runs.LastOrDefault();
            DateTime now = DateTime.UtcNow;
            _run = new RunRecord { Start = now, LastBeat = now };
            D.Runs.Add(_run);
            Prune(now);
            _dirty = true;
            Flush();
        }

        public void Heartbeat()
        {
            if (_run == null) return;
            _run.LastBeat = DateTime.UtcNow;
            _dirty = true;
        }

        public void StopRun()
        {
            DateTime now = DateTime.UtcNow;
            foreach (int c in _open.Keys.ToList()) Leave(c, now);
            if (_run != null) { _run.LastBeat = now; _run.Clean = true; }
            _dirty = true;
            Flush();
        }

        public DateTime RunStart { get { return _run != null ? _run.Start : DateTime.UtcNow; } }

        private void Prune(DateTime now)
        {
            DateTime cut = now.AddDays(-KeepDays);
            D.Sessions.RemoveAll(s => s.End != null && s.End < cut);
            D.Runs.RemoveAll(r => r != _run && r.LastBeat < cut);
            if (D.Freezes == null) D.Freezes = new List<FreezeRecord>();
            D.Freezes.RemoveAll(f => f.At < cut);
        }

        public void Freeze(double ms)
        {
            if (D.Freezes == null) D.Freezes = new List<FreezeRecord>();
            D.Freezes.Add(new FreezeRecord { At = DateTime.UtcNow, Ms = Math.Round(ms) });
            if (D.Freezes.Count > 3000) D.Freezes.RemoveRange(0, D.Freezes.Count - 3000);
            _dirty = true;
        }

        // ---- players -------------------------------------------------------------------------

        public PlayerRecord Player(ulong id) { return D.Players.FirstOrDefault(p => p.Id == id); }

        public PlayerRecord PlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return D.Players.OrderByDescending(p => p.LastSeen)
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? D.Players.OrderByDescending(p => p.LastSeen)
                .FirstOrDefault(p => p.OtherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        }

        public PlayerRecord Touch(ulong id, string name)
        {
            PlayerRecord p = Player(id);
            DateTime now = DateTime.UtcNow;
            if (p == null)
            {
                p = new PlayerRecord { Id = id, Name = name ?? "", FirstSeen = now, LastSeen = now };
                D.Players.Add(p);
            }
            else if (!string.IsNullOrEmpty(name) && p.Name != name)
            {
                if (!string.IsNullOrEmpty(p.Name) && !p.OtherNames.Contains(p.Name)) p.OtherNames.Add(p.Name);
                p.Name = name;
            }
            _dirty = true;
            return p;
        }

        public void Join(int connectionId, ulong id, string name)
        {
            DateTime now = DateTime.UtcNow;
            PlayerRecord p = Touch(id, name);
            p.LastSeen = now;
            p.Visits++;
            CountJoin(id, _open.Count + 1);
            if (_open.ContainsKey(connectionId)) Leave(connectionId, now);
            SessionRecord s = new SessionRecord { Id = id, Name = p.Name, Start = now };
            D.Sessions.Add(s);
            _open[connectionId] = s;
            _dirty = true;
        }

        public void Leave(int connectionId) { Leave(connectionId, DateTime.UtcNow); }

        private void Leave(int connectionId, DateTime now)
        {
            SessionRecord s;
            if (!_open.TryGetValue(connectionId, out s)) return;
            _open.Remove(connectionId);
            s.End = now;
            PlayerRecord p = Player(s.Id);
            if (p != null) { p.LastSeen = now; p.Seconds += (now - s.Start).TotalSeconds; }
            _dirty = true;
        }

        public bool IsOp(ulong id) { PlayerRecord p = Player(id); return p != null && p.Op; }
        public bool IsBanned(ulong id) { return D.Bans.Any(b => b.Id != 0 && b.Id == id); }
        public BanRecord Ban(ulong id) { return id == 0 ? null : D.Bans.FirstOrDefault(b => b.Id == id); }
        public BanRecord IpBan(string ip) { return string.IsNullOrEmpty(ip) ? null : D.Bans.FirstOrDefault(b => b.Ip == ip); }

        // ---- today's numbers -----------------------------------------------------------------

        public DayStats Today()
        {
            string key = DateTime.UtcNow.ToString("yyyy-MM-dd");
            DayStats d = D.Days.LastOrDefault();
            if (d == null || d.Date != key)
            {
                d = new DayStats { Date = key };
                D.Days.Add(d);
                if (D.Days.Count > 400) D.Days.RemoveRange(0, D.Days.Count - 400);
                _dirty = true;
            }
            return d;
        }

        public HourStats ThisHour()
        {
            string key = DateTime.UtcNow.ToString("yyyy-MM-ddTHH", System.Globalization.CultureInfo.InvariantCulture);
            if (D.Hours == null) D.Hours = new List<HourStats>();
            HourStats h = D.Hours.LastOrDefault();
            if (h == null || h.Hour != key)
            {
                h = new HourStats { Hour = key };
                D.Hours.Add(h);
                if (D.Hours.Count > 35 * 24) D.Hours.RemoveRange(0, D.Hours.Count - 35 * 24);
                _dirty = true;
            }
            return h;
        }

        public void CountJoin(ulong id, int onNow)
        {
            DayStats d = Today();
            d.Joins++;
            if (!d.Players.Contains(id)) d.Players.Add(id);
            if (onNow > d.Peak) d.Peak = onNow;
            HourStats h = ThisHour();
            h.Joins++;
            if (!h.Players.Contains(id)) h.Players.Add(id);
            if (onNow > h.Peak) h.Peak = onNow;
            _dirty = true;
        }

        /// <summary>Who is on now (every 30 s): the hour's and the day's most at once, and who was on in it.</summary>
        public void NoteOnline(IEnumerable<ulong> ids)
        {
            List<ulong> l = ids.ToList();
            HourStats h = ThisHour(); DayStats d = Today();
            if (l.Count > h.Peak) { h.Peak = l.Count; _dirty = true; }
            if (l.Count > d.Peak) { d.Peak = l.Count; _dirty = true; }
            foreach (ulong id in l) if (!h.Players.Contains(id)) { h.Players.Add(id); _dirty = true; }
        }

        public void CountMessage() { Today().Messages++; ThisHour().Messages++; _dirty = true; }
        public void CountShip() { Today().ShipsBuilt++; ThisHour().ShipsBuilt++; _dirty = true; }
        public void CountBan() { Today().Bans++; ThisHour().Bans++; _dirty = true; }
        public void CountBytes(long inB, long outB) { DayStats d = Today(); d.BytesIn += inB; d.BytesOut += outB; HourStats h = ThisHour(); h.BytesIn += inB; h.BytesOut += outB; _dirty = true; }

        public long Total(Func<DayStats, long> f) { return D.Days.Sum(f); }
        public bool IsWhitelisted(ulong id) { return D.Whitelist.Contains(id); }
        public void MarkDirty() { _dirty = true; }

        // ---- activity ------------------------------------------------------------------------

        public class Activity
        {
            public int Players, Visits;
            public double Hours;
        }

        public Activity ActivitySince(DateTime from)
        {
            DateTime now = DateTime.UtcNow;
            HashSet<ulong> who = new HashSet<ulong>();
            Activity a = new Activity();
            foreach (SessionRecord s in D.Sessions)
            {
                DateTime end = s.End ?? now;
                if (end < from) continue;
                who.Add(s.Id);
                a.Visits++;
                DateTime st = s.Start < from ? from : s.Start;
                a.Hours += Math.Max(0, (end - st).TotalHours);
            }
            a.Players = who.Count;
            return a;
        }

        /// <summary>The most players on at once since <paramref name="from"/>.</summary>
        public int PeakSince(DateTime from)
        {
            DateTime now = DateTime.UtcNow;
            List<KeyValuePair<DateTime, int>> edges = new List<KeyValuePair<DateTime, int>>();
            foreach (SessionRecord s in D.Sessions)
            {
                DateTime end = s.End ?? now;
                if (end < from) continue;
                edges.Add(new KeyValuePair<DateTime, int>(s.Start < from ? from : s.Start, 1));
                edges.Add(new KeyValuePair<DateTime, int>(end, -1));
            }
            int cur = 0, peak = 0;
            foreach (var e in edges.OrderBy(e => e.Key).ThenBy(e => e.Value))
            {
                cur += e.Value;
                if (cur > peak) peak = cur;
            }
            return peak;
        }

        public class Uptime
        {
            public double Window, Up, Down;   // seconds
            public int Starts, Crashes;
            public double Percent { get { return Window <= 0 ? 100 : 100.0 * Up / Window; } }
        }

        /// <summary>
        /// Up and down time over the last <paramref name="days"/>. Time before the server's
        /// first ever run is not downtime; it didn't exist yet.
        /// </summary>
        public Uptime UptimeOver(double days)
        {
            DateTime now = DateTime.UtcNow;
            DateTime from = now.AddDays(-days);
            RunRecord first = D.Runs.OrderBy(r => r.Start).FirstOrDefault();
            if (first != null && first.Start > from) from = first.Start;
            Uptime u = new Uptime { Window = Math.Max(0, (now - from).TotalSeconds) };
            // merge overlapping runs (two servers on one data folder would otherwise count twice)
            DateTime coveredTo = from;
            foreach (RunRecord r in D.Runs.OrderBy(r => r.Start))
            {
                DateTime end = r == _run ? now : r.LastBeat;
                if (end < from) continue;
                if (r.Start >= from) u.Starts++;
                if (r != _run && !r.Clean && r.Start >= from) u.Crashes++;
                DateTime st = r.Start < coveredTo ? coveredTo : r.Start;
                if (end > st) { u.Up += (end - st).TotalSeconds; coveredTo = end; }
            }
            u.Down = Math.Max(0, u.Window - u.Up);
            return u;
        }

        /// <summary>How long the server was down before this run started, or null on a first run.</summary>
        public TimeSpan? DowntimeBeforeThisRun
        {
            get { return PreviousRun == null || _run == null ? (TimeSpan?)null : _run.Start - PreviousRun.LastBeat; }
        }

        // ---- file ----------------------------------------------------------------------------

        public void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(D, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception e) { Log.Once("data-write", "could not write server-data.json: " + e.Message); _dirty = true; }
        }

        public static string Span(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalDays >= 1) return ((int)t.TotalDays) + "d " + t.Hours + "h";
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + "m " + t.Seconds + "s";
            return t.Seconds + "s";
        }
    }
}
