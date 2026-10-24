// Launch, Saves and Ships pages.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    public partial class MainForm
    {
        // ---- Launch -----------------------------------------------------------------------------

        private Label _state, _facts, _connect, _reach;
        private TextBox _public;
        private string _publicFor;

        /// <summary>What the server's network guard says: can players reach it through their address, has the port needed opening again.</summary>
        private static string NetLine(JsonNode net)
        {
            if (net == null || !Json.Bool(net, "on")) return "";
            string s = "";
            string pub = Json.Str(net, "public"), addr = Json.Str(net, "publicAddress");
            if (pub == "reachable") s += "\nPlayers can reach it through " + addr + " (checked " + Json.Date(net, "publicOkUtc").ToLocalTime().ToString("HH:mm") + ")";
            else if (pub == "unreachable") s += "\nPLAYERS CAN'T REACH IT through " + addr + " since " + Json.Date(net, "publicDownSinceUtc").ToLocalTime().ToString("ddd HH:mm")
                + ": the server answers on this PC, so the tunnel (is playit running and connected?) or the port forward is down.";
            if (!Json.Bool(net, "listening")) s += "\nThe game port isn't listening: the server is opening it again.";
            if (Json.Int(net, "reopened") > 0) s += "\nGame port opened again " + Json.Int(net, "reopened") + " time(s) (last " + Json.Str(net, "lastReopen") + ")";
            return s;
        }

        /// <summary>Is the server answering, here on this PC and through the address players use? With what to do when not.</summary>
        public async void CheckReach()
        {
            Server s = _current;
            ServerInfo info = s.LoadInfo();
            info.PublicAddress = _public.Text.Trim(); s.SaveInfo(info);
            int port = s.LoadConf().GetInt("Port");
            _reach.Text = "Asking..."; _reach.ForeColor = Theme.Muted;
            string said = await System.Threading.Tasks.Task.Run(() => ReachText(s.Running, port, info.PublicAddress));
            if (_current != s) return;
            _reach.Text = said;
            _reach.ForeColor = said.StartsWith("OK") ? Theme.Good : Theme.Warn;
        }

        public static string ReachText(bool running, int port, string address)
        {
            ServerQuery.Result here = ServerQuery.Ask("127.0.0.1", port, 1500, 2);
            if (!here.Ok)
                return running ? "The server isn't answering on this PC (UDP port " + port + "). If it was just started, wait for READY on the Launch console and check again."
                               : "The server isn't running. Start it, then check.";
            if (address.Length == 0) return "OK on this PC (" + here.Ms + " ms). Type the address players use from the internet (a playit.gg address, or your public IP:" + port + ") to check that too.";
            string host; int p;
            if (!ServerQuery.Parse(address, port, out host, out p)) return "That address can't be read. Like: name.tun.ply.gg:4064, or 81.2.3.4:" + port;
            ServerQuery.Result there = ServerQuery.Ask(host, p, 3000, 3);
            if (there.Ok) return "OK: players can reach it through " + host + ":" + p + " (answered in " + there.Ms + " ms).";
            return "The server answers on this PC, but not through " + host + ":" + p + " (" + there.Error + "), so players can't join that way.\n"
                + "- playit.gg: the tunnel must be UDP (or TCP+UDP), its local address 127.0.0.1 port " + port + ", and the playit program running on this PC.\n"
                + "- Port forwarding: forward UDP " + port + " on the router to this PC, and let StellarDrive.exe in this server's folder through Windows Firewall (a new folder is a new program to the firewall).";
        }
        private Button _start, _stop, _restart, _kill, _callOff;
        private TextBox _console, _commandBox;
        private long _consoleLen = -1;
        private DateTime _stopAskedAt = DateTime.MinValue;
        private bool _restartPending;

        private void BuildLaunch(TabPage page)
        {
            // the top part scrolls on its own, and the divider to the console can be dragged
            PageColumn top = new PageColumn { Padding = new Padding(18, 12, 18, 0) };
            top.Name = "LaunchTop";
            _state = Theme.Label("", Theme.Head, Theme.Muted);
            _facts = Theme.Wrap("", 900, Theme.Body, Theme.Text);
            _connect = Theme.Wrap("", 900);
            _start = Theme.Button("Start", (s, e) => DoStart(), 110);
            _stop = Theme.Button("Stop", (s, e) => { if (Send("stop")) _stopAskedAt = DateTime.Now; }, 110);
            _restart = Theme.Button("Restart...", (s, e) => AskRestart(), 110);
            _callOff = Theme.Button("Call it off", (s, e) => Send("restart cancel"), 110);
            _kill = Theme.Button("Force quit", (s, e) =>
            {
                if (MessageBox.Show(this, "Force the server to quit? Anything since the last save is lost.", "Force quit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) _current.Kill();
            }, 110);
            FlowLayoutPanel buttons = Theme.Row(_start, _stop, _restart, Theme.Button("Planned restarts...", (s, e) => AskPlannedRestarts(), 160), _kill,
                Theme.Button("Open folder", (s, e) => Process.Start("explorer.exe", "\"" + _current.Folder + "\""), 120),
                Theme.Button("Update from game", (s, e) => UpdateFromGame(), 150));
            buttons.Controls.Add(_callOff);
            _public = new TextBox { Width = 300, BackColor = Theme.FieldBack, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
            _public.Leave += (s, e) => { ServerInfo i = _current.LoadInfo(); if (i.PublicAddress != _public.Text.Trim()) { i.PublicAddress = _public.Text.Trim(); _current.SaveInfo(i); } };
            _reach = Theme.Wrap("", 900, Theme.Small, Theme.Muted);
            FlowLayoutPanel reach = Theme.Row(Theme.Label("Address players use: ", Theme.Body, Theme.Text), _public, Theme.Button("Check", (s, e) => CheckReach(), 90));
            top.Controls.AddRange(new Control[] { _state, _facts, _connect, reach, _reach, buttons, BuildComposer() });

            Panel bottom = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 12) };
            FlowLayoutPanel ch = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent };
            ch.Controls.Add(Theme.Label("CONSOLE   ", Theme.Sub, Theme.Line));
            foreach (string k in ConsoleKinds)
            {
                CheckBox cb = Theme.Check(k, !_settings.ConsoleHidden.Contains(k));
                cb.Margin = new Padding(0, 8, 14, 0);
                cb.CheckedChanged += (s, e) =>
                {
                    if (cb.Checked) _settings.ConsoleHidden.Remove(k); else if (!_settings.ConsoleHidden.Contains(k)) _settings.ConsoleHidden.Add(k);
                    _settings.Save();
                    TailConsole(true);
                };
                ch.Controls.Add(cb);
            }
            _console = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono, WordWrap = true };
            Panel input = new Panel { Dock = DockStyle.Bottom, Height = Theme.Mono.Height + 26, Padding = new Padding(0, 6, 0, 0) };
            _commandBox = new TextBox { Dock = DockStyle.Fill, Font = Theme.Mono };
            _commandBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendTyped(); } };
            Button send = Theme.Button("Send", (s, e) => SendTyped(), 90); send.Dock = DockStyle.Right;
            input.Controls.Add(_commandBox); input.Controls.Add(send);
            bottom.Controls.Add(_console); bottom.Controls.Add(input); bottom.Controls.Add(ch);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Back, Panel1MinSize = 120, Panel2MinSize = 120 };
            split.Panel1.Controls.Add(top);
            split.Panel2.Controls.Add(bottom);
            Theme.Remember(split, "launch.split", 470);
            page.Controls.Add(split);
        }

        // ---- messages to the players -----------------------------------------------------------------

        private ComboBox _msgKind, _msgSize, _msgFont;
        private RichTextBox _msgText;
        private Button _msgStyle;
        private CheckBox _msgOnScreen;
        private StylePreview _msgPreview;
        private string _msgSpec = "color=#C79BFF bold";
        private static readonly string[] MsgKinds = { "Announcement", "Alert", "My own style" };
        private const float MsgBaseSize = 11f;

        private Control BuildComposer()
        {
            FitFlow box = new FitFlow { BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
            _msgKind = Theme.Choice(MsgKinds, "Announcement", 170);
            _msgText = new RichTextBox { Width = 700, Height = 58, BackColor = Color.FromArgb(14, 20, 30), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", MsgBaseSize), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, Multiline = true };
            _msgText.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendMessage(); } };
            _msgStyle = Theme.Button("Style...", (s, e) =>
            {
                string spec = StyleDialog.Ask(this, "Message style", _msgSpec, PlainMessage());
                if (spec != null) { _msgSpec = spec; _msgKind.SelectedIndex = 2; UpdateMessagePreview(); }
            }, 90);
            _msgOnScreen = Theme.Check("Also show it in the middle of everyone's screen", false);
            _msgPreview = new StylePreview { Width = 700, Margin = new Padding(0, 2, 0, 4) };
            _msgKind.SelectedIndexChanged += (s, e) => UpdateMessagePreview();
            _msgText.TextChanged += (s, e) => UpdateMessagePreview();

            // styles for the selected letters or words
            FlowLayoutPanel tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(900, 0), BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            tools.Controls.Add(Theme.Label("Selected text: ", Theme.Small, Theme.Muted));
            foreach (var c in StyleSpec.Colors)
            {
                Color col = StyleSpec.ParseColor(c.Hex).Value;
                Panel sw = new Panel { Size = new Size(20, 20), Margin = new Padding(0, 4, 3, 0), BackColor = col, Cursor = Cursors.Hand };
                new ToolTip().SetToolTip(sw, c.Name);
                sw.Click += (s, e) => { _msgText.SelectionColor = col; _msgText.Focus(); };
                tools.Controls.Add(sw);
            }
            tools.Controls.Add(ToolButton("B", FontStyle.Bold));
            tools.Controls.Add(ToolButton("I", FontStyle.Italic));
            tools.Controls.Add(ToolButton("U", FontStyle.Underline));
            tools.Controls.Add(ToolButton("S", FontStyle.Strikeout));
            _msgSize = Theme.Choice(StyleSpec.Sizes.Select(x => x.Name).ToArray(), "Normal", 100);
            _msgSize.SelectedIndexChanged += (s, e) => ApplyToSelection(f => new Font(f.FontFamily, MsgBaseSize * StyleSpec.Sizes[_msgSize.SelectedIndex].Percent / 100f, f.Style));
            _msgFont = Theme.Choice(new[] { "Game font" }, "Game font", 190);
            _msgFont.DropDown += (s, e) => FillFonts();
            _msgFont.SelectedIndexChanged += (s, e) => ApplyToSelection(f => new Font(ShowFont(_msgFont.SelectedIndex <= 0 ? null : (string)_msgFont.SelectedItem), f.Size, f.Style));
            tools.Controls.Add(_msgSize); tools.Controls.Add(_msgFont);
            tools.Controls.Add(Theme.Button("Plain", (s, e) => { _msgText.SelectionColor = Color.White; ApplyToSelection(f => new Font("Segoe UI", MsgBaseSize, FontStyle.Regular)); }, 70));

            box.Controls.Add(Theme.Label("MESSAGE THE PLAYERS", Theme.Sub, Theme.Line));
            box.Controls.Add(Theme.Row(_msgKind, Theme.Label(" "), _msgStyle, Theme.Label(" "), _msgOnScreen));
            box.Controls.Add(Theme.Sized(_msgText, "launch.message", 58, 34));
            box.Controls.Add(tools);
            box.Controls.Add(Theme.Row(Theme.Button("Send to everyone", (s, e) => SendMessage(), 150)));
            box.Controls.Add(_msgPreview);
            box.Controls.Add(Theme.Wrap("Pick a kind for the whole message, then select letters or words to give them their own colour, size or font. How announcements, alerts, the welcome, joins and leaves and players' names look is set on the Settings page (the Style rows).", 900));
            return box;
        }

        private Button ToolButton(string text, FontStyle style)
        {
            Button b = Theme.Button(text, (s, e) => ApplyToSelection(f => new Font(f.FontFamily, f.Size, f.Style ^ style)), 34);
            b.Font = new Font(Theme.Body, style);
            b.Padding = new Padding(4, 2, 4, 2);
            return b;
        }

        private void ApplyToSelection(Func<Font, Font> change)
        {
            if (_msgText.SelectionLength == 0) return;
            int a = _msgText.SelectionStart, n = _msgText.SelectionLength;
            for (int i = a; i < a + n; i++)
            {
                _msgText.Select(i, 1);
                Font f = _msgText.SelectionFont ?? _msgText.Font;
                _msgText.SelectionFont = change(f);
            }
            _msgText.Select(a, n);
            _msgText.Focus();
        }

        // the game's fonts (from the server) and a Windows font that looks like each, for the editor
        private readonly Dictionary<string, string> _fontByShown = new Dictionary<string, string>();

        private void FillFonts()
        {
            List<string> fonts = Json.Arr(_current?.Status, "fonts").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0 && !x.Contains("Fallback")).ToList();
            if (fonts.Count == 0 || _msgFont.Items.Count == fonts.Count + 1) return;
            _msgFont.Items.Clear(); _msgFont.Items.Add("Game font");
            foreach (string f in fonts) _msgFont.Items.Add(f);
        }

        private string ShowFont(string gameFont)
        {
            if (gameFont == null) return "Segoe UI";
            string shown = gameFont.IndexOf("Anta", StringComparison.OrdinalIgnoreCase) >= 0 ? "Bahnschrift"
                : gameFont.IndexOf("Mulish", StringComparison.OrdinalIgnoreCase) >= 0 ? "Calibri"
                : gameFont.IndexOf("Liberation", StringComparison.OrdinalIgnoreCase) >= 0 ? "Arial" : "Consolas";
            _fontByShown[shown] = gameFont;
            return shown;
        }

        private string PlainMessage() { return _msgText.Text.Replace("\n", " ").Trim(); }

        private void UpdateMessagePreview()
        {
            if (_msgPreview == null || _current == null) return;
            Conf conf = _current.LoadConf();
            string prefix = conf.Get("MessagePrefix");
            int k = _msgKind.SelectedIndex;
            _msgPreview.Spec = k == 0 ? conf.Get("StyleAnnounce") : k == 1 ? conf.Get("StyleAlert") : _msgSpec;
            _msgPreview.Sample = (prefix.Length > 0 ? prefix + " " : "") + (PlainMessage().Length > 0 ? PlainMessage() : "What everyone will see");
            _msgPreview.Invalidate();
        }

        /// <summary>The editor's text with its styled parts as {style}...{/} (the server turns them into the chat's tags).</summary>
        public string RichMessage()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            string text = _msgText.Text.Replace("\n", " ");
            int keepStart = _msgText.SelectionStart, keepLen = _msgText.SelectionLength;
            string open = null;
            for (int i = 0; i < text.Length; i++)
            {
                _msgText.Select(i, 1);
                string spec = RunSpec(_msgText.SelectionColor, _msgText.SelectionFont ?? _msgText.Font);
                if (spec != open)
                {
                    if (open != null) sb.Append("{/}");
                    if (spec.Length > 0) sb.Append("{" + spec + "}");
                    open = spec.Length > 0 ? spec : null;
                }
                char ch = text[i];
                sb.Append(ch == '{' ? "{{" : ch.ToString());
            }
            if (open != null) sb.Append("{/}");
            _msgText.Select(keepStart, keepLen);
            return sb.ToString().Trim();
        }

        private string RunSpec(Color c, Font f)
        {
            StyleSpec sp = new StyleSpec();
            // the box's own text colour is "no colour of its own"
            if (c.ToArgb() != Color.White.ToArgb() && c.ToArgb() != _msgText.ForeColor.ToArgb() && c.A > 0) sp.Color = c;
            sp.Bold = f.Bold; sp.Italic = f.Italic; sp.Underline = f.Underline; sp.Strike = f.Strikeout;
            sp.Size = (int)Math.Round(f.Size / MsgBaseSize * 100f);
            if (Math.Abs(sp.Size - 100) < 3) sp.Size = 100;
            string spec = sp.ToString();
            string game;
            if (_fontByShown.TryGetValue(f.FontFamily.Name, out game)) spec = (spec + " font=\"" + game + "\"").Trim();
            return spec;
        }

        /// <summary>The console line for the message box (null: nothing to send).</summary>
        public string MessageCommand()
        {
            string t = RichMessage();
            if (t.Length == 0) return null;
            int k = _msgKind.SelectedIndex;
            bool screen = _msgOnScreen.Checked;
            if (k == 1) return "alert " + t;                       // on screen too when AlertsOnScreen is on
            string cmd = screen ? "announce " : "say ";
            if (k == 0) return cmd + (t.StartsWith("{") && !t.StartsWith("{{") ? "{} " : "") + t;
            return cmd + "{" + _msgSpec + "} " + t;
        }

        private void SendMessage()
        {
            string cmd = MessageCommand();
            if (cmd == null) return;
            if (!Send(cmd)) { MessageBox.Show(this, "The server isn't running."); return; }
            _msgText.Clear();
        }

