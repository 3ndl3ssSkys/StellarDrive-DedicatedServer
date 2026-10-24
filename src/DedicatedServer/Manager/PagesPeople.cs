// Users and Banned pages. While the server runs, every change is a console command to it (so it
// applies at once and the server writes its own file); while it's stopped, server-data.json is
// edited here. Player ids are Steam ids: bigger than a double holds exactly, so they're read and
// written as 64-bit integers, never through a double.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    public partial class MainForm
    {
        private DataGridView _users, _bans;
        private ComboBox _permMod, _permLevel;

        private static ulong Id(JsonNode n, string key = "Id")
        {
            try { return n[key].GetValue<ulong>(); }
            catch { ulong v; return ulong.TryParse(Json.Str(n, key), out v) ? v : 0; }
        }

        private void BuildUsers(TabPage page)
        {
            _users = Theme.NewGrid("Name", "Id", "Online", "Admin", "Visits", "Play time", "Last seen", "Last address", "Permissions", "Tied to a game");
            _permMod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            _permLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            _permMod.SelectedIndexChanged += (s, e) => FillLevels();
            Theme.Apply(_permMod); Theme.Apply(_permLevel);
            // grouped: each group wraps as a whole, and what a button does is in its tooltip
            ToolTip tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
            Func<string, string, EventHandler, int, Button> btn = (text, help, click, width) => { Button x = Theme.Button(text, click, width); tip.SetToolTip(x, help); return x; };
            Func<string, Control[], FlowLayoutPanel> group = (title, parts) =>
            {
                FlowLayoutPanel g = Theme.Row(new Control[] { Theme.Label(title, Theme.Body, Theme.Muted) }.Concat(parts).ToArray());
                g.Margin = new Padding(0, 0, 22, 2);
                return g;
            };
            FlowLayoutPanel b1 = Theme.Row(
                group("Access", new Control[] {
                    btn("Make admin", "Every command in chat, developer tools, every mod at its highest level.", (s, e) => ForUser(id => Send("op " + id), (p, id) => p["Op"] = true), 116),
                    btn("Remove admin", "No longer an admin.", (s, e) => ForUser(id => Send("deop " + id), (p, id) => p["Op"] = false), 126),
                    btn("Kick", "Disconnect them now (they can join again).", (s, e) => ForUser(id => Send("kick " + id + " " + (Prompt.Ask(this, "Kick", "Reason (optional):", "") ?? "")), null), 70),
                    btn("Ban...", "Keep them out, with a reason they are told.", (s, e) => BanUser(), 80),
                    btn("Unlink", "Let them join from a new PC or game copy: their character is tied to the game that first joined.", (s, e) => ForUser(id => Send("unlink " + id), (p, id) => p["Token"] = ""), 80) }),
                group("Whitelist", new Control[] {
                    btn("Add", "On the whitelist (only listed players and admins join while Whitelist is on).", (s, e) => ForUser(id => Send("whitelist add " + id), (p, id) => WhitelistFile(id, true)), 64),
                    btn("Remove", "Off the whitelist.", (s, e) => ForUser(id => Send("whitelist remove " + id), (p, id) => WhitelistFile(id, false)), 84) }));
            FlowLayoutPanel bp = Theme.Row(
                group("Character", new Control[] {
                    btn("Where?", "Where their character is, on now or not.", (s, e) => PlayerSay("info"), 84),
                    btn("Back to start", "To their own starting spot (or the world's). If they're not on, they're there when they next join.", (s, e) => PlayerSay("reset"), 126),
                    btn("Move to ship...", "On top of a ship you pick. If they're not on, they're there when they next join.", (s, e) => PlayerToShip(), 136),
                    btn("Delete profile...", "Their character and what the server knows about them; bans, whitelist and admin stay. The world is saved first as a backup.", (s, e) => DeleteProfile(), 146) }));
            FlowLayoutPanel b2 = group("Permission", new Control[] { _permMod, _permLevel, btn("Set", "This player's level for that mod (admins always have the highest).", (s, e) => SetPerm(), 64) });
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 8, 18, 0) };
            top.Controls.Add(b1); top.Controls.Add(bp); top.Controls.Add(b2);
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 12) };
            body.Controls.Add(_users);
            page.Controls.Add(body); page.Controls.Add(top);
        }

        // ---- a player's character: where, back to start, onto a ship, delete ------------------------

        private bool PickedUser(out string id, out string name)
        {
            id = name = null;
            if (_users.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a player first."); return false; }
            id = (string)_users.SelectedRows[0].Cells["Id"].Value;
            name = Convert.ToString(_users.SelectedRows[0].Cells["Name"].Value);
            if (!_current.Running) { MessageBox.Show(this, "Start the server first: players' characters are in the running world.", "Players"); return false; }
            return true;
        }

        private void PlayerSay(string verb)
        {
            string id, name;
            if (!PickedUser(out id, out name)) return;
            string reply = AskServer("player " + verb + " " + id, 6);
            MessageBox.Show(this, string.IsNullOrEmpty(reply) ? "The server didn't answer." : reply, name);
        }

        private void PlayerToShip()
        {
            string id, name;
            if (!PickedUser(out id, out name)) return;
            List<string> ships = Json.Arr(_current.Ships, "ships").OrderByDescending(s => Json.Int(s, "parts"))
                .Select(s => "#" + Json.Int(s, "id") + "  " + (Json.Str(s, "name").Length > 0 ? Json.Str(s, "name") : "(no beacon)") + ", " + Json.Int(s, "parts") + " parts").ToList();
            if (ships.Count == 0) { MessageBox.Show(this, "No ships listed yet (the list is written every 15 s while the server runs)."); return; }
            List<string> pick = PickPlayers("Move " + name + " to a ship", "Which ship? " + name + " is put on top of it (now if they're on, else when they next join).", ships, true);
            if (pick == null || pick.Count == 0) return;
            string shipId = pick[0].Substring(1).Split(' ')[0];
            string reply = AskServer("player ship " + id + " " + shipId, 6);
            MessageBox.Show(this, string.IsNullOrEmpty(reply) ? "The server didn't answer." : reply, name);
        }

        private void DeleteProfile()
        {
            string id, name;
            if (!PickedUser(out id, out name)) return;
            if (MessageBox.Show(this, "Delete " + name + "'s profile?\n\n"
                + "Their character goes (inventory, where they are, their bed) and so does what the server knows about them: play time, permissions, friends, "
                + "their own spawn spot, and the game they're tied to. If they're on, they're disconnected first. Next time they join they start fresh.\n\n"
                + "Bans, the whitelist and admin status stay. The world is saved first, keeping the save before it as a backup.",
                "Delete a profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string reply = AskServer("player delete " + id + " confirm", 10);
            MessageBox.Show(this, string.IsNullOrEmpty(reply) ? "The server didn't answer." : reply, "Delete a profile");
            _current.Forget(_current.DataPath);
            RefreshUsers();
        }

        private Dictionary<string, string[]> PermissionLevels()
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>();
            JsonObject o = null;
            try { o = _current.Status?["permissionLevels"] as JsonObject; } catch { }
            if (o != null) foreach (var kv in o) d[kv.Key] = (kv.Value as JsonArray)?.Select(x => x.ToString()).ToArray() ?? new string[0];
            if (d.Count == 0 && ModsIn(_current).Any(m => m.Name == "Demo Ship Tools")) d["Demo Ship Tools"] = new[] { "Normal", "Elevated", "Max" };
            return d;
        }

        private void FillLevels()
        {
            _permLevel.Items.Clear();
            string[] levels;
            if (_permMod.SelectedItem != null && PermissionLevels().TryGetValue((string)_permMod.SelectedItem, out levels)) _permLevel.Items.AddRange(levels);
            if (_permLevel.Items.Count > 0) _permLevel.SelectedIndex = 0;
        }

        private void RefreshUsers()
        {
            JsonNode data = _current.Data;
            JsonNode st = _current.Status;
            HashSet<string> online = new HashSet<string>(_current.Running ? Json.Arr(st, "players").Select(p => Json.Str(p, "id")) : new string[0]);
            string sig = _current.Folder + "|" + string.Join(",", online) + "|" + (data != null ? data["Players"]?.ToJsonString() : "") + "|" + string.Join(",", PermissionLevels().Keys);
            if (sig == _usersSig) return;
            _usersSig = sig;
            string keep = _users.SelectedRows.Count > 0 ? (string)_users.SelectedRows[0].Cells["Id"].Value : null;
            int scroll = _users.FirstDisplayedScrollingRowIndex;
            GridRebuilds++;
            _users.Rows.Clear();
            foreach (JsonNode p in Json.Arr(data, "Players").OrderByDescending(p => online.Contains(Id(p).ToString())).ThenByDescending(p => Json.Date(p, "LastSeen")))
            {
                string id = Id(p).ToString();
                string levels = "";
                try { JsonObject lv = p["Levels"] as JsonObject; if (lv != null) levels = string.Join(", ", lv.Select(kv => kv.Key + " " + kv.Value)); } catch { }
                int i = _users.Rows.Add(Json.Str(p, "Name"), id, online.Contains(id) ? "on now" : "", Json.Bool(p, "Op") ? "admin" : "", Json.Int(p, "Visits"),
                    Fmt.Span(Json.Num(p, "Seconds")), Json.Date(p, "LastSeen").ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Json.Str(p, "LastIp"), levels,
                    Json.Str(p, "Token").Length > 0 ? "yes" : "");
                if (online.Contains(id)) _users.Rows[i].DefaultCellStyle.ForeColor = Theme.Good;
                if (id == keep) _users.Rows[i].Selected = true;
            }
            if (scroll >= 0 && scroll < _users.Rows.Count) _users.FirstDisplayedScrollingRowIndex = scroll;
            string mod = (string)_permMod.SelectedItem;
            string[] mods = PermissionLevels().Keys.ToArray();
            if (!mods.SequenceEqual(_permMod.Items.Cast<string>()))
            {
                _permMod.Items.Clear(); _permMod.Items.AddRange(mods);
                if (_permMod.Items.Count > 0) _permMod.SelectedIndex = Math.Max(0, Array.IndexOf(mods, mod));
            }
        }

        /// <summary>Do something to the picked player: by command when running, else in server-data.json.</summary>
        private void ForUser(Func<string, bool> running, Action<JsonNode, ulong> stopped)
        {
            if (_users.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a player first."); return; }
            string id = (string)_users.SelectedRows[0].Cells["Id"].Value;
            if (_current.Running) { running(id); return; }
            if (stopped == null) { MessageBox.Show(this, "That needs the server running."); return; }
            EditData(d =>
            {
                JsonNode p = Json.Arr(d, "Players").FirstOrDefault(x => Id(x).ToString() == id);
                if (p != null) stopped(p, Id(p));
            });
        }

        private void EditData(Action<JsonNode> change)
        {
            JsonNode d = _current.Data ?? new JsonObject();
            change(d);
            File.WriteAllText(_current.DataPath, d.ToJsonString(Json.Indented));
            RefreshUsers();
            RefreshBans();
        }

        private void WhitelistFile(ulong id, bool add)
        {
            JsonNode d = _current.Data;
            JsonArray w = d?["Whitelist"] as JsonArray;
            if (w == null) return;
            JsonNode found = w.FirstOrDefault(x => x != null && x.ToString() == id.ToString());
            if (add && found == null) w.Add(JsonValue.Create(id));
            if (!add && found != null) w.Remove(found);
            File.WriteAllText(_current.DataPath, d.ToJsonString(Json.Indented));
        }

        private void BanUser()
        {
            if (_users.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a player first."); return; }
            string name = (string)_users.SelectedRows[0].Cells["Name"].Value;
            string reason = Prompt.Ask(this, "Ban " + name, "Reason (they're told it):", "");
            if (reason == null) return;
            ForUser(id => Send("ban " + id + " " + reason), (p, id) =>
            {
                JsonNode d = _current.Data;
                JsonArray bans = d["Bans"] as JsonArray;
                bans.Add(new JsonObject { ["Id"] = JsonValue.Create(id), ["Name"] = name, ["Ip"] = "", ["Reason"] = reason, ["At"] = DateTime.UtcNow.ToString("o") });
                File.WriteAllText(_current.DataPath, d.ToJsonString(Json.Indented));
            });
        }

        private void SetPerm()
        {
            if (_permMod.SelectedItem == null || _permLevel.SelectedItem == null) return;
            string mod = (string)_permMod.SelectedItem, level = (string)_permLevel.SelectedItem;
            int index = _permLevel.SelectedIndex;
            ForUser(id => Send("perm " + id + " \"" + mod + "\" " + level), (p, id) =>
            {
                JsonObject lv = p["Levels"] as JsonObject;
                if (lv == null) { lv = new JsonObject(); p["Levels"] = lv; }
                lv[mod] = index;
            });
        }

        // ---- Banned -----------------------------------------------------------------------------------

        private void BuildBans(TabPage page)
        {
            _bans = Theme.NewGrid("Kind", "Who", "Id or address", "Reason", "Since");
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 10, 18, 0) };
            top.Controls.Add(Theme.Row(
                Theme.Button("Unban", (s, e) => Unban(), 100),
                Theme.Button("Ban an address...", (s, e) => BanAddress(), 160),
                Theme.Wrap("  To ban a player (by their game, whatever address they use), pick them on the Users page.", 520)));
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 12) };
            body.Controls.Add(_bans);
            page.Controls.Add(body); page.Controls.Add(top);
        }

        private void RefreshBans()
        {
            string sig = _current.Folder + "|" + (_current.Data != null ? _current.Data["Bans"]?.ToJsonString() + _current.Data["Whitelist"]?.ToJsonString() : "");
            if (sig == _bansSig) return;
            _bansSig = sig;
            int scroll = _bans.FirstDisplayedScrollingRowIndex;
            GridRebuilds++;
            _bans.Rows.Clear();
            foreach (JsonNode b in Json.Arr(_current.Data, "Bans"))
            {
                bool ip = Json.Str(b, "Ip").Length > 0;
                _bans.Rows.Add(ip ? "Address" : "Player", ip ? "" : Json.Str(b, "Name"), ip ? Json.Str(b, "Ip") : Id(b).ToString(), Json.Str(b, "Reason"),
                    Json.Date(b, "At").ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }
            if (scroll >= 0 && scroll < _bans.Rows.Count) _bans.FirstDisplayedScrollingRowIndex = scroll;
        }

        private void Unban()
        {
            if (_bans.SelectedRows.Count == 0) return;
            string kind = (string)_bans.SelectedRows[0].Cells["Kind"].Value, key = (string)_bans.SelectedRows[0].Cells["Idoraddress"].Value;
            if (_current.Running) { Send(kind == "Address" ? "unbanip " + key : "unban " + key); return; }
            EditData(d =>
            {
                JsonArray bans = d["Bans"] as JsonArray;
                JsonNode hit = bans.FirstOrDefault(b => kind == "Address" ? Json.Str(b, "Ip") == key : Id(b).ToString() == key && Json.Str(b, "Ip").Length == 0);
                if (hit != null) bans.Remove(hit);
            });
        }

        private void BanAddress()
        {
            string ip = Prompt.Ask(this, "Ban an address", "Internet address (IPv4) to ban:", "");
            if (string.IsNullOrWhiteSpace(ip)) return;
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(ip, out parsed)) { MessageBox.Show(this, "That isn't an address."); return; }
            string reason = Prompt.Ask(this, "Ban " + ip, "Reason (they're told it):", "") ?? "";
            if (_current.Running) { Send("banip " + ip + " " + reason); return; }
            EditData(d => (d["Bans"] as JsonArray).Add(new JsonObject { ["Id"] = 0, ["Name"] = "", ["Ip"] = ip, ["Reason"] = reason, ["At"] = DateTime.UtcNow.ToString("o") }));
        }
    }
}
