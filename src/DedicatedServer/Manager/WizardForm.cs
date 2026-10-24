// New-server setup: Game -> Server -> Mods -> Rules -> Admins -> Saving & World -> make it.
// Everything chosen here goes into the new server's dedicatedserver.conf (the file the mod reads)
// and dedicatedserver.manager.json; all of it can be changed later on the Settings page.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SDServerManager
{
    public class WizardForm : Form
    {
        private readonly ManagerSettings _settings;
        private readonly List<Control> _pages = new List<Control>();
        private int _page;
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill };
        private readonly Label _step = new Label();
        private Button _back, _next;
        public ServerEntry CreatedServer;

        // page 1
        private TextBox _game;
        private Label _gameState;
        // page 2
        private TextBox _name, _root, _display, _motd;
        private NumericUpDown _port, _max;
        // page 3
        private DataGridView _mods;
        private List<ModMeta> _modList = new List<ModMeta>();
        // page 4
        private ComboBox _devTools, _research;
        private TextBox _password;
        private CheckBox _whitelist, _protect, _matchVersions, _requireMods;
        // page 5
        private TextBox _admins;
        // page 6
        private NumericUpDown _autosave, _keep, _deleteDays;
        private CheckBox _saveOnLeave, _pause;
        private RadioButton _newWorld, _fromMine;
        private TextBox _worldName;
        private ComboBox _mine;
        // making it
        private Label _progress;

        private static readonly string[] StepNames = { "Game", "Server", "Mods", "Rules", "Admins", "Saving & world", "Create" };

        /// <summary>The self test never takes the focus from whoever is using the PC.</summary>
        protected override bool ShowWithoutActivation { get { return Program.Quiet; } }

        public WizardForm(ManagerSettings settings)
        {
            _settings = settings;
            Text = "New dedicated server";
            Size = new Size(900, 720);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterParent;
            Icon = Program.AppIcon;

            Panel top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Panel, AutoSize = true, MinimumSize = new Size(0, 64) };
            _step.Font = Theme.Head; _step.ForeColor = Theme.Line; _step.AutoSize = true; _step.Location = new Point(18, 16);
            top.Controls.Add(_step);
            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10), BackColor = Theme.Panel };
            _back = Theme.Button("Back", (s, e) => Go(_page - 1));
            _next = Theme.Button("Next", (s, e) => Next());
            Button cancel = Theme.Button("Cancel", (s, e) => Close(), 100);
            bottom.Controls.Add(_next); bottom.Controls.Add(_back); bottom.Controls.Add(cancel);
            Controls.Add(_body); Controls.Add(bottom); Controls.Add(top);

            _pages.Add(GamePage());
            _pages.Add(ServerPage());
            _pages.Add(ModsPage());
            _pages.Add(RulesPage());
            _pages.Add(AdminsPage());
            _pages.Add(SavingPage());
            _pages.Add(CreatePage());
            Theme.Apply(this);
            Go(0);
        }

        private void Go(int i)
        {
            if (i < 0 || i >= _pages.Count) return;
            _page = i;
            _body.Controls.Clear();
            _body.Controls.Add(_pages[i]);
            _step.Text = (i + 1) + " / " + _pages.Count + "   " + StepNames[i];
            _back.Enabled = i > 0 && i < _pages.Count - 1;
            _next.Text = i == _pages.Count - 2 ? "Create" : i == _pages.Count - 1 ? "Open" : "Next";
            if (i == 2) FillMods();
        }

        private void Next()
        {
            string err = Check(_page);
            if (err != null) { MessageBox.Show(this, err, "Not yet", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_page == _pages.Count - 2) { Go(_page + 1); CreateServer(); return; }
            if (_page == _pages.Count - 1) { DialogResult = DialogResult.OK; Close(); return; }
            Go(_page + 1);
        }

        private string Check(int page)
        {
            switch (page)
            {
                case 0:
                    if (!GameCopy.IsGameFolder(_game.Text)) return "That folder doesn't have StellarDrive.exe in it.";
                    if (!GameCopy.HasMelonLoader(_game.Text)) return "MelonLoader isn't installed in that game folder (the server runs mods through it).";
                    return null;
                case 1:
                    if (_name.Text.Trim().Length == 0 || _name.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Give the server a name that can be a folder name.";
                    if (_root.Text.Trim().Length == 0) return "Choose where the server's folder goes.";
                    string target = Path.Combine(_root.Text.Trim(), _name.Text.Trim());
                    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return "There is already a folder " + target + ". Pick another name.";
                    if (_settings.Servers.Any(s => s.Name.Equals(_name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) return "You already have a server called that.";
                    return null;
                case 5:
                    if (_newWorld.Checked && (_worldName.Text.Trim().Length == 0 || _worldName.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return "Give the world a name that can be a folder name.";
                    if (_fromMine.Checked && _mine.SelectedItem == null) return "Pick one of your worlds, or start a new one.";
                    return null;
            }
            return null;
        }

        // ---- pages ---------------------------------------------------------------------------------

        private Control GamePage()
        {
            FlowLayoutPanel c = Theme.Column();
            c.Controls.Add(Theme.Wrap("The server runs a copy of your StellarDrive game with MelonLoader and the mods you pick next. Point this at the game's folder (the one with StellarDrive.exe).", 800, Theme.Body, Theme.Text));
            string found = !string.IsNullOrEmpty(_settings.LastGameFolder) && GameCopy.IsGameFolder(_settings.LastGameFolder) ? _settings.LastGameFolder : GameCopy.Find();
            _game = Theme.Box(found, 560);
            _gameState = Theme.Wrap("", 800);
            _game.TextChanged += (s, e) => GameState();
            Button browse = Theme.Button("Browse...", (s, e) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "The StellarDrive game folder", SelectedPath = _game.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) _game.Text = d.SelectedPath;
            }, 110);
            c.Controls.Add(Theme.Field("Game folder", Theme.Row(_game, browse), null));
            c.Controls.Add(_gameState);
            GameState();
            return c;
        }

        private void GameState()
        {
            string g = _game.Text;
            if (!GameCopy.IsGameFolder(g)) { _gameState.Text = "No StellarDrive.exe here."; _gameState.ForeColor = Theme.Bad; return; }
            if (!GameCopy.HasMelonLoader(g)) { _gameState.Text = "StellarDrive found, but not MelonLoader."; _gameState.ForeColor = Theme.Bad; return; }
            int mods = GameCopy.ModFiles(g).Count;
            _gameState.Text = "StellarDrive with MelonLoader and " + mods + " mod file(s). The server's copy links to these game files, so it takes almost no extra disk (same drive), and nothing in this folder is changed.";
            _gameState.ForeColor = Theme.Good;
        }

        private Control ServerPage()
        {
            FlowLayoutPanel c = Theme.Column();
            _name = Theme.Box("My Server", 300);
            string root = !string.IsNullOrEmpty(_settings.LastRoot) ? _settings.LastRoot : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StellarDrive Servers");
            _root = Theme.Box(root, 480);
            Button browse = Theme.Button("Browse...", (s, e) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "Where server folders go", SelectedPath = _root.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) _root.Text = d.SelectedPath;
            }, 110);
            _display = Theme.Box("StellarDrive Dedicated Server", 480);
            _name.TextChanged += (s, e) => { if (_display.Tag == null) _display.Text = _name.Text; };
            _display.KeyPress += (s, e) => _display.Tag = "typed";
            _port = Theme.Number(7777 + _settings.Servers.Count, 1024, 65535);
            _max = Theme.Number(8, 1, 250);
            _motd = Theme.Box("", 560);
            c.Controls.Add(Theme.Field("Name", _name, "Your name for it, and its folder's."));
            c.Controls.Add(Theme.Field("Where", Theme.Row(_root, browse), "The server gets a folder of its own here: its game copy, mods, worlds, settings and logs."));
            c.Controls.Add(Theme.Field("Name players see", _display, "Shown to players when they join."));
            c.Controls.Add(Theme.Field("Port", _port, "The UDP port players connect to. Forward it on your router, or point a playit.gg (UDP) tunnel at it. Each server needs its own."));
            c.Controls.Add(Theme.Field("Players at once", _max, "Admins can still join when it's full."));
            c.Controls.Add(Theme.Field("Message of the day", _motd, "Sent to each player as they join. Optional."));
            return c;
        }

        private Control ModsPage()
        {
            Panel p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
            Label help = Theme.Wrap(ModUse.Help, 820, Theme.Body, Theme.Text);
            help.Dock = DockStyle.Top;
            _mods = new DataGridView { Dock = DockStyle.Fill };
            _mods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mod", HeaderText = "Mod", ReadOnly = true, FillWeight = 50 });
            _mods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Version", ReadOnly = true, FillWeight = 18 });
            _mods.Columns.Add(new DataGridViewComboBoxColumn { Name = "Use", HeaderText = "Where it runs", FillWeight = 70, DataSource = ModUse.Choices, FlatStyle = FlatStyle.Flat });
            _mods.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "File", ReadOnly = true, FillWeight = 40 });
            Theme.Grid(_mods);
            _mods.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _mods.EditMode = DataGridViewEditMode.EditOnEnter;
            _mods.CellValueChanged += (s, e) => LockDedicated();
            _mods.CurrentCellDirtyStateChanged += (s, e) => { if (_mods.IsCurrentCellDirty) _mods.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            p.Controls.Add(_mods);
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 });
            p.Controls.Add(help);
            return p;
        }

        private string _modsFor;

        private void FillMods()
        {
            if (_modsFor == _game.Text) return;
            _modsFor = _game.Text;
            _mods.Rows.Clear();
            _modList = GameCopy.ModFiles(_game.Text).Select(ModMeta.Read).ToList();
            foreach (ModMeta m in _modList)
            {
                _mods.Rows.Add(m.Name, m.Version, ModUse.Default(m.Name), Path.GetFileName(m.File));
            }
            LockDedicated();
        }

        private void LockDedicated()
        {
            foreach (DataGridViewRow r in _mods.Rows)
                if ((string)r.Cells["Mod"].Value == "DedicatedServer")
                {
                    if ((string)r.Cells["Use"].Value != ModUse.Required) r.Cells["Use"].Value = ModUse.Required;
                    r.Cells["Use"].ReadOnly = true;
                }
        }

        private Control RulesPage()
        {
            FlowLayoutPanel c = Theme.Column();
            _devTools = Theme.Choice(new[] { "admins", "everyone", "nobody" }, "admins");
            _research = Theme.Choice(new[] { "shared", "individual" }, "shared");
            _password = Theme.Box("", 300);
            _password.UseSystemPasswordChar = true;
            _whitelist = Theme.Check("Whitelist: only players you list (and admins) may join", false);
            _protect = Theme.Check("Protect names: a name belongs to the first player who joins with it", true);
            _requireMods = Theme.Check("Turn away players missing required mods", true);
            _matchVersions = Theme.Check("Required mods must be the same version as the server's", true);
            c.Controls.Add(Theme.Field("Developer tools", _devTools, "Cheat flying, free build and Ship Tools. admins = only admins; everyone; nobody (not even admins)."));
            c.Controls.Add(Theme.Field("Research", _research, "shared = one research tree for the whole server (the game's own way). individual = each player researches, and pays, for themselves."));
            c.Controls.Add(Theme.Field("Password", _password, "Players type it on the Join page. Leave empty for none. Admins don't need it."));
            c.Controls.Add(Theme.Label("Joining", Theme.Sub, Theme.Line));
            c.Controls.Add(_protect);
            c.Controls.Add(Theme.Wrap("Either way a player's character is tied to their own game (a private key it keeps), so nobody else can join as them and use their save, whatever name they type.", 780));
            c.Controls.Add(_whitelist);
            c.Controls.Add(_requireMods);
            c.Controls.Add(_matchVersions);
            return c;
        }

        private Control AdminsPage()
        {
            FlowLayoutPanel c = Theme.Column();
            _admins = new TextBox { Multiline = true, Width = 420, Height = 140, ScrollBars = ScrollBars.Vertical };
            Theme.Apply(_admins);
            c.Controls.Add(Theme.Field("Admins", _admins, "One per line: a player's name or Steam id (17 digits). A listed player becomes admin when they first join. Admins can use every command in chat (/help), are never turned away when the server is full or by the password, and may use developer tools."));
            c.Controls.Add(Theme.Label("Mod permissions", Theme.Sub, Theme.Line));
            c.Controls.Add(Theme.Wrap("Admins get each mod's highest level. For everyone else you can set levels per player on the Users page once they've joined.", 780));
            bool shipTools = GameCopy.ModFiles(_game.Text).Any(f => Path.GetFileName(f).Equals("DemoShipTools.dll", StringComparison.OrdinalIgnoreCase));
            c.Controls.Add(Theme.Wrap((shipTools ? "Demo Ship Tools: Normal (look, copy blueprints) < Elevated (teleport) < Max (spawn blueprints, delete ships). Admins get Max." : "Demo Ship Tools isn't in this game's mods.")
                + "\nOther mods that declare permission levels show up on the Users page after the server's first start.", 780, Theme.Body, Theme.Text));
            return c;
        }

        private Control SavingPage()
        {
            FlowLayoutPanel c = Theme.Column();
            _autosave = Theme.Number(10, 0, 1440);
            _keep = Theme.Number(10, 0, 1000);
            _deleteDays = Theme.Number(0, 0, 3650);
            _saveOnLeave = Theme.Check("Save when a player leaves", true);
            _pause = Theme.Check("With nobody on, the world stands still", true);
            c.Controls.Add(Theme.Field("Autosave every (minutes)", _autosave, "While anyone is on. 0 = never (the server still saves when it stops)."));
            c.Controls.Add(Theme.Field("Backups to keep", _keep, "Each autosave first keeps the previous world as backup_<date>.json; the oldest go first."));
            c.Controls.Add(Theme.Field("Delete backups older than (days)", _deleteDays, "0 = only the count above matters."));
            c.Controls.Add(_saveOnLeave);
            c.Controls.Add(_pause);
            c.Controls.Add(Theme.Label("World", Theme.Sub, Theme.Line));
            _newWorld = new RadioButton { Text = "A new world, called", Checked = true, AutoSize = true, ForeColor = Theme.Text };
            _worldName = Theme.Box("default", 220);
            _fromMine = new RadioButton { Text = "A copy of one of my worlds", AutoSize = true, ForeColor = Theme.Text };
            _mine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
            foreach (string w in GameCopy.PlayerWorlds()) _mine.Items.Add(new WorldChoice(w));
            Theme.Apply(_mine);
            _mine.DropDownWidth = 700;
            _mine.SelectedIndexChanged += (s, e) => _fromMine.Checked = true;
            // the two are in different rows of the page, so Windows doesn't pair them by itself
            _newWorld.CheckedChanged += (s, e) => { if (_newWorld.Checked) _fromMine.Checked = false; };
            _fromMine.CheckedChanged += (s, e) => { if (_fromMine.Checked) _newWorld.Checked = false; };
            _worldName.Enter += (s, e) => _newWorld.Checked = true;
            c.Controls.Add(Theme.Row(_newWorld, _worldName));
            c.Controls.Add(_fromMine);
            c.Controls.Add(_mine);
            c.Controls.Add(Theme.Wrap("A copy: your own save is never changed by the server.", 700));
            return c;
        }

        /// <summary>One of the player's own worlds, shown by name and date instead of its whole path.</summary>
        private class WorldChoice
        {
            public readonly string Path;
            public WorldChoice(string path) { Path = path; }
            public override string ToString()
            {
                string name = new DirectoryInfo(System.IO.Path.GetDirectoryName(Path)).Name;
                DateTime t = File.Exists(Path) ? File.GetLastWriteTime(Path) : DateTime.MinValue;
                return name + "   (saved " + t.ToString("yyyy-MM-dd HH:mm") + ")";
            }
        }

        private Control CreatePage()
        {
            FlowLayoutPanel c = Theme.Column();
            _progress = Theme.Wrap("", 800, Theme.Body, Theme.Text);
            c.Controls.Add(_progress);
            return c;
        }

        // ---- make it -------------------------------------------------------------------------------

        private async void CreateServer()
        {
            _next.Enabled = false;
            string game = _game.Text.Trim(), target = Path.Combine(_root.Text.Trim(), _name.Text.Trim());
            List<string> mods = new List<string>(), optional = new List<string>();
            foreach (DataGridViewRow r in _mods.Rows)
            {
                string use = (string)r.Cells["Use"].Value;
                if (!ModUse.OnServer(use)) continue;
                ModMeta m = _modList.First(x => Path.GetFileName(x.File) == (string)r.Cells["File"].Value);
                mods.Add(m.File);
                if (use == ModUse.Optional) optional.Add(m.Name);
            }
            string copyFrom = _fromMine.Checked && _mine.SelectedItem is WorldChoice wc ? wc.Path : null;
            string worldName = _fromMine.Checked ? new DirectoryInfo(Path.GetDirectoryName(copyFrom)).Name : _worldName.Text.Trim();
            Action<string> say = s => BeginInvoke((Action)(() => _progress.Text = s));
            try
            {
                await Task.Run(() =>
                {
                    GameCopy.Create(game, target, mods, say);
                    Server srv = new Server(new ServerEntry { Name = _name.Text.Trim(), Folder = target });
                    if (copyFrom != null)
                    {
                        string wd = Path.Combine(srv.WorldsDir, worldName);
                        Directory.CreateDirectory(wd);
                        File.Copy(copyFrom, Path.Combine(wd, "world.json"), true);
                    }
                    Conf conf = Conf.Load(srv.ConfPath);
                    conf.Set("ServerName", _display.Text.Trim());
                    conf.Set("World", worldName);
                    conf.SetInt("Port", (int)_port.Value);
                    conf.SetInt("MaxPlayers", (int)_max.Value);
                    conf.Set("Motd", _motd.Text);
                    conf.Set("Password", _password.Text);
                    conf.Set("Admins", string.Join(", ", _admins.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)));
                    conf.SetBool("ProtectNames", _protect.Checked);
                    conf.Set("DevTools", (string)_devTools.SelectedItem);
                    conf.Set("Research", (string)_research.SelectedItem);
                    conf.SetInt("AutoSaveMinutes", (int)_autosave.Value);
                    conf.SetInt("BackupsToKeep", (int)_keep.Value);
                    conf.SetInt("DeleteBackupsAfterDays", (int)_deleteDays.Value);
                    conf.SetBool("SaveWhenPlayerLeaves", _saveOnLeave.Checked);
                    conf.SetBool("PauseWhenEmpty", _pause.Checked);
                    conf.SetBool("RequireMods", _requireMods.Checked);
                    conf.Set("RequiredMods", "auto");
                    conf.Set("OptionalMods", string.Join(", ", optional));
                    conf.SetBool("MatchModVersions", _matchVersions.Checked);
                    conf.SetBool("Whitelist", _whitelist.Checked);
                    conf.Save(srv.ConfPath);
                    srv.SaveInfo(new ServerInfo { SourceGame = game, Mods = mods.Select(Path.GetFileName).ToList(), Created = DateTime.Now, ManagerVersion = Program.Version });
                });
                CreatedServer = new ServerEntry { Name = _name.Text.Trim(), Folder = target };
                _settings.Servers.Add(CreatedServer);
                _settings.LastGameFolder = game;
                _settings.LastRoot = _root.Text.Trim();
                _settings.Save();
                _progress.Text = "Server \"" + CreatedServer.Name + "\" is ready in " + target + ".\n\nOpen it to start it: players join on the Join Game page with your address and port " + _port.Value + ".";
                _progress.ForeColor = Theme.Good;
                _next.Enabled = true;
            }
            catch (Exception ex)
            {
                _progress.Text = "Could not make the server: " + ex.Message;
                _progress.ForeColor = Theme.Bad;
                _back.Enabled = true;
            }
        }

        /// <summary>For the self test: fill the wizard without a person.</summary>
        public void AutoFill(string game, string root, string name, int port, string admins, string devTools, string research)
        {
            _game.Text = game; _root.Text = root; _name.Text = name; _display.Text = name; _port.Value = port;
            _admins.Text = admins; _devTools.SelectedItem = devTools; _research.SelectedItem = research;
            Go(2);
        }

        public Control PageControl(int i) { Go(i); return _pages[i]; }
        public int PageCount { get { return _pages.Count; } }
#if SDSMTEST
        public Task CreateForTest() { Go(_pages.Count - 1); CreateServer(); return Task.CompletedTask; }
#endif
        public string ProgressText { get { return _progress.Text; } }

#if SDSMTEST
        /// <summary>For the self test: pick "a copy of one of my worlds", then "a new world", as a person would.</summary>
        public string RadiosForTest()
        {
            _fromMine.Checked = true;
            string a = "copy picked: new " + _newWorld.Checked + ", copy " + _fromMine.Checked;
            _newWorld.Checked = true;
            return a + "; new picked: new " + _newWorld.Checked + ", copy " + _fromMine.Checked;
        }
#endif
    }
}
