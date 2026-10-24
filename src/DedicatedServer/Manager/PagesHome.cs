// Home: what the Manager shows with no server picked. Every server, most recently used first, with
// Start / Stop / Open; the last 30 days of all of them in one chart; and the players who keep
// coming back, across every server.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    /// <summary>A grid that takes the page's width and is as tall as its rows.</summary>
    public class FitGrid : DataGridView, IFitWidth
    {
        public void FitWidth(int width) { if (Width != width) Width = width; }
        public void FitRows(int min = 2)
        {
            int rows = Math.Max(min, Rows.Count);
            Height = ColumnHeadersHeight + rows * RowTemplate.Height + 4;
        }
    }

    public partial class MainForm
    {
        private Panel _home;
        private FitGrid _homeServers, _homePlayers;
        private LaneChart _homeChart;
        private Label _homeNote;

        private void BuildHome(Panel parent)
        {
            _home = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Visible = false };
            PageColumn c = new PageColumn();
            c.Controls.Add(Theme.Label("Your servers", Theme.Head, Theme.Line));
            _homeNote = Theme.Wrap("", 1100);
            c.Controls.Add(_homeNote);
            _homeServers = new FitGrid { ReadOnly = true, Width = 900 };
            foreach (string col in new[] { "Server", "State", "Players", "Last run", "Up, last 7 days" }) _homeServers.Columns.Add(col.Replace(" ", "").Replace(",", ""), col);
            _homeServers.Columns.Add(new DataGridViewButtonColumn { Name = "Go", HeaderText = "", Text = "Start", UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat, FillWeight = 40 });
            _homeServers.Columns.Add(new DataGridViewButtonColumn { Name = "Open", HeaderText = "", Text = "Open", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, FillWeight = 40 });
            Theme.Grid(_homeServers);
            _homeServers.CellClick += HomeServerClick;
            c.Controls.Add(_homeServers);
            _homeChart = new LaneChart("All servers together (each line on its own scale; point at a line for its number)");
            c.Controls.Add(Theme.Label("Activity", Theme.Sub, Theme.Line));
            c.Controls.Add(SpanRow(() => RefreshHome()));
            c.Controls.Add(_homeChart);
            c.Controls.Add(Theme.Label("Players who keep coming back", Theme.Sub, Theme.Line));
            _homePlayers = new FitGrid { ReadOnly = true, Width = 900 };
            foreach (string col in new[] { "Player", "Visits", "Hours played", "Servers", "Last seen" }) _homePlayers.Columns.Add(col.Replace(" ", ""), col);
            Theme.Grid(_homePlayers);
            c.Controls.Add(_homePlayers);
            _home.Controls.Add(c);
            parent.Controls.Add(_home);
            _home.BringToFront();
        }

        private List<Server> HomeOrder()
        {
            return _settings.Servers.Select(Get).OrderByDescending(s => s.Running).ThenByDescending(LastActive).ToList();
        }

        private static DateTime LastActive(Server s)
        {
            JsonNode last = Json.Arr(s.Data, "Runs").LastOrDefault();
            return last != null ? Json.Date(last, "LastBeat") : DateTime.MinValue;
        }

        private void RefreshHome()
        {
            List<Server> servers = HomeOrder();
            _homeNote.Text = servers.Count == 0 ? "No servers yet: make one with New server (left)." : "Pick a server on the left for its pages, or start one here.";
            int keep = _homeServers.CurrentCell != null ? _homeServers.CurrentCell.RowIndex : -1;
            _homeServers.Rows.Clear();
            foreach (Server s in servers)
            {
                JsonNode st = s.Status;
                string state = s.State;
                List<JsonNode> runs = Json.Arr(s.Data, "Runs").ToList();
                JsonNode last = runs.LastOrDefault();
                string lastRun = last == null ? "never" : s.Running ? "up " + Fmt.Span((DateTime.UtcNow - Json.Date(last, "Start")).TotalSeconds) : "stopped " + Fmt.Span((DateTime.UtcNow - Json.Date(last, "LastBeat")).TotalSeconds) + " ago";
                string up7 = runs.Count > 0 ? Uptime(runs, 7, s.Running).Split(new[] { "up " }, StringSplitOptions.None).Skip(1).FirstOrDefault()?.Split(' ')[0] ?? "" : "";
                int i = _homeServers.Rows.Add(s.Entry.Name, state, state == "running" ? Json.Arr(st, "players").Count() + "/" + Json.Int(st, "maxPlayers") : "", lastRun, up7, s.Running ? "Stop" : "Start", "Open");
                _homeServers.Rows[i].Tag = s;
                _homeServers.Rows[i].Cells["State"].Style.ForeColor = state == "running" ? Theme.Good : state == "failed" ? Theme.Bad : state == "starting" ? Theme.Warn : Theme.Muted;
            }
            _homeServers.FitRows();

            // every server's numbers, added up
            MarkSpanButtons();
            Activity a = Activity.Build(servers.Select(s => s.Data).ToList(), servers.Select(s => s.Running).ToList(), _spanDays, Hourly);
            _homeChart.Set(Lanes(a), a.Times, a.Hourly);

            // players across servers, by visits
            Dictionary<string, (string name, int visits, double secs, int servers, DateTime last)> who = new Dictionary<string, (string, int, double, int, DateTime)>();
            foreach (Server s in servers)
                foreach (JsonNode p in Json.Arr(s.Data, "Players"))
                {
                    string id = Json.Str(p, "Id");
                    (string name, int visits, double secs, int servers, DateTime last) cur;
                    if (!who.TryGetValue(id, out cur)) cur = (Json.Str(p, "Name"), 0, 0, 0, DateTime.MinValue);
                    DateTime seen = Json.Date(p, "LastSeen");
                    who[id] = (seen > cur.last ? Json.Str(p, "Name") : cur.name, cur.visits + Json.Int(p, "Visits"), cur.secs + Json.Num(p, "Seconds"), cur.servers + 1, seen > cur.last ? seen : cur.last);
                }
            _homePlayers.Rows.Clear();
            foreach (var p in who.Values.OrderByDescending(x => x.visits).ThenByDescending(x => x.secs).Take(15))
                _homePlayers.Rows.Add(p.name, p.visits, (p.secs / 3600.0).ToString("0.0"), p.servers, p.last == DateTime.MinValue ? "" : p.last.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            _homePlayers.FitRows(1);
        }

        private void HomeServerClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            Server s = _homeServers.Rows[e.RowIndex].Tag as Server;
            if (s == null) return;
            string col = _homeServers.Columns[e.ColumnIndex].Name;
            if (col == "Go")
            {
                if (s.Running) s.Command("stop");
                else { string err = s.Start(); if (err != null) MessageBox.Show(this, err, "Start"); }
                RefreshHome();
            }
            else if (col == "Open" || e.ColumnIndex == 0)
                foreach (ListViewItem it in _list.Items) it.Selected = ((Server)it.Tag).Folder == s.Folder;
        }
    }
}
