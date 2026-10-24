// Restarts at set times of day (RestartTimes, e.g. "04:00, 16:30"; a day can go first: "Sun 04:00"), in
// the server machine's own time. Everyone is warned before each one at the times in RestartWarnings
// (e.g. "10m, 5m, 1m, 30s"); timed restarts and stops from the console use the same warnings.
// `restarts` lists the next ones; `restarts skip` skips the next.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Restarts
    {
        public struct When { public DayOfWeek? Day; public int Minute; }       // minute of the day

        private static DateTime _skip = DateTime.MinValue;       // a planned time (local) that is skipped
        private static DateTime _armed = DateTime.MinValue;       // the planned time a countdown was started for
        private static float _nextCheck;

        public static readonly string[] Days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        /// <summary>"04:00, Sun 16:30" -> the times; null and an error if something can't be read.</summary>
        public static List<When> ParseTimes(string text, out string error)
        {
            error = null;
            List<When> l = new List<When>();
            foreach (string raw in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                DayOfWeek? day = null;
                int sp = t.IndexOf(' ');
                if (sp > 0)
                {
                    int d = Array.FindIndex(Days, x => t.StartsWith(x));
                    if (d < 0) { error = "'" + raw.Trim() + "': a day is sun, mon, tue, wed, thu, fri or sat"; return null; }
                    day = (DayOfWeek)d;
                    t = t.Substring(sp + 1).Trim();
                }
                DateTime parsed;
                string[] formats = { "H:mm", "HH:mm", "h:mmtt", "h:mm tt", "htt", "h tt" };
                if (!DateTime.TryParseExact(t.ToUpperInvariant(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                { error = "'" + raw.Trim() + "': a time is like 04:00, 16:30 or 4:30pm"; return null; }
                l.Add(new When { Day = day, Minute = parsed.Hour * 60 + parsed.Minute });
            }
            return l;
        }

        /// <summary>"10m, 5m, 1m, 30s" (a bare number is minutes) -> seconds, largest first.</summary>
        public static List<int> ParseWarnings(string text, out string error)
        {
            error = null;
            List<int> l = new List<int>();
            foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                int mult = 60;
                if (t.EndsWith("h")) { mult = 3600; t = t.TrimEnd('h'); }
                else if (t.EndsWith("min")) { t = t.Substring(0, t.Length - 3); }
                else if (t.EndsWith("m")) { t = t.TrimEnd('m'); }
                else if (t.EndsWith("s")) { mult = 1; t = t.TrimEnd('s'); }
                double n;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n <= 0 || n * mult > 24 * 3600)
                { error = "'" + raw.Trim() + "': a warning is like 10m, 1m or 30s (at most 24h)"; return null; }
                l.Add((int)Math.Round(n * mult));
            }
            return l.Distinct().OrderByDescending(x => x).ToList();
        }

        public class Message { public int Seconds; public bool Any, OnScreen; public string Text = ""; }

        /// <summary>"10m: words || 1m!: words || *: words" -> the messages; null and an error if something can't be read.</summary>
        public static List<Message> ParseMessages(string text, out string error)
        {
            error = null;
            List<Message> l = new List<Message>();
            foreach (string raw in (text ?? "").Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int colon = part.IndexOf(':');
                if (colon <= 0) { error = "'" + part + "': each message starts with when, like 10m: or 30s!: or *:"; return null; }
                string when = part.Substring(0, colon).Trim();
                Message m = new Message { Text = part.Substring(colon + 1).Trim() };
                if (when.EndsWith("!")) { m.OnScreen = true; when = when.Substring(0, when.Length - 1).Trim(); }
                if (when == "*") m.Any = true;
                else
                {
                    string e;
                    List<int> s = ParseWarnings(when, out e);
                    if (s == null || s.Count != 1) { error = "'" + when + "': a time is like 10m, 1m or 30s (or * for every other warning)"; return null; }
                    m.Seconds = s[0];
                }
                l.Add(m);
            }
            return l;
        }

        /// <summary>The message for the warning at `seconds` before (its own, else the * one), or null for the built-in words.</summary>
        public static Message MessageFor(int seconds)
        {
            string e;
            List<Message> l = ParseMessages(Server.Cfg.RestartMessages.Value, out e);
            if (l == null) return null;
            return l.FirstOrDefault(m => !m.Any && m.Seconds == seconds) ?? l.FirstOrDefault(m => m.Any);
        }

        public static int[] Warnings()
        {
            string e;
            List<int> l = ParseWarnings(Server.Cfg.RestartWarnings.Value, out e);
            return l != null && l.Count > 0 ? l.ToArray() : new[] { 600, 300, 120, 60, 30, 10 };
        }

        /// <summary>The next planned restarts after a time (local), soonest first.</summary>
        public static List<DateTime> Next(DateTime fromLocal, int count)
        {
            string e;
            List<When> times = ParseTimes(Server.Cfg.RestartTimes.Value, out e) ?? new List<When>();
            List<DateTime> all = new List<DateTime>();
            for (int d = 0; d < 8; d++)
            {
                DateTime day = fromLocal.Date.AddDays(d);
                foreach (When w in times)
                {
                    if (w.Day.HasValue && day.DayOfWeek != w.Day.Value) continue;
                    DateTime at = day.AddMinutes(w.Minute);
                    if (at > fromLocal) all.Add(at);
                }
            }
            return all.Distinct().OrderBy(x => x).Take(count).ToList();
        }

        public static DateTime NextPlanned()
        {
            foreach (DateTime t in Next(DateTime.Now, 3)) if (t != _skip) return t;
            return DateTime.MinValue;
        }

        /// <summary>Every frame: start the countdown when the first warning is due.</summary>
        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 1f;
            if (Chat.Pending || !Server.Ready) return;
            DateTime next = NextPlanned();
            if (next == DateTime.MinValue || next == _armed) return;
            double left = (next - DateTime.Now).TotalSeconds;
            int first = Warnings().Max();
            if (left > first + 0.5) return;
            // just started, right before a planned time (a restart that ran a little late): leave it for the next one
            if (now < 180f && left < 60) { _skip = next; Log.Msg("restarts: started " + left.ToString("0") + " s before the planned restart at " + next.ToString("HH:mm") + "; skipping that one"); return; }
            _armed = next;
            string reason = Server.Cfg.RestartReason.Value ?? "";
            Chat.Schedule(true, (float)left, reason.Length > 0 ? reason : "planned restart (" + next.ToString("HH:mm", CultureInfo.InvariantCulture) + ")");
        }

        public static string Describe()
        {
            List<DateTime> n = Next(DateTime.Now, 4).Where(t => t != _skip).ToList();
            string times = Server.Cfg.RestartTimes.Value;
            if (string.IsNullOrWhiteSpace(times)) return "No planned restarts (RestartTimes is empty). Warnings: " + WarningsText() + ".";
            return "Planned restarts: " + times + " (this machine's time, now " + DateTime.Now.ToString("ddd HH:mm") + "). Next: "
                + (n.Count > 0 ? string.Join(", ", n.Select(t => t.ToString("ddd HH:mm")).ToArray()) : "none") + (_skip > DateTime.Now ? " (skipping " + _skip.ToString("ddd HH:mm") + ")" : "")
                + ". Warnings: " + WarningsText() + ".";
        }

        public static string WarningsText() { return string.Join(", ", Warnings().Select(s => Chat.Span(s)).ToArray()) + " before"; }

        public static string Skip()
        {
            DateTime n = NextPlanned();
            if (n == DateTime.MinValue) return "There's no planned restart to skip.";
            if (Chat.Pending && _armed == n) Chat.Cancel();
            _skip = n;
            return "The restart planned for " + n.ToString("ddd HH:mm") + " is skipped.";
        }
    }
}
