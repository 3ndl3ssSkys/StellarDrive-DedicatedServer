// Settings, Logs and Uptime pages.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    public partial class MainForm
    {
        // ---- Settings -------------------------------------------------------------------------------

        private DataGridView _set;
        private Label _setNote;
        private string _settingsLoadedFor;
        private Conf _conf;

        private void BuildSettings(TabPage page)
        {
            _set = new DataGridView { Dock = DockStyle.Fill };
            _set.Columns.Add(new DataGridViewTextBoxColumn { Name = "Setting", HeaderText = "Setting", ReadOnly = true, FillWeight = 26 });
            _set.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 34 });
            _set.Columns.Add(new DataGridViewTextBoxColumn { Name = "What", HeaderText = "What it does", ReadOnly = true, FillWeight = 80 });
            _set.Columns.Add(new DataGridViewTextBoxColumn { Name = "When", HeaderText = "Applies", ReadOnly = true, FillWeight = 16 });
            Theme.Grid(_set);
            _set.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _set.EditMode = DataGridViewEditMode.EditOnEnter;
            _set.Columns["What"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _set.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _set.CurrentCellDirtyStateChanged += (s, e) => { if (_set.IsCurrentCellDirty) _set.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _set.DataError += (s, e) => e.ThrowException = false;
            // a Style row: click its value for the picker
            _set.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0 || _set.Columns[e.ColumnIndex].Name != "Value") return;
                string key = Convert.ToString(_set.Rows[e.RowIndex].Cells["Setting"].Value) ?? "";
                if (!IsStyleKey(key)) return;
                DataGridViewCell cell = _set.Rows[e.RowIndex].Cells["Value"];
                string spec = StyleDialog.Ask(this, key, Convert.ToString(cell.Value) ?? "", StyleSample(key));
                if (spec == null) return;
                cell.Value = spec;
                ShowStyle(cell);
            };
            _setNote = Theme.Wrap("", 700);
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 10, 18, 0) };
            top.Controls.Add(Theme.Row(Theme.Button("Save changes", (s, e) => SaveSettings(), 140), Theme.Button("Undo", (s, e) => { _settingsLoadedFor = null; RefreshSettings(); }, 90),
                _setNote));
            _iconBox = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.FieldBack, Margin = new Padding(0, 4, 12, 4) };
            _iconNote = Theme.Wrap("", 560);
            top.Controls.Add(Theme.Row(_iconBox, new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0),
                Controls =
                {
                    Theme.Label("SERVER ICON", Theme.Sub, Theme.Line),
                    Theme.Row(Theme.Button("Choose a picture...", (s, e) => ChooseIcon(), 170), Theme.Button("Remove", (s, e) => RemoveIcon(), 90)),
                    _iconNote,
                }
            }));
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 12) };
            body.Controls.Add(_set);
            page.Controls.Add(body); page.Controls.Add(top);
        }

        private PictureBox _iconBox;
        private Label _iconNote;

        private void ShowIcon()
        {
            Image old = _iconBox.Image;
            _iconBox.Image = null;
            if (old != null) old.Dispose();
            try
            {
                if (File.Exists(_current.IconPath))
                    using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(_current.IconPath))) _iconBox.Image = new Bitmap(Image.FromStream(ms));
            }
            catch { }
            _iconNote.Text = _iconBox.Image != null
                ? "Players see it next to the server's name: on the Join page (saved servers), in the pause menu and in the player list. A new one reaches players already on within seconds."
                : "No icon yet. Pick any picture: it is cut to a square and made 128 x 128. Players see it on the Join page, in the pause menu and in the player list.";
        }

        private void ChooseIcon()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "The server's icon", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string err = SetIcon(_current, d.FileName);
                if (err != null) MessageBox.Show(this, err, "Server icon");
                ShowIcon();
            }
        }

        private void RemoveIcon()
        {
            try { if (File.Exists(_current.IconPath)) File.Delete(_current.IconPath); } catch (Exception e) { MessageBox.Show(this, e.Message); }
            ShowIcon();
        }

        /// <summary>A picture cut to the middle square, 128 x 128 PNG (smaller if it would be over the server's 64 KB), as the server's icon.</summary>
        public static string SetIcon(Server s, string picture)
        {
            try
            {
                using (Image src = Image.FromFile(picture))
                {
                    int side = Math.Min(src.Width, src.Height);
                    Rectangle from = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                    foreach (int size in new[] { 128, 96, 64 })
                    {
                        using (Bitmap b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(b))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                g.DrawImage(src, new Rectangle(0, 0, size, size), from, GraphicsUnit.Pixel);
                            }
                            using (MemoryStream ms = new MemoryStream())
                            {
                                b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                if (ms.Length > 60 * 1024 && size > 64) continue;
                                Directory.CreateDirectory(s.DataDir);
                                File.WriteAllBytes(s.IconPath, ms.ToArray());
                                return null;
                            }
                        }
                    }
                }
                return "That picture is too detailed to make a small enough icon.";
            }
            catch (Exception e) { return "Could not use that picture: " + e.Message; }
        }

        private void RefreshSettings()
        {
            if (_settingsLoadedFor == _current.Folder) return;
            _settingsLoadedFor = _current.Folder;
            ShowIcon();
            _conf = _current.LoadConf();
            _set.SuspendLayout();
            _set.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _set.Rows.Clear();
            foreach (Conf.ConfItem i in _conf.Items)
            {
                int r = _set.Rows.Add(i.Key, "", i.Comment.Length > 0 ? i.Comment : (Conf.Known.FirstOrDefault(k => k.Key == i.Key).Comment ?? ""), Conf.RestartOnly.Contains(i.Key) ? "restart" : "at once");
                DataGridViewRow row = _set.Rows[r];
                string[] choices;
                if (i.IsBool) choices = new[] { "on", "off" };
                else if (!Conf.Choices.TryGetValue(i.Key, out choices)) choices = null;
                if (choices != null)
                {
                    DataGridViewComboBoxCell cell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                    cell.Items.AddRange(choices);
                    row.Cells["Value"] = cell;
                    cell.Value = i.IsBool ? (i.Raw == "true" ? "on" : "off") : choices.FirstOrDefault(c => c == i.Text) ?? choices[0];
                }
                else row.Cells["Value"].Value = i.Text;
                row.Cells["Value"].Tag = row.Cells["Value"].Value;
                if (IsStyleKey(i.Key)) { row.Cells["Value"].ReadOnly = true; row.Cells["Value"].ToolTipText = "Click to choose the colour, size and look"; ShowStyle(row.Cells["Value"]); }
            }
            _set.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _set.ResumeLayout();
            _setNote.Text = _current.Running ? "The server is running: changes are sent to it and apply at once (except those marked restart)." : "The server is stopped: changes go to dedicatedserver.conf.";
        }

        public static bool IsStyleKey(string key) { return key.StartsWith("Style", StringComparison.OrdinalIgnoreCase); }

        private static string StyleSample(string key)
        {
            switch (key)
            {
                case "StyleAnnounce": return "[Server] The event starts at the space station in 10 minutes";
                case "StyleAlert": return "[Server] The server will restart in 5 minutes: update";
                case "StyleWelcome": return "Welcome to the server! Be nice.";
                case "StyleJoin": return "Alex has joined the game.";
                case "StyleLeave": return "Alex has left the game.";
                case "StylePlayerName": return "Alex";
                case "StylePlayerText": return "anyone want to go mining?";
                case "StyleGameNotice": return "Waiting for other players to sleep...";
                default: return "You are an operator.";
            }
        }

        /// <summary>A style's value shown in its own colour (the text is the spec the server reads).</summary>
        private static void ShowStyle(DataGridViewCell cell)
        {
            StyleSpec sp = StyleSpec.Parse(Convert.ToString(cell.Value));
            cell.Style.ForeColor = sp.Color ?? Theme.Text;
            cell.Style.SelectionForeColor = sp.Color ?? Theme.Text;
            if (Convert.ToString(cell.Value) == "") cell.Style.NullValue = "(the game's own look)";
        }

        private void SaveSettings()
        {
            int changed = 0;
            List<string> restart = new List<string>();
            foreach (DataGridViewRow row in _set.Rows)
            {
                string key = (string)row.Cells["Setting"].Value;
                string now = Convert.ToString(row.Cells["Value"].Value) ?? "", was = Convert.ToString(row.Cells["Value"].Tag) ?? "";
                if (now == was) continue;
                Conf.ConfItem item = _conf.Find(key);
                if (item.IsInt) { int n; if (!int.TryParse(now, out n)) { MessageBox.Show(this, key + " needs a whole number."); return; } }
                changed++;
                if (Conf.RestartOnly.Contains(key)) restart.Add(key);
                if (_current.Running) Send("set " + key + " " + (now.Length == 0 ? "\"\"" : now));
                if (item.IsBool) _conf.SetBool(key, now == "on");
                else if (item.IsInt) _conf.SetInt(key, int.Parse(now));
                else _conf.Set(key, now);
                row.Cells["Value"].Tag = now;
            }
            if (!_current.Running) _conf.Save(_current.ConfPath);
            _setNote.Text = changed == 0 ? "Nothing changed." : changed + " setting(s) saved" + (restart.Count > 0 ? "; " + string.Join(", ", restart) + " take effect when the server restarts." : ".");
        }

        // ---- Logs -----------------------------------------------------------------------------------

        private ComboBox _logSource;
        private TextBox _logText, _logFilter;
        private CheckBox _logErrors;
        private long _logLen = -1;
        private string _logKey;

        private void BuildLogs(TabPage page)
        {
            _logSource = Theme.Choice(new[] { "This run: server log" }, "", 330);
            _logFilter = Theme.Box("", 220);
            _logErrors = Theme.Check("Only warnings and errors", false);
            _logSource.SelectedIndexChanged += (s, e) => RefreshLogs(true);
            _logFilter.TextChanged += (s, e) => RefreshLogs(true);
            _logErrors.CheckedChanged += (s, e) => RefreshLogs(true);
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 10, 18, 0) };
            top.Controls.Add(Theme.Row(_logSource, Theme.Label("   Filter ", Theme.Body, Theme.Muted), _logFilter, Theme.Label("  "), _logErrors));
            top.Controls.Add(Theme.Row(
                Theme.Button("Export what's shown...", (s, e) => ExportShown(), 190),
                Theme.Button("Debug report (zip)...", (s, e) => ExportAll(), 190),
                Theme.Button("Open logs folder", (s, e) => { Directory.CreateDirectory(_current.LogArchiveDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + _current.LogArchiveDir + "\""); }, 160)));
            top.Controls.Add(Theme.Wrap("Every run's logs are kept (UserData\\DedicatedServer\\logs, copied when the server starts), so earlier days can be read and exported here. "
                + "The debug report has every log, the settings (without the password), status, stats and the respawn record: send it to whoever is fixing a problem. "
                + "For more detail, set DebugLog on the Settings page (all, or areas such as respawn, chat or net), then let the problem happen again. "
                + "Players can do the same in their game: DebugLog under [DedicatedServer] in UserData\\MelonPreferences.cfg; their log is MelonLoader\\Latest.log in the game folder.", 900));
            _logText = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = Theme.Mono };
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 12) };
            body.Controls.Add(_logText);
            page.Controls.Add(body); page.Controls.Add(top);
        }

        private List<KeyValuePair<string, string>> _logFiles = new List<KeyValuePair<string, string>>();   // label -> path
        private string _logFilesFor;
        private int _logFilesTick;

        /// <summary>This run's three logs, then every kept log, newest first.</summary>
        private void ListLogs()
        {
            if (_logFilesFor == _current.Folder && _ticks - _logFilesTick < 15) return;
            _logFilesFor = _current.Folder; _logFilesTick = _ticks;
            List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("This run: server log", _current.LogPath),
                new KeyValuePair<string, string>("This run: console (commands and replies)", _current.OutputPath),
                new KeyValuePair<string, string>("This run: Unity log", _current.UnityLogPath),
            };
            if (Directory.Exists(_current.LogArchiveDir))
                foreach (FileInfo f in new DirectoryInfo(_current.LogArchiveDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime))
                    l.Add(new KeyValuePair<string, string>((f.Name.StartsWith("unity") ? "Unity log, " : "Server log, ") + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "  (" + Fmt.Bytes(f.Length) + ")", f.FullName));
            if (l.Select(x => x.Value).SequenceEqual(_logFiles.Select(x => x.Value))) return;
            string keep = _logSource.SelectedItem as string;
            _logFiles = l;
            _logSource.Items.Clear();
            _logSource.Items.AddRange(l.Select(x => (object)x.Key).ToArray());
            int i = l.FindIndex(x => x.Key == keep);
            _logSource.SelectedIndex = i >= 0 ? i : 0;
        }

        private List<string> _logShown = new List<string>();

        private void ExportShown()
        {
            using (SaveFileDialog d = new SaveFileDialog { Title = "Save the log as shown", FileName = _current.Entry.Name + " " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".log", Filter = "Log|*.log;*.txt|All files|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) { File.WriteAllLines(d.FileName, _logShown); MessageBox.Show(this, _logShown.Count + " line(s) saved."); }
        }

        private void ExportAll()
        {
            using (SaveFileDialog d = new SaveFileDialog { Title = "Save every log, the stats and the settings as a zip", FileName = _current.Entry.Name + " logs " + DateTime.Now.ToString("yyyy-MM-dd") + ".zip", Filter = "Zip|*.zip" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { MessageBox.Show(this, ExportZip(_current, d.FileName) + " file(s) saved in " + Path.GetFileName(d.FileName) + "."); }
                catch (Exception e) { MessageBox.Show(this, "Could not make the zip: " + e.Message); }
            }
        }

        /// <summary>Every kept log, this run's logs, the console, server-data.json and the settings (not the password).</summary>
        public static int ExportZip(Server s, string zip)
        {
            if (File.Exists(zip)) File.Delete(zip);
            int n = 0;
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                Action<string, string> add = (path, name) =>
                {
                    if (!File.Exists(path)) return;
                    System.IO.Compression.ZipArchiveEntry e = z.CreateEntry(name);
                    using (Stream o = e.Open()) { byte[] b = System.Text.Encoding.UTF8.GetBytes(Json.ReadShared(path)); o.Write(b, 0, b.Length); }
                    n++;
                };
                if (Directory.Exists(s.LogArchiveDir)) foreach (string f in Directory.GetFiles(s.LogArchiveDir, "*.log")) add(f, "logs/" + Path.GetFileName(f));
                add(s.LogPath, "this-run/server.log");
                add(s.UnityLogPath, "this-run/unity.log");
                add(s.OutputPath, "this-run/console.log");
                add(s.DataPath, "server-data.json");
                add(s.StatusPath, "status.json");
                add(Path.Combine(s.DataDir, "catalog.json"), "catalog.json");
                add(Path.Combine(Path.GetDirectoryName(s.CurrentWorldFile()), "respawn.json"), "respawn.json");
                add(s.ShipsPath, "ships.json");
                string conf = Json.ReadShared(s.ConfPath);
                if (conf.Length > 0)
                {
                    conf = System.Text.RegularExpressions.Regex.Replace(conf, "(?m)^Password = .*$", "Password = \"(left out)\"");
                    System.IO.Compression.ZipArchiveEntry e = z.CreateEntry("dedicatedserver.conf");
                    using (Stream o = e.Open()) { byte[] b = System.Text.Encoding.UTF8.GetBytes(conf); o.Write(b, 0, b.Length); }
                    n++;
                }
            }
            return n;
        }

        private void RefreshLogs(bool force)
        {
            ListLogs();
            int sel = Math.Max(0, _logSource.SelectedIndex);
            string path = sel < _logFiles.Count ? _logFiles[sel].Value : _current.LogPath;
            long len = File.Exists(path) ? new FileInfo(path).Length : 0;
            string key = path + "|" + _logFilter.Text + "|" + _logErrors.Checked;
            if (!force && len == _logLen && key == _logKey) return;
            if (!force && key == _logKey && _ticks % 3 != 0) return;      // a growing log: read again every 3 s, not every second
            _logLen = len; _logKey = key;
            IEnumerable<string> lines = Json.ReadShared(path).Split('\n').Select(l => l.TrimEnd('\r'));
            if (_logErrors.Checked) lines = lines.Where(l => l.Contains("WARN") || l.Contains("ERROR") || l.Contains("Exception") || l.Contains("stalled") || l.Contains("TURNED AWAY") || l.Contains("error", StringComparison.OrdinalIgnoreCase));
            if (_logFilter.Text.Length > 0) lines = lines.Where(l => l.IndexOf(_logFilter.Text, StringComparison.OrdinalIgnoreCase) >= 0);
            List<string> list = lines.ToList();
            _logShown = list;
            _logText.Text = list.Count == 0 ? (File.Exists(path) ? "(nothing matches)" : "(no log yet: " + path + ")") : string.Join(Environment.NewLine, list.Skip(Math.Max(0, list.Count - 3000)));
            _logText.SelectionStart = _logText.TextLength;
            _logText.ScrollToCaret();
        }

        // ---- Uptime ----------------------------------------------------------------------------------

        private Label _upNow, _upWeeks, _totals, _explore, _spanNote;
        private TimelineChart _timeline;
        private CheckBox _bandwidth;
        private SeriesChart _joinsChart, _playersChart, _messagesChart, _shipsChart, _bytesChart, _memChart, _cpuChart;
        private LiveNetChart _liveNet;
        private LaneChart _allChart;
        private bool _bwUpdating;
        private int _spanDays = 1;
        private readonly List<Button> _spanButtons = new List<Button>(), _perButtons = new List<Button>();

        /// <summary>The span buttons (24 hours / 7 days / 30 days) and by hour / by day, shared by Uptime and Home.</summary>
        private FlowLayoutPanel SpanRow(Action changed)
        {
            FlowLayoutPanel row = Theme.Row(Theme.Label("Show:  ", Theme.Sub, Theme.Line));
            foreach (var kv in new[] { new KeyValuePair<string, int>("24 hours", 1), new KeyValuePair<string, int>("7 days", 7), new KeyValuePair<string, int>("30 days", 30) })
            {
                int d = kv.Value;
                Button b = Theme.Button(kv.Key, (s, e) => { _spanDays = d; changed(); }, 90);
                b.Tag = d; _spanButtons.Add(b); row.Controls.Add(b);
            }
            row.Controls.Add(Theme.Label("    by ", Theme.Body, Theme.Muted));
            foreach (bool hourly in new[] { true, false })
            {
                bool h = hourly;
                Button b = Theme.Button(hourly ? "hour" : "day", (s, e) => { _settings.ChartsHourly = h; _settings.Save(); changed(); }, 70);
                b.Tag = h; _perButtons.Add(b); row.Controls.Add(b);
            }
            return row;
        }

        private void MarkSpanButtons()
        {
            foreach (Button b in _spanButtons) { bool on = (int)b.Tag == _spanDays; b.BackColor = on ? Color.FromArgb(30, 77, 115) : Theme.ButtonBack; b.FlatAppearance.BorderSize = on ? 2 : 1; }
            foreach (Button b in _perButtons) { bool on = (bool)b.Tag == Hourly; b.BackColor = on ? Color.FromArgb(30, 77, 115) : Theme.ButtonBack; b.FlatAppearance.BorderSize = on ? 2 : 1; }
        }

        /// <summary>By the hour unless asked for days; 30 days by the hour is 720 points, still fine as lines.</summary>
        private bool Hourly { get { return _settings.ChartsHourly; } }

        private void BuildUptime(TabPage page)
        {
            PageColumn c = new PageColumn();
            _upNow = Theme.Label("", Theme.Head, Theme.Line);
            _upWeeks = Theme.Wrap("", 1000, Theme.Body, Theme.Text);
            _totals = Theme.Wrap("", 1000, Theme.Body, Theme.Text);
            _explore = Theme.Wrap("", 1000, Theme.Body, Theme.Text);
            _spanNote = Theme.Wrap("", 1000);
            _bandwidth = Theme.Check("Count network use (bytes in and out; needed for the network charts)", false);
            _bandwidth.CheckedChanged += (s, e) =>
            {
                if (_bwUpdating) return;
                if (_current.Running) Send("set TrackBandwidth " + (_bandwidth.Checked ? "on" : "off"));
                else { Conf cf = _current.LoadConf(); cf.SetBool("TrackBandwidth", _bandwidth.Checked); cf.Save(_current.ConfPath); }
            };
            Func<double, string> count = v => v.ToString("0");
            _joinsChart = new SeriesChart("Joins", SeriesChart.Style.Bars, count);
            _playersChart = new SeriesChart("Players", SeriesChart.Style.Bars, count);
            _messagesChart = new SeriesChart("Chat messages", SeriesChart.Style.Bars, count);
            _shipsChart = new SeriesChart("Ships built", SeriesChart.Style.Bars, count);
            _bytesChart = new SeriesChart("Network use (average speed)", SeriesChart.Style.Lines, v => LiveNetChart.Rate(v));
            _liveNet = new LiveNetChart();
            _memChart = new SeriesChart("Memory", SeriesChart.Style.Lines, v => Fmt.Bytes(v * 1048576));
            _cpuChart = new SeriesChart("CPU", SeriesChart.Style.Lines, v => v.ToString("0") + "%");
            _timeline = new TimelineChart();
            _allChart = new LaneChart("All together (each line on its own scale)");
            c.Controls.Add(_upNow);
            c.Controls.Add(SpanRow(() => RefreshUptime()));
            c.Controls.Add(_spanNote);
            c.Controls.Add(Theme.Sized(_timeline, "up.timeline", 150));
            c.Controls.Add(_upWeeks);
            c.Controls.Add(Theme.Label("Totals", Theme.Sub, Theme.Line)); c.Controls.Add(_totals); c.Controls.Add(_bandwidth);
            c.Controls.Add(Theme.Label("Explored", Theme.Sub, Theme.Line)); c.Controls.Add(_explore);
            c.Controls.Add(Theme.Sized(_memChart, "up.memory", 190));
            c.Controls.Add(Theme.Sized(_cpuChart, "up.cpu", 170));
            c.Controls.Add(Theme.Sized(_liveNet, "up.live", 200));
            c.Controls.Add(Theme.Sized(_joinsChart, "up.joins", 190));
            c.Controls.Add(Theme.Sized(_playersChart, "up.players", 190));
            c.Controls.Add(Theme.Sized(_messagesChart, "up.messages", 190));
            c.Controls.Add(Theme.Sized(_shipsChart, "up.ships", 190));
            c.Controls.Add(Theme.Sized(_bytesChart, "up.bytes", 190));
            c.Controls.Add(_allChart);
            page.Controls.Add(c);
        }

        private void RefreshUptime()
        {
            JsonNode data = _current.Data, st = _current.Status;
            bool running = _current.Running;
            List<JsonNode> runs = Json.Arr(data, "Runs").ToList();
            JsonNode last = runs.LastOrDefault();
            DateTime now = DateTime.UtcNow;
            MarkSpanButtons();
            if (running && last != null) _upNow.Text = "Up for " + Fmt.Span((now - Json.Date(last, "Start")).TotalSeconds) + " (since " + Json.Date(last, "Start").ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ")";
            else _upNow.Text = last != null ? "Down for " + Fmt.Span((now - Json.Date(last, "LastBeat")).TotalSeconds) + (Json.Bool(last, "Clean") ? "" : " (the last run ended in a crash)") : "Never run yet";
            _upNow.ForeColor = running ? Theme.Good : Theme.Muted;
            _upWeeks.Text = string.Join("\n", new[] { 1.0, 7.0, 30.0 }.Select(d => Uptime(runs, d, running)));

            List<JsonNode> days = Json.Arr(data, "Days").ToList();
            Func<string, long> sum = k => days.Sum(d => (long)Json.Num(d, k));
            _totals.Text = "Players ever: " + Json.Arr(data, "Players").Count() + "   Joins: " + sum("Joins") + "   Messages: " + sum("Messages") + "   Players banned: " + sum("Bans")
                + "   Ships built: " + sum("ShipsBuilt") + (st != null ? "   Ships now: " + Json.Int(st, "ships") : "")
                + "\nNetwork: " + Fmt.Bytes(sum("BytesIn")) + " in, " + Fmt.Bytes(sum("BytesOut")) + " out in all";
            _bwUpdating = true;
            bool counting = _current.LoadConf().GetBool("TrackBandwidth");
            _bandwidth.Checked = counting;
            _bwUpdating = false;

            JsonObject ex = null;
            try { ex = st?["planetsExplored"] as JsonObject; } catch { }
            if (ex != null && ex.Count > 0)
                _explore.Text = string.Join("   ", ex.Select(kv => kv.Key + ": " + kv.Value + "%")) + "\nPlanets on average: " + Json.Num(st, "worldExploredPercent").ToString("0.00") + "%   Space: " + Json.Num(st, "spaceExploredPercent").ToString("0.000") + "% of the star system";
            else _explore.Text = running ? "Nobody has explored yet." : "Shown while the server runs.";

            RefreshLiveNet(running, counting);
            RefreshUsage();

            Activity a = Activity.Build(new[] { data }, new[] { running }, _spanDays, Hourly);
            string per = a.Hourly ? "an hour" : "a day";
            _spanNote.Text = a.Hourly && a.HoursSince > a.Times[0] ? "Hour by hour from " + (a.HoursSince == DateTime.MaxValue ? "now on" : a.HoursSince.ToLocalTime().ToString("yyyy-MM-dd HH:00")) + " (servers before 1.4.0 kept only days)." : "";
            _joinsChart.Set(new[] { new SeriesChart.Series { Name = "Joins in " + per, Color = Theme.Line, Values = a.Joins } }, a.Times, a.Hourly);
            _playersChart.Set(new[]
            {
                new SeriesChart.Series { Name = "Different players", Color = Theme.Good, Values = a.Players },
                new SeriesChart.Series { Name = "Most on at once", Color = Color.FromArgb(60, 140, 90), Values = a.Peak },
            }, a.Times, a.Hourly);
            _messagesChart.Set(new[] { new SeriesChart.Series { Name = "Messages in " + per, Color = Theme.Warn, Values = a.Messages } }, a.Times, a.Hourly);
            _shipsChart.Set(new[] { new SeriesChart.Series { Name = "Ships built in " + per, Color = Color.FromArgb(120, 200, 230), Values = a.Ships } }, a.Times, a.Hourly);
            _bytesChart.Set(new[]
            {
                new SeriesChart.Series { Name = "Receive", Color = LiveNetChart.In, Values = a.InRate },
                new SeriesChart.Series { Name = "Send", Color = LiveNetChart.Out, Values = a.OutRate },
            }, a.Times, a.Hourly, counting ? "" : "Network use isn't being counted (tick the box above)");
            _allChart.Set(Lanes(a), a.Times, a.Hourly);

            DateTime from = now.AddDays(-_spanDays);
            List<TimelineChart.Run> tr = new List<TimelineChart.Run>();
            for (int i = 0; i < runs.Count; i++)
            {
                bool isLive = i == runs.Count - 1 && running;
                tr.Add(new TimelineChart.Run { Start = Json.Date(runs[i], "Start"), End = isLive ? now : Json.Date(runs[i], "LastBeat"), Clean = Json.Bool(runs[i], "Clean"), Live = isLive });
            }
            _timeline.Set(tr, Json.Arr(data, "Freezes").Select(f => new TimelineChart.Freeze { At = Json.Date(f, "At"), Ms = Json.Num(f, "Ms") }), from, now);
        }

        /// <summary>Every number as its own lane.</summary>
        private static List<LaneChart.Lane> Lanes(Activity a)
        {
            Func<double, string> count = v => v.ToString("0");
            List<LaneChart.Lane> l = new List<LaneChart.Lane>
            {
                new LaneChart.Lane { Name = "Joins", Color = Theme.Line, Values = a.Joins, Format = count },
                new LaneChart.Lane { Name = "Players", Color = Theme.Good, Values = a.Players, Format = count },
                new LaneChart.Lane { Name = "Chat messages", Color = Theme.Warn, Values = a.Messages, Format = count },
                new LaneChart.Lane { Name = "Ships built", Color = Color.FromArgb(120, 200, 230), Values = a.Ships, Format = count },
                new LaneChart.Lane { Name = "Bans", Color = Theme.Bad, Values = a.Bans, Format = count },
                new LaneChart.Lane { Name = a.Hourly ? "Up (minutes)" : "Up (hours)", Color = Color.FromArgb(170, 175, 190), Values = a.Up, Format = v => v.ToString(a.Hourly ? "0" : "0.0") },
            };
            if (a.InRate.Any(x => x > 0) || a.OutRate.Any(x => x > 0))
                l.Add(new LaneChart.Lane { Name = "Network", Color = LiveNetChart.Out, Values = a.InRate.Zip(a.OutRate, (x, y) => x + y).ToArray(), Format = v => LiveNetChart.Rate(v) });
            return l;
        }

        // resources.csv: a row a minute (server memory and CPU, the machine's), shown over the chosen span
        private long _usageStamp;
        private List<string[]> _usageRows = new List<string[]>();

        private void RefreshUsage()
        {
            string path = _current.ResourcesPath;
            long stamp = File.Exists(path) ? new FileInfo(path).Length ^ File.GetLastWriteTimeUtc(path).Ticks : 0;
            if (stamp != _usageStamp)
            {
                _usageStamp = stamp;
                _usageRows = Json.ReadShared(path).Split('\n').Skip(1).Select(l => l.Trim().Split(',')).Where(a => a.Length >= 9).ToList();
            }
            DateTime from = DateTime.UtcNow.AddDays(-_spanDays);
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            List<KeyValuePair<DateTime, string[]>> rows = new List<KeyValuePair<DateTime, string[]>>();
            foreach (string[] a in _usageRows)
            {
                DateTime t;
                if (DateTime.TryParse(a[0], inv, System.Globalization.DateTimeStyles.AdjustToUniversal, out t) && t >= from) rows.Add(new KeyValuePair<DateTime, string[]>(t, a));
            }
            // at most a few hundred points: minutes averaged into longer steps over long spans
            int step = Math.Max(1, rows.Count / 400);
            List<DateTime> times = new List<DateTime>();
            List<double>[] cols = Enumerable.Range(0, 9).Select(_ => new List<double>()).ToArray();
            for (int i = 0; i < rows.Count; i += step)
            {
                var chunk = rows.Skip(i).Take(step).ToList();
                times.Add(chunk[0].Key);
                for (int k = 1; k < 9; k++) cols[k].Add(chunk.Average(r => { double v; return double.TryParse(r.Value[k], System.Globalization.NumberStyles.Float, inv, out v) ? v : 0; }));
            }
            string note = rows.Count == 0 ? (_current.Running ? "The first numbers come a minute after the server starts." : "Shown from the server's record (resources.csv); none yet.") : "";
            _memChart.Set(new[]
            {
                new SeriesChart.Series { Name = "Server", Color = Theme.Line, Values = cols[1].ToArray() },
                new SeriesChart.Series { Name = "Whole PC in use", Color = Color.FromArgb(150, 160, 180), Values = cols[6].ToArray(), Dashed = true },
                new SeriesChart.Series { Name = "PC total", Color = Theme.Bad, Values = cols[7].ToArray(), Dashed = true },
            }, times.ToArray(), true, note);
            _cpuChart.Set(new[]
            {
                new SeriesChart.Series { Name = "Server", Color = Theme.Warn, Values = cols[5].ToArray() },
                new SeriesChart.Series { Name = "Whole PC", Color = Color.FromArgb(150, 160, 180), Values = cols[8].ToArray(), Dashed = true },
            }, times.ToArray(), true, note);
        }

        /// <summary>The live network graph, from the server's bandwidth-live.json (a sample a second).</summary>
        private void RefreshLiveNet(bool running, bool counting)
        {
            if (_liveNet == null) return;
            JsonNode live = running ? Json.ReadFile(_current.BandwidthLivePath) : null;
            List<JsonNode> s = Json.Arr(live, "samples").ToList();
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Func<JsonNode, int, double> at = (x, i) => { try { return x[i].GetValue<double>(); } catch { return 0; } };
            _liveNet.Set(s.Select(x => epoch.AddSeconds(at(x, 0))).ToArray(), s.Select(x => at(x, 1)).ToArray(), s.Select(x => at(x, 2)).ToArray(),
                !running ? "Shown while the server runs." : !counting ? "Network use isn't being counted: tick \"Count network use\" above." : "");
        }

        /// <summary>Up and down time over the last few days, from the server's runs (the same sums as the server's stats).</summary>
        private static string Uptime(List<JsonNode> runs, double days, bool running)
        {
            DateTime now = DateTime.UtcNow, from = now.AddDays(-days);
            JsonNode first = runs.OrderBy(r => Json.Date(r, "Start")).FirstOrDefault();
            if (first == null) return "";
            if (Json.Date(first, "Start") > from) from = Json.Date(first, "Start");
            double window = Math.Max(1, (now - from).TotalSeconds), up = 0;
            int starts = 0, crashes = 0;
            DateTime covered = from;
            for (int i = 0; i < runs.Count; i++)
            {
                JsonNode r = runs[i];
                bool isLast = i == runs.Count - 1;
                DateTime st = Json.Date(r, "Start"), end = isLast && running ? now : Json.Date(r, "LastBeat");
                if (end < from) continue;
                if (st >= from) starts++;
                if (!(isLast && running) && !Json.Bool(r, "Clean") && st >= from) crashes++;
                DateTime s0 = st < covered ? covered : st;
                if (end > s0) { up += (end - s0).TotalSeconds; covered = end; }
            }
            string label = (days <= 1 ? "Last 24 hours" : "Last " + days + " days") + (Json.Date(first, "Start") > now.AddDays(-days) ? " (since it was first started, " + Fmt.Span((now - Json.Date(first, "Start")).TotalSeconds) + " ago)" : "");
            return label + ": up " + (100.0 * up / window).ToString("0.0") + "% (" + Fmt.Span(up) + " up, " + Fmt.Span(window - up) + " down), " + starts + " start(s), " + crashes + " crash(es)";
        }

#if SDSMTEST
        public SeriesChart JoinsChartForTest { get { return _joinsChart; } }
        public LaneChart AllChartForTest { get { return _allChart; } }
        public LiveNetChart LiveNetForTest { get { return _liveNet; } }
        public void SpanForTest(int days, bool hourly) { _spanDays = days; _settings.ChartsHourly = hourly; RefreshUptime(); }
#endif
    }
}
