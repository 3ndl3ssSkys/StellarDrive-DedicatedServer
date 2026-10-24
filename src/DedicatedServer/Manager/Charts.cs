// The Manager's charts:
//   TimelineChart  when the server was up, with its stops, crashes and freezes
//   SeriesChart    one kind of number over time (bars for counts, lines for rates), with a scale
//   LiveNetChart   bytes in and out each second, the last two minutes (like Task Manager's network graph)
//   LaneChart      several numbers over the same time, each in a lane of its own
// Hovering a bar, or near a point on a line, says what it is; nothing shows when the mouse is elsewhere.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SDServerManager
{
    /// <summary>
    /// Up time as a band across the chosen span: green while the server ran, nothing while it was
    /// down, a red mark where a run ended in a crash, and an orange tick (taller = longer) for every
    /// time the server loop froze for half a second or more.
    /// </summary>
    public class TimelineChart : Control, IFitWidth
    {
        public struct Run { public DateTime Start, End; public bool Clean, Live; }
        public struct Freeze { public DateTime At; public double Ms; }

        private List<Run> _runs = new List<Run>();
        private List<Freeze> _freezes = new List<Freeze>();
        private DateTime _from, _to;
        private readonly ToolTip _tip = new ToolTip();
        private string _lastTip = "";

        public TimelineChart()
        {
            Size = new Size(900, 150);
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            Margin = new Padding(0, 6, 0, 4);
        }

        public void FitWidth(int width) { if (Width != width) { Width = width; Invalidate(); } }

        public void Set(IEnumerable<Run> runs, IEnumerable<Freeze> freezes, DateTime fromUtc, DateTime toUtc)
        {
            _runs = runs.ToList(); _freezes = freezes.ToList(); _from = fromUtc; _to = toUtc;
            Invalidate();
        }

        private Rectangle Band { get { return new Rectangle(10, 30, Math.Max(10, Width - 20), Math.Max(20, Height - 62)); } }

        private float X(DateTime t)
        {
            Rectangle b = Band;
            double f = (t - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds);
            return (float)(b.Left + Math.Max(0, Math.Min(1, f)) * b.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle b = Band;
            double upS = 0, span = Math.Max(1, (_to - _from).TotalSeconds);
            foreach (Run r in _runs)
            {
                DateTime s = r.Start < _from ? _from : r.Start, en = r.End > _to ? _to : r.End;
                if (en > s) upS += (en - s).TotalSeconds;
            }
            int crashes = _runs.Count(r => !r.Clean && !r.Live && r.End >= _from && r.End <= _to);
            int freezes = _freezes.Count(f => f.At >= _from && f.At <= _to);
            TextRenderer.DrawText(g, "Up " + (100.0 * upS / span).ToString("0.0") + "% of this span   " + crashes + " crash(es)   " + freezes + " freeze(s) of 0.5 s or more",
                Theme.Small, new Point(8, 6), Theme.Muted);
            using (SolidBrush down = new SolidBrush(Color.FromArgb(28, 40, 56))) g.FillRectangle(down, b);
            using (SolidBrush up = new SolidBrush(Color.FromArgb(70, 170, 100)))
            using (SolidBrush live = new SolidBrush(Theme.Good))
            using (Pen crash = new Pen(Theme.Bad, 3))
            {
                foreach (Run r in _runs)
                {
                    if (r.End < _from || r.Start > _to) continue;
                    float x0 = X(r.Start), x1 = Math.Max(X(r.End), x0 + 1.5f);
                    g.FillRectangle(r.Live ? live : up, x0, b.Top + 4, x1 - x0, b.Height - 8);
                    if (!r.Clean && !r.Live && r.End <= _to) g.DrawLine(crash, x1, b.Top, x1, b.Bottom);
                }
            }
            using (Pen fz = new Pen(Theme.Warn, 2))
                foreach (Freeze f in _freezes)
                {
                    if (f.At < _from || f.At > _to) continue;
                    float x = X(f.At);
                    float h = (float)(Math.Min(1.0, Math.Log10(Math.Max(500, f.Ms) / 500.0 * 9 + 1)) * (b.Height - 6));
                    g.DrawLine(fz, x, b.Bottom - 2, x, b.Bottom - 2 - Math.Max(6, h));
                }
            using (Pen axis = new Pen(Theme.Dim)) g.DrawRectangle(axis, b);
            for (int i = 0; i <= 6; i++)
            {
                DateTime t = _from.AddSeconds(span * i / 6.0).ToLocalTime();
                string label = span <= 2 * 86400 ? t.ToString("HH:mm") : t.ToString("MM-dd");
                Size sz = TextRenderer.MeasureText(label, Theme.Small);
                int x = (int)(b.Left + b.Width * i / 6.0) - sz.Width / 2;
                x = Math.Max(0, Math.Min(Width - sz.Width, x));
                TextRenderer.DrawText(g, label, Theme.Small, new Point(x, b.Bottom + 4), Theme.Muted);
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _tip.Hide(this); _lastTip = ""; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle b = Band;
            if (e.X < b.Left || e.X > b.Right || e.Y < b.Top || e.Y > b.Bottom) { if (_lastTip.Length > 0) { _tip.Hide(this); _lastTip = ""; } return; }
            DateTime t = _from.AddSeconds((_to - _from).TotalSeconds * (e.X - b.Left) / Math.Max(1, b.Width));
            double px = (_to - _from).TotalSeconds / Math.Max(1, b.Width) * 3;
            string when = t.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string s;
            List<Freeze> near = _freezes.Where(f => Math.Abs((f.At - t).TotalSeconds) <= px).ToList();
            Run? crash = _runs.Where(x => !x.Clean && !x.Live && Math.Abs((x.End - t).TotalSeconds) <= px).Select(x => (Run?)x).FirstOrDefault();
            Run? r = _runs.Where(x => x.Start <= t && x.End >= t).Select(x => (Run?)x).FirstOrDefault();
            if (near.Count > 0)
                s = "Freeze: the server stood still for " + string.Join(", ", near.Select(f => (f.Ms / 1000).ToString("0.0") + " s")) + " at " + near[0].At.ToLocalTime().ToString("HH:mm:ss");
            else if (crash.HasValue)
                s = "Crash: the run from " + crash.Value.Start.ToLocalTime().ToString("MM-dd HH:mm") + " ended without stopping properly, " + crash.Value.End.ToLocalTime().ToString("MM-dd HH:mm");
            else if (r.HasValue)
                s = "Up: " + when + "\nThis run: " + r.Value.Start.ToLocalTime().ToString("MM-dd HH:mm") + " to " + (r.Value.Live ? "now (running)" : r.Value.End.ToLocalTime().ToString("MM-dd HH:mm")) + ", " + Fmt.Span((r.Value.End - r.Value.Start).TotalSeconds);
            else
            {
                DateTime a = _runs.Where(x => x.End <= t).Select(x => x.End).DefaultIfEmpty(_from).Max();
                DateTime z = _runs.Where(x => x.Start >= t).Select(x => x.Start).DefaultIfEmpty(_to).Min();
                s = "Down: " + when + "\nDown from " + a.ToLocalTime().ToString("MM-dd HH:mm") + " to " + (z >= _to ? "now" : z.ToLocalTime().ToString("MM-dd HH:mm")) + ", " + Fmt.Span((z - a).TotalSeconds);
            }
            if (s != _lastTip) { _lastTip = s; _tip.Show(s, this, e.X + 14, e.Y + 16); }
        }
    }

    /// <summary>What every chart shares: a title, a scale, gridlines and a hover box drawn inside the chart.</summary>
    public abstract class ChartBase : Control, IFitWidth
    {
        protected string Title = "";
        protected string HoverText;      // drawn when set
        protected PointF HoverAt;

        protected ChartBase()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            Margin = new Padding(0, 8, 0, 4);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void FitWidth(int width) { if (Width != width) { Width = width; Invalidate(); } }

        /// <summary>A round top for the scale (1, 2 or 5 times a power of ten) and its step.</summary>
        public static double NiceMax(double max, int ticks, out double step)
        {
            if (max <= 0) { step = 1; return ticks; }
            double raw = max / ticks, pow = Math.Pow(10, Math.Floor(Math.Log10(raw))), f = raw / pow;
            step = (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * pow;
            return step * Math.Ceiling(max / step - 1e-9);
        }

        protected void DrawScale(Graphics g, Rectangle plot, double top, double step, Func<double, string> fmt)
        {
            using (Pen grid = new Pen(Color.FromArgb(34, 52, 74)))
            using (Pen axis = new Pen(Theme.Dim))
            {
                for (double v = 0; v <= top + step / 2; v += step)
                {
                    float y = (float)(plot.Bottom - v / top * plot.Height);
                    g.DrawLine(v == 0 ? axis : grid, plot.Left, y, plot.Right, y);
                    string s = fmt(v);
                    Size sz = TextRenderer.MeasureText(s, Theme.Small);
                    TextRenderer.DrawText(g, s, Theme.Small, new Point(plot.Left - sz.Width - 2, (int)y - sz.Height / 2), Theme.Muted);
                }
            }
        }

        protected int ScaleWidth(double top, double step, Func<double, string> fmt)
        {
            int w = 0;
            for (double v = 0; v <= top + step / 2; v += step) w = Math.Max(w, TextRenderer.MeasureText(fmt(v), Theme.Small).Width);
            return w + 6;
        }

        protected void DrawHover(Graphics g)
        {
            if (string.IsNullOrEmpty(HoverText)) return;
            Size sz = TextRenderer.MeasureText(HoverText, Theme.Small);
            int w = sz.Width + 14, h = sz.Height + 10;
            float x = HoverAt.X + 12, y = HoverAt.Y - h - 8;
            if (x + w > Width - 4) x = HoverAt.X - w - 12;
            if (y < 2) y = HoverAt.Y + 12;
            if (y + h > Height - 2) y = Height - h - 2;
            Rectangle r = new Rectangle((int)Math.Max(2, x), (int)y, w, h);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 12, 20, 32))) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.Line)) g.DrawRectangle(p, r);
            TextRenderer.DrawText(g, HoverText, Theme.Small, new Point(r.Left + 7, r.Top + 5), Theme.Text);
        }

        protected void SetHover(string text, PointF at)
        {
            if (text == HoverText && (text == null || at == HoverAt)) return;
            HoverText = text; HoverAt = at;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(null, PointF.Empty); }

