// How a server message looks in the players' chat: the picker behind the Launch page's message box
// and the Settings page's Style* rows.
//
// A style is the server's own spec (see Chat.cs in the mod): "color=#4FC3F7 bold size=115", with
// italic, underline, strike, caps, smallcaps and highlight=<colour> too. The picker writes exactly
// that, and its preview draws the text roughly as the game's chat will (the game's font differs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SDServerManager
{
    public class StyleSpec
    {
        public Color? Color, Highlight;
        public bool Bold, Italic, Underline, Strike, Caps, SmallCaps;
        public int Size = 100;

        // the same names the server knows
        public static readonly (string Name, string Hex)[] Colors =
        {
            ("white", "#FFFFFF"), ("grey", "#B8C2CC"), ("red", "#FF6B5E"), ("orange", "#FFB84D"), ("yellow", "#FFE16B"), ("green", "#9BE7A0"),
            ("teal", "#5FE0D0"), ("blue", "#4FC3F7"), ("lightblue", "#8FD3FF"), ("purple", "#C79BFF"), ("pink", "#FF9BD2"),
        };

        public static readonly (string Name, int Percent)[] Sizes = { ("Small", 85), ("Normal", 100), ("Bigger", 115), ("Big", 130), ("Huge", 160) };

        public static Color? ParseColor(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var named = Colors.FirstOrDefault(c => c.Name.Equals(v, StringComparison.OrdinalIgnoreCase) || (v.Equals("gray", StringComparison.OrdinalIgnoreCase) && c.Name == "grey"));
            if (named.Hex != null) v = named.Hex;
            v = v.TrimStart('#');
            if (v.Length == 3) v = string.Concat(v.Select(ch => new string(ch, 2)));
            if (v.Length == 8) v = v.Substring(0, 6);
            int n;
            if (v.Length != 6 || !int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return null;
            return System.Drawing.Color.FromArgb((n >> 16) & 255, (n >> 8) & 255, n & 255);
        }

        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static StyleSpec Parse(string spec)
        {
            StyleSpec s = new StyleSpec();
            foreach (string raw in (spec ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                string v = t.Contains("=") ? t.Substring(t.IndexOf('=') + 1) : "";
                if (t.StartsWith("color=") || t.StartsWith("colour=")) s.Color = ParseColor(v);
                else if (t.StartsWith("highlight=") || t.StartsWith("mark=")) s.Highlight = ParseColor(v);
                else if (t.StartsWith("size=")) { int n; if (int.TryParse(v.TrimEnd('%'), out n) && n >= 50 && n <= 250) s.Size = n; }
                else if (t == "bold" || t == "b") s.Bold = true;
                else if (t == "italic" || t == "i") s.Italic = true;
                else if (t == "underline" || t == "u") s.Underline = true;
                else if (t == "strike" || t == "s") s.Strike = true;
                else if (t == "caps" || t == "upper" || t == "uppercase") s.Caps = true;
                else if (t == "smallcaps") s.SmallCaps = true;
            }
            return s;
        }

        public override string ToString()
        {
            List<string> p = new List<string>();
            if (Color.HasValue) p.Add("color=" + Hex(Color.Value));
            if (Bold) p.Add("bold");
            if (Italic) p.Add("italic");
            if (Underline) p.Add("underline");
            if (Strike) p.Add("strike");
            if (Caps) p.Add("caps");
            if (SmallCaps) p.Add("smallcaps");
            if (Size != 100) p.Add("size=" + Size);
            if (Highlight.HasValue) p.Add("highlight=" + Hex(Highlight.Value));
            return string.Join(" ", p);
        }

        /// <summary>The text drawn about as the game's chat will draw it.</summary>
        public void Draw(Graphics g, Rectangle r, string text, Color fallback)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            FontStyle fs = FontStyle.Regular;
            if (Bold) fs |= FontStyle.Bold;
            if (Italic) fs |= FontStyle.Italic;
            if (Underline) fs |= FontStyle.Underline;
            if (Strike) fs |= FontStyle.Strikeout;
            string shown = Caps || SmallCaps ? (text ?? "").ToUpperInvariant() : text ?? "";
            float pt = 10.5f * Size / 100f * (SmallCaps && !Caps ? 0.85f : 1f);
            using (Font f = new Font("Segoe UI", pt, fs))
            {
                Size sz = TextRenderer.MeasureText(g, shown, f, new Size(r.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                Rectangle at = new Rectangle(r.X, r.Y + Math.Max(0, (r.Height - sz.Height) / 2), Math.Min(r.Width, sz.Width + 2), Math.Min(r.Height, sz.Height));
                if (Highlight.HasValue) using (SolidBrush b = new SolidBrush(System.Drawing.Color.FromArgb(85, Highlight.Value))) g.FillRectangle(b, at);
                TextRenderer.DrawText(g, shown, f, at, Color ?? fallback, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>A preview of a style on the chat's dark background.</summary>
    public class StylePreview : Control, IFitWidth
    {
        public void FitWidth(int width) { if (Width != width) { Width = width; Invalidate(); } }
        public string Spec = "", Sample = "Sample message";
        public StylePreview() { DoubleBuffered = true; Height = 46; BackColor = Color.FromArgb(14, 20, 30); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Dim)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            StyleSpec.Parse(Spec).Draw(e.Graphics, new Rectangle(10, 4, Width - 20, Height - 8), Sample, Color.White);
        }
    }

    /// <summary>Colour, size and text options for a style, with a preview.</summary>
    public class StylePicker : FlowLayoutPanel
    {
        public event EventHandler Changed;
        private readonly StylePreview _preview = new StylePreview();
        private readonly FlowLayoutPanel _swatches = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 4), BackColor = Color.Transparent };
        private readonly ComboBox _size, _highlight;
        private readonly CheckBox _bold, _italic, _underline, _strike, _caps, _smallcaps;
        private StyleSpec _spec = new StyleSpec();
        private bool _loading;

        public StylePicker(int width)
        {
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoSize = true; BackColor = Color.Transparent;
            _swatches.MaximumSize = new Size(width, 0);
            _swatches.Controls.Add(Swatch(null, "Game's own colour"));
            foreach (var c in StyleSpec.Colors) _swatches.Controls.Add(Swatch(StyleSpec.ParseColor(c.Hex), c.Name));
            Button custom = Theme.Button("Other...", (s, e) =>
            {
                using (ColorDialog d = new ColorDialog { FullOpen = true, Color = _spec.Color ?? Color.White })
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { _spec.Color = d.Color; Changed2(); }
            }, 84);
            custom.Height = 26; custom.Margin = new Padding(4, 0, 0, 0);
            _swatches.Controls.Add(custom);

            _bold = Box("Bold"); _italic = Box("Italic"); _underline = Box("Underline"); _strike = Box("Strikethrough"); _caps = Box("CAPITALS"); _smallcaps = Box("Small capitals");
            FlowLayoutPanel checks = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(width, 0), BackColor = Color.Transparent };
            checks.Controls.AddRange(new Control[] { _bold, _italic, _underline, _strike, _caps, _smallcaps });

            _size = Theme.Choice(StyleSpec.Sizes.Select(x => x.Name + " (" + x.Percent + "%)").ToArray(), "", 150);
            _size.SelectedIndexChanged += (s, e) => { if (_loading || _size.SelectedIndex < 0) return; _spec.Size = StyleSpec.Sizes[_size.SelectedIndex].Percent; Changed2(); };
            _highlight = Theme.Choice(new[] { "none" }.Concat(StyleSpec.Colors.Select(c => c.Name)).ToArray(), "none", 130);
            _highlight.SelectedIndexChanged += (s, e) => { if (_loading) return; _spec.Highlight = _highlight.SelectedIndex <= 0 ? (Color?)null : StyleSpec.ParseColor(StyleSpec.Colors[_highlight.SelectedIndex - 1].Hex); Changed2(); };
            FlowLayoutPanel row = Theme.Row(Theme.Label("Size ", Theme.Body, Theme.Muted), _size, Theme.Label("   Highlight ", Theme.Body, Theme.Muted), _highlight);

            _preview.Width = width;
            Controls.Add(Theme.Label("Colour", Theme.Body, Theme.Muted));
            Controls.Add(_swatches);
            Controls.Add(checks);
            Controls.Add(row);
            Controls.Add(_preview);
        }

        public string Sample { get { return _preview.Sample; } set { _preview.Sample = string.IsNullOrEmpty(value) ? "Sample message" : value; _preview.Invalidate(); } }

        public string Spec
        {
            get { return _spec.ToString(); }
            set
            {
                _loading = true;
                _spec = StyleSpec.Parse(value);
                _bold.Checked = _spec.Bold; _italic.Checked = _spec.Italic; _underline.Checked = _spec.Underline; _strike.Checked = _spec.Strike; _caps.Checked = _spec.Caps; _smallcaps.Checked = _spec.SmallCaps;
                int si = Array.FindIndex(StyleSpec.Sizes, x => x.Percent == _spec.Size);
                if (si < 0) { si = Array.FindIndex(StyleSpec.Sizes, x => x.Percent >= _spec.Size); if (si < 0) si = StyleSpec.Sizes.Length - 1; }
                _size.SelectedIndex = si;
                int hi = _spec.Highlight.HasValue ? Array.FindIndex(StyleSpec.Colors, c => StyleSpec.ParseColor(c.Hex) == _spec.Highlight) + 1 : 0;
                _highlight.SelectedIndex = Math.Max(0, hi);
                _loading = false;
                _preview.Spec = _spec.ToString(); _preview.Invalidate(); _swatches.Invalidate(true);
            }
        }

        private void Changed2()
        {
            _preview.Spec = _spec.ToString(); _preview.Invalidate(); _swatches.Invalidate(true);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private CheckBox Box(string t)
        {
            CheckBox cb = Theme.Check(t, false);
            cb.Margin = new Padding(0, 4, 14, 2);
            cb.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _spec.Bold = _bold.Checked; _spec.Italic = _italic.Checked; _spec.Underline = _underline.Checked; _spec.Strike = _strike.Checked;
                _spec.Caps = _caps.Checked; _spec.SmallCaps = _smallcaps.Checked;
                Changed2();
            };
            return cb;
        }

        private Control Swatch(Color? c, string tip)
        {
            Panel p = new Panel { Size = new Size(26, 26), Margin = new Padding(0, 0, 4, 0), Cursor = Cursors.Hand, BackColor = c ?? Theme.Panel };
            new ToolTip().SetToolTip(p, tip);
            p.Paint += (s, e) =>
            {
                bool on = c.HasValue ? _spec.Color.HasValue && _spec.Color.Value.ToArgb() == c.Value.ToArgb() : !_spec.Color.HasValue;
                if (!c.HasValue) using (Pen x = new Pen(Theme.Muted, 1.5f)) { e.Graphics.DrawLine(x, 6, 20, 20, 6); }
                using (Pen b = new Pen(on ? Color.White : Theme.Dim, on ? 3 : 1)) e.Graphics.DrawRectangle(b, on ? 1 : 0, on ? 1 : 0, p.Width - (on ? 3 : 1), p.Height - (on ? 3 : 1));
            };
            p.Click += (s, e) => { _spec.Color = c; Changed2(); };
            return p;
        }
    }

    public static class StyleDialog
    {
        /// <summary>A style chosen in a small window, or null if cancelled.</summary>
        public static string Ask(IWin32Window owner, string title, string spec, string sample)
        {
            using (Form f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(14) })
            {
                FlowLayoutPanel col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
                StylePicker picker = new StylePicker(520) { Spec = spec, Sample = sample };
                string result = null;
                Button ok = Theme.Button("OK", (s, e) => { result = picker.Spec; f.DialogResult = DialogResult.OK; f.Close(); }, 90);
                Button cancel = Theme.Button("Cancel", (s, e) => f.Close(), 90);
                Button reset = Theme.Button("Game's own look", (s, e) => picker.Spec = "", 150);
                col.Controls.Add(picker);
                col.Controls.Add(Theme.Row(ok, cancel, reset));
                f.Controls.Add(col);
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.Apply(f);
                f.ShowDialog(owner);
                return result;
            }
        }
    }
}
