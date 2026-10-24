// The Mods page: every mod in the game folder the server was made from, and where each one runs -
// on the server with every player needing it, on the server but optional for players, or not on
// the server at all (players' own mods, or ones that misbehave on a server). Each mod opens to show
// its own settings on this server (its MelonPreferences), which can be changed here: through the
// running server (`modset`), or straight in UserData\MelonPreferences.cfg while it is stopped.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    /// <summary>One mod setting, from the server's modsettings.json or read from MelonPreferences.cfg.</summary>
    public class ModSetting
    {
        public string Category, Id, Name, Desc = "", Type = "text", Value = "", Default = "";
        public string[] Choices = new string[0];
        public string Mod = "";

        /// <summary>Every category and setting: what the server last wrote, or the cfg file itself.</summary>
        public static List<ModSetting> Load(Server s, IList<ModMeta> mods)
        {
            List<ModSetting> l = new List<ModSetting>();
            JsonNode written = s.ModSettings;
            Dictionary<string, Dictionary<string, string>> file = ReadCfg(s.PrefsPath);
            if (written != null)
            {
                foreach (JsonNode c in Json.Arr(written, "categories"))
                    foreach (JsonNode e in Json.Arr(c, "entries"))
                    {
                        ModSetting m = new ModSetting
                        {
                            Category = Json.Str(c, "id"), Mod = Json.Str(c, "mod"), Id = Json.Str(e, "id"), Name = Json.Str(e, "name"), Desc = Json.Str(e, "desc"),
                            Type = Json.Str(e, "type"), Value = Json.Str(e, "value"), Default = Json.Str(e, "default"),
                            Choices = Json.Arr(e, "choices").Select(x => x.ToString()).ToArray(),
                        };
                        // the file wins while the server is stopped (it may have been changed here since)
                        Dictionary<string, string> sec; string raw;
                        if (!s.Running && file.TryGetValue(m.Category, out sec) && sec.TryGetValue(m.Id, out raw)) m.Value = Conf.Unquote(raw);
                        l.Add(m);
                    }
                return l;
            }
            // never run with this version: what the file says, typed by how each value is written
            foreach (var cat in file)
            {
                if (cat.Key == "DedicatedServerHost" || cat.Key == "DedicatedServer") continue;
                string mod = Owner(cat.Key, mods);
                foreach (var kv in cat.Value)
                {
                    if (kv.Key.StartsWith("#")) continue;
                    string raw = kv.Value;
                    string type = raw == "true" || raw == "false" ? "bool" : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "int"
                        : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "number" : raw.StartsWith("\"") ? "text" : "other";
                    string desc; cat.Value.TryGetValue("#" + kv.Key, out desc);
                    l.Add(new ModSetting { Category = cat.Key, Mod = mod, Id = kv.Key, Name = kv.Key, Desc = desc ?? "", Type = type, Value = Conf.Unquote(raw) });
                }
            }
            return l;
        }

        private static string Squash(string s) { s = (s ?? "").ToLowerInvariant().Replace("+", "plus"); return new string(s.Where(char.IsLetterOrDigit).ToArray()); }

        /// <summary>The mod a category belongs to, by name (the same rule the server uses).</summary>
        public static string Owner(string category, IList<ModMeta> mods)
        {
            string c = Squash(category);
            Func<ModMeta, IEnumerable<string>> names = m =>
            {
                string n = Squash(m.Name);
                return new[] { n, n.StartsWith("demo") ? n.Substring(4) : n, Squash(Path.GetFileNameWithoutExtension(m.File)) }.Where(x => x.Length > 0);
            };
            ModMeta hit = mods.FirstOrDefault(m => names(m).Contains(c)) ?? mods.FirstOrDefault(m => names(m).Any(n => n.Length >= 4 && c.Length >= 4 && (n.Contains(c) || c.Contains(n))));
            return hit != null ? hit.Name : "";
        }

        /// <summary>[Category] -> key -> raw value (and "#key" -> its comment).</summary>
        public static Dictionary<string, Dictionary<string, string>> ReadCfg(string path)
        {
            Dictionary<string, Dictionary<string, string>> d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            Dictionary<string, string> cur = null;
            string comment = "";
            foreach (string line in Json.ReadShared(path).Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { string name = t.Trim('[', ']').Trim(); if (!d.TryGetValue(name, out cur)) d[name] = cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); comment = ""; continue; }
                if (cur == null) continue;
                if (t.StartsWith("#")) { comment = (comment.Length > 0 ? comment + " " : "") + t.TrimStart('#').Trim(); continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string key = t.Substring(0, eq).Trim();
                cur[key] = t.Substring(eq + 1).Trim();
                if (comment.Length > 0) cur["#" + key] = comment;
                comment = "";
            }
            return d;
        }

        /// <summary>How a value is written in the cfg file.</summary>
        public string Literal(string v)
        {
            v = (v ?? "").Trim();
            if (Type == "bool") return v == "true" || v == "on" ? "true" : "false";
            if (Type == "int") { long n; return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n.ToString(CultureInfo.InvariantCulture) : null; }
            if (Type == "number")
            {
                double n;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return null;
                string s = n.ToString("R", CultureInfo.InvariantCulture);
                return s.Contains(".") || s.Contains("E") ? s : s + ".0";
            }
            return Conf.Quote(v);
        }

        /// <summary>Writes changed values into MelonPreferences.cfg (keeping everything else as it is).</summary>
        public static void WriteCfg(string path, IEnumerable<KeyValuePair<ModSetting, string>> changes)
        {
            List<string> lines = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList() : new List<string>();
            foreach (var ch in changes)
            {
                string literal = ch.Key.Literal(ch.Value);
                if (literal == null) continue;
                int sec = lines.FindIndex(l => l.Trim() == "[" + ch.Key.Category + "]");
                if (sec < 0) { lines.Add(""); lines.Add("[" + ch.Key.Category + "]"); sec = lines.Count - 1; }
                int end = lines.FindIndex(sec + 1, l => l.Trim().StartsWith("["));
                if (end < 0) end = lines.Count;
                int at = -1;
                for (int i = sec + 1; i < end; i++)
                {
                    string t = lines[i].Trim();
                    int eq = t.IndexOf('=');
                    if (!t.StartsWith("#") && eq > 0 && string.Equals(t.Substring(0, eq).Trim(), ch.Key.Id, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                }
                string line = ch.Key.Id + " = " + literal;
                if (at >= 0) lines[at] = line;
                else { int ins = end; while (ins > sec + 1 && lines[ins - 1].Trim().Length == 0) ins--; lines.Insert(ins, line); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
        }
    }

    public partial class MainForm
    {
        private PageColumn _modsList;
        private Label _modsNote;
        private Button _modsSave;
        private List<ModMeta> _modsAll = new List<ModMeta>();
        private string _modsLoadedFor;
        private readonly List<ModRow> _modRows = new List<ModRow>();

        /// <summary>One mod: where it runs, and (opened) its settings on this server.</summary>
        private class ModRow
        {
            public ModMeta Meta;
            public string Category = "";       // for the "other settings" row: no mod
            public FlowLayoutPanel Box, Body;
            public Label Arrow, Title, Now;
            public ChoiceLabel Use;
            public string UseWas;
            public List<KeyValuePair<ModSetting, Control>> Fields = new List<KeyValuePair<ModSetting, Control>>();
            public Label SaveNote;
            public bool Open;
        }

        private void BuildMods(TabPage page)
        {
            _modsNote = Theme.Wrap("", 900);
            _modsSave = Theme.Button("Save where mods run", (s, e) => SaveMods(), 170);
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 10, 18, 0) };
            Label help = Theme.Wrap("Pick where each mod runs (hover here for what each choice means); click a mod for its settings. Drop .dll files on this page to add them.", 1100, Theme.Body, Theme.Text);
            new ToolTip { AutoPopDelay = 30000 }.SetToolTip(help, ModUse.Help);
            top.Controls.Add(help);
            top.Controls.Add(Theme.Row(Theme.Button("Add mod...", (s, e) => AddModsPicked(), 120), _modsSave, Theme.Button("Undo", (s, e) => { _modsLoadedFor = null; RefreshMods(); }, 90)));
            _modsGame = Theme.Wrap("", 700, Theme.Small, Theme.Muted);
            top.Controls.Add(Theme.Row(_modsGame,
                Link("Change...", () => ChangeGameFolder()),
                Link("Open game folder", () => { string g = _current.LoadInfo().SourceGame; if (GameCopy.IsGameFolder(g)) OpenFolder(g); }),
                Link("Open server's Mods", () => OpenFolder(Path.Combine(_current.Folder, "Mods"))),
                Link("Open server folder", () => OpenFolder(_current.Folder))));
            top.Controls.Add(_modsNote);
            page.AllowDrop = true;
            page.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            page.DragDrop += (s, e) => { string[] f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) AddMods(f); };

            _modsList = new PageColumn { Padding = new Padding(0, 4, 8, 4) };
            _modsList.Name = "ModsList";

            // mods players have that the server doesn't run
            _extraGrid = Theme.NewGrid("Mod", "Version", "Seen", "Last player", "Allowed");
            _extraReject = Theme.Check("Turn away players with other mods, unless allowed", false);
            _extraReject.CheckedChanged += (s, e) => { if (!_extraLoading) SetConf("RejectExtraMods", _extraReject.Checked ? "on" : "off"); };
            PageColumn etop = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            etop.Controls.Add(Theme.Label("MODS PLAYERS HAVE THAT THE SERVER DOESN'T RUN", Theme.Sub, Theme.Line));
            etop.Controls.Add(_extraReject);
            etop.Controls.Add(Theme.Row(Theme.Button("Allow", (s, e) => AllowExtra(true), 90), Theme.Button("Don't allow", (s, e) => AllowExtra(false), 120),
                Theme.Wrap("Allowed mods are let in without being on the server (players' own screen mods, say). The list fills as players join.", 600)));

            Panel list = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 4) };
            list.Controls.Add(_modsList);
            Panel flagged = new Panel { Dock = DockStyle.Bottom, Height = Math.Max(100, Theme.SizeOf("mods.flagged", 230)), Padding = new Padding(18, 0, 18, 12) };
            flagged.Controls.Add(_extraGrid); flagged.Controls.Add(etop);
            Splitter edge = new Splitter { Dock = DockStyle.Bottom, Height = 7, BackColor = Theme.Back, MinSize = 100, MinExtra = 120, Cursor = Cursors.HSplit };
            edge.SplitterMoved += (s, e) => Theme.RememberSize("mods.flagged", flagged.Height);
            page.Controls.Add(list); page.Controls.Add(edge); page.Controls.Add(flagged); page.Controls.Add(top);
            // the mod list always keeps some room: the panel below gives way on a small window
            page.Resize += (s, e) =>
            {
                int room = page.ClientSize.Height - top.Height - edge.Height;
                int most = Math.Max(100, room * 2 / 5), want = Math.Max(100, Theme.SizeOf("mods.flagged", 230));
                flagged.Height = Math.Min(want, most);
            };
        }

        private DataGridView _extraGrid;
        private CheckBox _extraReject;
        private bool _extraLoading;
        private Label _modsGame;
        private Dictionary<string, string> _newerInGame = new Dictionary<string, string>();

        private static LinkLabel Link(string text, Action click)
        {
            LinkLabel l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 4, 14, 0), LinkColor = Theme.Line, ActiveLinkColor = Theme.Good, Font = Theme.Small };
            l.Click += (s, e) => click();
            return l;
        }

        private static void OpenFolder(string dir)
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        private void AddModsPicked()
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "MelonLoader mods (*.dll)|*.dll", Multiselect = true, Title = "Add mods to this server" })
                if (d.ShowDialog(this) == DialogResult.OK) AddMods(d.FileNames);
        }

        /// <summary>DLLs onto this server (Add mod..., or dropped on the page).</summary>
        public string AddMods(IEnumerable<string> files)
        {
            List<string> said;
            try { said = GameCopy.AddMods(_current.Folder, files, _current.Running); }
            catch (Exception e) { said = new List<string> { "Could not add: " + e.Message }; }
            _modsLoadedFor = null; _modsInStamp = 0; RefreshMods(); _settingsLoadedFor = null;
            _modsNote.Text = string.Join("\n", said);
            _modsNote.ForeColor = said.Any(x => x.Contains(": ")) ? Theme.Warn : Theme.Good;
            return _modsNote.Text;
        }

        private void RemoveMod(ModMeta m)
        {
            if (MessageBox.Show(this, "Take " + m.Name + " off this server?\n\nIts file goes to " + GameCopy.RemovedFolder + " in the server's folder (nothing is deleted). A copy in the game folder stays there and is still listed here.",
                "Remove " + m.Name, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            RemoveModNow(m);
        }

        public string RemoveModNow(ModMeta m)
        {
            string said;
            try { said = GameCopy.RemoveMod(_current.Folder, m, _current.Running); }
            catch (Exception e) { said = "Could not remove " + m.Name + ": " + e.Message; }
            _modsLoadedFor = null; _modsInStamp = 0; RefreshMods();
            _modsNote.Text = said; _modsNote.ForeColor = said.Contains("removed") ? Theme.Good : Theme.Warn;
            return said;
        }

        private void ChangeGameFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "The StellarDrive folder this server takes the game's files (and its mods list) from" })
            {
                string now = _current.LoadInfo().SourceGame;
                if (Directory.Exists(now)) d.SelectedPath = now;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GameCopy.IsGameFolder(d.SelectedPath)) { MessageBox.Show(this, "No StellarDrive.exe in that folder."); return; }
                ServerInfo info = _current.LoadInfo(); info.SourceGame = d.SelectedPath; _current.SaveInfo(info);
                _modsLoadedFor = null; RefreshMods();
            }
        }

        private void RefreshExtraMods()
        {
            _extraLoading = true;
            try
            {
                Conf c = _current.LoadConf();
                _extraReject.Checked = c.GetBool("RejectExtraMods");
                HashSet<string> allowed = new HashSet<string>(c.Get("AllowedExtraMods").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
                string keep = _extraGrid.SelectedRows.Count > 0 ? (string)_extraGrid.SelectedRows[0].Cells["Mod"].Value : null;
                _extraGrid.Rows.Clear();
                foreach (JsonNode f in Json.Arr(_current.Data, "FlaggedMods").OrderByDescending(x => Json.Str(x, "LastSeen")))
                {
                    string name = Json.Str(f, "Name");
                    DateTime seen = Json.Date(f, "LastSeen");
                    int i = _extraGrid.Rows.Add(name, Json.Str(f, "Version"), Json.Int(f, "Times") + " time(s), last " + (seen > DateTime.MinValue ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?"), Json.Str(f, "LastPlayer"), allowed.Contains(name) ? "yes" : "");
                    if (name == keep) _extraGrid.Rows[i].Selected = true;
                }
                foreach (string a in allowed.Where(a => !_extraGrid.Rows.Cast<DataGridViewRow>().Any(r => string.Equals((string)r.Cells["Mod"].Value, a, StringComparison.OrdinalIgnoreCase))))
                    _extraGrid.Rows.Add(a, "", "(not seen yet)", "", "yes");
            }
            finally { _extraLoading = false; }
        }

        private void AllowExtra(bool allow)
        {
            if (_extraGrid.SelectedRows.Count == 0) return;
            List<string> allowed = _current.LoadConf().Get("AllowedExtraMods").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            foreach (DataGridViewRow r in _extraGrid.SelectedRows)
            {
                string name = (string)r.Cells["Mod"].Value;
                allowed.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                if (allow) allowed.Add(name);
            }
            SetConf("AllowedExtraMods", string.Join(", ", allowed));
            RefreshExtraMods();
        }

        /// <summary>A setting, sent to the running server (which saves it) or written to the file.</summary>
        private void SetConf(string key, string value)
        {
            if (_current.Running) { Send("set " + key + " " + (value.Length == 0 ? "\"\"" : value)); System.Threading.Thread.Sleep(300); }
            else
            {
                Conf c = _current.LoadConf();
                Conf.ConfItem item = c.Find(key);
                if (item != null && item.IsBool) c.SetBool(key, value == "on"); else c.Set(key, value);
                c.Save(_current.ConfPath);
            }
            _settingsLoadedFor = null;
        }

        private void RefreshMods()
        {
            if (_ticks % 5 == 0 || _modsLoadedFor != _current.Folder) RefreshExtraMods();
            if (_modsLoadedFor == _current.Folder) return;
            _modsLoadedFor = _current.Folder;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            ServerInfo info = _current.LoadInfo();
            List<ModMeta> onServer = ModsIn(_current);
            HashSet<string> on = new HashSet<string>(onServer.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            HashSet<string> optional = new HashSet<string>(_current.LoadConf().Get("OptionalMods").Split(',').Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
            _modsAll = GameCopy.AllMods(_current.Folder, info.SourceGame, out _newerInGame);
            _modsGame.Text = GameCopy.IsGameFolder(info.SourceGame) ? "Game folder: " + info.SourceGame : "Game folder: " + (info.SourceGame.Length > 0 ? info.SourceGame + " (not there any more)" : "(none)");
            PageMs["Mods: reading mods"] = sw.Elapsed.TotalMilliseconds;
            List<ModSetting> settings = ModSetting.Load(_current, _modsAll);
            PageMs["Mods: reading settings"] = sw.Elapsed.TotalMilliseconds;

            // the rows are made off the page and go on it together (adding them one by one laid the page out each time)
            _modsList.SuspendLayout();
            _modsList.Visible = false;
            foreach (ModRow r in _modRows) r.Box.Dispose();
            _modRows.Clear();
            List<Control> made = new List<Control>();
            foreach (ModMeta m in _modsAll.OrderBy(x => x.Name))
            {
                bool isOn = on.Contains(m.Name) || m.Name == GameCopy.ServerMod;
                string use = m.Name == GameCopy.ServerMod ? ModUse.Required : !isOn ? ModUse.PlayersOnly : optional.Contains(m.Name) ? ModUse.Optional : ModUse.Required;
                ModRow row = NewModRow(m, use, isOn, settings.Where(x => x.Mod == m.Name).ToList());
                _modRows.Add(row);
                made.Add(row.Box);
            }
            List<ModSetting> other = settings.Where(x => x.Mod.Length == 0 || !_modsAll.Any(m => m.Name == x.Mod)).ToList();
            if (other.Count > 0)
            {
                ModRow row = NewModRow(null, null, false, other);
                _modRows.Add(row);
                made.Add(row.Box);
            }
            _modsList.Controls.AddRange(made.ToArray());
            PageMs["Mods: making rows"] = sw.Elapsed.TotalMilliseconds;
            PageMs["Mods: of which choices"] = _rowCombo * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            PageMs["Mods: of which colours"] = _rowApply * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _rowCombo = _rowApply = 0;
            _modsList.ResumeLayout(true);
            _modsList.Visible = true;
            PageMs["Mods: laying out"] = sw.Elapsed.TotalMilliseconds;
            _modsNote.Text = _modsAll.Count + " mod(s): on this server, turned off for it, or in the game folder's Mods. Where they run changes with the server stopped; their settings can change at any time"
                  + (_current.ModSettings == null ? " (start the server once to see every setting with its description)." : ".")
                  + (!GameCopy.IsGameFolder(info.SourceGame) ? " The game folder isn't set or isn't there: Change game folder... to pick it." : "");
            _modsNote.ForeColor = Theme.Muted;
            _modsSave.Enabled = false;
        }

        /// <summary>Is this file in the server's Mods-off (sub = OffFolder), or anywhere outside the server's folder (sub = null)?</summary>
        private bool InFolder(string file, string sub)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(file ?? ""));
            if (sub != null) return string.Equals(dir, Path.GetFullPath(Path.Combine(_current.Folder, sub)), StringComparison.OrdinalIgnoreCase);
            return !dir.StartsWith(Path.GetFullPath(_current.Folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private long _rowCombo, _rowApply;

        private ModRow NewModRow(ModMeta m, string use, bool isOn, List<ModSetting> settings)
        {
            ModRow row = new ModRow { Meta = m, UseWas = use };
            row.Box = new FitFlow { BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(8, 6, 8, 6) };
            FlowLayoutPanel head = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0), Cursor = Cursors.Hand };
            row.Arrow = Theme.Label(settings.Count > 0 || m == null ? "▸" : " ", Theme.Sub, Theme.Line);
            row.Arrow.Margin = new Padding(0, 6, 6, 0);
            row.Arrow.Width = 18; row.Arrow.AutoSize = false; row.Arrow.Height = 24;
            row.Title = Theme.Label(m != null ? m.Name + "  " : "Other settings  ", Theme.Sub, Theme.Text);
            Label ver = Theme.Label(m != null ? "v" + m.Version + "   " : "(not tied to one mod)   ", Theme.Small, Theme.Muted);
            ver.Margin = new Padding(0, 9, 8, 0);
            head.Controls.Add(row.Arrow); head.Controls.Add(row.Title); head.Controls.Add(ver);
            if (m != null)
            {
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                row.Use = Theme.ChoiceLight(ModUse.Choices, use);
                _rowCombo += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                row.Use.Margin = new Padding(0, 4, 12, 0);
                if (m.Name == GameCopy.ServerMod) row.Use.Enabled = false;
                row.Use.SelectedIndexChanged += (s, e) => ModsChanged();
                head.Controls.Add(row.Use);
                string where = isOn ? "on the server now" : InFolder(m.File, GameCopy.OffFolder) ? "not on the server now (turned off)" : "not on the server (in the game folder)";
                string newer;
                if (_newerInGame.TryGetValue(m.Name, out newer)) where += "; the game folder has v" + newer + " (Update from game on the Launch page)";
                row.Now = Theme.Label(where, Theme.Small, isOn ? Theme.Good : Theme.Muted);
                row.Now.Margin = new Padding(0, 9, 12, 0);
                head.Controls.Add(row.Now);
                if (m.Name != GameCopy.ServerMod && !InFolder(m.File, null))
                {
                    LinkLabel rm = new LinkLabel { Text = "Remove", AutoSize = true, Margin = new Padding(0, 9, 12, 0), LinkColor = Theme.Muted, ActiveLinkColor = Theme.Bad };
                    rm.Click += (s, e) => RemoveMod(m);
                    head.Controls.Add(rm);
                }
            }
            Label count = Theme.Label(m != null && m.Name == GameCopy.ServerMod ? "settings: on the Settings page" : settings.Count == 0 ? "no settings" : settings.Count + " setting(s)", Theme.Small, Theme.Muted);
            count.Margin = new Padding(0, 9, 0, 0);
            head.Controls.Add(count);
            row.Body = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Visible = false, Padding = new Padding(26, 6, 0, 4), Margin = new Padding(0) };
            row.Box.Controls.Add(head); row.Box.Controls.Add(row.Body);
            EventHandler toggle = (s, e) => { if (settings.Count == 0) return; row.Open = !row.Open; if (row.Open && row.Fields.Count == 0) FillModSettings(row, settings); row.Body.Visible = row.Open; row.Arrow.Text = row.Open ? "▾" : "▸"; _modsList.PerformLayout(); };
            head.Click += toggle; row.Arrow.Click += toggle; row.Title.Click += toggle; ver.Click += toggle; count.Click += toggle;
            long a0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Theme.Apply(row.Box);
            _rowApply += System.Diagnostics.Stopwatch.GetTimestamp() - a0;
            row.Box.BackColor = Theme.Panel;
            return row;
        }

        /// <summary>A field for each setting: a tick box, a number, a choice or a line of text, with what it does under it.</summary>
        private void FillModSettings(ModRow row, List<ModSetting> settings)
        {
            row.Body.SuspendLayout();
            string lastCat = null;
            foreach (ModSetting s in settings)
            {
                if (row.Meta == null && s.Category != lastCat) { row.Body.Controls.Add(Theme.Label("[" + s.Category + "]", Theme.Sub, Theme.Line)); lastCat = s.Category; }
                Control input;
                if (s.Type == "bool") input = new CheckBox { Checked = s.Value == "true", AutoSize = true, Text = s.Name, Font = Theme.Body, ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 0) };
                else if (s.Type == "choice" && s.Choices.Length > 0) input = Theme.ChoiceFitted(s.Choices, s.Value);
                else input = Theme.Box(s.Value, s.Type == "text" || s.Type == "other" ? 420 : 140);
                FlowLayoutPanel f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 6) };
                if (s.Type != "bool") f.Controls.Add(Theme.Label(s.Name + (s.Type == "int" ? "  (whole number)" : s.Type == "number" ? "  (number)" : ""), Theme.Body, Theme.Line));
                f.Controls.Add(input);
                if (s.Desc.Length > 0 || s.Default.Length > 0) f.Controls.Add(Theme.Wrap(s.Desc + (s.Default.Length > 0 ? (s.Desc.Length > 0 ? " " : "") + "Default: " + s.Default + "." : ""), 760));
                row.Body.Controls.Add(f);
                row.Fields.Add(new KeyValuePair<ModSetting, Control>(s, input));
            }
            row.SaveNote = Theme.Wrap("", 700);
            Button save = Theme.Button("Save these settings", (x, e) => SaveModSettings(row), 170);
            row.Body.Controls.Add(Theme.Row(save, row.SaveNote));
            Theme.Apply(row.Body);
            row.Body.ResumeLayout(true);
            _modsList.Fit();
        }

        private static string FieldValue(ModSetting s, Control c)
        {
            if (c is CheckBox cb) return cb.Checked ? "true" : "false";
            if (c is ComboBox co) return co.SelectedItem as string ?? "";
            return c.Text.Trim();
        }

        /// <summary>The changed settings: to the running server one by one, or into the cfg file.</summary>
        private string SaveModSettings(ModRow row)
        {
            List<KeyValuePair<ModSetting, string>> changed = row.Fields.Select(f => new KeyValuePair<ModSetting, string>(f.Key, FieldValue(f.Key, f.Value)))
                .Where(kv => kv.Value != kv.Key.Value).ToList();
            string bad = string.Join(", ", changed.Where(kv => kv.Key.Literal(kv.Value) == null).Select(kv => kv.Key.Name));
            string note;
            if (bad.Length > 0) note = "Not saved: " + bad + " needs a number.";
            else if (changed.Count == 0) note = "Nothing changed.";
            else
            {
                if (_current.Running)
                    foreach (var kv in changed) Send("modset " + kv.Key.Category + " " + kv.Key.Id + " " + (kv.Key.Type == "text" ? Conf.Quote(kv.Value) : kv.Value));
                else ModSetting.WriteCfg(_current.PrefsPath, changed);
                foreach (var kv in changed) kv.Key.Value = kv.Value;
                note = changed.Count + " setting(s) saved" + (_current.Running ? ", sent to the server: mods that read them as they go use them now, others after a restart." : " to MelonPreferences.cfg: the server uses them when it starts.");
            }
            if (row.SaveNote != null) { row.SaveNote.Text = note; row.SaveNote.ForeColor = note.StartsWith("Not") ? Theme.Bad : changed.Count > 0 ? Theme.Good : Theme.Muted; }
            return note;
        }

        private void ModsChanged()
        {
            int changed = _modRows.Count(r => r.Use != null && (string)r.Use.SelectedItem != r.UseWas);
            _modsSave.Enabled = changed > 0;
            if (changed > 0) { _modsNote.Text = changed + " change(s) not saved yet."; _modsNote.ForeColor = Theme.Warn; }
        }

        private void SaveMods()
        {
            if (_current.Running) { MessageBox.Show(this, "Stop the server first: it has its mods loaded."); return; }
            List<string> files = new List<string>(), opt = new List<string>();
            foreach (ModRow r in _modRows)
            {
                if (r.Meta == null) continue;
                string use = (string)r.Use.SelectedItem;
                if (!ModUse.OnServer(use) && r.Meta.Name != GameCopy.ServerMod) continue;
                if (!File.Exists(r.Meta.File)) continue;
                files.Add(r.Meta.File);
                if (use == ModUse.Optional) opt.Add(r.Meta.Name);
            }
            try
            {
                GameCopy.SetMods(_current.Folder, files, x => { });
                Conf c = _current.LoadConf(); c.Set("OptionalMods", string.Join(", ", opt)); c.Save(_current.ConfPath);
                ServerInfo info = _current.LoadInfo(); info.Mods = files.Select(Path.GetFileName).ToList(); _current.SaveInfo(info);
                _modsLoadedFor = null; _modsInStamp = 0; RefreshMods();
                _settingsLoadedFor = null;
                _modsNote.Text = "Saved: " + files.Count + " mod(s) on the server, " + opt.Count + " of them optional for players.";
                _modsNote.ForeColor = Theme.Good;
            }
            catch (Exception e) { _modsNote.Text = "Could not change the mods: " + e.Message; _modsNote.ForeColor = Theme.Bad; }
        }