#if SDSMTEST
        /// <summary>For the self test: the mouse at a point, and what the chart then says (null: nothing).</summary>
        public string HoverForTest(int x, int y) { OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, x, y, 0)); return HoverText; }

        public Bitmap RenderForTest() { Bitmap b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); return b; }
#endif

        /// <summary>About this many time labels along the bottom, at round places.</summary>
        protected static IEnumerable<int> LabelIndexes(int n, int want)
        {
            if (n <= 0) yield break;
            int every = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, want)));
            for (int i = n - 1; i >= 0; i -= every) yield return i;
        }
    }

    /// <summary>Numbers over time for one kind of thing: bars (counts in each hour or day) or lines (rates, levels).</summary>
    public class SeriesChart : ChartBase
    {
        public enum Style { Bars, Lines }

        public class Series
        {
            public string Name; public Color Color; public double[] Values = new double[0];
            public bool Dashed;
        }

        private List<Series> _series = new List<Series>();
        private DateTime[] _times = new DateTime[0];
        private bool _hourly;
        private readonly Style _style;
        private readonly Func<double, string> _fmt;
        private string _note = "";

        public SeriesChart(string title, Style style, Func<double, string> format = null, int height = 190)
        {
            Title = title; _style = style;
            _fmt = format ?? (v => v.ToString(v < 10 && v != Math.Floor(v) ? "0.#" : "0"));
            Size = new Size(900, height);
        }

        public void Set(IEnumerable<Series> series, DateTime[] times, bool hourly, string note = "")
        {
            _series = series.ToList(); _times = times; _hourly = hourly; _note = note ?? "";
            HoverText = null;
            Invalidate();
        }

        private string When(int i)
        {
            if (i < 0 || i >= _times.Length) return "";
            DateTime t = _times[i].ToLocalTime();
            return _hourly ? t.ToString("ddd MM-dd HH:00") + "-" + t.AddHours(1).ToString("HH:00") : t.ToString("ddd yyyy-MM-dd");
        }

        private Rectangle _plot;
        private double _top;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, Title, Theme.Sub, new Point(8, 4), Theme.Line);
            // key, right-aligned
            int kx = Width - 8;
            foreach (Series s in Enumerable.Reverse(_series))
            {
                string t = s.Name;
                int w = TextRenderer.MeasureText(t, Theme.Small).Width;
                kx -= w;
                TextRenderer.DrawText(g, t, Theme.Small, new Point(kx, 8), Theme.Text);
                kx -= 18;
                using (SolidBrush b = new SolidBrush(s.Color)) g.FillRectangle(b, kx, 13, 13, 6);
                kx -= 14;
            }
            int n = _times.Length;
            double max = _series.Where(s => s.Values.Length > 0).Select(s => s.Values.Max()).DefaultIfEmpty(0).Max();
            double step;
            _top = NiceMax(Math.Max(max, _style == Style.Bars ? 1 : 0), 4, out step);
            if (max <= 0 && _style == Style.Bars) { _top = 4; step = 1; }
            int sw = ScaleWidth(_top, step, _fmt);
            _plot = new Rectangle(8 + sw, 34, Math.Max(20, Width - 16 - sw), Math.Max(20, Height - 34 - 24));
            DrawScale(g, _plot, _top, step, _fmt);
            if (n == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_style == Style.Bars)
            {
                float slot = _plot.Width / (float)n;
                int k = Math.Max(1, _series.Count);
                float gap = slot > 6 ? Math.Max(1, slot * 0.18f) : 0, bw = Math.Max(1, (slot - gap) / k);
                g.SmoothingMode = SmoothingMode.None;
                for (int si = 0; si < _series.Count; si++)
                    using (SolidBrush b = new SolidBrush(_series[si].Color))
                        for (int i = 0; i < n && i < _series[si].Values.Length; i++)
                        {
                            double v = _series[si].Values[i];
                            if (v <= 0) continue;
                            float h = (float)(v / _top * _plot.Height);
                            g.FillRectangle(b, _plot.Left + i * slot + gap / 2 + si * bw, _plot.Bottom - h, Math.Max(1, bw - (k > 1 ? 1 : 0)), h);
                        }
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            else
            {
                foreach (Series s in _series)
                {
                    PointF[] pts = Points(s);
                    if (pts.Length == 0) continue;
                    if (pts.Length > 1)
                    {
                        using (GraphicsPath area = new GraphicsPath())
                        {
                            area.AddLines(pts);
                            area.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, _plot.Bottom));
                            area.AddLine(new PointF(pts[0].X, _plot.Bottom), pts[0]);
                            using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, s.Color))) g.FillPath(fill, area);
                        }
                        using (Pen p = new Pen(s.Color, 1.8f) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid }) g.DrawLines(p, pts);
                    }
                    if (n <= 48) using (SolidBrush b = new SolidBrush(s.Color)) foreach (PointF pt in pts) g.FillEllipse(b, pt.X - 2, pt.Y - 2, 4, 4);
                }
            }
            int lastRight = int.MinValue;
            foreach (int i in LabelIndexes(n, Math.Max(2, _plot.Width / 110)))
            {
                DateTime t = _times[i].ToLocalTime();
                string label = _hourly ? (t.Hour == 0 || n <= 30 ? t.ToString("MM-dd HH:00") : t.ToString("HH:00")) : t.ToString("MM-dd");
                Size sz = TextRenderer.MeasureText(label, Theme.Small);
                float x = XOf(i);
                int left = (int)Math.Min(Width - sz.Width, Math.Max(_plot.Left - 4, x - sz.Width / 2f));
                if (left < lastRight + 10) continue;              // it would run into the one before
                TextRenderer.DrawText(g, label, Theme.Small, new Point(left, _plot.Bottom + 4), Theme.Muted);
                lastRight = left + sz.Width;
            }
            if (_series.All(s => s.Values.All(v => v <= 0)))
                TextRenderer.DrawText(g, _note.Length > 0 ? _note : "None in this span", Theme.Body, _plot, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (!string.IsNullOrEmpty(HoverText) && _style == Style.Lines)
                using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillEllipse(b, HoverAt.X - 4, HoverAt.Y - 4, 8, 8);
            DrawHover(g);
        }