#if SDSMTEST
        public void SetMessageForTest(int kind, string spec, string text, bool onScreen = false, int boldFrom = -1, int boldLen = 0, string colorHex = null)
        {
            if (spec != null) _msgSpec = spec;
            _msgKind.SelectedIndex = kind;
            _msgText.Clear();
            _msgText.Text = text;
            _msgOnScreen.Checked = onScreen;
            if (boldFrom >= 0)
            {
                _msgText.Select(boldFrom, boldLen);
                ApplyToSelection(f => new Font(f.FontFamily, f.Size, f.Style | FontStyle.Bold));
                if (colorHex != null) { _msgText.Select(boldFrom, boldLen); _msgText.SelectionColor = StyleSpec.ParseColor(colorHex).Value; }
            }
            UpdateMessagePreview();
        }
#endif

        private void AskRestart()
        {
            using (Form f = new Form { Text = "Restart or stop", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(14) })
            {
                FlowLayoutPanel col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
                RadioButton restart = new RadioButton { Text = "Restart (it starts again by itself)", Checked = true, AutoSize = true, BackColor = Color.Transparent };
                RadioButton stop = new RadioButton { Text = "Stop", AutoSize = true, BackColor = Color.Transparent };
                NumericUpDown minutes = Theme.Number(5, 0, 240, 80);
                TextBox reason = Theme.Box("", 360);
                col.Controls.Add(restart); col.Controls.Add(stop);
                col.Controls.Add(Theme.Row(Theme.Label("In ", Theme.Body, Theme.Muted), minutes, Theme.Label(" minutes (0 = now)", Theme.Body, Theme.Muted)));
                col.Controls.Add(Theme.Label("Reason (players see it)", Theme.Body, Theme.Muted));
                col.Controls.Add(reason);
                col.Controls.Add(Theme.Wrap("Everyone is warned in chat, in the alert style: straight away, then 10, 5, 2 and 1 minutes and 30 and 10 seconds before. The world is saved first.", 380));
                string cmd = null;
                Button ok = Theme.Button("OK", (s, e) =>
                {
                    cmd = RestartCommand(restart.Checked, (int)minutes.Value, reason.Text);
                    f.DialogResult = DialogResult.OK; f.Close();
                }, 90);
                col.Controls.Add(Theme.Row(ok, Theme.Button("Cancel", (s, e) => f.Close(), 90)));
                f.Controls.Add(col);
                f.AcceptButton = ok;
                Theme.Apply(f);
                if (f.ShowDialog(this) != DialogResult.OK || cmd == null) return;
                if (!Send(cmd)) return;
                if (restart.Checked) _restartPending = true;
                if (minutes.Value == 0) _stopAskedAt = DateTime.Now;
            }
        }

        /// <summary>One warning before a restart: how long before, what players are told, and whether it's on screen too.</summary>
        public class WarningRow
        {
            public int Seconds; public string Text = ""; public bool OnScreen;
        }

        /// <summary>RestartWarnings + RestartMessages -> one row per warning (largest first).</summary>
        public static List<WarningRow> WarningRows(string warnings, string messages, bool screenDefault)
        {
            List<int> secs = ParseWarningsText(warnings);
            if (secs.Count == 0) secs = new List<int> { 600, 300, 120, 60, 30, 10 };
            Dictionary<int, WarningRow> own = new Dictionary<int, WarningRow>();
            WarningRow any = null;
            foreach (string raw in (messages ?? "").Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                string when = part.Substring(0, colon).Trim();
                WarningRow r = new WarningRow { Text = part.Substring(colon + 1).Trim() };
                if (when.EndsWith("!")) { r.OnScreen = true; when = when.Substring(0, when.Length - 1).Trim(); }
                if (when == "*") { any = r; continue; }
                List<int> s = ParseWarningsText(when);
                if (s.Count == 1) { r.Seconds = s[0]; own[s[0]] = r; }
            }
            return secs.Select(s => own.ContainsKey(s) ? own[s] : new WarningRow { Seconds = s, Text = any != null ? any.Text : "", OnScreen = any != null ? any.OnScreen : screenDefault }).ToList();
        }

        public static List<int> ParseWarningsText(string text)
        {
            List<int> l = new List<int>();
            foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(raw.Trim().ToLowerInvariant(), @"^(\d+(?:\.\d+)?)(h|min|m|s)?$");
                if (!m.Success) continue;
                double n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string u = m.Groups[2].Value;
                l.Add((int)Math.Round(n * (u == "h" ? 3600 : u == "s" ? 1 : 60)));
            }
            return l.Distinct().OrderByDescending(x => x).ToList();
        }

        public static string WhenText(int s) { return s % 3600 == 0 ? (s / 3600) + "h" : s % 60 == 0 ? (s / 60) + "m" : s + "s"; }

        /// <summary>The rows back into RestartWarnings and RestartMessages.</summary>
        public static void RowsToConf(List<WarningRow> rows, out string warnings, out string messages)
        {
            List<WarningRow> r = rows.Where(x => x.Seconds > 0).GroupBy(x => x.Seconds).Select(g => g.First()).OrderByDescending(x => x.Seconds).ToList();
            warnings = string.Join(", ", r.Select(x => WhenText(x.Seconds)));
            messages = string.Join(" || ", r.Select(x => WhenText(x.Seconds) + (x.OnScreen ? "!" : "") + ": " + (x.Text ?? "").Replace("||", "|").Trim()));
        }

        /// <summary>What players read for a warning: [time], [reason], [action] filled in as the server does (Chat.Fill).</summary>
        public static string PreviewText(string text, int seconds, string reason, bool restart = true)
        {
            string action = restart ? "restart" : "shut down";
            string r = (reason ?? "").Trim();
            if ((text ?? "").Trim().Length == 0) return "The server will " + action + " in " + SpanText(seconds) + (r.Length > 0 ? ": " + r : ".");
            string s = System.Text.RegularExpressions.Regex.Replace(text, @"\[time\]", SpanText(seconds), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[reason\]", r.Length > 0 ? r : (restart ? "a restart" : "a shutdown"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return System.Text.RegularExpressions.Regex.Replace(s, @"\[action\]", action, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>Chat.Span on the server.</summary>
        public static string SpanText(int n)
        {
            if (n >= 120) return (n / 60) + " minutes" + (n % 60 != 0 ? " " + (n % 60) + " s" : "");
            if (n >= 60) return "1 minute" + (n % 60 != 0 ? " " + (n % 60) + " s" : "");
            return n + " seconds";
        }

        /// <summary>Times of day the server restarts by itself, and each warning players get before (its own words, on screen or not).</summary>
        private void AskPlannedRestarts()
        {
            Conf c = _current.LoadConf();
            bool screenDefault = c.Get("AlertsOnScreen") != "off" && c.Get("AlertsOnScreen") != "false";
            List<WarningRow> rows = WarningRows(c.Get("RestartWarnings"), c.Get("RestartMessages"), screenDefault);
            using (Form f = new Form { Text = "Planned restarts", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(16) })
            {
                const int W = 720;
                FlowLayoutPanel col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
                TextBox times = Theme.Box(c.Get("RestartTimes"), 300), reason = Theme.Box(c.Get("RestartReason"), 300);
                reason.PlaceholderText = "planned restart";
                times.PlaceholderText = "none";
                col.Controls.Add(Theme.Row(Theme.Label("Restart every day at ", Theme.Sub, Theme.Line), times));
                col.Controls.Add(Theme.Wrap("This PC's clock, commas between: 04:00, 16:30. A day first for once a week: Sun 04:00.", W, Theme.Small, Theme.Muted));
                col.Controls.Add(Theme.Row(Theme.Label("Reason players see ", Theme.Sub, Theme.Line), reason));

                col.Controls.Add(Theme.Label("Warnings", Theme.Sub, Theme.Line));
                col.Controls.Add(Theme.Wrap("Before each restart, and before Restart... and the console's restart/stop. [time] is the time really left when it's sent, [reason] the reason, [action] restart or shut down. Leave the words empty for: The server will restart in [time]: [reason].", W, Theme.Small, Theme.Muted));
                FlowLayoutPanel list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
                col.Controls.Add(list);
                Label preview = Theme.Wrap("", W, Theme.Body, Theme.Good);
                List<Action> collect = new List<Action>();
                Action<WarningRow> addRow = null;
                Action showPreview = null;
                addRow = r =>
                {
                    TextBox when = Theme.Box(WhenText(r.Seconds), 64);
                    TextBox words = Theme.Box(r.Text, 470);
                    words.PlaceholderText = "The server will restart in [time]: [reason]";
                    CheckBox screen = Theme.Check("Screen", r.OnScreen);
                    FlowLayoutPanel line = null;
                    LinkLabel x = new LinkLabel { Text = "Remove", AutoSize = true, Margin = new Padding(6, 6, 0, 0), LinkColor = Theme.Muted, ActiveLinkColor = Theme.Bad, BackColor = Color.Transparent };
                    x.Click += (s, e) => { list.Controls.Remove(line); rows.Remove(r); showPreview(); };
                    line = Theme.Row(when, words, screen, x);
                    line.Margin = new Padding(0, 1, 0, 1);
                    EventHandler changed = (s, e) =>
                    {
                        List<int> t = ParseWarningsText(when.Text);
                        if (t.Count == 1) r.Seconds = t[0];
                        r.Text = words.Text; r.OnScreen = screen.Checked;
                        showPreview();
                    };
                    when.TextChanged += changed; words.TextChanged += changed; screen.CheckedChanged += changed;
                    when.Enter += (s, e) => showPreview(); words.Enter += (s, e) => { _previewRow = r; showPreview(); };
                    list.Controls.Add(line);
                };
                showPreview = () =>
                {
                    WarningRow r = rows.Contains(_previewRow) ? _previewRow : rows.FirstOrDefault();
                    preview.Text = r == null ? "No warnings: players aren't told before a restart." :
                        "Players see, " + SpanText(r.Seconds) + " before" + (r.OnScreen ? " (in chat and the middle of the screen)" : " (in chat)") + ":\n" + PreviewText(r.Text, r.Seconds, reason.Text);
                };
                reason.TextChanged += (s, e) => showPreview();
                foreach (WarningRow r in rows.ToList()) addRow(r);
                col.Controls.Add(Theme.Row(
                    Theme.Button("Add a warning", (s, e) => { WarningRow r = new WarningRow { Seconds = 15 * 60, OnScreen = screenDefault }; rows.Add(r); addRow(r); _previewRow = r; showPreview(); }, 160),
                    Theme.Button("Back to the usual ones", (s, e) =>
                    {
                        list.Controls.Clear(); rows.Clear();
                        foreach (WarningRow r in WarningRows("10m, 5m, 2m, 1m, 30s, 10s", "", screenDefault)) { rows.Add(r); addRow(r); }
                        showPreview();
                    }, 210)));
                col.Controls.Add(preview);
                Label err = Theme.Wrap("", W, Theme.Body, Theme.Bad);
                col.Controls.Add(err);
                bool ok = false;
                Button save = Theme.Button("Save", (s, e) =>
                {
                    string w0, m0;
                    RowsToConf(rows, out w0, out m0);
                    string bad = CheckRestartTimes(times.Text) ?? CheckRestartWarnings(w0)
                        ?? (rows.Any(r => ParseWarningsText(WhenText(r.Seconds)).Count != 1 || r.Seconds <= 0) ? "Each warning needs a time like 10m, 1m or 30s." : null);
                    if (bad != null) { err.Text = bad; return; }
                    ok = true; f.Close();
                }, 100);
                col.Controls.Add(Theme.Row(save, Theme.Button("Cancel", (s, e) => f.Close(), 100)));
                f.Controls.Add(col);
                f.AcceptButton = save;
                Theme.Apply(f);
                showPreview();
#if SDSMTEST
                if (PlannedForTest != null) { f.Shown += (s, e) => { PlannedForTest(f, rows); }; }
#endif
                f.ShowDialog(this);
                if (!ok) return;
                string warnings, messages;
                RowsToConf(rows, out warnings, out messages);
                SetConf("RestartTimes", times.Text.Trim());
                SetConf("RestartWarnings", warnings);
                SetConf("RestartMessages", messages);
                SetConf("RestartReason", reason.Text.Trim());
#if SDSMTEST
                if (PlannedForTest != null) return;
#endif
                MessageBox.Show(this, times.Text.Trim().Length == 0 ? "No planned restarts. The warnings are used for Restart... and the console." : "The server restarts at " + times.Text.Trim() + " (this PC's clock), warning players " + warnings + " before.", "Planned restarts");
            }
        }

        private WarningRow _previewRow;
#if SDSMTEST
        public Action<Form, List<WarningRow>> PlannedForTest;
        public void OpenPlannedForTest() { AskPlannedRestarts(); }
#endif

        /// <summary>The same rules the server uses (Restarts.ParseTimes): null when fine.</summary>
        public static string CheckRestartTimes(string text)
        {
            foreach (string raw in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                int sp = t.IndexOf(' ');
                if (sp > 0)
                {
                    if (!new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }.Any(d => t.StartsWith(d))) return "'" + raw.Trim() + "': a day is sun, mon, tue, wed, thu, fri or sat";
                    t = t.Substring(sp + 1).Trim();
                }
                DateTime parsed;
                if (!DateTime.TryParseExact(t.ToUpperInvariant(), new[] { "H:mm", "HH:mm", "h:mmtt", "h:mm tt", "htt", "h tt" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed))
                    return "'" + raw.Trim() + "': a time is like 04:00, 16:30 or 4:30pm";
            }
            return null;
        }

        public static string CheckRestartWarnings(string text)
        {
            foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!System.Text.RegularExpressions.Regex.IsMatch(raw.Trim().ToLowerInvariant(), @"^\d+(\.\d+)?(h|min|m|s)?$")) return "'" + raw.Trim() + "': a warning is like 10m, 1m or 30s";
            return null;
        }

        /// <summary>"restart 5 maintenance", "stop", ...</summary>
        public static string RestartCommand(bool restart, int minutes, string reason)
        {
            string r = (reason ?? "").Trim();
            // a reason starting with a digit would be read as the minutes
            if (minutes == 0 && r.Length > 0 && char.IsDigit(r[0])) r = "- " + r;
            return (restart ? "restart" : "stop") + (minutes > 0 ? " " + minutes : "") + (r.Length > 0 ? " " + r : "");
        }

        private void SendTyped()
        {
            string t = _commandBox.Text.Trim();
            if (t.Length == 0) return;
            if (!Send(t)) { AppendConsole("(the server isn't running)"); return; }
            _commandBox.Clear();
        }

        private void AppendConsole(string s) { _console.AppendText(s + Environment.NewLine); }

        /// <summary>Before a start: the game updated since this server's copy was made has its new files linked in. What was done, or "".</summary>
        public static string BringGameUpToDate(Server s)
        {
            try
            {
                ServerInfo info = s.LoadInfo();
                if (s.Running || !GameCopy.Outdated(info.SourceGame, s.Folder)) return "";
                GameCopy.Relink(info.SourceGame, s.Folder, x => { });
                return "The game in " + info.SourceGame + " was updated: this server's game files were brought up to date before it started.";
            }
            catch (Exception e) { return "Could not bring the game files up to date: " + e.Message; }
        }

        private void DoStart()
        {
            // the game was updated since this server's copy was made: bring its game files up to date first, so it runs the build players have
            string note = BringGameUpToDate(_current);
            if (note.Length > 0) { _reach.Text = note; _reach.ForeColor = note.StartsWith("Could not") ? Theme.Bad : Theme.Warn; }
            string err = _current.Start();
            if (err != null) MessageBox.Show(this, err, "Start");
            _consoleLen = -1;
        }

        private void RefreshLaunch(bool force)
        {
            Server s = _current;
            JsonNode st = s.Status;
            string state = s.State;
            bool running = s.Running;
            // a restart (from here, the console or a timer) exits with "restarting": start it again,
            // if this Manager started it (StartDedicatedServer.bat restarts the ones it started)
            if (!running && (_restartPending || s.Launched != null) && Json.Str(st, "state") == "restarting") { _restartPending = false; DoStart(); return; }
            if (!running) _restartPending = false;
            JsonNode sch = running && st != null ? st["scheduled"] : null;
            _callOff.Visible = sch != null;
            _state.Text = state == "running" ? "Running" : state == "starting" ? "Starting..." : state == "failed" ? "Failed to start" : state == "not responding" ? "Not responding" : "Stopped";
            _state.ForeColor = state == "running" ? Theme.Good : state == "failed" || state == "not responding" ? Theme.Bad : state == "starting" ? Theme.Warn : Theme.Muted;
            _start.Enabled = !running;
            _stop.Enabled = _restart.Enabled = running;
            _kill.Enabled = running && (state == "not responding" || _stopAskedAt != DateTime.MinValue && (DateTime.Now - _stopAskedAt).TotalSeconds > 20);
            Conf conf = s.LoadConf();
            if (state == "not responding")
            {
                int wd = conf.GetInt("WatchdogSeconds");
                _facts.Text = "The server hasn't updated for " + Fmt.Span(s.QuietSeconds) + ": it is stuck. "
                    + (wd > 0 ? "It restarts itself after " + wd + " s stuck (WatchdogSeconds)" + (s.Launched != null ? " and this Manager starts it again" : "") + ". " : "")
                    + "Force quit ends it now (anything since the last save is lost); then press Start.";
            }
            else if (running && st != null)
            {
                string players = string.Join(", ", Json.Arr(st, "players").Select(p => Json.Str(p, "name") + (Json.Bool(p, "op") ? " [admin]" : "")));
                _facts.Text = "Players: " + Json.Arr(st, "players").Count() + "/" + Json.Int(st, "maxPlayers") + (players.Length > 0 ? "  -  " + players : "")
                    + "\nUp " + Fmt.Span(Json.Num(st, "uptimeSeconds")) + "   World: " + Path.GetFileName(Path.GetDirectoryName(Json.Str(st, "world")))
                    + "   Saves: " + Json.Int(st, "saves") + "   Research: " + Json.Str(st["rules"], "research") + "   Dev tools: " + Json.Str(st["rules"], "devTools")
                    + "\nLoop every " + Json.Num(st, "avgFrameMs").ToString("0.0") + " ms, working " + Json.Num(st, "avgWorkMs").ToString("0.00") + " ms of it"
                    + (st["resources"] != null ? "   Memory " + Fmt.Bytes(Json.Num(st["resources"], "privateMB") * 1048576) + " (this PC: " + Fmt.Bytes(Json.Num(st["resources"], "machineUsedMB") * 1048576) + " of " + Fmt.Bytes(Json.Num(st["resources"], "machineTotalMB") * 1048576) + " used)   CPU " + Json.Num(st["resources"], "cpu").ToString("0") + "%" : "")
                    + (Json.Str(st, "nextRestart").Length > 0 ? "\nNext planned restart: " + Json.Date(st, "nextRestart").ToLocalTime().ToString("ddd HH:mm") : "")
                    + "   Stalls: " + Json.Int(st, "stalls") + (Json.Int(st, "stalls") > 0 ? " (longest " + Json.Num(st, "longestStallMs").ToString("0") + " ms)" : "")
                    + (sch != null ? "\n" + (Json.Bool(sch, "restart") ? "Restarting" : "Stopping") + " in " + Fmt.Span(Json.Num(sch, "secondsLeft"))
                        + (Json.Str(sch, "reason").Length > 0 ? " (" + Json.Str(sch, "reason") + ")" : "") + " - players are being warned." : "")
                    + NetLine(st["net"]);
            }
            else
            {
                _facts.Text = state == "failed" ? "It stopped before it was ready. See the Logs page (Server log)." :
                    "World: " + conf.Get("World") + "   Port: " + conf.Get("Port") + "   Players at once: " + conf.Get("MaxPlayers") + "\nMods: " + string.Join(", ", ModsIn(s).Select(m => m.Name));
            }
            if (_publicFor != s.Folder) { _publicFor = s.Folder; _public.Text = s.LoadInfo().PublicAddress; _reach.Text = ""; }
            if (force || _ticks % 10 == 0)
                _connect.Text = "Players join on the Join Game page with: " + string.Join("  or  ", LocalAddresses().Select(a => a + ":" + conf.Get("Port"))) + " (on your network)"
                    + ", or your public address / a playit.gg address from the internet (forward UDP port " + conf.Get("Port") + " on your router, or run a playit.gg UDP tunnel to it).";
            if (force) UpdateMessagePreview();
            TailConsole(force);
        }

        // What the console can show, each with a tick box; all of them to start with.
        public static readonly string[] ConsoleKinds = { "Chat", "Joins and leaves", "Commands", "Saves", "Warnings and errors", "Performance", "Startup", "Other mods", "Everything else" };

        /// <summary>Which of ConsoleKinds a server log line is (beforeReady: it came before the server said READY).</summary>
        public static string KindOf(string line, bool beforeReady)
        {
            if (line.Contains("[chat command]") || line.Contains("] > ") || line.Contains("setting ") && line.Contains(" = ")) return "Commands";
            if (line.Contains("[chat]")) return "Chat";
            if (line.Contains("JOINED ") || line.Contains("LEFT ") || line.Contains("ACCEPTED ") || line.Contains("TURNED AWAY") || line.Contains("kicked") || line.Contains(" banned")) return "Joins and leaves";
            if (line.Contains("[WARNING]") || line.Contains("[ERROR]") || line.Contains("Exception") || line.Contains("FAILED")) return "Warnings and errors";
            if (line.Contains("stalled") || line.Contains("ground:") || line.Contains("Trace:") || line.Contains("profile")) return "Performance";
            if (line.IndexOf("saved", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("saving", StringComparison.OrdinalIgnoreCase) >= 0 || line.Contains("backup")) return "Saves";
            if (beforeReady) return "Startup";
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"^\[[0-9:.]+\] \[([^\]]+)\]");
            if (m.Success && m.Groups[1].Value != "DedicatedServer") return "Other mods";
            return "Everything else";
        }

        // the log as read so far, each line with its kind: only what the server adds is read each second
        private readonly List<KeyValuePair<string, string>> _logLines = new List<KeyValuePair<string, string>>();
        private string _logLinesPath, _logPartial = "";
        private long _logLinesPos;
        private bool _logReady;

        private void ReadNewLogLines(string path)
        {
            long len = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (path != _logLinesPath || len < _logLinesPos) { _logLines.Clear(); _logLinesPos = 0; _logPartial = ""; _logReady = false; _logLinesPath = path; }
            if (len == _logLinesPos) return;
            string add;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Position = _logLinesPos;
                    using (StreamReader r = new StreamReader(fs)) add = r.ReadToEnd();
                    _logLinesPos = len;
                }
            }
            catch { return; }
            string text = _logPartial + add;
            int lastNl = text.LastIndexOf('\n');
            _logPartial = lastNl < 0 ? text : text.Substring(lastNl + 1);       // a line still being written waits for its end
            if (lastNl < 0) return;
            foreach (string raw in text.Substring(0, lastNl).Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (l.Length == 0) continue;
                string kind = KindOf(l, !_logReady);
                if (l.Contains("READY - ")) _logReady = true;
                _logLines.Add(new KeyValuePair<string, string>(l, kind));
            }
            if (_logLines.Count > 20000) _logLines.RemoveRange(0, _logLines.Count - 20000);
        }

        /// <summary>The server's whole log (every mod, MelonLoader and the console), minus the kinds unticked.</summary>
        private void TailConsole(bool force)
        {
            string path = File.Exists(_current.LogPath) ? _current.LogPath : _current.OutputPath;
            int before = _logLines.Count; long posBefore = _logLinesPos; string pathBefore = _logLinesPath;
            ReadNewLogLines(path);
            if (!force && _logLines.Count == before && posBefore == _logLinesPos && pathBefore == _logLinesPath && _consoleLen >= 0) return;
            HashSet<string> hidden = new HashSet<string>(_settings.ConsoleHidden);
            List<string> shown = new List<string>();
            for (int i = _logLines.Count - 1; i >= 0 && shown.Count < 800; i--)
                if (!hidden.Contains(_logLines[i].Value)) shown.Add(_logLines[i].Key);
            shown.Reverse();
            _console.Text = shown.Count == 0 ? (File.Exists(path) ? "(nothing to show with these boxes ticked)" : "(the server hasn't run yet)") : string.Join(Environment.NewLine, shown);
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
            _consoleLen = _logLinesPos;
        }

