// The numbers the Uptime and Home charts show, by hour or by day, from one or more servers'
// server-data.json (Days, Hours and Runs). Hours are kept by the server from version 1.4.0 on.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SDServerManager
{
    public class Activity
    {
        public DateTime[] Times;          // the start of each bucket, UTC
        public bool Hourly;
        public double[] Joins, Players, Peak, Messages, Ships, Bans, InRate, OutRate, Up;
        public DateTime HoursSince = DateTime.MaxValue;     // the first hour the server kept

        public static Activity Build(IList<JsonNode> datas, IList<bool> running, int days, bool hourly)
        {
            DateTime now = DateTime.UtcNow;
            Activity a = new Activity { Hourly = hourly };
            int n = hourly ? days * 24 : days;
            DateTime first = hourly ? new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1 - n) : now.Date.AddDays(1 - n);
            a.Times = Enumerable.Range(0, n).Select(i => hourly ? first.AddHours(i) : first.AddDays(i)).ToArray();
            double bucketSeconds = hourly ? 3600 : 86400;
            a.Joins = new double[n]; a.Players = new double[n]; a.Peak = new double[n]; a.Messages = new double[n]; a.Ships = new double[n];
            a.Bans = new double[n]; a.InRate = new double[n]; a.OutRate = new double[n]; a.Up = new double[n];
            HashSet<string>[] who = Enumerable.Range(0, n).Select(_ => new HashSet<string>()).ToArray();
            Dictionary<string, int> index = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) index[hourly ? a.Times[i].ToString("yyyy-MM-ddTHH") : a.Times[i].ToString("yyyy-MM-dd")] = i;
            int servers = 0;
            for (int s = 0; s < datas.Count; s++)
            {
                JsonNode d = datas[s];
                if (d == null) continue;
                servers++;
                foreach (JsonNode b in Json.Arr(d, hourly ? "Hours" : "Days"))
                {
                    int i;
                    if (!index.TryGetValue(Json.Str(b, hourly ? "Hour" : "Date"), out i)) continue;
                    a.Joins[i] += Json.Num(b, "Joins"); a.Messages[i] += Json.Num(b, "Messages"); a.Ships[i] += Json.Num(b, "ShipsBuilt"); a.Bans[i] += Json.Num(b, "Bans");
                    a.Peak[i] += Json.Num(b, "Peak");
                    a.InRate[i] += Json.Num(b, "BytesIn") / bucketSeconds; a.OutRate[i] += Json.Num(b, "BytesOut") / bucketSeconds;
                    foreach (JsonNode p in Json.Arr(b, "Players")) who[i].Add(p.ToString());
                }
                foreach (JsonNode h in Json.Arr(d, "Hours").Take(1))
                {
                    DateTime t;
                    if (DateTime.TryParseExact(Json.Str(h, "Hour"), "yyyy-MM-ddTHH", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out t) && t < a.HoursSince) a.HoursSince = t;
                }
                // up: hours up in each bucket (by day) or minutes up (by hour), from the runs
                List<JsonNode> runs = Json.Arr(d, "Runs").ToList();
                bool live = s < running.Count && running[s];
                for (int r = 0; r < runs.Count; r++)
                {
                    DateTime st = Json.Date(runs[r], "Start"), en = r == runs.Count - 1 && live ? now : Json.Date(runs[r], "LastBeat");
                    if (en <= first) continue;
                    for (int i = 0; i < n; i++)
                    {
                        DateTime b0 = a.Times[i], b1 = hourly ? b0.AddHours(1) : b0.AddDays(1);
                        DateTime x0 = st > b0 ? st : b0, x1 = en < b1 ? en : b1;
                        if (x1 > x0) a.Up[i] += hourly ? (x1 - x0).TotalMinutes : (x1 - x0).TotalHours;
                    }
                }
            }
            for (int i = 0; i < n; i++) a.Players[i] = who[i].Count;
            if (servers > 1) for (int i = 0; i < n; i++) a.Up[i] /= servers;      // the average server
            return a;
        }
    }
}
