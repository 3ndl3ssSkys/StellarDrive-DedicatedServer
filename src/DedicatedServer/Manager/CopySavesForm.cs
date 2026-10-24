// Saves > Copy players / ships between saves: pick the save to copy from and the one to copy into,
// then players (where they are and/or what they carry) and ships (with everything docked to them).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SDServerManager
{
    public class CopySavesForm : Form
    {
        private readonly Server _server;
        private readonly ComboBox _from, _into;
        private readonly CheckedListBox _players, _ships;
        private readonly CheckBox _where, _carry;
        private readonly Label _note;
        private readonly TextBox _report;
        private readonly List<string> _files = new List<string>();
        private WorldFile _source;
        public bool Done;
        public string Target;

        public CopySavesForm(Server server, string preferredSource)
        {
            _server = server;
            Text = "Copy players and ships between saves";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 720);
            MinimumSize = new Size(700, 560);
            BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;

            PageColumn col = new PageColumn { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12) };
            _from = Theme.Choice(new string[0], "", 520);
            _into = Theme.Choice(new string[0], "", 520);
            foreach (string f in AllSaves())
            {
                _files.Add(f);
                _from.Items.Add(Label(f));
                if (Path.GetFileName(f) == "world.json") _into.Items.Add(Label(f));
            }
            _from.Items.Add("Another file...");
            _from.SelectedIndexChanged += (s, e) => LoadSource();
            col.Controls.Add(Theme.Row(Theme.Label("From  ", Theme.Body, Theme.Muted), _from));
            col.Controls.Add(Theme.Row(Theme.Label("Into   ", Theme.Body, Theme.Muted), _into));

            col.Controls.Add(Theme.Label("PLAYERS", Theme.Sub, Theme.Line));
            _where = Theme.Check("Where they are", true);
            _carry = Theme.Check("What they carry", true);
            col.Controls.Add(Theme.Row(_where, Theme.Label("   "), _carry));
            col.Controls.Add(Theme.Wrap("A player who isn't in the target save yet is copied whole (place, inventory, health). One who was on a ship that isn't copied is put where that ship was.", 820));
            _players = List(150);
            col.Controls.Add(_players);
            col.Controls.Add(Theme.Label("SHIPS AND BUILDS", Theme.Sub, Theme.Line));
            col.Controls.Add(Theme.Wrap("Ships docked to a picked one, riding on it or carrying it come along, with the cables between them. They keep their place.", 820));
            _ships = List(170);
            col.Controls.Add(_ships);
            _note = Theme.Wrap("", 820, Theme.Body, Theme.Warn);
            col.Controls.Add(_note);
            col.Controls.Add(Theme.Row(Theme.Button("Copy into the target", (s, e) => DoCopy(), 190), Theme.Button("Close", (s, e) => Close(), 100)));
            _report = new TextBox { Multiline = true, ReadOnly = true, Width = 820, Height = 110, ScrollBars = ScrollBars.Vertical, Font = Theme.Small };
            col.Controls.Add(_report);
            Controls.Add(col);
            Theme.Apply(this);

            int pre = preferredSource != null ? _files.FindIndex(f => string.Equals(f, preferredSource, StringComparison.OrdinalIgnoreCase)) : -1;
            _from.SelectedIndex = pre >= 0 ? pre : (_files.Count > 0 ? 0 : -1);
            string active = _server.CurrentWorldFile();
            int ai = _into.Items.IndexOf(Label(active));
            _into.SelectedIndex = ai >= 0 ? ai : (_into.Items.Count > 0 ? 0 : -1);
        }

        private static CheckedListBox List(int height)
        {
            CheckedListBox l = new CheckedListBox { Width = 820, Height = height, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.FieldBack, ForeColor = Theme.Text, IntegralHeight = false };
            return l;
        }

        private IEnumerable<string> AllSaves()
        {
            List<string> l = new List<string>();
            if (Directory.Exists(_server.WorldsDir))
                foreach (string d in Directory.GetDirectories(_server.WorldsDir))
                {
                    string w = Path.Combine(d, "world.json");
                    if (File.Exists(w)) l.Add(w);
                    l.AddRange(Directory.GetFiles(d, "*.json").Where(f => Path.GetFileName(f).StartsWith("backup") || Path.GetFileName(f).StartsWith("world-before")).OrderByDescending(File.GetLastWriteTime));
                }
            return l;
        }

        private string Label(string f)
        {
            string world = new DirectoryInfo(Path.GetDirectoryName(f)).Name;
            return Path.GetFileName(f) == "world.json" ? world : world + " / " + Path.GetFileName(f);
        }

        private string Selected(ComboBox c, bool from)
        {
            if (c.SelectedIndex < 0) return null;
            string label = (string)c.SelectedItem;
            return _files.FirstOrDefault(f => Label(f) == label);
        }

        private void LoadSource()
        {
            _players.Items.Clear(); _ships.Items.Clear(); _source = null; _note.Text = "";
            string f;
            if ((string)_from.SelectedItem == "Another file...")
            {
                using (OpenFileDialog d = new OpenFileDialog { Title = "A save to copy from", Filter = "Saves (*.json)|*.json" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    f = d.FileName;
                    _files.Add(f);
                    _from.Items.Insert(_from.Items.Count - 1, Label(f));
                    _from.SelectedIndex = _from.Items.Count - 2;
                    return;
                }
            }
            f = Selected(_from, true);
            if (f == null) return;
            try
            {
                _source = WorldFile.Read(f, Catalog.Load(_server));
                foreach (WorldFile.PlayerRow p in _source.Players.OrderBy(x => x.Name))
                    _players.Items.Add(new Item { Id = (long)p.SteamId, Text = p.Name + "   (" + p.Where + "; " + p.ItemsUsed + " slots used)" });
                foreach (WorldFile.ShipRow s in _source.Ships.OrderByDescending(x => x.Parts))
                    _ships.Items.Add(new Item { Id = s.Id, Text = s.Name + "   (" + s.Parts + " parts, " + s.Where + (s.DockedTo != 0 ? ", docked to #" + s.DockedTo : "") + ")" });
            }
            catch (Exception e) { _note.Text = "Could not read that save: " + e.Message; }
        }

        private class Item { public long Id; public string Text; public override string ToString() { return Text; } }

        private void DoCopy()
        {
            string from = Selected(_from, true), into = Selected(_into, false);
            if (_source == null || from == null || into == null) { _note.Text = "Pick a save to copy from and one to copy into."; return; }
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(into), StringComparison.OrdinalIgnoreCase)) { _note.Text = "Those are the same save."; return; }
            if (_server.Running && string.Equals(Path.GetFullPath(into), Path.GetFullPath(_server.CurrentWorldFile()), StringComparison.OrdinalIgnoreCase))
            { _note.Text = "The server is running this world: stop it first (it would save over the copy)."; return; }
            WorldFile.CopyRequest req = new WorldFile.CopyRequest { Location = _where.Checked, Inventory = _carry.Checked };
            foreach (Item i in _players.CheckedItems) req.Players.Add((ulong)i.Id);
            foreach (Item i in _ships.CheckedItems) req.Ships.Add(i.Id);
            if (req.Players.Count == 0 && req.Ships.Count == 0) { _note.Text = "Tick at least one player or ship."; return; }
            if (MessageBox.Show(this, "Copy " + req.Players.Count + " player(s) and " + req.Ships.Count + " ship(s) into " + Label(into) + "? It is backed up first.", "Copy", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                List<string> said = WorldFile.Copy(from, into, req);
                _report.Text = string.Join(Environment.NewLine, said);
                _note.Text = "";
                Done = true; Target = into;
            }
            catch (Exception e) { _note.Text = "Nothing was copied: " + e.Message; }
        }
    }
}
