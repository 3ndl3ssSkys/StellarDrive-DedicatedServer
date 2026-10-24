// The main window: every server on the left with its live state, and pages for the one picked:
// Launch, Saves, Ships, Users, Banned, Settings, Logs, Uptime.
//
// While a server runs, changes go to it as console commands (commands.txt), so they apply at once
// and the server itself writes its files. While it's stopped, the Manager edits its files directly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    public partial class MainForm : Form
    {
        private readonly ManagerSettings _settings;
        private readonly ListView _list = new ListView();
        private readonly TabControl _tabs = new TabControl();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Timer _timer = new Timer { Interval = 1000 };
        private Server _current;
        private readonly Dictionary<string, Server> _servers = new Dictionary<string, Server>();

        /// <summary>
        /// The whole window is drawn in one go (WS_EX_COMPOSITED), not control by control: switching pages and
        /// scrolling no longer show half-drawn parts flickering in one after another.
        /// </summary>
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; } }

        /// <summary>The self test never takes the focus from whoever is using the PC.</summary>
        protected override bool ShowWithoutActivation { get { return Program.Quiet; } }

        public MainForm(ManagerSettings settings)
        {
            _settings = settings;
            Theme.Settings = settings;
            Text = "StellarDrive Server Manager " + Program.Version;
            Size = new Size(1280, 820);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Program.AppIcon;

            // left: the servers
            Panel left = new Panel { Dock = DockStyle.Left, Width = Math.Max(180, Math.Min(600, Theme.SizeOf("left", 290))), BackColor = Theme.Panel, Padding = new Padding(10) };
            Label lh = Theme.Label("SERVERS", Theme.Sub, Theme.Line); lh.Dock = DockStyle.Top;
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HeaderStyle = ColumnHeaderStyle.None;
            _list.Columns.Add("Server", 140);
            _list.Columns.Add("State", 125);
            _list.BackColor = Theme.Panel; _list.ForeColor = Theme.Text; _list.BorderStyle = BorderStyle.None;
            _list.Font = Theme.Body;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.SelectedIndexChanged += (s, e) => Pick();
            FlowLayoutPanel lb = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel };
            Button[] side =
            {
                Theme.Button("Home: all servers", (s, e) => { foreach (ListViewItem it in _list.SelectedItems) it.Selected = false; Pick(); }, 160),
                Theme.Button("New server...", (s, e) => NewServer(), 160),
                Theme.Button("Add existing folder...", (s, e) => AddExisting(), 160),
            };
            foreach (Button b in side) lb.Controls.Add(b);
            // the list's columns and the buttons follow the panel's width
            left.Resize += (s, e) =>
            {
                int w = left.ClientSize.Width - left.Padding.Horizontal;
                foreach (Button b in side) { b.MinimumSize = new Size(Math.Max(120, w - 10), 0); b.MaximumSize = new Size(Math.Max(120, w - 10), 0); }
                if (_list.Columns.Count == 2) { _list.Columns[1].Width = Math.Max(90, Math.Min(140, w / 2 - 4)); _list.Columns[0].Width = Math.Max(60, w - _list.Columns[1].Width - 6); }
            };
            left.Controls.Add(_list); left.Controls.Add(lb); left.Controls.Add(lh);

            // right: header + pages
            FlowLayoutPanel head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Back, Padding = new Padding(18, 10, 18, 6) };
            _title.Font = Theme.Head; _title.ForeColor = Theme.Line; _title.AutoSize = true;
            _subtitle.Font = Theme.Small; _subtitle.ForeColor = Theme.Muted; _subtitle.AutoSize = true;
            head.Controls.Add(_title); head.Controls.Add(_subtitle);
            _tabs.Dock = DockStyle.Fill;
            _tabs.Font = Theme.Sub;
            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.SizeMode = TabSizeMode.Fixed;
            // every tab in one row at any width: they share the width, and a tab too narrow for its name uses a smaller font
            bool sizing = false;      // (setting the tabs' size resizes the control, which would come back here)
            _tabs.Resize += (s, e) =>
            {
                if (sizing) return;
                int per = Math.Max(56, (_tabs.Width - 6) / TabNames.Length);
                if (_tabs.ItemSize.Width == per) return;
                sizing = true;
                try { _tabs.ItemSize = new Size(per, _tabs.ItemSize.Height); } finally { sizing = false; }
            };
            _tabs.ItemSize = new Size(TextRenderer.MeasureText("Settings", Theme.Sub).Width + 14, Theme.Sub.Height + 16);
            _tabs.DrawItem += DrawTab;
            _tabs.SelectedIndexChanged += (s, e) => RefreshPage(true);
            foreach (string t in TabNames)
                _tabs.TabPages.Add(new TabPage(t) { BackColor = Theme.Back, ForeColor = Theme.Text });
            Panel right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(_tabs); right.Controls.Add(head);
            // the server list's width: drag its edge
            Splitter edge = new Splitter { Dock = DockStyle.Left, Width = 6, BackColor = Theme.Back, MinSize = 180, MinExtra = 600 };
            edge.SplitterMoved += (s, e) => Theme.RememberSize("left", left.Width);
            Controls.Add(right); Controls.Add(edge); Controls.Add(left);

            BuildLaunch(_tabs.TabPages[0]);
            BuildMods(_tabs.TabPages[1]);
            BuildSaves(_tabs.TabPages[2]);
            BuildShips(_tabs.TabPages[3]);
            BuildUsers(_tabs.TabPages[4]);
            BuildBans(_tabs.TabPages[5]);
            BuildSettings(_tabs.TabPages[6]);
            BuildLogs(_tabs.TabPages[7]);
            BuildUptime(_tabs.TabPages[8]);
            BuildHome(right);
            Theme.Apply(this);
            _list.BackColor = Theme.Panel;

            FillList();
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            Shown += (s, e) => { if (_settings.Servers.Count == 0) NewServer(); };
        }

        private void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabPage p = _tabs.TabPages[e.Index];
            bool sel = e.Index == _tabs.SelectedIndex;
            using (SolidBrush b = new SolidBrush(sel ? Theme.ButtonBack : Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (Pen pen = new Pen(Theme.Line, 2)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Font f = TextRenderer.MeasureText(p.Text, Theme.Sub).Width <= e.Bounds.Width - 6 ? Theme.Sub : TextRenderer.MeasureText(p.Text, Theme.Body).Width <= e.Bounds.Width - 4 ? Theme.Body : Theme.Small;
            TextRenderer.DrawText(e.Graphics, p.Text, f, e.Bounds, sel ? Theme.Line : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- the list ------------------------------------------------------------------------------

        private Server Get(ServerEntry e)
        {
            Server s;
            if (!_servers.TryGetValue(e.Folder, out s)) { s = new Server(e); _servers[e.Folder] = s; }
            return s;
        }

        private void FillList()
        {
            string keep = _current != null ? _current.Folder : null;
            _list.Items.Clear();
            foreach (ServerEntry e in _settings.Servers)
            {
                ListViewItem it = new ListViewItem(new[] { e.Name, "" }) { Tag = Get(e) };
                _list.Items.Add(it);
                if (e.Folder == keep) it.Selected = true;
            }
            UpdateList();
            if (_list.Items.Count == 0 || _list.SelectedItems.Count == 0) { _current = null; Pick(); }
        }

        private void UpdateList()
        {
            foreach (ListViewItem it in _list.Items)
            {
                Server s = (Server)it.Tag;
                string state = s.State;
                string players = state == "running" ? " " + Json.Arr(s.Status, "players").Count() + "/" + Json.Int(s.Status, "maxPlayers") : "";
                string text = state + players;
                if (it.SubItems[1].Text != text) it.SubItems[1].Text = text;
                it.ForeColor = state == "running" ? Theme.Good : state == "failed" ? Theme.Bad : state == "starting" ? Theme.Warn : Theme.Muted;
            }
        }

        private static readonly string[] TabNames = { "Launch", "Mods", "Saves", "Ships", "Users", "Banned", "Settings", "Logs", "Uptime" };

        private void Pick()
        {
            _current = _list.SelectedItems.Count > 0 ? (Server)_list.SelectedItems[0].Tag : null;
            _tabs.Visible = _current != null;
            if (_home != null) { _home.Visible = _current == null; if (_current == null) RefreshHome(); }
            _title.Text = _current != null ? _current.Entry.Name : "Home";
            _subtitle.Text = _current != null ? _current.Folder : _settings.Servers.Count + " server(s)";
            _settingsLoadedFor = null;
            _modsLoadedFor = null;
            RefreshPage(true);
        }

        private void NewServer()
        {
            using (WizardForm w = new WizardForm(_settings))
            {
                w.ShowDialog(this);
                if (w.CreatedServer == null) return;
                FillList();
                foreach (ListViewItem it in _list.Items) it.Selected = ((Server)it.Tag).Folder == w.CreatedServer.Folder;
            }
        }

        private void AddExisting()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "A server folder made by the Manager (or any StellarDrive folder with DedicatedServer in Mods)" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GameCopy.IsGameFolder(d.SelectedPath)) { MessageBox.Show(this, "No StellarDrive.exe in that folder."); return; }
                if (_settings.Servers.Any(s => s.Folder.Equals(d.SelectedPath, StringComparison.OrdinalIgnoreCase))) return;
                _settings.Servers.Add(new ServerEntry { Name = new DirectoryInfo(d.SelectedPath).Name, Folder = d.SelectedPath });
                _settings.Save();
                FillList();
            }
        }

        // ---- the timer -------------------------------------------------------------------------------

        private int _ticks;

        private void Tick()
        {
            _ticks++;
            UpdateList();
            if (_current == null) { if (_home != null && _home.Visible && _ticks % 3 == 0) RefreshHome(); return; }
            RefreshPage(false);
        }

        public readonly Dictionary<string, double> PageMs = new Dictionary<string, double>();

        private void RefreshPage(bool force)
        {
            if (_current == null) return;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                switch (_tabs.SelectedIndex)
                {
                    case 0: RefreshLaunch(force); break;
                    case 1: if (force) RefreshMods(); break;
                    case 2: if (force || _ticks % 5 == 0) RefreshSaves(); break;
                    case 3: if (force || _ticks % 5 == 0) RefreshShips(); break;
                    case 4: if (force || _ticks % 3 == 0) RefreshUsers(); break;
                    case 5: if (force || _ticks % 3 == 0) RefreshBans(); break;
                    case 6: if (force) RefreshSettings(); break;
                    case 7: RefreshLogs(force); break;
                    case 8: if (force || _ticks % 5 == 0) RefreshUptime(); break;
                }
            }
            catch (Exception e) { _subtitle.Text = _current.Folder + "   (" + e.Message + ")"; }
            if (force) PageMs[TabNames[Math.Max(0, _tabs.SelectedIndex)]] = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>A console command to the running server; false (and a note) when it isn't running.</summary>
        private bool Send(string line)
        {
            if (_current == null) return false;
            if (!_current.Running) return false;
            _current.Command(line);
            return true;
        }

        /// <summary>Lists (players, bans, ships, saves) emptied and filled again; only when what they show changed.</summary>
        public int GridRebuilds;

#if SDSMTEST
        public void SelectForTest(int serverIndex, int tab)
        {
            if (serverIndex < _list.Items.Count) { _list.Items[serverIndex].Selected = true; Pick(); }
            _tabs.SelectedIndex = tab;
            RefreshPage(true);
        }

        /// <summary>Switching to a page until it is painted, in ms.</summary>
        public double SwitchMsForTest(int tab)
        {
            Stopwatch sw = Stopwatch.StartNew();
            _tabs.SelectedIndex = tab;
            RefreshPage(true);
            Refresh();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>The page scrolled top to bottom in steps, each painted: the slowest step in ms (steps = 0: nothing scrolls).</summary>
        public double ScrollMsForTest(out int steps)
        {
            steps = 0;
            Control page = _tabs.SelectedTab;
            ScrollableControl sc = All(page).OfType<ScrollableControl>().FirstOrDefault(c => c.Visible && c.AutoScroll && c.DisplayRectangle.Height > c.ClientSize.Height + 20);
            if (sc == null) return 0;
            double worst = 0;
            int total = sc.DisplayRectangle.Height - sc.ClientSize.Height;
            int step = Math.Max(40, total / 12);
            for (int y = step; y <= total + step && steps < 14; y += step)
            {
                Stopwatch sw = Stopwatch.StartNew();
                sc.AutoScrollPosition = new Point(0, Math.Min(y, total));
                sc.Refresh();
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                steps++;
            }
            sc.AutoScrollPosition = new Point(0, 0);
            return worst;
        }

        private static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (Control y in All(x)) yield return y; } }
#endif

        public int TabCount { get { return _tabs.TabPages.Count; } }
        public int TabIndexOf(string name) { return Array.IndexOf(TabNames, name); }
#if SDSMTEST
        public void ShowHomeForTest() { foreach (ListViewItem it in _list.SelectedItems) it.Selected = false; Pick(); }
#endif
        public string TabName(int i) { return _tabs.TabPages[i].Text; }
#if SDSMTEST
        public TabPage TabForTest(int i) { return _tabs.TabPages[i]; }
        public string[] TabNamesForTest { get { return TabNames; } }
#endif
        public Server Current { get { return _current; } }
    }
}
