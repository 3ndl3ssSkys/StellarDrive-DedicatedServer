// How the server's messages look in the players' chat, and messages sent on a timer.
//
// The game's chat is TextMeshPro, so colour, size, bold, italic and underline are tags in the text
// (<color=#4FC3F7>, <size=115%>, <b>, ...). Each kind of server message has a style setting:
//   StyleServer    replies to commands, and short notes          e.g. "color=#8FD3FF"
//   StyleAnnounce  `say` and announcements                        e.g. "color=#4FC3F7 bold size=115"
//   StyleAlert     restarts, shutdowns, `alert`                    e.g. "color=#FFB84D bold size=120"
//   StyleWelcome   the welcome and the message of the day          e.g. "color=#9BE7A0 bold"
// A style is a list of: color=<#rrggbb or a name>, bold, italic, underline, strike, caps, smallcaps,
// size=<percent> and highlight=<colour> (drawn see-through behind the text).
// Any one message can carry its own in braces at the start: `say {color=red bold size=130} Hello`.
//
// Players' own chat could carry the same tags and pass for the server, so tags in what players type
// are shown as typed (<noparse>), unless PlayerChatFormatting is on.
//
// `restart [minutes] [reason]` and `stop [minutes] [reason]` warn everyone (10, 5, 2 and 1 minutes,
// 30 and 10 seconds before), then save and stop. A restart exits with code 3 and writes "restarting"
// to status.json: the Server Manager starts the server again, and so does StartDedicatedServer.bat.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Chat
    {
        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "white", "#FFFFFF" }, { "grey", "#B8C2CC" }, { "gray", "#B8C2CC" }, { "red", "#FF6B5E" }, { "orange", "#FFB84D" },
            { "yellow", "#FFE16B" }, { "green", "#9BE7A0" }, { "teal", "#5FE0D0" }, { "blue", "#4FC3F7" }, { "lightblue", "#8FD3FF" },
            { "purple", "#C79BFF" }, { "pink", "#FF9BD2" },
        };

        /// <summary>The text wrapped in a style's tags. A bad style word is left out, never shown.</summary>
        public static string Format(string spec, string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string open, close;
            Tags(spec, out open, out close);
            return open + text + close;
        }

        /// <summary>A style's opening and closing tags.</summary>
        public static void Tags(string spec, out string open, out string close)
        {
            open = ""; close = "";
            foreach (string word in Words(spec))
            {
                string t = word.Trim();
                string lower = t.ToLowerInvariant();
                string v = t.Contains("=") ? t.Substring(t.IndexOf('=') + 1).Trim('"') : "";
                if (lower.StartsWith("color=") || lower.StartsWith("colour="))
                {
                    string c = Color(v.ToLowerInvariant());
                    if (c != null) { open += "<color=" + c + ">"; close = "</color>" + close; }
                }
                else if (lower.StartsWith("size="))
                {
                    int n;
                    if (int.TryParse(v.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 50 && n <= 250) { open += "<size=" + n + "%>"; close = "</size>" + close; }
                }
                else if (lower.StartsWith("font="))
                {
                    string f = FontName(v);
                    if (f != null) { open += "<font=\"" + f + "\">"; close = "</font>" + close; }
                }
                else if (lower == "bold" || lower == "b") { open += "<b>"; close = "</b>" + close; }
                else if (lower == "italic" || lower == "i") { open += "<i>"; close = "</i>" + close; }
                else if (lower == "underline" || lower == "u") { open += "<u>"; close = "</u>" + close; }
                else if (lower == "strike" || lower == "s") { open += "<s>"; close = "</s>" + close; }
                else if (lower == "caps" || lower == "upper" || lower == "uppercase") { open += "<uppercase>"; close = "</uppercase>" + close; }
                else if (lower == "smallcaps") { open += "<smallcaps>"; close = "</smallcaps>" + close; }
                else if (lower.StartsWith("highlight=") || lower.StartsWith("mark="))
                {
                    string c = Color(v.ToLowerInvariant());
                    if (c != null) { open += "<mark=" + (c.Length == 7 ? c + "55" : c) + ">"; close = "</mark>" + close; }
                }
                else if (Names.ContainsKey(lower) || (lower.StartsWith("#") && Color(lower) != null))
                {
                    // a bare colour: {red}
                    open += "<color=" + Color(lower) + ">"; close = "</color>" + close;
                }
            }
        }

        /// <summary>A style's words; font="Some Name" may have spaces.</summary>
        private static IEnumerable<string> Words(string spec)
        {
            List<string> l = new List<string>();
            foreach (Match m in Regex.Matches(spec ?? "", "\\w+=\"[^\"]*\"|[^\\s,;]+")) l.Add(m.Value);
            return l;
        }

        // ---- fonts ----------------------------------------------------------------------------
        //
        // The game ships a few TextMeshPro fonts. A style can use one by (part of) its name:
        // font=exo, font="Exo2-Bold SDF". Players' games register the same fonts so the tag works.

        private static List<string> _fonts;

        public static List<string> Fonts
        {
            get
            {
                if (_fonts != null) return _fonts;
                _fonts = new List<string>();
                try
                {
                    foreach (TMPro.TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
                        if (f != null && !string.IsNullOrEmpty(f.name) && !_fonts.Contains(f.name)) _fonts.Add(f.name);
                    _fonts.Sort(StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                if (_fonts.Count == 0) _fonts = null;
                return _fonts ?? new List<string>();
            }
        }

        private static string Squash(string s) { return Regex.Replace((s ?? "").ToLowerInvariant().Replace(" sdf", ""), "[^a-z0-9]", ""); }

        /// <summary>The game font a name (or part of one) means, or null.</summary>
        public static string FontName(string want)
        {
            if (string.IsNullOrEmpty(want) || !Regex.IsMatch(want, "^[A-Za-z0-9 _.()+-]{1,64}$")) return null;
            List<string> fonts = Fonts;
            string exact = fonts.FirstOrDefault(f => f.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            string w = Squash(want);
            if (w.Length == 0) return null;
            return fonts.FirstOrDefault(f => Squash(f) == w) ?? fonts.FirstOrDefault(f => Squash(f).StartsWith(w)) ?? fonts.FirstOrDefault(f => Squash(f).Contains(w));
        }

        // ---- styles inside a message ---------------------------------------------------------
        //
        // In anything the server or an admin writes (say, alert, announce, the message of the day,
        // restart reasons), {style}text{/} styles part of it: "Meet at {yellow bold}the station{/} now".
        // {/} closes the last one; anything still open is closed at the end. {{ is a plain {.

        public static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text ?? "";
            StringBuilder sb = new StringBuilder();
            Stack<string> open = new Stack<string>();
            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                if (ch == '{')
                {
                    int end = text.IndexOf('}', i + 1);
                    if (end < 0) { sb.Append(text.Substring(i)); break; }
                    string inner = text.Substring(i + 1, end - i - 1).Trim();
                    if (inner == "/" || inner == "") { if (open.Count > 0) sb.Append(open.Pop()); }
                    else
                    {
                        string o, c;
                        Tags(inner, out o, out c);
                        if (o.Length == 0) sb.Append(text, i, end - i + 1);      // not a style: leave it as typed
                        else { sb.Append(o); open.Push(c); }
                    }
                    i = end + 1;
                    continue;
                }
                sb.Append(ch);
                i++;
            }
            while (open.Count > 0) sb.Append(open.Pop());
            return sb.ToString();
        }

        // ---- players' lines and @mentions -----------------------------------------------------

        /// <summary>
        /// What a player said, ready for chat: shown as typed (unless PlayerChatFormatting), each @name
        /// of a player who is on in the mention style. 'mentioned' gets those players' names.
        /// </summary>
        public static string PlayerLine(string said, IEnumerable<string> online, List<string> mentioned)
        {
            said = said ?? "";
            List<string> names = online.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
            StringBuilder sb = new StringBuilder();
            int last = 0, i = said.IndexOf('@');
            while (i >= 0)
            {
                string hit = null;
                foreach (string n in names)
                    if (i + 1 + n.Length <= said.Length && string.Compare(said, i + 1, n, 0, n.Length, StringComparison.OrdinalIgnoreCase) == 0
                        && (i + 1 + n.Length == said.Length || !char.IsLetterOrDigit(said[i + 1 + n.Length])))
                    { hit = n; break; }
                if (hit == null && i + 9 <= said.Length && string.Compare(said, i + 1, "everyone", 0, 8, StringComparison.OrdinalIgnoreCase) == 0) hit = "everyone";
                if (hit != null)
                {
                    sb.Append(FromPlayer(said.Substring(last, i - last)));
                    sb.Append(Format(DedicatedServer.Server.Cfg.StyleMention.Value, "@" + hit.Replace("<", "\u2039")));
                    if (mentioned != null && !mentioned.Contains(hit)) mentioned.Add(hit);
                    last = i + 1 + hit.Length;
                    i = said.IndexOf('@', last);
                }
                else i = said.IndexOf('@', i + 1);
            }
            sb.Append(FromPlayer(said.Substring(last)));
            return Format(DedicatedServer.Server.Cfg.StylePlayerText.Value, sb.ToString());
        }

        public static string Whisper(string text) { return Format(DedicatedServer.Server.Cfg.StyleWhisper.Value, text); }

        /// <summary>#rrggbb (or #rgb, or a colour's name), or null.</summary>
        public static string Color(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            string named;
            if (Names.TryGetValue(v, out named)) return named;
            if (!v.StartsWith("#")) v = "#" + v;
            return Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$") ? v.ToUpperInvariant() : null;
        }

        /// <summary>"{bold color=red} Hello" -> spec "bold color=red", text "Hello". No braces: the default.</summary>
        public static string SplitSpec(string message, string fallback, out string text)
        {
            text = message ?? "";
            string m = text.TrimStart();
            if (m.StartsWith("{"))
            {
                int close = m.IndexOf('}');
                if (close > 0) { text = m.Substring(close + 1).TrimStart(); return m.Substring(1, close - 1); }
            }
            return fallback;
        }

        /// <summary>What a player typed, shown exactly as typed (tags and all), unless formatting is allowed.</summary>
        public static string FromPlayer(string text)
        {
            if (DedicatedServer.Server.Cfg.PlayerChatFormatting.Value || string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            return "<noparse>" + Regex.Replace(text, "</?noparse>", "", RegexOptions.IgnoreCase) + "</noparse>";
        }

        public static string Server(string text) { return Format(DedicatedServer.Server.Cfg.StyleServer.Value, text); }
        public static string Announce(string text) { return Format(DedicatedServer.Server.Cfg.StyleAnnounce.Value, Prefix() + Inline(text)); }
        public static string Alert(string text) { return Format(DedicatedServer.Server.Cfg.StyleAlert.Value, Prefix() + Inline(text)); }
        public static string Welcome(string text) { return Format(DedicatedServer.Server.Cfg.StyleWelcome.Value, Inline(text)); }

        /// <summary>Shown in the middle of every player's screen too (over menus; Enter closes it).</summary>
        public static void OnScreen(string richText)
        {
            Net.EventAll("announce", richText, DedicatedServer.Server.Cfg.OnScreenSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>An alert in chat, and on screen when AlertsOnScreen is on.</summary>
        public static void AlertAll(string text)
        {
            DedicatedServer.Server.SayAll(Alert(text));
            if (DedicatedServer.Server.Cfg.AlertsOnScreen.Value) OnScreen(Format(DedicatedServer.Server.Cfg.StyleAlert.Value, Inline(text)));
        }

        private static string Prefix()
        {
            string p = DedicatedServer.Server.Cfg.MessagePrefix.Value ?? "";
            return p.Length > 0 ? p + " " : "";
        }

        // ---- stopping and restarting on a timer ---------------------------------------------

        private static float _at = -1f;          // realtime when it happens
        private static bool _restart;
        private static string _reason = "";
        private static int[] Warnings = { 600, 300, 120, 60, 30, 10 };      // RestartWarnings, read when a countdown starts
        private static int _nextWarning;

        public static bool Pending { get { return _at > 0f; } }
        public static bool PendingRestart { get { return _restart; } }
        public static string PendingReason { get { return _reason; } }
        public static float SecondsLeft { get { return Pending ? Mathf.Max(0f, _at - Time.realtimeSinceStartup) : 0f; } }

        public static string Schedule(bool restart, float seconds, string reason)
        {
            _restart = restart; _reason = (reason ?? "").Trim();
            _at = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            Warnings = Restarts.Warnings();
            _nextWarning = 0;
            while (_nextWarning < Warnings.Length && Warnings[_nextWarning] > seconds + 0.5f) _nextWarning++;
            string what = restart ? "restart" : "shut down";
            // the first word goes out now: the warning for this time when there is one (a planned restart starts at its first warning)
            int slot = _nextWarning < Warnings.Length && Mathf.Abs(Warnings[_nextWarning] - seconds) < 1f ? Warnings[_nextWarning] : -1;
            Warn(slot, seconds);
            Log.Msg("the server will " + what + " in " + Span(seconds) + (_reason.Length > 0 ? " (" + _reason + ")" : ""));
            if (slot >= 0) _nextWarning++;
            return "The server will " + what + " in " + Span(seconds) + ". `" + (restart ? "restart" : "stop") + " cancel` to call it off.";
        }

        public static string Cancel()
        {
            if (!Pending) return "Nothing is scheduled.";
            _at = -1f;
            AlertAll("The " + (_restart ? "restart" : "shutdown") + " is called off.");
            return "Called off.";
        }

        /// <summary>Every frame on the server.</summary>
        public static void Update()
        {
            if (!Pending) return;
            float left = _at - Time.realtimeSinceStartup;
            if (_nextWarning < Warnings.Length && left <= Warnings[_nextWarning])
            {
                // (several due at once after a stall: only the latest is said)
                while (_nextWarning + 1 < Warnings.Length && left <= Warnings[_nextWarning + 1]) _nextWarning++;
                Warn(Warnings[_nextWarning], left);
                _nextWarning++;
            }
            if (left > 0f) return;
            _at = -1f;
            DedicatedServer.Server.Stop((_restart ? "scheduled restart" : "scheduled stop") + (_reason.Length > 0 ? ": " + _reason : ""), _restart);
        }

        public static int WarningsSent;
        public static string LastWarning = "";

        /// <summary>A warning, in the owner's words for this time (RestartMessages) or the built-in ones; [time] is what's really left.</summary>
        private static void Warn(int slot, float left)
        {
            Restarts.Message m = null;
            try { m = Restarts.MessageFor(slot >= 0 ? slot : Mathf.RoundToInt(left)); } catch { }
            string what = _restart ? "restart" : "shut down";
            string text;
            bool screen = DedicatedServer.Server.Cfg.AlertsOnScreen.Value;
            if (m != null && m.Text.Length > 0)
            {
                text = Fill(m.Text, left, what);
                screen = m.OnScreen;
            }
            else text = "The server will " + what + " in " + Span(left) + (_reason.Length > 0 ? ": " + _reason : ".");
            DedicatedServer.Server.SayAll(Alert(text));
            if (screen) OnScreen(Format(DedicatedServer.Server.Cfg.StyleAlert.Value, Inline(text)));
            WarningsSent++;
            LastWarning = text + (screen ? " [screen]" : "");
            Log.Msg("warned: " + text + (screen ? " (also on screen)" : ""));
        }

        /// <summary>[time], [reason], [action] filled in (any case).</summary>
        public static string Fill(string template, float left, string action)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(template, @"\[time\]", Span(left), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[reason\]", _reason.Length > 0 ? _reason : (_restart ? "a restart" : "a shutdown"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return System.Text.RegularExpressions.Regex.Replace(s, @"\[action\]", action, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static string Span(float s)
        {
            int n = Mathf.RoundToInt(s);
            if (n >= 120) return (n / 60) + " minutes" + (n % 60 != 0 ? " " + (n % 60) + " s" : "");
            if (n >= 60) return "1 minute" + (n % 60 != 0 ? " " + (n % 60) + " s" : "");
            return n + " seconds";
        }
    }
}