#if SDSMTEST
        /// <summary>For the self test: pick where a mod runs, as a person would in its row.</summary>
        public bool SetModUseForTest(string mod, string use)
        {
            ModRow r = _modRows.FirstOrDefault(x => x.Meta != null && x.Meta.Name == mod);
            if (r == null) return false;
            r.Use.SelectedItem = use;
            return true;
        }

        public void SaveModsForTest() { SaveMods(); }

        /// <summary>For the self test: open a mod, change one of its settings as a person would, and save. Returns the note.</summary>
        public string SetModSettingForTest(string mod, string id, string value)
        {
            ModRow r = _modRows.FirstOrDefault(x => x.Meta != null && x.Meta.Name == mod);
            if (r == null) return "no row for " + mod;
            List<ModSetting> all = ModSetting.Load(_current, _modsAll).Where(x => x.Mod == mod).ToList();
            if (r.Fields.Count == 0) FillModSettings(r, all);
            r.Open = true; r.Body.Visible = true;
            var f = r.Fields.FirstOrDefault(x => x.Key.Id == id);
            if (f.Value == null) return "no setting " + id + " (has: " + string.Join(", ", r.Fields.Select(x => x.Key.Id)) + ")";
            if (f.Value is CheckBox cb) cb.Checked = value == "true"; else if (f.Value is ComboBox co) co.SelectedItem = value; else f.Value.Text = value;
            return SaveModSettings(r);
        }

        public int ModRowsForTest { get { return _modRows.Count; } }

        /// <summary>For the self test: a mod with a whole-number setting: mod, category, id, value.</summary>
        public string[] IntSettingForTest()
        {
            ModSetting s = ModSetting.Load(_current, _modsAll).FirstOrDefault(x => x.Type == "int" && x.Mod.Length > 0 && _modRows.Any(r => r.Meta != null && r.Meta.Name == x.Mod));
            return s == null ? null : new[] { s.Mod, s.Category, s.Id, s.Value };
        }
#endif
    }
}
