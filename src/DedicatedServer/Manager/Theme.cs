// The Manager's look: the game's dark navy with its light-blue lines, and helpers to build the
// forms in code (labels, fields, buttons, grids) so every page looks alike.
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SDServerManager
{
    /// <summary>
    /// A page that scrolls: its parts top to bottom. Two things Windows' own panel gets wrong are
    /// fixed here: the scroll range ends at the last part's bottom edge, so the last line could sit
    /// under a button bar (a spacer keeps room after it), and wrapped text keeps the width it was
    /// made with, so a narrow window cut words off (text is re-wrapped to the page's width).
    /// </summary>
    public class PageColumn : FlowLayoutPanel
    {
        public PageColumn()
        {
            Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true;
            Padding = new Padding(18); BackColor = Theme.Back;
            // Windows sizes the scroll range to the last part's bottom edge, leaving out its margin and
            // the page's padding, so the last line ended up half hidden: an empty strip is always last.
            Controls.Add(_end);
            // everything is fitted to the width (above), so a page only ever scrolls up and down
            // (Windows adds a sideways bar when the up-down one takes its width; switching AutoScroll off
            // and on again with the sideways range at zero is the way to keep it off)
            AutoScroll = false;
            HorizontalScroll.Maximum = 0;
            HorizontalScroll.Enabled = false;
            HorizontalScroll.Visible = false;
            AutoScroll = true;
            // fitting waits for the next layout: adding many parts one by one fitted the whole page after each
            ControlAdded += (s, e) =>
            {
                if (e.Control != _end && !_moving) { _moving = true; Controls.SetChildIndex(_end, -1); Controls.SetChildIndex(_end, Controls.Count - 1); _moving = false; }
                _fittedCount = -1;
            };
        }

        private readonly Panel _end = new Panel { Name = "PageEnd", Height = 40, Width = 4, Margin = new Padding(0), BackColor = Color.Transparent };
        private bool _moving;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            int endH = AutoScroll ? 40 : 0;          // only a scrolling page needs the room
            if (_end.Height != endH) _end.Height = endH;
            if (Controls.Count > 0 && Controls[Controls.Count - 1] != _end && !_moving) { _moving = true; Controls.SetChildIndex(_end, Controls.Count - 1); _moving = false; }
            base.OnLayout(levent);
            if (!_fitting && Parent != null) Fit();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        /// <summary>Every wrapped label, chart and wide box inside fits the visible width.</summary>
        private bool _fitting;
        private int _fittedWidth = -1, _fittedCount = -1;

        public void Fit()
        {
            int avail = ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
            if (avail < 200 || _fitting) return;
            int count = Descendants(this).Count();
            if (avail == _fittedWidth && count == _fittedCount) return;     // (fitting resizes things, which would ask for it again)
            _fitting = true;
            try { FitTo(avail); }
            finally { _fittedWidth = avail; _fittedCount = count; _fitting = false; }
        }

        private void FitTo(int avail)
        {
            SuspendLayout();
            foreach (Control c in Descendants(this))
            {
                int x = OffsetIn(c);
                int room = Math.Max(120, avail - x);
                if (c is Label l && l.AutoSize && l.MaximumSize.Width > 0)
                {
                    if (!(l.Tag is int)) l.Tag = l.MaximumSize.Width;
                    int want = Math.Min((int)l.Tag, room);
                    if (l.MaximumSize.Width != want) l.MaximumSize = new Size(want, 0);
                }
                else if (c is IFitWidth f) f.FitWidth(room);
                else if (c is FlowLayoutPanel row && row.FlowDirection == FlowDirection.LeftToRight && row.AutoSize)
                {
                    // a row of buttons or boxes goes onto a second line rather than off the edge
                    row.WrapContents = true;
                    if (row.MaximumSize.Width != room) row.MaximumSize = new Size(room, 0);
                }
                else if ((c is TextBox || c is ComboBox) && c.Dock == DockStyle.None)
                {
                    // boxes keep their width unless the page is narrower; then they take what there is,
                    // but a dropdown never gets narrower than its longest choice (its row wraps instead)
                    int want;
                    if (!_boxWidth.TryGetValue(c, out want)) _boxWidth[c] = want = c.Width;
                    int floor = c is ComboBox cb ? Theme.ComboWidth(cb) : 80;
                    int w = Math.Max(floor, Math.Min(want, room - SiblingsRight(c)));
                    if (c.Width != w) c.Width = w;
                }
            }
            ResumeLayout(true);
        }

        private readonly System.Collections.Generic.Dictionary<Control, int> _boxWidth = new System.Collections.Generic.Dictionary<Control, int>();

        /// <summary>The width of what follows a box in its row (a Browse button, say).</summary>
        private static int SiblingsRight(Control c)
        {
            if (!(c.Parent is FlowLayoutPanel f) || f.FlowDirection != FlowDirection.LeftToRight) return 0;
            int w = 0; bool after = false;
            foreach (Control x in f.Controls) { if (x == c) { after = true; continue; } if (after && x.Visible) w += x.Width + x.Margin.Horizontal; }
            return w;
        }

        private int OffsetIn(Control c)
        {
            int x = 0;
            for (Control p = c; p != null && p != this; p = p.Parent) x += p.Left;
            return x - Padding.Left;
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control c)
        {
            foreach (Control x in c.Controls)
            {
                yield return x;
                foreach (Control y in Descendants(x)) yield return y;
            }
        }
    }

    /// <summary>
    /// A dropdown drawn by itself: looks like the Manager's dropdowns, opens a menu of the choices when clicked.
    /// Much cheaper to make than a ComboBox (a page with a dropdown on every row of a long list built in half
    /// the time). SelectedItem and SelectedIndexChanged work as a ComboBox's do.
    /// </summary>
    public class ChoiceLabel : Control
    {
        private readonly string[] _items;
        private string _value;
        public event EventHandler SelectedIndexChanged;

        public ChoiceLabel(string[] items, string value, int width)
        {
            _items = items;
            _value = items.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? (items.Length > 0 ? items[0] : "");
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            Size = new Size(width, Font.Height + 12);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public object SelectedItem
        {
            get { return _value; }
            set { string v = value as string; if (v == null || v == _value || !_items.Contains(v)) return; _value = v; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        }

        public string[] Items { get { return _items; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(Enabled ? Theme.FieldBack : Theme.Panel)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Focused ? Theme.Line : Theme.Dim)) g.DrawRectangle(p, r);
            TextRenderer.DrawText(g, _value, Font, new Rectangle(6, 0, Width - 26, Height), Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (Enabled) TextRenderer.DrawText(g, "\u25BE", Font, new Rectangle(Width - 20, 0, 16, Height), Theme.Line, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Open(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) Open(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        private void Open()
        {
            if (!Enabled) return;
            ContextMenuStrip m = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Font, ShowImageMargin = false, RenderMode = ToolStripRenderMode.System };
            foreach (string it in _items)
            {
                string v = it;
                ToolStripMenuItem mi = new ToolStripMenuItem(v) { Checked = v == _value, BackColor = Theme.Panel, ForeColor = Theme.Text };
                mi.Click += (s, e) => SelectedItem = v;
                m.Items.Add(mi);
            }
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, new Point(0, Height));
        }
    }

    /// <summary>A part that can take the page's width (charts).</summary>
    public interface IFitWidth { void FitWidth(int width); }

    /// <summary>A column of parts as tall as they are and as wide as the page (a mod's row, say).</summary>
    public class FitFlow : FlowLayoutPanel, IFitWidth
    {
        // its width is the page's, given to the layout as its preferred size (a minimum size would stay
        // behind when the window got smaller and hold the page wide)
        private int _w;
        public FitFlow() { AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FlowDirection = FlowDirection.TopDown; WrapContents = false; }
        public void FitWidth(int width) { if (_w != width) { _w = width; MaximumSize = new Size(width, 0); PerformLayout(); Parent?.PerformLayout(); } }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size s = base.GetPreferredSize(_w > 0 ? new Size(_w, proposedSize.Height) : proposedSize);
            if (_w > 0) s.Width = _w;
            return s;
        }
    }

    /// <summary>
    /// A box (grid, chart, text) with a grip strip along its bottom edge. Dragging the grip makes
    /// the box taller or shorter; the height is kept in the Manager's settings under its key.
    /// </summary>
    public class SizedBox : Panel, IFitWidth
    {
        private readonly Panel _grip = new Panel { Dock = DockStyle.Bottom, Height = 9, Cursor = Cursors.SizeNS, BackColor = Color.Transparent };
        private readonly string _key;
        private readonly int _min;
        private int _dragFrom = -1, _startH;
        public Control Inner { get; private set; }

        public SizedBox(Control inner, string key, int height, int min)
        {
            _key = key; _min = min; Inner = inner;
            Margin = new Padding(0, 4, 0, 6);
            BackColor = Color.Transparent;
            Height = Math.Max(min, Theme.SizeOf(key, height)) + _grip.Height;
            Width = Math.Max(200, inner.Width);
            inner.Dock = DockStyle.Fill;
            inner.Margin = new Padding(0);
            Controls.Add(inner);
            Controls.Add(_grip);
            new ToolTip().SetToolTip(_grip, "Drag to make this taller or shorter");
            _grip.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Dim)) e.Graphics.DrawLine(p, 0, 1, _grip.Width, 1);
                using (SolidBrush b = new SolidBrush(_dragFrom >= 0 ? Theme.Line : Theme.Dim))
                    for (int i = -3; i <= 3; i++) e.Graphics.FillRectangle(b, _grip.Width / 2 + i * 7 - 1, 4, 3, 3);
            };
            _grip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { _dragFrom = Cursor.Position.Y; _startH = Height; _grip.Capture = true; _grip.Invalidate(); } };
            _grip.MouseMove += (s, e) =>
            {
                if (_dragFrom < 0) return;
                int h = Math.Max(_min + _grip.Height, Math.Min(4000, _startH + Cursor.Position.Y - _dragFrom));
                if (h != Height) Height = h;
            };
            _grip.MouseUp += (s, e) =>
            {
                if (_dragFrom < 0) return;
                _dragFrom = -1; _grip.Capture = false; _grip.Invalidate();
                Theme.RememberSize(_key, Height - _grip.Height);
            };
        }

        public void FitWidth(int width) { if (Width != width) Width = width; if (Inner is IFitWidth f) f.FitWidth(width); }