#if SDSMTEST
        public string ConsoleTextForTest { get { return _console.Text; } }
#endif

        private static IEnumerable<string> LocalAddresses()
        {
            List<string> r = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) r.Add(a.Address.ToString());
                }
            }
            catch { }
            if (r.Count == 0) r.Add("127.0.0.1");
            return r.Take(3);
        }

        // the server's mods, read again only when its Mods folder changes (reading each DLL's name takes a while)
        private List<ModMeta> _modsIn = new List<ModMeta>();
        private long _modsInStamp;
        private string _modsInFor;

        private List<ModMeta> ModsIn(Server s)
        {
            string dir = Path.Combine(s.Folder, "Mods");
            if (!Directory.Exists(dir)) return new List<ModMeta>();
            string[] files = Directory.GetFiles(dir, "*.dll");
            long stamp = files.Aggregate(Directory.GetLastWriteTimeUtc(dir).Ticks, (a, f) => a ^ File.GetLastWriteTimeUtc(f).Ticks ^ f.GetHashCode());
            if (_modsInFor == s.Folder && _modsInStamp == stamp) return _modsIn;
            _modsIn = files.OrderBy(f => f).Select(ModMeta.Read).ToList();
            _modsInFor = s.Folder; _modsInStamp = stamp;
            return _modsIn;
        }

        /// <summary>After the game or its mods were updated: re-link the game files and copy the server's mods again.</summary>
        private void UpdateFromGame()
        {
            Server s = _current;
            if (s.Running) { MessageBox.Show(this, "Stop the server first."); return; }
            ServerInfo info = s.LoadInfo();
            if (!GameCopy.IsGameFolder(info.SourceGame)) { MessageBox.Show(this, "The game folder this server was made from isn't there any more: " + info.SourceGame); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                GameCopy.Relink(info.SourceGame, s.Folder, x => { });
                List<string> updated = GameCopy.UpdateNewer(s.Folder, info.SourceGame);
                _modsLoadedFor = null;
                MessageBox.Show(this, "Updated from " + info.SourceGame + ":\n- game files re-linked\n- " + (updated.Count == 0 ? "no newer mods there" : "newer mods copied: " + string.Join(", ", updated))
                    + "\n\nNo mod was removed. Mods are added and removed on the Mods page.");
            }
            catch (Exception e) { MessageBox.Show(this, "Update failed: " + e.Message); }
            finally { Cursor = Cursors.Default; }
        }

        // ---- Saves ----------------------------------------------------------------------------------

        private DataGridView _worlds, _backups, _savePlayers, _saveShips;
        private Label _savesNote, _saveTitle;
        private string _shownSave;

        private void BuildSaves(TabPage page)
        {
            SplitContainer outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 560, BackColor = Theme.Back };
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240, BackColor = Theme.Back };
            Theme.Remember(outer, "saves.outer"); Theme.Remember(split, "saves.split");
            _worlds = Theme.NewGrid("World", "Active", "Size", "Saved", "Folder");
            _backups = Theme.NewGrid("Backup", "Saved", "Size");
            _worlds.SelectionChanged += (s, e) => { if (_worlds.Focused || _shownSave == null) ShowSave(SelectedSaveFile(false)); };
            _backups.SelectionChanged += (s, e) => { if (_backups.Focused) ShowSave(SelectedSaveFile(true)); };
            _worlds.Enter += (s, e) => ShowSave(SelectedSaveFile(false));
            _backups.Enter += (s, e) => ShowSave(SelectedSaveFile(true));
            PageColumn wtop = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(0) };
            wtop.Controls.Add(Theme.Row(
                Theme.Button("Save now", (s, e) => { if (!Send("save")) MessageBox.Show(this, "The server isn't running (it saves when it stops)."); }, 110),
                Theme.Button("Back up now", (s, e) => BackUpNow(), 130),
                Theme.Button("Make active", (s, e) => MakeActive(), 120)));
            wtop.Controls.Add(Theme.Row(
                Theme.Button("Duplicate...", (s, e) => DuplicateWorld(), 120),
                Theme.Button("Export...", (s, e) => ExportSave(), 110),
                Theme.Button("Import my world...", (s, e) => ImportWorld(), 160),
                Theme.Button("New world...", (s, e) => NewWorld(), 120)));
            wtop.Controls.Add(Theme.Row(
                Theme.Button("Copy players / ships between saves...", (s, e) => CopyBetweenSaves(), 280),
                Theme.Button("Open folder", (s, e) => { Directory.CreateDirectory(_current.WorldsDir); Process.Start("explorer.exe", "\"" + _current.WorldsDir + "\""); }, 120)));
            PageColumn btop = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(0) };
            _savesNote = Theme.Wrap("", 500);
            btop.Controls.Add(Theme.Row(Theme.Button("Restore this backup", (s, e) => RestoreBackup(), 180)));
            btop.Controls.Add(_savesNote);
            split.Panel1.Padding = split.Panel2.Padding = new Padding(18, 8, 8, 8);
            split.Panel1.Controls.Add(_worlds); split.Panel1.Controls.Add(wtop);
            split.Panel2.Controls.Add(_backups); split.Panel2.Controls.Add(btop);
            outer.Panel1.Controls.Add(split);

            // what is in the picked save
            _saveTitle = Theme.Label("Pick a world or a backup to see who and what is in it.", Theme.Sub, Theme.Line);
            _saveTitle.Dock = DockStyle.Top; _saveTitle.AutoSize = false; _saveTitle.Height = 30;
            _savePlayers = Theme.NewGrid("Player", "Where", "Health", "Carrying");
            _saveShips = Theme.NewGrid("Ship", "Parts", "Where", "Speed", "Docked to");
            _savePlayers.Columns["Carrying"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _savePlayers.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            SplitContainer detail = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220, BackColor = Theme.Back };
            Theme.Remember(detail, "saves.detail");
            detail.Panel1.Controls.Add(_savePlayers); detail.Panel2.Controls.Add(_saveShips);
            outer.Panel2.Padding = new Padding(8, 8, 18, 8);
            outer.Panel2.Controls.Add(detail); outer.Panel2.Controls.Add(_saveTitle);
            page.Controls.Add(outer);
        }

        private string SelectedSaveFile(bool backup)
        {
            if (backup)
            {
                if (_backups.SelectedRows.Count == 0) return null;
                return Path.Combine(Path.GetDirectoryName(_current.CurrentWorldFile()), (string)_backups.SelectedRows[0].Cells["Backup"].Value);
            }
            if (_worlds.SelectedRows.Count == 0) return null;
            return Path.Combine((string)_worlds.SelectedRows[0].Cells["Folder"].Value, "world.json");
        }

        /// <summary>The players and ships in a save, by name (planets and items from the server's catalog.json).</summary>
        private void ShowSave(string file)
        {
            if (file == null || !File.Exists(file)) return;
            _shownSave = file;
            _savePlayers.Rows.Clear(); _saveShips.Rows.Clear();
            try
            {
                Catalog cat = Catalog.Load(_current);
                WorldFile w = WorldFile.Read(file, cat);
                _saveTitle.Text = Path.GetFileName(Path.GetDirectoryName(file)) + " / " + Path.GetFileName(file) + ": " + w.Players.Count + " player(s), " + w.Ships.Count + " ship(s), " + w.Cables + " cable(s)"
                    + (cat.Known ? "" : "  (start the server once for planet and item names)");
                foreach (WorldFile.PlayerRow p in w.Players.OrderBy(x => x.Name))
                    _savePlayers.Rows.Add(p.Name, p.Where, p.Health.ToString("0") + " hp, " + p.Oxygen.ToString("0") + " O2", p.ItemsUsed + "/" + p.Slots + " slots" + (p.Items.Length > 0 ? ": " + p.Items : ""));
                foreach (WorldFile.ShipRow sh in w.Ships.OrderByDescending(x => x.Parts))
                    _saveShips.Rows.Add(sh.Name, sh.Parts, sh.Where, sh.Speed.ToString("0.0") + " m/s", sh.DockedTo != 0 ? "#" + sh.DockedTo : "");
            }
            catch (Exception e) { _saveTitle.Text = "Could not read " + Path.GetFileName(file) + ": " + e.Message; }
        }

        private void RefreshSaves()
        {
            Server s = _current;
            string active = s.CurrentWorldFile();
            string keep = _worlds.SelectedRows.Count > 0 ? (string)_worlds.SelectedRows[0].Cells["Folder"].Value : null;
            List<string> files = new List<string>();
            if (Directory.Exists(s.WorldsDir)) files.AddRange(Directory.GetDirectories(s.WorldsDir).Select(d => Path.Combine(d, "world.json")).Where(File.Exists));
            if (File.Exists(active) && !files.Contains(active, StringComparer.OrdinalIgnoreCase)) files.Add(active);
            string bdir = Path.GetDirectoryName(active);
            FileInfo[] backups = Directory.Exists(bdir) ? new DirectoryInfo(bdir).GetFiles("*.json").Where(x => x.Name.StartsWith("backup") || x.Name.StartsWith("world-before")).OrderByDescending(x => x.LastWriteTime).ToArray() : new FileInfo[0];
            // the lists are only filled again when what they show changed (refilling every few seconds threw them back to the top)
            string sig = s.Folder + "|" + active + "|" + string.Join("|", files.Select(f => { FileInfo i = new FileInfo(f); return f + i.Length + i.LastWriteTimeUtc.Ticks; }))
                + "|" + string.Join("|", backups.Select(b => b.Name + b.Length + b.LastWriteTimeUtc.Ticks));
            if (sig == _savesSig) return;
            _savesSig = sig;
            int wScroll = _worlds.FirstDisplayedScrollingRowIndex, bScroll = _backups.FirstDisplayedScrollingRowIndex;
            GridRebuilds++;
            _worlds.Rows.Clear();
            foreach (string f in files)
            {
                FileInfo fi = new FileInfo(f);
                bool isActive = string.Equals(Path.GetFullPath(f), Path.GetFullPath(active), StringComparison.OrdinalIgnoreCase);
                int i = _worlds.Rows.Add(fi.Directory.Name, isActive ? "yes" : "", Fmt.Bytes(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), fi.DirectoryName);
                if (fi.DirectoryName == keep) _worlds.Rows[i].Selected = true;
            }
            _backups.Rows.Clear();
            foreach (FileInfo b in backups) _backups.Rows.Add(b.Name, b.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), Fmt.Bytes(b.Length));
            if (wScroll >= 0 && wScroll < _worlds.Rows.Count) _worlds.FirstDisplayedScrollingRowIndex = wScroll;
            if (bScroll >= 0 && bScroll < _backups.Rows.Count) _backups.FirstDisplayedScrollingRowIndex = bScroll;
            _savesNote.Text = "Backups of the active world. Restoring needs the server stopped; the current world is kept as world-before-restore.json.";
            if (_shownSave == null || !File.Exists(_shownSave)) ShowSave(SelectedSaveFile(false) ?? (File.Exists(active) ? active : null));
        }

        /// <summary>A copy of the active world as a backup now (the server makes it when running, so it is current).</summary>
        private void BackUpNow()
        {
            if (_current.Running) { Send("save backup"); MessageBox.Show(this, "The server is saving, keeping the previous save as a backup first."); return; }
            string active = _current.CurrentWorldFile();
            if (!File.Exists(active)) { MessageBox.Show(this, "There's no world yet."); return; }
            string b = Path.Combine(Path.GetDirectoryName(active), "backup_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".json");
            File.Copy(active, b);
            RefreshSaves();
        }

        private void DuplicateWorld()
        {
            if (_worlds.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a world first."); return; }
            string from = (string)_worlds.SelectedRows[0].Cells["Folder"].Value;
            string name = Prompt.Ask(this, "Duplicate", "Name for the copy:", new DirectoryInfo(from).Name + "-copy");
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            string to = Path.Combine(_current.WorldsDir, name);
            if (Directory.Exists(to)) { MessageBox.Show(this, "There is a world called \"" + name + "\" already."); return; }
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                if (!Path.GetFileName(f).StartsWith("backup")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            MessageBox.Show(this, "Copied as \"" + name + "\" (without its backups). Pick it and press Make active to play it.");
            RefreshSaves();
        }

        /// <summary>A world (with its respawn record, exploration and research) or one backup, as a zip.</summary>
        private void ExportSave()
        {
            bool backup = _backups.Focused || (_worlds.SelectedRows.Count == 0 && _backups.SelectedRows.Count > 0);
            string file = SelectedSaveFile(backup) ?? _current.CurrentWorldFile();
            if (!File.Exists(file)) { MessageBox.Show(this, "Pick a world or a backup first."); return; }
            string label = backup ? Path.GetFileNameWithoutExtension(file) : new DirectoryInfo(Path.GetDirectoryName(file)).Name;
            using (SaveFileDialog d = new SaveFileDialog { Title = "Export", Filter = "Zip|*.zip", FileName = label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int n = ExportSaveZip(file, backup, d.FileName);
                MessageBox.Show(this, "Exported " + n + " file(s) to " + d.FileName + ".\nTo play it in your own game, put world.json into your saves folder under any name.");
            }
        }

        public static int ExportSaveZip(string worldFile, bool backupOnly, string zip)
        {
            if (File.Exists(zip)) File.Delete(zip);
            int n = 0;
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                if (backupOnly) { System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, worldFile, "world.json"); return 1; }
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(worldFile)))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("backup") || name.StartsWith("world-before") || name.EndsWith(".tmp")) continue;
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, f, name);
                    n++;
                }
            }
            return n;
        }

        private void CopyBetweenSaves()
        {
            using (CopySavesForm f = new CopySavesForm(_current, SelectedSaveFile(false)))
            {
                f.ShowDialog(this);
                if (f.Done) { RefreshSaves(); ShowSave(f.Target); }
            }
        }

        private void MakeActive()
        {
            if (_worlds.SelectedRows.Count == 0) return;
            string name = (string)_worlds.SelectedRows[0].Cells["World"].Value;
            if (_current.Running)
            {
                Send("set World " + name);
                MessageBox.Show(this, "The server will load \"" + name + "\" when it restarts.");
            }
            else { Conf c = _current.LoadConf(); c.Set("World", name); c.Save(_current.ConfPath); }
            RefreshSaves();
        }

        private void NewWorld()
        {
            string name = Prompt.Ask(this, "New world", "Name for the new world (it's made when the server starts with it):", "world2");
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            if (_current.Running) { Send("set World " + name); MessageBox.Show(this, "The server makes and loads \"" + name + "\" when it restarts."); }
            else { Conf c = _current.LoadConf(); c.Set("World", name); c.Save(_current.ConfPath); MessageBox.Show(this, "\"" + name + "\" is made the next time the server starts."); }
        }

        private void ImportWorld()
        {
            List<string> mine = GameCopy.PlayerWorlds();
            using (OpenFileDialog d = new OpenFileDialog { Title = "One of your worlds (world.json)", Filter = "world.json|world.json|All json|*.json", InitialDirectory = mine.Count > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(mine[0])) : "" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string name = new DirectoryInfo(Path.GetDirectoryName(d.FileName)).Name;
                string target = Path.Combine(_current.WorldsDir, name);
                if (Directory.Exists(target)) name += "-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                target = Path.Combine(_current.WorldsDir, name);
                Directory.CreateDirectory(target);
                File.Copy(d.FileName, Path.Combine(target, "world.json"));
                MessageBox.Show(this, "Copied as \"" + name + "\". Pick it and press Make active to play it.");
                RefreshSaves();
            }
        }

        private void RestoreBackup()
        {
            if (_backups.SelectedRows.Count == 0) return;
            if (_current.Running) { MessageBox.Show(this, "Stop the server first."); return; }
            string active = _current.CurrentWorldFile();
            string backup = Path.Combine(Path.GetDirectoryName(active), (string)_backups.SelectedRows[0].Cells["Backup"].Value);
            if (MessageBox.Show(this, "Put " + Path.GetFileName(backup) + " back as the world?", "Restore", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            File.Copy(active, Path.Combine(Path.GetDirectoryName(active), "world-before-restore.json"), true);
            File.Copy(backup, active, true);
            RefreshSaves();
        }

        // ---- Ships ------------------------------------------------------------------------------------

        private DataGridView _ships, _bin;
        private Label _shipsNote, _binNote;
        private TextBox _shipFind;

        private void BuildShips(TabPage page)
        {
            _ships = Theme.NewGrid("Ship", "Id", "Parts", "Where", "Coordinates", "Height", "Speed", "Awake", "Protected");
            _ships.MultiSelect = false;
            _ships.Columns["Ship"].FillWeight = 160; _ships.Columns["Coordinates"].FillWeight = 150; _ships.Columns["Id"].FillWeight = 45; _ships.Columns["Parts"].FillWeight = 50;
            _ships.Columns["Height"].FillWeight = 55; _ships.Columns["Speed"].FillWeight = 55; _ships.Columns["Awake"].FillWeight = 50; _ships.Columns["Protected"].FillWeight = 60;
            _shipsNote = Theme.Wrap("", 900);
            _shipFind = Theme.Box("", 200);
            _shipFind.TextChanged += (s, e) => RefreshShips();
            PageColumn top = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(18, 10, 18, 0) };
            top.Controls.Add(Theme.Row(Theme.Label("Find ", Theme.Body, Theme.Muted), _shipFind,
                Theme.Button("Copy coordinates", (s, e) => CopyShipPlace(), 150),
                Theme.Button("Take players there...", (s, e) => TakePlayersToShip(), 180),
                Theme.Button("Delete...", (s, e) => DeleteShip(), 100)));
            top.Controls.Add(_shipsNote);

            _bin = Theme.NewGrid("Deleted", "Ship", "Parts", "By", "Recovered", "It held", "BinId");
            _bin.Columns["BinId"].Visible = false;
            _bin.Columns["Recovered"].FillWeight = 220; _bin.Columns["Itheld"].FillWeight = 140;
            _bin.Columns["Recovered"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _bin.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _binNote = Theme.Wrap("", 700);
            PageColumn btop = new PageColumn { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            btop.Controls.Add(Theme.Label("DELETED SHIPS (THE BIN)", Theme.Sub, Theme.Line));
            btop.Controls.Add(Theme.Row(Theme.Button("Restore where it stood", (s, e) => RestoreShip(false), 190),
                Theme.Button("Restore next to a player...", (s, e) => RestoreShip(true), 210), _binNote));

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Back, Panel1MinSize = 100, Panel2MinSize = 90 };
            split.Panel1.Padding = new Padding(18, 4, 18, 4);
            split.Panel2.Padding = new Padding(18, 0, 18, 12);
            split.Panel1.Controls.Add(_ships);
            split.Panel2.Controls.Add(_bin); split.Panel2.Controls.Add(btop);
            Theme.Remember(split, "ships.split", 330);
            page.Controls.Add(split); page.Controls.Add(top);
        }

        private static string Coords(JsonNode w)
        {
            if (w == null || w["x"] == null) return "";
            return Json.Num(w, "x").ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ", " + Json.Num(w, "y").ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ", " + Json.Num(w, "z").ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string _savesSig, _shipsSig, _usersSig, _bansSig;

        private void RefreshShips()
        {
            JsonNode sh = _current.Ships;
            string sig = _current.Folder + "|" + _shipFind.Text + "|" + (sh != null ? sh.ToJsonString() : "");
            if (sig == _shipsSig) return;
            _shipsSig = sig;
            int keep = SelectedShipId();
            int scroll = _ships.FirstDisplayedScrollingRowIndex;
            _ships.SuspendLayout();
            GridRebuilds++;
            _ships.Rows.Clear();
            if (sh == null) _shipsNote.Text = "The ship list appears while the server runs (it's written every 15 s).";
            else
            {
                List<JsonNode> ships = Json.Arr(sh, "ships").ToList();
                string find = _shipFind.Text.Trim();
                // each assembly together: its root, then what is docked to it
                foreach (var group in ships.GroupBy(x => Json.Int(x, "root") != 0 ? Json.Int(x, "root") : Json.Int(x, "id")).OrderByDescending(g => g.Sum(x => Json.Int(x, "parts"))))
                    foreach (JsonNode s in group.OrderBy(x => Json.Int(x, "id") == group.Key ? 0 : 1).ThenByDescending(x => Json.Int(x, "parts")))
                    {
                        int id = Json.Int(s, "id");
                        bool child = id != group.Key;
                        string name = Json.Str(s, "name");
                        if (find.Length > 0 && name.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0 && id.ToString() != find.TrimStart('#')) continue;
                        JsonNode w = s["where"];
                        string where = w != null && w["planet"] != null ? Json.Str(w, "planet") : w != null && w["space"] != null ? "in " + Json.Str(w, "space") : Json.Str(s, "near");
                        int i = _ships.Rows.Add((child ? "    docked: " : "") + (name.Length > 0 ? name : "(no beacon)"), id, Json.Int(s, "parts"), where, Coords(w),
                            w != null && w["height"] != null ? Json.Num(w, "height").ToString("0") + " m" : "", Json.Num(s, "speed").ToString("0.0") + " m/s", Json.Bool(s, "loaded") ? "yes" : "asleep", Json.Bool(s, "protected") ? "yes" : "");
                        _ships.Rows[i].Tag = s;
                        if (name.Length == 0) _ships.Rows[i].Cells["Ship"].Style.ForeColor = Theme.Muted;
                        if (id == keep) _ships.Rows[i].Selected = true;
                    }
                _shipsNote.Text = ships.Count + " ship(s), " + ships.Sum(x => Json.Int(x, "parts")) + " parts in all. Named from each ship's beacon. As of " + Json.Date(sh, "writtenUtc").ToLocalTime().ToString("HH:mm:ss")
                    + (_current.Running ? "" : " (the last time the server ran; start it to take players there or delete ships)") + ". Coordinates are metres from the planet's centre.";
            }
            if (scroll >= 0 && scroll < _ships.Rows.Count) _ships.FirstDisplayedScrollingRowIndex = scroll;
            _ships.ResumeLayout();
            RefreshBin();
        }

        private int SelectedShipId() { return _ships.SelectedRows.Count > 0 && _ships.SelectedRows[0].Cells["Id"].Value is int id ? id : 0; }

        private void RefreshBin()
        {
            string keep = _bin.SelectedRows.Count > 0 ? (string)_bin.SelectedRows[0].Cells["BinId"].Value : null;
            _bin.Rows.Clear();
            int n = 0;
            if (Directory.Exists(_current.BinDir))
                foreach (FileInfo f in new DirectoryInfo(_current.BinDir).GetFiles("*.json").OrderByDescending(x => x.Name))
                {
                    JsonNode o = Json.ReadFile(f.FullName);
                    if (o == null) continue;
                    n++;
                    Func<string, string> list = k => { JsonObject m = o[k] as JsonObject; return m == null || m.Count == 0 ? "" : string.Join(", ", m.Select(kv => kv.Value + " " + kv.Key)); };
                    int i = _bin.Rows.Add(Json.Date(o, "deletedUtc").ToLocalTime().ToString("yyyy-MM-dd HH:mm"), (Json.Str(o, "name").Length > 0 ? Json.Str(o, "name") : "ship #" + Json.Str(o, "shipId")) + "\n" + Json.Str(o, "whereText"),
                        Json.Int(o, "parts"), Json.Str(o, "by"), list("materials"), list("stored"), Json.Str(o, "id"));
                    if (Json.Str(o, "id") == keep) _bin.Rows[i].Selected = true;
                }
            _bin.Columns["Ship"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            int days = _current.LoadConf().GetInt("ShipBinDays");
            _binNote.Text = n == 0 ? "Nothing deleted." : n + " ship(s); kept " + (days > 0 ? days + " days (ShipBinDays)" : "until removed") + ". Recovered = what the parts are made of.";
        }

        /// <summary>A console command and the server's answer (what it writes to console-output.txt after it).</summary>
        private string AskServer(string cmd, double seconds = 5)
        {
            if (!_current.Running) return null;
            long from = File.Exists(_current.OutputPath) ? new FileInfo(_current.OutputPath).Length : 0;
            _current.Command(cmd);
            DateTime until = DateTime.Now.AddSeconds(seconds), quietSince = DateTime.MaxValue;
            string got = "";
            Cursor = Cursors.WaitCursor;
            try
            {
                while (DateTime.Now < until)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                    string all = Json.ReadShared(_current.OutputPath);
                    string now = all.Length > from ? all.Substring((int)from) : "";
                    List<string> lines = now.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.Contains("> " + cmd)).ToList();
                    if (lines.Count > 0)
                    {
                        string text = string.Join("\n", lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d\d:\d\d:\d\d\s+", "")));
                        if (text != got) { got = text; quietSince = DateTime.Now; }
                        else if ((DateTime.Now - quietSince).TotalSeconds > 0.4) break;      // the whole answer is in
                    }
                }
            }
            finally { Cursor = Cursors.Default; }
            return got;
        }

        private bool NeedRunning(string what)
        {
            if (_current.Running) return true;
            MessageBox.Show(this, "Start the server first: " + what + " happens in the running world.", "Ships");
            return false;
        }

        private void CopyShipPlace()
        {
            if (_ships.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a ship first."); return; }
            JsonNode s = _ships.SelectedRows[0].Tag as JsonNode;
            JsonNode w = s?["where"];
            string text = "ship #" + Json.Int(s, "id") + (Json.Str(s, "name").Length > 0 ? " \"" + Json.Str(s, "name") + "\"" : "") + ": "
                + (w != null && w["planet"] != null ? Json.Str(w, "planet") + " " : "") + Coords(w) + (w != null && w["height"] != null ? " (" + Json.Num(w, "height").ToString("0") + " m up)" : "");
            try { Clipboard.SetText(text); _shipsNote.Text = "Copied: " + text; } catch (Exception e) { MessageBox.Show(this, e.Message); }
        }

        private void TakePlayersToShip()
        {
            int id = SelectedShipId();
            if (id == 0) { MessageBox.Show(this, "Pick a ship first."); return; }
            if (!NeedRunning("taking players to a ship")) return;
            List<string> online = Json.Arr(_current.Status, "players").Select(p => Json.Str(p, "name")).Where(n => n.Length > 0).ToList();
            if (online.Count == 0) { MessageBox.Show(this, "Nobody is on."); return; }
            List<string> who = PickPlayers("Take players to ship #" + id, "Who goes to ship #" + id + "? They are put on top of it.", online);
            if (who == null || who.Count == 0) return;
            List<string> replies = new List<string>();
            foreach (string n in who.Count == online.Count ? new List<string> { "all" } : who) replies.Add(AskServer("ship tp " + Quoted(n) + " " + id) ?? "");
            _shipsNote.Text = string.Join(" / ", replies.Where(r => r.Length > 0));
        }

        /// <summary>A name the console reads as one word (the server matches the start of a name).</summary>
        private static string Quoted(string name) { return name.Contains(" ") ? name.Split(' ')[0] : name; }

        private void DeleteShip()
        {
            if (_ships.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a ship first."); return; }
            if (!NeedRunning("deleting a ship")) return;
            JsonNode s = _ships.SelectedRows[0].Tag as JsonNode;
            int id = Json.Int(s, "id");
            string name = Json.Str(s, "name");
            int days = _current.LoadConf().GetInt("ShipBinDays");
            if (MessageBox.Show(this, "Delete " + (name.Length > 0 ? "\"" + name + "\" " : "") + "(ship #" + id + ", " + Json.Int(s, "parts") + " parts) and every ship docked to it?\n\n"
                + "It goes to the bin below and can be restored" + (days > 0 ? " for " + days + " days" : "") + ". Nobody may be aboard.", "Delete a ship", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string reply = AskServer("ship delete " + id, 8) ?? "";
            MessageBox.Show(this, reply.Length > 0 ? reply : "The server didn't answer.", "Delete a ship");
            _current.Forget(_current.ShipsPath);
            RefreshShips();
        }

        private void RestoreShip(bool nearPlayer)
        {
            if (_bin.SelectedRows.Count == 0) { MessageBox.Show(this, "Pick a deleted ship first."); return; }
            if (!NeedRunning("restoring a ship")) return;
            string bin = (string)_bin.SelectedRows[0].Cells["BinId"].Value;
            string cmd = "ship restore " + bin;
            if (nearPlayer)
            {
                List<string> online = Json.Arr(_current.Status, "players").Select(p => Json.Str(p, "name")).Where(n => n.Length > 0).ToList();
                if (online.Count == 0) { MessageBox.Show(this, "Nobody is on."); return; }
                List<string> who = PickPlayers("Restore next to a player", "Next to whom? (It is put 12 m in front of them.)", online, true);
                if (who == null || who.Count == 0) return;
                cmd += " here " + Quoted(who[0]);
            }
            string reply = AskServer(cmd, 8) ?? "";
            MessageBox.Show(this, reply.Length > 0 ? reply : "The server didn't answer.", "Restore a ship");
            RefreshShips();
        }

        /// <summary>Tick the players (or pick one).</summary>
        private List<string> PickPlayers(string title, string text, List<string> names, bool one = false)
        {
            using (Form f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(14) })
            {
                FlowLayoutPanel col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
                col.Controls.Add(Theme.Wrap(text, 380, Theme.Body, Theme.Text));
                List<CheckBox> boxes = new List<CheckBox>();
                List<RadioButton> radios = new List<RadioButton>();
                foreach (string n in names)
                {
                    if (one) { RadioButton r = new RadioButton { Text = n, AutoSize = true, Checked = radios.Count == 0, BackColor = Color.Transparent }; radios.Add(r); col.Controls.Add(r); }
                    else { CheckBox c = Theme.Check(n, true); boxes.Add(c); col.Controls.Add(c); }
                }
                List<string> result = null;
                Button ok = Theme.Button("OK", (s, e) =>
                {
                    result = one ? radios.Where(r => r.Checked).Select(r => r.Text).ToList() : boxes.Where(c => c.Checked).Select(c => c.Text).ToList();
                    f.DialogResult = DialogResult.OK; f.Close();
                }, 90);
                col.Controls.Add(Theme.Row(ok, Theme.Button("Cancel", (s, e) => f.Close(), 90)));
                f.Controls.Add(col);
                f.AcceptButton = ok;
                Theme.Apply(f);
                return f.ShowDialog(this) == DialogResult.OK ? result : null;
            }
        }

#if SDSMTEST
        public string AskServerForTest(string cmd, double seconds = 8) { return AskServer(cmd, seconds); }
        public int ShipRowsForTest { get { return _ships.Rows.Count; } }
        public string ShipNameForTest(int id) { foreach (DataGridViewRow r in _ships.Rows) if (r.Cells["Id"].Value is int x && x == id) return Convert.ToString(r.Cells["Ship"].Value); return null; }
        public int BinRowsForTest { get { return _bin.Rows.Count; } }
#endif
    }

    /// <summary>A one-line question.</summary>
    public static class Prompt
    {
        public static string Ask(IWin32Window owner, string title, string text, string value)
        {
            using (Form f = new Form { Text = title, Size = new Size(520, 190), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            {
                Label l = Theme.Wrap(text, 470, Theme.Body, Theme.Text); l.Location = new Point(14, 12);
                TextBox t = Theme.Box(value, 470); t.Location = new Point(14, 60);
                Button ok = Theme.Button("OK", (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); }, 90); ok.Location = new Point(300, 100);
                Button cancel = Theme.Button("Cancel", (s, e) => f.Close(), 90); cancel.Location = new Point(396, 100);
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok;
                Theme.Apply(f);
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }
    }
}