#if SDSMTEST
        public Rectangle PlotForTest { get { return _plot; } }
        public double TopForTest { get { return _top; } }
#endif

        private float XOf(int i)
        {
            int n = _times.Length;
            if (_style == Style.Bars) return _plot.Left + (i + 0.5f) * _plot.Width / Math.Max(1, n);
            return n <= 1 ? _plot.Left : _plot.Left + _plot.Width * i / (float)(n - 1);
        }

        private PointF[] Points(Series s)
        {
            int n = Math.Min(_times.Length, s.Values.Length);
            PointF[] pts = new PointF[n];
            for (int i = 0; i < n; i++) pts[i] = new PointF(XOf(i), (float)(_plot.Bottom - s.Values[i] / _top * _plot.Height));
            return pts;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = _times.Length;
            if (n == 0 || _plot.Width <= 0) return;
            if (_style == Style.Bars)
            {
                // over a bar (or just above a short one)
                float slot = _plot.Width / (float)n;
                int i = (int)((e.X - _plot.Left) / slot);
                if (i < 0 || i >= n || e.X < _plot.Left) { SetHover(null, PointF.Empty); return; }
                int k = Math.Max(1, _series.Count);
                float gap = slot > 6 ? Math.Max(1, slot * 0.18f) : 0, bw = Math.Max(1, (slot - gap) / k);
                double tallest = _series.Where(s => i < s.Values.Length).Select(s => s.Values[i]).DefaultIfEmpty(0).Max();
                float topY = (float)(_plot.Bottom - tallest / _top * _plot.Height);
                if (tallest <= 0 || e.Y < topY - 8 || e.Y > _plot.Bottom + 2) { SetHover(null, PointF.Empty); return; }
                string text = When(i) + "\n" + string.Join("\n", _series.Select(s => s.Name + ": " + _fmt(i < s.Values.Length ? s.Values[i] : 0)));
                SetHover(text, new PointF(_plot.Left + i * slot + slot / 2, topY));
                return;
            }
            // near a point on a line
            float best = 12; string hit = null; PointF at = PointF.Empty;
            foreach (Series s in _series)
            {
                PointF[] pts = Points(s);
                int i = (int)Math.Round((e.X - _plot.Left) / (double)Math.Max(1, _plot.Width) * (n - 1));
                for (int j = Math.Max(0, i - 1); j <= Math.Min(pts.Length - 1, i + 1); j++)
                {
                    float d = (float)Math.Sqrt((pts[j].X - e.X) * (pts[j].X - e.X) + (pts[j].Y - e.Y) * (pts[j].Y - e.Y));
                    // between points: the distance to the line itself counts too
                    if (d < best) { best = d; hit = s.Name + ": " + _fmt(s.Values[j]) + "\n" + When(j); at = pts[j]; }
                }
                if (hit == null && pts.Length > 1 && i >= 0 && i < pts.Length)
                {
                    int j2 = e.X >= pts[i].X ? Math.Min(pts.Length - 1, i + 1) : Math.Max(0, i - 1);
                    PointF a = pts[i], b = pts[j2];
                    float y = a.X == b.X ? a.Y : a.Y + (b.Y - a.Y) * (e.X - a.X) / (b.X - a.X);
                    if (Math.Abs(y - e.Y) < 6) { hit = s.Name + ": " + _fmt(s.Values[i]) + "\n" + When(i); at = a; best = 6; }
                }
            }
            SetHover(hit, at);
        }
    }

    /// <summary>
    /// The server's network use as it happens: bytes received and sent each second over the last two
    /// minutes, newest at the right, scaled to the busiest second shown (like Task Manager's graph).
    /// </summary>
    public class LiveNetChart : ChartBase
    {
        public static readonly Color In = Color.FromArgb(89, 199, 255), Out = Color.FromArgb(220, 140, 255);
        private double[] _in = new double[0], _out = new double[0];
        private DateTime[] _at = new DateTime[0];
        private string _note = "";
        public const int Seconds = 120;

        public LiveNetChart() { Title = "Network, live"; Size = new Size(900, 200); }

#if SDSMTEST
        public int PointsForTest { get { return _at.Length; } }
#endif

        public void Set(DateTime[] at, double[] inPerSec, double[] outPerSec, string note)
        {
            _at = at; _in = inPerSec; _out = outPerSec; _note = note ?? "";
            Invalidate();
        }

        public static string Rate(double bytesPerSec)
        {
            double bits = bytesPerSec * 8;
            if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e7 ? "0" : "0.0") + " Mbps";
            if (bits >= 1e3) return (bits / 1e3).ToString(bits >= 1e4 ? "0" : "0.0") + " Kbps";
            return bits.ToString("0") + " bps";
        }

        private Rectangle _plot;
        private double _top;

        private float XAt(DateTime t)
        {
            DateTime now = _at.Length > 0 ? _at[_at.Length - 1] : DateTime.UtcNow;
            return (float)(_plot.Right - (now - t).TotalSeconds / Seconds * _plot.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, Title, Theme.Sub, new Point(8, 4), Theme.Line);
            string nowText = _at.Length > 0 ? "Receive " + Rate(_in.Last()) + "    Send " + Rate(_out.Last()) : "";
            Size ns = TextRenderer.MeasureText(nowText, Theme.Small);
            int kx = Width - 8 - ns.Width;
            if (nowText.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(In)) g.FillRectangle(b, kx - 18, 13, 13, 6);
                TextRenderer.DrawText(g, nowText, Theme.Small, new Point(kx, 8), Theme.Text);
                int sendX = kx + TextRenderer.MeasureText("Receive " + Rate(_in.Last()) + "  ", Theme.Small).Width;
                using (SolidBrush b = new SolidBrush(Out)) g.FillRectangle(b, sendX - 4, 13, 10, 6);
            }
            double max = Math.Max(_in.DefaultIfEmpty(0).Max(), _out.DefaultIfEmpty(0).Max());
            double step;
            _top = NiceMax(Math.Max(max, 1024 / 8.0), 4, out step);
            Func<double, string> fmt = v => Rate(v);
            int sw = ScaleWidth(_top, step, fmt);
            _plot = new Rectangle(8 + sw, 34, Math.Max(20, Width - 16 - sw), Math.Max(20, Height - 34 - 24));
            DrawScale(g, _plot, _top, step, fmt);
            using (Pen grid = new Pen(Color.FromArgb(34, 52, 74)))
                for (int s = 10; s < Seconds; s += 10) { float x = _plot.Right - _plot.Width * s / (float)Seconds; g.DrawLine(grid, x, _plot.Top, x, _plot.Bottom); }
            TextRenderer.DrawText(g, Seconds + " seconds", Theme.Small, new Point(_plot.Left, _plot.Bottom + 4), Theme.Muted);
            TextRenderer.DrawText(g, "now", Theme.Small, new Point(_plot.Right - TextRenderer.MeasureText("now", Theme.Small).Width, _plot.Bottom + 4), Theme.Muted);
            if (_at.Length < 2)
            {
                TextRenderer.DrawText(g, _note.Length > 0 ? _note : "Waiting for the first seconds...", Theme.Body, _plot, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                DrawHover(g);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(_plot);
            foreach (var line in new[] { new { V = _in, C = In }, new { V = _out, C = Out } })
            {
                PointF[] pts = _at.Select((t, i) => new PointF(XAt(t), (float)(_plot.Bottom - line.V[i] / _top * _plot.Height))).ToArray();
                using (GraphicsPath area = new GraphicsPath())
                {
                    area.AddLines(pts);
                    area.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, _plot.Bottom));
                    area.AddLine(new PointF(pts[0].X, _plot.Bottom), pts[0]);
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(46, line.C))) g.FillPath(fill, area);
                }
                using (Pen p = new Pen(line.C, 1.6f)) g.DrawLines(p, pts);
            }
            g.ResetClip();
            if (!string.IsNullOrEmpty(HoverText)) using (Pen p = new Pen(Color.FromArgb(120, Theme.Text))) g.DrawLine(p, HoverAt.X, _plot.Top, HoverAt.X, _plot.Bottom);
            DrawHover(g);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_at.Length < 2) return;
            int best = -1; float bd = 10;
            for (int i = 0; i < _at.Length; i++)
            {
                float x = XAt(_at[i]);
                float d = Math.Abs(x - e.X);
                if (d >= bd) continue;
                float yi = (float)(_plot.Bottom - _in[i] / _top * _plot.Height), yo = (float)(_plot.Bottom - _out[i] / _top * _plot.Height);
                if (Math.Min(Math.Abs(yi - e.Y), Math.Abs(yo - e.Y)) > 14) continue;
                bd = d; best = i;
            }
            if (best < 0) { SetHover(null, PointF.Empty); return; }
            SetHover(_at[best].ToLocalTime().ToString("HH:mm:ss") + "\nReceive " + Rate(_in[best]) + "\nSend " + Rate(_out[best]), new PointF(XAt(_at[best]), e.Y));
        }
    }

    /// <summary>Several numbers over the same time, each in its own lane with its own scale, so none hides another.</summary>
    public class LaneChart : ChartBase
    {
        public class Lane { public string Name; public Color Color; public double[] Values = new double[0]; public Func<double, string> Format; }

        private List<Lane> _lanes = new List<Lane>();
        private DateTime[] _times = new DateTime[0];
        private bool _hourly;
        private const int LaneH = 46, NameW = 150;

        public LaneChart(string title) { Title = title; Size = new Size(900, 120); }

        public void Set(IEnumerable<Lane> lanes, DateTime[] times, bool hourly)
        {
            _lanes = lanes.ToList(); _times = times; _hourly = hourly;
            int want = 32 + _lanes.Count * LaneH + 26;
            if (Height != want) Height = want;
            HoverText = null;
            Invalidate();
        }

        private static string Fmt0(Lane l, double v) { return l.Format != null ? l.Format(v) : v.ToString(v < 10 && v != Math.Floor(v) ? "0.#" : "0"); }

#if SDSMTEST
        public Rectangle LaneForTest(int i) { return LaneRect(i); }
#endif

        private Rectangle LaneRect(int i) { return new Rectangle(NameW, 32 + i * LaneH + 4, Math.Max(20, Width - NameW - 10), LaneH - 10); }

        private float XOf(int i, Rectangle r) { int n = _times.Length; return n <= 1 ? r.Left : r.Left + r.Width * i / (float)(n - 1); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, Title, Theme.Sub, new Point(8, 4), Theme.Line);
            int n = _times.Length;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int li = 0; li < _lanes.Count; li++)
            {
                Lane l = _lanes[li];
                Rectangle r = LaneRect(li);
                double max = l.Values.Length > 0 ? l.Values.Max() : 0;
                using (SolidBrush b = new SolidBrush(l.Color)) g.FillRectangle(b, 8, r.Top + 4, 4, r.Height - 8);
                TextRenderer.DrawText(g, l.Name, Theme.Small, new Point(16, r.Top), Theme.Text);
                TextRenderer.DrawText(g, "most " + Fmt0(l, max), Theme.Small, new Point(16, r.Top + 16), Theme.Muted);
                using (Pen axis = new Pen(Color.FromArgb(34, 52, 74))) g.DrawLine(axis, r.Left, r.Bottom, r.Right, r.Bottom);
                if (n < 2 || max <= 0) continue;
                PointF[] pts = l.Values.Take(n).Select((v, i) => new PointF(XOf(i, r), (float)(r.Bottom - v / max * r.Height))).ToArray();
                using (GraphicsPath area = new GraphicsPath())
                {
                    area.AddLines(pts);
                    area.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, r.Bottom));
                    area.AddLine(new PointF(pts[0].X, r.Bottom), pts[0]);
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(50, l.Color))) g.FillPath(fill, area);
                }
                using (Pen p = new Pen(l.Color, 1.6f)) g.DrawLines(p, pts);
            }
            if (n >= 2 && _lanes.Count > 0)
            {
                Rectangle r = LaneRect(_lanes.Count - 1);
                int lastRight = int.MinValue;
                foreach (int i in LabelIndexes(n, Math.Max(2, r.Width / 110)))
                {
                    DateTime t = _times[i].ToLocalTime();
                    string label = _hourly ? (t.Hour == 0 || n <= 30 ? t.ToString("MM-dd HH:00") : t.ToString("HH:00")) : t.ToString("MM-dd");
                    Size sz = TextRenderer.MeasureText(label, Theme.Small);
                    float x = XOf(i, r);
                    int left = (int)Math.Min(Width - sz.Width, Math.Max(r.Left - 4, x - sz.Width / 2f));
                    if (left < lastRight + 10) continue;          // it would run into the one before
                    TextRenderer.DrawText(g, label, Theme.Small, new Point(left, r.Bottom + 6), Theme.Muted);
                    lastRight = left + sz.Width;
                }
            }
            if (!string.IsNullOrEmpty(HoverText)) using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillEllipse(b, HoverAt.X - 4, HoverAt.Y - 4, 8, 8);
            DrawHover(g);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = _times.Length;
            if (n < 2) return;
            for (int li = 0; li < _lanes.Count; li++)
            {
                Rectangle r = LaneRect(li);
                if (e.Y < r.Top - 4 || e.Y > r.Bottom + 4 || e.X < r.Left - 6 || e.X > r.Right + 6) continue;
                Lane l = _lanes[li];
                double max = l.Values.Length > 0 ? l.Values.Max() : 0;
                int i = Math.Max(0, Math.Min(n - 1, (int)Math.Round((e.X - r.Left) / (double)Math.Max(1, r.Width) * (n - 1))));
                float y = max > 0 ? (float)(r.Bottom - l.Values[i] / max * r.Height) : r.Bottom;
                if (Math.Abs(y - e.Y) > 10) break;          // only on the line itself
                DateTime t = _times[i].ToLocalTime();
                SetHover(l.Name + ": " + Fmt0(l, l.Values[i]) + "\n" + (_hourly ? t.ToString("ddd MM-dd HH:00") : t.ToString("ddd yyyy-MM-dd")), new PointF(XOf(i, r), y));
                return;
            }
            SetHover(null, PointF.Empty);
        }
    }
}