#if SDSMTEST
        /// <summary>For the self test: drag the grip by this many pixels, as a person would.</summary>
        public void DragForTest(int dy) { _startH = Height; Height = Math.Max(_min + _grip.Height, _startH + dy); Theme.RememberSize(_key, Height - _grip.Height); }
#endif
    }

    public static class Theme
    {
        public static readonly Color Back = Color.FromArgb(10, 16, 26);
        public static readonly Color Panel = Color.FromArgb(16, 26, 40);
        public static readonly Color FieldBack = Color.FromArgb(6, 10, 16);
        public static readonly Color Line = Color.FromArgb(89, 199, 255);
        public static readonly Color Dim = Color.FromArgb(60, 90, 120);
        public static readonly Color Text = Color.FromArgb(220, 235, 250);
        public static readonly Color Muted = Color.FromArgb(140, 160, 185);
        public static readonly Color Good = Color.FromArgb(110, 220, 140);
        public static readonly Color Bad = Color.FromArgb(255, 120, 100);
        public static readonly Color Warn = Color.FromArgb(255, 200, 90);
        public static readonly Color ButtonBack = Color.FromArgb(20, 51, 77);

        public static Font Body = new Font("Segoe UI", 10f);
        public static Font Small = new Font("Segoe UI", 8.75f);
        public static Font Head = new Font("Segoe UI Semibold", 15f);
        public static Font Sub = new Font("Segoe UI Semibold", 11f);
        public static Font Mono = new Font("Consolas", 9.5f);

        /// <summary>The Manager's settings, for sizes the person has dragged (set by the main window).</summary>
        public static ManagerSettings Settings;

        public static void Apply(Control c)
        {
            c.BackColor = c is TextBox || c is ComboBox || c is NumericUpDown || c is ListBox ? FieldBack : c is Button ? ButtonBack : c.BackColor == SystemColors.Control || c is Form || c is TabPage ? Back : c.BackColor;
            c.ForeColor = Text;
            if (c.Font == null || c.Font.Name == "Microsoft Sans Serif") c.Font = Body;
            if (c is Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Line;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 77, 115);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(51, 115, 153);
                b.Cursor = Cursors.Hand;
            }
            if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle;
            if (c is ComboBox cb) DarkCombo(cb);
            if (c is DataGridView g) Grid(g);
            foreach (Control child in c.Controls) Apply(child);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, object> _dark = new System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, object>();

        /// <summary>A dropdown drawn in the dark colours (Windows draws a plain one white).</summary>
        public static void DarkCombo(ComboBox cb)
        {
            cb.BackColor = FieldBack; cb.ForeColor = Text;
            if (_dark.TryGetValue(cb, out _)) return;
            _dark.Add(cb, null);
            cb.FlatStyle = FlatStyle.Flat;
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.ItemHeight = cb.Font.Height + 6;          // (reading ItemHeight makes Windows create the box there and then: slow, row by row)
            cb.DrawItem += DrawComboItem;
            cb.DropDown += (s, e) => { int w = ComboWidth(cb); if (cb.DropDownWidth < w) cb.DropDownWidth = w; };
        }

        // every item is painted in full (background and text) whatever state Windows passes, so none is
        // left black: a grid's dropdown is reused between cells and can arrive with its own colours
        private static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = (ComboBox)sender;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (SolidBrush bg = new SolidBrush(sel ? Color.FromArgb(30, 77, 115) : FieldBack)) e.Graphics.FillRectangle(bg, e.Bounds);
            string t = e.Index >= 0 && e.Index < cb.Items.Count ? cb.GetItemText(cb.Items[e.Index]) : e.Index < 0 ? cb.Text : "";
            Rectangle r = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top, e.Bounds.Width - 3, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, t, cb.Font, r, Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        /// <summary>Wide enough for the longest choice and the arrow.</summary>
        public static int ComboWidth(ComboBox cb)
        {
            int w = 0;
            foreach (object o in cb.Items) w = Math.Max(w, TextRenderer.MeasureText(cb.GetItemText(o), cb.Font).Width);
            return w + SystemInformation.VerticalScrollBarWidth + 14;
        }

        public static void Grid(DataGridView g)
        {
            g.BackgroundColor = Panel;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Dim;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = ButtonBack;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Line;
            g.ColumnHeadersDefaultCellStyle.Font = Sub;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.DefaultCellStyle.BackColor = Panel;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 77, 115);
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 32, 48);
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;      // a long heading wraps instead of being cut off
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            g.RowTemplate.Height = 28;
            // a choice cell's dropdown in the grid's colours (Windows' own was sometimes black text on black)
            g.EditingControlShowing += (s, e) => { if (e.Control is ComboBox cb) { cb.Font = g.DefaultCellStyle.Font ?? Body; DarkCombo(cb); } };
            typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true);
        }

        // ---- sizes the person picks ------------------------------------------------------------

        public static int SizeOf(string key, int fallback)
        {
            int v;
            return Settings != null && key != null && Settings.Sizes.TryGetValue(key, out v) && v > 0 ? v : fallback;
        }

        public static void RememberSize(string key, int v)
        {
            if (Settings == null || key == null) return;
            Settings.Sizes[key] = v;
            try { Settings.Save(); } catch { }
        }

        /// <summary>A divider the person drags stays where they left it (also next time).</summary>
        public static void Remember(SplitContainer sc, string key, int start = -1)
        {
            bool applied = false;
            sc.SplitterWidth = 7;
            sc.Paint += (s, e) =>
            {
                // a grip in the middle of the divider, so it can be seen to move
                Rectangle r = sc.SplitterRectangle;
                using (SolidBrush b = new SolidBrush(Dim))
                    for (int i = -2; i <= 2; i++)
                    {
                        if (sc.Orientation == Orientation.Horizontal) e.Graphics.FillRectangle(b, r.Left + r.Width / 2 + i * 6 - 1, r.Top + r.Height / 2 - 1, 3, 3);
                        else e.Graphics.FillRectangle(b, r.Left + r.Width / 2 - 1, r.Top + r.Height / 2 + i * 6 - 1, 3, 3);
                    }
            };
            sc.Layout += (s, e) =>
            {
                if (applied) return;
                int total = sc.Orientation == Orientation.Horizontal ? sc.Height : sc.Width;
                if (total < sc.Panel1MinSize + sc.Panel2MinSize + sc.SplitterWidth + 20) return;
                applied = true;
                int want = SizeOf(key, start);
                if (want > 0) try { sc.SplitterDistance = Math.Max(sc.Panel1MinSize, Math.Min(total - sc.Panel2MinSize - sc.SplitterWidth, want)); } catch { }
            };
            sc.SplitterMoved += (s, e) => { if (applied) RememberSize(key, sc.SplitterDistance); };
        }

        /// <summary>A box on a scrolling page with a grip under it: drag the grip to make the box taller or shorter.</summary>
        public static SizedBox Sized(Control inner, string key, int height, int min = 70)
        {
            return new SizedBox(inner, key, height, min);
        }

        // ---- building blocks -------------------------------------------------------------------

        public static Label Label(string text, Font f = null, Color? color = null)
        {
            return new Label { Text = text, AutoSize = true, Font = f ?? Body, ForeColor = color ?? Text, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 2) };
        }

        public static Label Wrap(string text, int width, Font f = null, Color? color = null)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Font = f ?? Small, ForeColor = color ?? Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        }

        public static Button Button(string text, EventHandler click, int width = 130)
        {
            // sized by its text, so it fits at any display scaling
            Button b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(width, 0), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 4, 8, 4) };
            b.Click += click;
            Apply(b);
            return b;
        }

        public static FlowLayoutPanel Row(params Control[] cs)
        {
            FlowLayoutPanel p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        public static FlowLayoutPanel Column()
        {
            return new PageColumn();
        }

        public static TextBox Box(string text, int width = 320)
        {
            TextBox t = new TextBox { Text = text ?? "", Width = width };
            Apply(t);
            return t;
        }

        public static ComboBox Choice(string[] items, string value, int width = 200)
        {
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
            c.Items.AddRange(items.Cast<object>().ToArray());
            int i = Array.FindIndex(items, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
            c.SelectedIndex = i >= 0 ? i : 0;
            Apply(c);
            c.Width = Math.Max(width, FittedWidth(c, items));
            return c;
        }

        public static NumericUpDown Number(int value, int min, int max, int width = 100)
        {
            NumericUpDown n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = width };
            Apply(n);
            return n;
        }

        public static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Text, BackColor = Color.Transparent, Font = Body, Margin = new Padding(0, 6, 0, 2) };
        }

        /// <summary>A labelled field: the label, the control, then the explanation.</summary>
        public static Control Field(string label, Control input, string help, int width = 620)
        {
            FlowLayoutPanel p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 8) };
            p.Controls.Add(Label(label, Sub, Line));
            p.Controls.Add(input);
            if (!string.IsNullOrEmpty(help)) p.Controls.Add(Wrap(help, width));
            return p;
        }

        private static readonly System.Collections.Generic.Dictionary<string, int> _fitted = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>A ChoiceLabel as wide as its longest choice.</summary>
        public static ChoiceLabel ChoiceLight(string[] items, string value)
        {
            int w = 0;
            foreach (string s in items) w = Math.Max(w, TextRenderer.MeasureText(s, Body).Width);
            return new ChoiceLabel(items, value, w + 34);
        }

        public static ComboBox ChoiceFitted(string[] items, string value)
        {
            ComboBox c = Choice(items, value, 60);
            c.Width = FittedWidth(c, items);
            return c;
        }

        /// <summary>ComboWidth, measured once for each set of choices and font.</summary>
        private static int FittedWidth(ComboBox c, string[] items)
        {
            string key = string.Join("", items) + "|" + c.Font.Name + c.Font.Size;
            int w;
            if (!_fitted.TryGetValue(key, out w)) _fitted[key] = w = ComboWidth(c);
            return w;
        }

        public static DataGridView NewGrid(params string[] columns)
        {
            DataGridView g = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
            foreach (string c in columns) g.Columns.Add(c.Replace(" ", ""), c);
            Grid(g);
            return g;
        }
    }
}
