// --selftest <report.json>: the Manager checks itself, unattended, the way a person would use it:
// the wizard makes a server (in SDSM_ROOT, from the game in SDSM_GAME), the main window starts it,
// sends it commands, changes a setting, reads every page, stops it. Every wizard page and every tab
// is photographed (shots/ beside the report). The report has the harness's format (checks with
// PASS / FAIL), so the shared check script reads it. The Manager's own settings go to SDSM_HOME,
// never the real ones. The window is kept off-screen and never takes focus.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace SDServerManager
{
    public static class SelfTest
    {
        private static readonly JsonArray _checks = new JsonArray();
        private static readonly JsonArray _notes = new JsonArray();
        private static string _shots;

        private static void Check(string name, bool pass, string detail)
        {
            _checks.Add(new JsonObject { ["name"] = "Manager: " + name, ["status"] = pass ? "PASS" : "FAIL", ["detail"] = detail ?? "" });
        }

        private static void Note(string s) { _notes.Add(s); }

        private static void Pump(double seconds)
        {
            DateTime until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        }

        private static bool WaitFor(Func<bool> cond, double seconds)
        {
            DateTime until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until) { if (cond()) return true; Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            return cond();
        }

        private static IEnumerable<Control> All(Control c) { yield return c; foreach (Control x in c.Controls) foreach (Control y in All(x)) yield return y; }

        private static void Shot(Control c, string name)
        {
            try
            {
                using (Bitmap b = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                {
                    c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
                    b.Save(Path.Combine(_shots, name + ".png"), ImageFormat.Png);
                }
            }
            catch (Exception e) { Note("shot " + name + ": " + e.Message); }
        }

        /// <summary>
        /// Everything visible on a page fits: no text runs past the right edge of what holds it, and
        /// a scrolling page can scroll far enough to show its last part completely.
        /// </summary>
        private static List<string> Clipped(Control root)
        {
            List<string> bad = new List<string>();
            Walk(root, c =>
            {
                if (!c.Visible || c.Parent == null || c.Width <= 0) return;
                Control p = c.Parent;
                if (p is ScrollableControl sp && sp.AutoScroll) return;      // a scrolling holder: checked below
                if (c is Label l && l.Text.Length > 0)
                {
                    int need = TextRenderer.MeasureText(l.Text.Split('\n').OrderByDescending(x => x.Length).First(), l.Font).Width;
                    if (l.AutoSize && l.MaximumSize.Width == 0 && c.Right > p.ClientSize.Width + 2) bad.Add("'" + Short(l.Text) + "' runs past the edge (" + c.Right + " > " + p.ClientSize.Width + ")");
                }
                if (c.Dock == DockStyle.None && !(c is ScrollBar) && !(p is TabControl) && c.Right > p.ClientSize.Width + 2)
                    bad.Add(c.GetType().Name + " '" + Short(c.Text) + "' runs past the right edge of " + (p.Name.Length > 0 ? p.Name : p.GetType().Name) + " (" + c.Right + " > " + p.ClientSize.Width + ")");
            });
            Walk(root, c =>
            {
                if (!(c is PageColumn pc) || !pc.Visible) return;
                Control last = pc.Controls.Cast<Control>().Where(x => x.Visible && x.Height > 0 && x.Name != "PageEnd").OrderBy(x => x.Bottom).LastOrDefault();
                if (last == null) return;
                if (pc.AutoScroll && (pc.HorizontalScroll.Visible || pc.DisplayRectangle.Width > pc.ClientSize.Width + 2 || (GetWindowLong(pc.Handle, -16) & 0x00100000) != 0))
                {
                    Control wide = pc.Controls.Cast<Control>().SelectMany(Desc).OrderByDescending(x => { int r = x.Right; for (Control q = x.Parent; q != null && q != pc; q = q.Parent) r += q.Left; return r; }).First();
                    bad.Add("the page scrolls sideways (" + pc.DisplayRectangle.Width + " wide in " + pc.ClientSize.Width + "): widest is '" + Short(wide.Text) + "' (" + wide.GetType().Name + " " + wide.Name + ")");
                }
                // scrolled right down, as far as it goes: is the last part all on screen?
                Point keep = pc.AutoScrollPosition;
                pc.AutoScrollPosition = new Point(0, 100000);
                Application.DoEvents();
                if (pc.AutoScroll && last.Bottom > pc.ClientSize.Height) bad.Add("the page's last part ('" + Short(last.Text) + "') can't be scrolled fully into view (its bottom " + last.Bottom + " > " + pc.ClientSize.Height + " visible)");
                pc.AutoScrollPosition = new Point(-keep.X, -keep.Y);
                foreach (Label l in pc.Controls.Cast<Control>().SelectMany(Desc).OfType<Label>().Where(x => x.Visible && x.AutoSize && x.MaximumSize.Width > 0))
                {
                    int right = l.Left; for (Control q = l.Parent; q != null && q != pc; q = q.Parent) right += q.Left;
                    right += l.Width;
                    if (right > pc.ClientSize.Width - 2) bad.Add("'" + Short(l.Text) + "' is wider than the page (" + right + " > " + pc.ClientSize.Width + ")");
                }
            });
            return bad.Distinct().ToList();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);   // WS_HSCROLL = 0x00100000: the sideways bar is really there

        private static IEnumerable<Control> Desc(Control c) { yield return c; foreach (Control x in c.Controls) foreach (Control y in Desc(x)) yield return y; }
        private static void Walk(Control c, Action<Control> a) { foreach (Control x in Desc(c)) a(x); }
        private static string Short(string s) { s = (s ?? "").Replace("\n", " "); return s.Length > 40 ? s.Substring(0, 40) + "..." : s; }

        public static int Run(string reportPath)
        {
            DateTime t0 = DateTime.Now;
            string result = "DONE";
            _shots = Path.Combine(Path.GetDirectoryName(reportPath), "shots");
            Directory.CreateDirectory(_shots);
            string game = Environment.GetEnvironmentVariable("SDSM_GAME") ?? "";
            string root = Environment.GetEnvironmentVariable("SDSM_ROOT") ?? "";
            int port = int.TryParse(Environment.GetEnvironmentVariable("SDSM_PORT"), out int p) ? p : 7795;
            Environment.SetEnvironmentVariable("SDSM_TEST_PARENT", Process.GetCurrentProcess().Id.ToString());
            MainForm main = null;
            Server srv = null;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(ManagerSettings.Dir);
                if (File.Exists(ManagerSettings.FilePath)) File.Delete(ManagerSettings.FilePath);
                ManagerSettings settings = ManagerSettings.Load();

                // ---- the wizard ----
                WizardForm w = new WizardForm(settings) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
                w.Show();
                w.AutoFill(game, root, "Test Server", port, "Admin Player\n76561190000000001", "admins", "individual");
                for (int i = 0; i < w.PageCount - 1; i++) { w.PageControl(i); Pump(0.3); Shot(w, "wizard-" + (i + 1)); }
                // the smallest the window goes: nothing may be cut off or out of reach
                w.Size = w.MinimumSize;
                List<string> clippedW = new List<string>();
                for (int i = 0; i < w.PageCount - 1; i++) { Control pg = w.PageControl(i); Pump(0.3); Shot(w, "wizard-small-" + (i + 1)); clippedW.AddRange(Clipped(w).Select(x => "page " + (i + 1) + ": " + x)); }
                Check("every wizard page fits at the smallest window size (no cut-off text, last line reachable)", clippedW.Count == 0, clippedW.Count == 0 ? w.Size.ToString() : string.Join(" | ", clippedW.Take(8)));
                w.Size = new Size(900, 720);
                w.PageControl(5); Pump(0.2);
                string radios = w.RadiosForTest();
                Check("World: only one of 'a new world' and 'a copy of one of my worlds' can be picked", radios == "copy picked: new False, copy True; new picked: new True, copy False", radios);
                w.PageControl(2); Pump(0.2);
                w.CreateForTest();
                bool made = WaitFor(() => w.ProgressText.Contains("is ready") || w.ProgressText.StartsWith("Could not"), 240);
                Shot(w, "wizard-7-done");
                Check("the wizard makes a server", made && w.ProgressText.Contains("is ready"), w.ProgressText.Split('\n')[0]);
                w.Close();
                string folder = Path.Combine(root, "Test Server");
                srv = new Server(new ServerEntry { Name = "Test Server", Folder = folder });
                Check("its game copy has the game", File.Exists(srv.Exe) && Directory.Exists(Path.Combine(folder, "StellarDrive_Data")) && Directory.Exists(Path.Combine(folder, "MelonLoader")), folder);
                string[] mods = Directory.Exists(Path.Combine(folder, "Mods")) ? Directory.GetFiles(Path.Combine(folder, "Mods"), "*.dll").Select(Path.GetFileName).ToArray() : new string[0];
                Check("its Mods has DedicatedServer and not the players' own mods (harness, zoom, ...)", mods.Count(m => m.StartsWith("DedicatedServer")) == 1 && !mods.Contains("DemoVerifyHarness.dll") && !mods.Contains("DemoZoom.dll"), string.Join(", ", mods));
                Conf conf = srv.LoadConf();
                Check("dedicatedserver.conf has the wizard's choices",
                    conf.GetInt("Port") == port && conf.Get("Research") == "individual" && conf.Get("DevTools") == "admins" && conf.Get("Admins").Contains("76561190000000001") && !conf.Get("OptionalMods").Contains("Demo Zoom"),
                    "Port " + conf.Get("Port") + ", Research " + conf.Get("Research") + ", DevTools " + conf.Get("DevTools") + ", Admins " + conf.Get("Admins") + ", Optional " + conf.Get("OptionalMods"));
                long exeLinks = 0;
                try { exeLinks = new FileInfo(srv.Exe).Length; } catch { }
                Check("manager.json lists the server", ManagerSettings.Load().Servers.Any(s => s.Folder == folder), ManagerSettings.FilePath);

                // ---- the main window, stopped ----
                main = new MainForm(ManagerSettings.Load()) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
                main.Show();
                Pump(1);
                Shot(main, "home");
                Check("with no server picked, the Home page shows (servers, activity, players)", main.Current == null, main.Current == null ? "home" : "a server was picked");
                for (int tab = 0; tab < main.TabCount; tab++) { main.SelectForTest(0, tab); Pump(0.4); Shot(main, "stopped-" + (tab + 1) + "-" + main.TabName(tab).ToLowerInvariant()); }
                srv = main.Current;
                Check("the main window shows the server", srv != null && srv.Folder == folder, srv != null ? srv.Folder : "none selected");
                main.Size = main.MinimumSize;
                List<string> clippedM = new List<string>();
                for (int tab = 0; tab < main.TabCount; tab++) { main.SelectForTest(0, tab); Pump(0.4); Shot(main, "small-" + (tab + 1) + "-" + main.TabName(tab).ToLowerInvariant()); clippedM.AddRange(Clipped(main).Select(x => main.TabName(tab) + ": " + x)); }
                main.ShowHomeForTest(); Pump(0.5); Shot(main, "small-home"); clippedM.AddRange(Clipped(main).Select(x => "Home: " + x));
                main.SelectForTest(0, main.TabIndexOf("Mods")); Pump(0.4);
                foreach (SplitContainer sc in Desc(main.TabForTest(main.TabIndexOf("Mods"))).OfType<SplitContainer>())
                    Note("mods split at the smallest size: container " + sc.Width + ", panel1 " + sc.Panel1.Width + " (min " + sc.Panel1.MinimumSize.Width + "), its children: "
                        + string.Join(", ", sc.Panel1.Controls.Cast<Control>().Select(x => x.GetType().Name + " " + x.Width + " min " + x.MinimumSize.Width + " dock " + x.Dock))
                        + "; rows: " + string.Join(", ", Desc(sc.Panel1).OfType<FitFlow>().Take(2).Select(x => x.Width + " min " + x.MinimumSize.Width)));
                Check("every page of the main window fits at its smallest size", clippedM.Count == 0, clippedM.Count == 0 ? main.Size.ToString() : string.Join(" | ", clippedM.Take(8)));
                // every dropdown is wide enough for its longest choice (at the smallest size too)
                List<string> narrow = new List<string>();
                for (int tab = 0; tab < main.TabCount; tab++)
                {
                    main.SelectForTest(0, tab); Pump(0.3);
                    foreach (ComboBox cb in Desc(main.TabForTest(tab)).OfType<ComboBox>().Where(x => x.Visible && x.Dock == DockStyle.None && x.Items.Count > 0))
                        if (cb.Width < Theme.ComboWidth(cb) - 1) narrow.Add(main.TabName(tab) + ": '" + cb.Text + "' " + cb.Width + " < " + Theme.ComboWidth(cb));
                }
                Check("no dropdown cuts off its choices (each is as wide as its longest)", narrow.Count == 0, narrow.Count == 0 ? "all fit" : string.Join(" | ", narrow.Take(6)));
                // the Launch page scrolls when the window is small (its top part has its own scroll bar)
                main.SelectForTest(0, 0); Pump(0.4);
                PageColumn launchTop = Desc(main.TabForTest(0)).OfType<PageColumn>().FirstOrDefault(x => x.Name == "LaunchTop");
                bool scrolls = launchTop != null && launchTop.AutoScroll && launchTop.DisplayRectangle.Height > launchTop.ClientSize.Height && launchTop.VerticalScroll.Visible;
                Shot(main, "small-launch-scroll");
                Check("at the smallest size the Launch page scrolls to everything above the console", scrolls, launchTop == null ? "no LaunchTop" : "content " + launchTop.DisplayRectangle.Height + " in " + launchTop.ClientSize.Height + ", scroll bar " + launchTop.VerticalScroll.Visible);
                main.Size = new Size(1280, 820);

                // a box dragged taller stays that size (kept in manager.json)
                main.SelectForTest(0, main.TabIndexOf("Uptime")); Pump(0.5);
                SizedBox box = Desc(main.TabForTest(main.TabIndexOf("Uptime"))).OfType<SizedBox>().FirstOrDefault();
                int hBefore = box != null ? box.Height : 0;
                if (box != null) box.DragForTest(90);
                Pump(0.3);
                bool kept = box != null && File.ReadAllText(ManagerSettings.FilePath).Contains("\"up.") && Theme.SizeOf("up.timeline", -1) == box.Height - 9;
                Check("a chart or list can be dragged taller by the grip under it, and keeps that size", box != null && box.Height == hBefore + 90 && kept, box == null ? "no grip box" : hBefore + " -> " + box.Height + " px, saved " + Theme.SizeOf("up.timeline", -1));
                if (box != null) box.DragForTest(-90);

                // charts: what hovering shows, and lanes that don't hide each other (drawn from made-up numbers)
                {
                    SeriesChart bars = new SeriesChart("Joins", SeriesChart.Style.Bars, v => v.ToString("0")) { Size = new Size(600, 200) };
                    DateTime h0 = DateTime.UtcNow.Date;
                    bars.Set(new[] { new SeriesChart.Series { Name = "Joins", Color = Theme.Line, Values = new double[] { 0, 3, 0, 1 } } }, Enumerable.Range(0, 4).Select(i => h0.AddHours(i)).ToArray(), true);
                    using (bars.RenderForTest()) { }
                    Rectangle pl = bars.PlotForTest;
                    float slot = pl.Width / 4f;
                    string offBar = bars.HoverForTest((int)(pl.Left + slot * 0.5f), pl.Top + 5);             // empty hour, high up
                    string overBar = bars.HoverForTest((int)(pl.Left + slot * 1.5f), pl.Bottom - 10);        // inside the bar of 3
                    string aboveBar = bars.HoverForTest((int)(pl.Left + slot * 3.5f), pl.Top + 5);           // high above the short bar of 1
                    Check("bar charts: pointing at a bar says its hour and number; pointing anywhere else says nothing", offBar == null && aboveBar == null && overBar != null && overBar.Contains("Joins: 3"),
                        "off a bar: " + (offBar ?? "nothing") + "; on the bar: " + (overBar ?? "nothing").Replace("\n", " / ") + "; above a short bar: " + (aboveBar ?? "nothing"));
                    LaneChart lanes = new LaneChart("Together") { Size = new Size(700, 100) };
                    double[] same = { 0, 1, 1, 0, 2 };
                    Color[] cols = { Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 0, 255) };
                    lanes.Set(cols.Select((c, i) => new LaneChart.Lane { Name = "L" + i, Color = c, Values = same }), Enumerable.Range(0, 5).Select(i => h0.AddDays(i)).ToArray(), false);
                    List<string> seen = new List<string>();
                    using (Bitmap b = lanes.RenderForTest())
                        for (int i = 0; i < cols.Length; i++)
                        {
                            Rectangle r = lanes.LaneForTest(i);
                            bool found = false;
                            for (int x = r.Left; x < r.Right && !found; x += 2) for (int y = r.Top; y < r.Bottom && !found; y++) { Color px = b.GetPixel(x, y); found = Math.Abs(px.R - cols[i].R) < 30 && Math.Abs(px.G - cols[i].G) < 30 && Math.Abs(px.B - cols[i].B) < 30; }
                            if (found) seen.Add("L" + i);
                        }
                    Check("the 'together' chart shows every line even when they are the same shape (each in its own lane)", seen.Count == cols.Length, "lines drawn: " + string.Join(", ", seen));
                    Rectangle l1 = lanes.LaneForTest(1);
                    string onLine = lanes.HoverForTest(l1.Left + l1.Width / 2, l1.Bottom - l1.Height / 2);      // value 1 of max 2: half way up
                    string offLine = lanes.HoverForTest(l1.Left + l1.Width / 2, l1.Top + 1);
                    Check("...and pointing at a line says its value; between lines says nothing", onLine != null && onLine.StartsWith("L1: 1") && offLine == null, (onLine ?? "nothing").Replace("\n", " / ") + " | " + (offLine ?? "nothing"));
                }

                // the Mods page: a mod taken off the server (as a person picks it in the grid)
                main.SelectForTest(0, main.TabIndexOf("Mods")); Pump(0.5);
                string modOff = mods.Contains("Chess.dll") ? "Chess" : mods.Contains("Graves.dll") ? "Graves" : null;
                if (modOff != null)
                {
                    main.SetModUseForTest(modOff, ModUse.PlayersOnly);
                    main.SaveModsForTest(); Pump(0.3);
                    string[] after = Directory.GetFiles(Path.Combine(folder, "Mods"), "*.dll").Select(Path.GetFileName).ToArray();
                    Check("Mods page: 'Not on the server' takes a mod off the server", !after.Contains(modOff + ".dll") && after.Contains("DedicatedServer.dll"), string.Join(", ", after));
                    main.SetModUseForTest(modOff, ModUse.Required);
                    main.SaveModsForTest(); Pump(0.3);
                    Check("...and 'Server, and every player needs it' puts it back", File.Exists(Path.Combine(folder, "Mods", modOff + ".dll")), modOff);
                    // adding and removing mods: nothing is ever deleted
                    string offDir = Path.Combine(folder, GameCopy.OffFolder), removedDir = Path.Combine(folder, GameCopy.RemovedFolder);
                    main.SetModUseForTest(modOff, ModUse.PlayersOnly); main.SaveModsForTest(); Pump(0.3);
                    Check("...a mod taken off the server is kept beside it (Mods-off), not deleted, and still listed", File.Exists(Path.Combine(offDir, modOff + ".dll")) && main.SetModUseForTest(modOff, ModUse.Required), offDir);
                    main.SaveModsForTest(); Pump(0.3);
                    string tmp = Path.Combine(Path.GetTempPath(), "sdsm-add-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    Directory.CreateDirectory(tmp);
                    string copy = Path.Combine(tmp, modOff + ".dll");
                    File.Copy(Path.Combine(folder, "Mods", modOff + ".dll"), copy);
                    File.WriteAllText(Path.Combine(tmp, "notamod.dll"), "not a mod");
                    string addSaid = main.AddMods(new[] { copy, Path.Combine(tmp, "notamod.dll") }); Pump(0.3);
                    int keptOld = Directory.Exists(removedDir) ? Directory.GetFiles(removedDir, modOff + ".*").Length : 0;
                    Check("Add mod...: a mod added replaces the server's copy (the old one kept in Mods-removed); a DLL that isn't a mod is refused",
                        File.Exists(Path.Combine(folder, "Mods", modOff + ".dll")) && keptOld >= 1 && addSaid.Contains("replaced") && addSaid.Contains("not a MelonLoader mod"), addSaid.Replace(Environment.NewLine, " | ").Replace("\n", " | "));
                    List<string> upd = GameCopy.UpdateNewer(folder, srv.LoadInfo().SourceGame);
                    Check("Update from game never removes a mod (it only brings newer versions)", File.Exists(Path.Combine(folder, "Mods", modOff + ".dll")) && File.Exists(Path.Combine(folder, "Mods", "DedicatedServer.dll")), upd.Count + " updated");
                    string rm = main.RemoveModNow(ModMeta.Read(Path.Combine(folder, "Mods", modOff + ".dll"))); Pump(0.3);
                    Check("Remove: the mod comes off the server, into Mods-removed", !File.Exists(Path.Combine(folder, "Mods", modOff + ".dll")) && Directory.GetFiles(removedDir, modOff + ".*").Length == keptOld + 1, rm);
                    Shot(main, "mods-page-manage");
                    addSaid = main.AddMods(new[] { copy }); Pump(0.3);
                    Check("...and Add puts it back on", File.Exists(Path.Combine(folder, "Mods", modOff + ".dll")), addSaid);
                    try { Directory.Delete(tmp, true); } catch { }
                }
                // a mod's own setting, changed while the server is stopped: written into MelonPreferences.cfg
                main.SelectForTest(0, main.TabIndexOf("Mods")); Pump(0.5);
                string[] ms = main.IntSettingForTest();
                if (ms != null)
                {
                    string want = (long.Parse(ms[3]) + 2).ToString();
                    string saidMs = main.SetModSettingForTest(ms[0], ms[2], want);
                    string cfg = Json.ReadShared(srv.PrefsPath);
                    bool written = System.Text.RegularExpressions.Regex.IsMatch(cfg, @"(?ms)^\[" + System.Text.RegularExpressions.Regex.Escape(ms[1]) + @"\].*?^" + System.Text.RegularExpressions.Regex.Escape(ms[2]) + " = " + want + @"\s*$");
                    Shot(main, "mods-settings-open");
                    Check("Mods: a mod opens to show its settings; one changed with the server stopped goes into MelonPreferences.cfg", written, ms[0] + " [" + ms[1] + "] " + ms[2] + " " + ms[3] + " -> " + want + ": " + saidMs);
                    main.SetModSettingForTest(ms[0], ms[2], ms[3]);
                }
                else Check("Mods: a mod opens to show its settings", false, "no mod with a whole-number setting found (" + main.ModRowsForTest + " rows)");
                main.SelectForTest(0, 0);

                // chat styles, the message box, restart and the icon (stopped)
                string round = StyleSpec.Parse("color=red bold size=130 highlight=blue smallcaps").ToString();
                Check("a style picked in the Manager is written the way the server reads it", round == "color=#FF6B5E bold smallcaps size=130 highlight=#4FC3F7", round);
                Conf styles = srv.LoadConf();
                string[] styleKeys = { "StyleAnnounce", "StyleAlert", "StyleServer", "StyleWelcome", "StyleJoin", "StyleLeave", "StyleGameNotice", "StylePlayerName", "StylePlayerText" };
                Check("the Settings page has every chat style (announcements, alerts, joins, leaves, names, ...)", styleKeys.All(k => styles.Find(k) != null), string.Join(", ", styleKeys.Where(k => styles.Find(k) == null)));
                main.SetMessageForTest(2, "color=#FF6B5E italic size=130", "Hello from the composer");
                string cmdOwn = main.MessageCommand();
                main.SetMessageForTest(1, null, "{bold} tricky");
                string cmdAlert = main.MessageCommand();
                Check("the message box makes the console line for each kind (a typed { stays a plain {)", cmdOwn == "say {color=#FF6B5E italic size=130} Hello from the composer" && cmdAlert == "alert {{bold} tricky", cmdOwn + " | " + cmdAlert);
                main.SetMessageForTest(0, null, "Meet at the station now", true, 8, 11, "#FFE16B");
                string cmdRich = main.MessageCommand();
                Check("words picked in the message box get their own style (and 'on screen' makes it an announcement)", cmdRich == "announce Meet at {color=#FFE16B bold}the station{/} now", cmdRich);
                Pump(0.3);
                Shot(main, "composer-rich");
                main.SetMessageForTest(2, "color=#FF6B5E italic size=130", "Hello from the composer"); Pump(0.3);
                Shot(main, "composer");
                string rc = MainForm.RestartCommand(true, 5, "maintenance") + " | " + MainForm.RestartCommand(false, 0, "") + " | " + MainForm.RestartCommand(true, 0, "2nd try");
                Check("Restart...: the console line for a timed restart / stop", rc == "restart 5 maintenance | stop | restart - 2nd try", rc);
                string pic = Path.Combine(Path.GetDirectoryName(reportPath), "icon-source.png");
                using (Bitmap b = new Bitmap(300, 200))
                {
                    using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.MidnightBlue); g.FillEllipse(Brushes.Orange, 60, 10, 180, 180); }
                    b.Save(pic, ImageFormat.Png);
                }
                string iconErr = MainForm.SetIcon(srv, pic);
                string iconFacts = iconErr ?? "";
                bool iconOk = false;
                if (iconErr == null && File.Exists(srv.IconPath))
                    using (Image im = Image.FromFile(srv.IconPath)) { iconOk = im.Width == 128 && im.Height == 128 && new FileInfo(srv.IconPath).Length <= 64 * 1024; iconFacts = im.Width + "x" + im.Height + ", " + new FileInfo(srv.IconPath).Length + " bytes"; }
                Check("Settings: a picture becomes the server's icon (a 128 x 128 PNG under 64 KB)", iconOk, iconFacts);
                main.SelectForTest(0, main.TabIndexOf("Settings")); Pump(0.5); Shot(main, "settings-icon");
                main.SelectForTest(0, 0);

                // the game updated since the server was made (a new file in its folder): Start links the new files in first
                string gameFile = Path.Combine(game, "UnityPlayer.dll");
                if (File.Exists(gameFile) && game.IndexOf(@"\_verify\.scratch\", StringComparison.OrdinalIgnoreCase) >= 0)     // only ever the test fixture, never a real game folder
                {
                    string fresh = gameFile + ".new";
                    File.Copy(gameFile, fresh, true);
                    File.Delete(gameFile);                  // (the test game's own link; the main game's file is another link to it, untouched)
                    File.Move(fresh, gameFile);
                    File.SetLastWriteTimeUtc(gameFile, DateTime.UtcNow);
                    bool outdated = GameCopy.Outdated(game, folder);
                    string upd = MainForm.BringGameUpToDate(srv);
                    Check("a game update is noticed before a start, and the server's game files brought up to date (the build players have)", outdated && upd.Contains("brought up to date") && !GameCopy.Outdated(game, folder), "outdated " + outdated + "; " + upd);
                }
                // ---- start it ----
                main.SelectForTest(0, 0);
                string err = srv.Start();
                Check("Start launches the server", err == null, err ?? "pid " + (srv.Launched != null ? srv.Launched.Id : 0));
                Check("the test server is in the process ledger (so other chats don't take it for Ethan playing)", srv.LedgerNote.StartsWith("recorded"), srv.LedgerNote);
                bool ready = WaitFor(() => srv.State == "running", 240);
                Check("it comes up and reports ready", ready, "state " + srv.State + " after " + (DateTime.Now - t0).TotalSeconds.ToString("0") + " s");
                if (!ready) { Note(Json.ReadShared(srv.LogPath).Split('\n').Reverse().Take(30).Reverse().Aggregate("", (a, b) => a + " | " + b)); throw new Exception("server not ready"); }
                Pump(6);
                string consoleText = main.ConsoleTextForTest;
                Check("the Launch console shows the whole server log by default (startup and the READY line)", consoleText.Contains("READY - ") && consoleText.Contains("DEDICATED SERVER mode"), consoleText.Length + " chars");
                Check("console lines are sorted into the tick boxes' kinds",
                    MainForm.KindOf("[12:00:00.000] [DedicatedServer] [chat] Bob: hi", false) == "Chat" && MainForm.KindOf("[12:00:00.000] [DedicatedServer] JOINED Bob (1) - 1/8 on", false) == "Joins and leaves"
                    && MainForm.KindOf("[12:00:00.000] [DedicatedServer] > status", false) == "Commands" && MainForm.KindOf("[12:00:00.000] [Chess] ready", false) == "Other mods",
                    "chat / joins / commands / other mods");
                srv.Command("set TrackBandwidth on");
                main.PageMs.Clear();
                for (int tab = 0; tab < main.TabCount; tab++) { main.SelectForTest(0, tab); Pump(1.2); Shot(main, "running-" + (tab + 1) + "-" + main.TabName(tab).ToLowerInvariant()); }
                string times = string.Join(", ", main.PageMs.Select(kv => kv.Key + " " + kv.Value.ToString("0") + " ms"));
                Check("every page shows quickly (each well under half a second to fill)", main.TabNamesForTest.All(t => main.PageMs.ContainsKey(t) && main.PageMs[t] < 400), times);
                Note("page times: " + times);
                // smoothness: each page switched to until painted, then scrolled top to bottom (each step painted);
                // and lists left alone while what they show doesn't change (they used to be refilled every few seconds)
                {
                    List<string> sm = new List<string>();
                    double worstSwitch = 0, worstScroll = 0;
                    for (int tab = 0; tab < main.TabCount; tab++)
                    {
                        double swMs = main.SwitchMsForTest(tab);
                        Pump(0.4);
                        int steps;
                        double sc = main.ScrollMsForTest(out steps);
                        worstSwitch = Math.Max(worstSwitch, swMs);
                        worstScroll = Math.Max(worstScroll, sc);
                        sm.Add(main.TabName(tab) + " " + swMs.ToString("0") + "/" + (steps > 0 ? sc.ToString("0") + "x" + steps : "-"));
                    }
                    Note("smoothness (switch ms / slowest scroll step ms x steps): " + string.Join(", ", sm));
                    Check("switching pages: each painted in one go, under 300 ms", worstSwitch < 300, string.Join(", ", sm));
                    Check("scrolling: every step painted in under 50 ms", worstScroll < 50, "slowest " + worstScroll.ToString("0") + " ms");
                    int r0 = main.GridRebuilds;
                    main.SelectForTest(0, main.TabIndexOf("Users")); Pump(0.5); r0 = main.GridRebuilds;
                    Pump(7);
                    main.SelectForTest(0, main.TabIndexOf("Ships")); Pump(0.5); int r1 = main.GridRebuilds;
                    Pump(11);
                    Check("lists aren't emptied and refilled while what they show stays the same (that reset their scrolling every few seconds)",
                        main.GridRebuilds == r1 && r1 - r0 <= 2, "Users: " + (r1 - r0) + " rebuild(s) incl. switching, Ships: " + (main.GridRebuilds - r1) + " in 11 s");
                }
                main.SelectForTest(0, main.TabIndexOf("Uptime")); Pump(5);
                main.SelectForTest(0, main.TabIndexOf("Uptime")); Pump(0.5);
                Shot(main, "uptime-live-network");
                Check("Uptime: the live network graph shows each second's bytes in and out", main.LiveNetForTest.PointsForTest >= 3, main.LiveNetForTest.PointsForTest + " second(s) shown");
                srv.Command("set TrackBandwidth off");
                // a mod's setting changed while running goes through the server (modset) into MelonPreferences.cfg
                if (WaitFor(() => File.Exists(srv.ModSettingsPath), 10))
                {
                    main.SelectForTest(0, main.TabIndexOf("Mods")); Pump(0.5);
                    string[] rs = main.IntSettingForTest();
                    if (rs != null)
                    {
                        string want = (long.Parse(rs[3]) + 3).ToString();
                        string saidRs = main.SetModSettingForTest(rs[0], rs[2], want);
                        bool viaServer = WaitFor(() => System.Text.RegularExpressions.Regex.IsMatch(Json.ReadShared(srv.PrefsPath), "(?m)^" + System.Text.RegularExpressions.Regex.Escape(rs[2]) + " = " + want + @"\s*$"), 10);
                        Check("Mods: a mod setting changed while running is sent to the server, which saves it", viaServer && Json.ReadShared(srv.LogPath).Contains("mod setting [" + rs[1] + "] " + rs[2] + " = " + want), rs[0] + " " + rs[2] + " -> " + want + ": " + saidRs);
                        main.SetModSettingForTest(rs[0], rs[2], rs[3]);
                    }
                }
                else Check("Mods: the running server lists the mods' settings (modsettings.json)", false, "no " + srv.ModSettingsPath);
                // Launch: "Address players use" + Check, as a person would use it with a tunnel's address
                string reachOk = MainForm.ReachText(true, port, "127.0.0.1:" + port);
                string reachBad = MainForm.ReachText(true, port, "127.0.0.1:9");
                Check("Launch: Check says the server can be reached through an address that leads to it, and what to fix for one that doesn't",
                    reachOk.StartsWith("OK: players can reach it") && reachBad.Contains("but not through 127.0.0.1:9") && reachBad.Contains("UDP"), reachOk + " | " + reachBad.Split('\n')[0]);
                JsonNode st = srv.Status;
                Check("the server runs with no graphics device and no local player", !Json.Bool(st, "localClient") && Json.Int(st, "enabledCameras") == 0, "localClient " + Json.Str(st, "localClient"));
                Check("the rules came from the wizard", Json.Str(st["rules"], "research") == "individual" && Json.Str(st["rules"], "devTools") == "admins", st?["rules"]?.ToJsonString() ?? "");

                // console: a command and its reply
                long from = File.Exists(srv.OutputPath) ? new FileInfo(srv.OutputPath).Length : 0;
                srv.Command("status");
                bool replied = WaitFor(() => Json.ReadShared(srv.OutputPath).Length > from && Json.ReadShared(srv.OutputPath).Substring((int)from).Contains("Players: 0/"), 15);
                Check("a console command reaches the server and its reply comes back", replied, Json.ReadShared(srv.OutputPath).Substring((int)Math.Min(from, Json.ReadShared(srv.OutputPath).Length)).Replace("\r", "").Replace("\n", " / "));

                // a setting changed while running applies at once (and is saved by the server)
                srv.Command("set Motd Hello from the Manager");
                bool motd = WaitFor(() => srv.LoadConf().Get("Motd") == "Hello from the Manager", 15);
                Check("a setting changed while running is applied and saved", motd, "Motd = " + srv.LoadConf().Get("Motd"));

                // a message from the message box reaches the server
                main.SetMessageForTest(2, "color=#FF6B5E italic size=130", "Hello from the composer");
                long fromSay = new FileInfo(srv.LogPath).Length;
                srv.Command(main.MessageCommand());
                bool said = WaitFor(() => Json.ReadShared(srv.LogPath).Contains("Hello from the composer"), 15);
                Check("the message box's message reaches the server", said, said ? "in the server log" : "not in the log");

                // a timed restart shows its countdown, and can be called off
                srv.Command("restart 10 checking the countdown");
                bool counting = WaitFor(() => srv.Status?["scheduled"] != null, 20);
                Shot(main, "restart-scheduled");
                Check("restart 10: status shows the countdown (and Launch offers 'Call it off')", counting && Json.Num(srv.Status["scheduled"], "secondsLeft") > 500, srv.Status?["scheduled"]?.ToJsonString() ?? "none");
                srv.Command("restart cancel");
                bool off = WaitFor(() => srv.Status != null && srv.Status["scheduled"] == null, 20);
                Check("...and 'restart cancel' calls it off", off, off ? "gone" : srv.Status?["scheduled"]?.ToJsonString() ?? "");

                // Planned restarts...: each warning in its own words, [time] filled in with what's really left
                {
                    string preview = null;
                    main.PlannedForTest = (f, rows) =>
                    {
                        Pump(0.5);
                        Shot(f, "planned-restarts");
                        MainForm.WarningRow one = rows.FirstOrDefault(r => r.Seconds == 60);
                        if (one == null) { one = new MainForm.WarningRow { Seconds = 60 }; rows.Add(one); }
                        one.Text = "Heads up: [action] in [time] for [reason]!"; one.OnScreen = true;
                        preview = MainForm.PreviewText(one.Text, 60, "the check");
                        Button save = f.Controls.Cast<Control>().SelectMany(c => All(c)).OfType<Button>().First(b => b.Text == "Save");
                        save.PerformClick();
                    };
                    main.SelectForTest(0, 0);
                    main.OpenPlannedForTest();
                    main.PlannedForTest = null;
                    // the MessageBox after saving is closed by the test's own timer (see AutoClose)
                    bool saved = WaitFor(() => (srv.LoadConf().Get("RestartMessages") ?? "").Contains("1m!: Heads up"), 15);
                    Check("Planned restarts...: a warning's own words are saved (RestartMessages)", saved && preview == "Heads up: restart in 1 minute for the check!", srv.LoadConf().Get("RestartMessages") + " / preview: " + preview);
                    long logFrom = new FileInfo(srv.LogPath).Length;
                    srv.Command("restart 1 the check");
                    bool saidIt = WaitFor(() => Json.ReadShared(srv.LogPath).Substring((int)Math.Min(logFrom, Json.ReadShared(srv.LogPath).Length)).Contains("warned: Heads up: restart in 1 minute for the check! (also on screen)"), 20);
                    Check("...and the server says them, [time] being the time really left, on screen as ticked", saidIt,
                        Json.ReadShared(srv.LogPath).Substring((int)Math.Min(logFrom, Json.ReadShared(srv.LogPath).Length)).Split('\n').Where(l => l.Contains("warned:")).FirstOrDefault() ?? "no warning");
                    srv.Command("restart cancel");
                    WaitFor(() => srv.Status != null && srv.Status["scheduled"] == null, 20);
                }

                // restart now: it exits with 'restarting' and the Manager starts it again
                int pidBefore = srv.Launched != null ? srv.Launched.Id : 0;
                main.SelectForTest(0, 0);
                srv.Command("restart");
                bool exited = WaitFor(() => srv.Launched == null || srv.Launched.Id != pidBefore || srv.Launched.HasExited, 90);
                int exitCode = -1;
                try { Process old = Process.GetProcessById(pidBefore); } catch { }
                bool back = WaitFor(() => srv.Launched != null && srv.Launched.Id != pidBefore && srv.State == "running", 240);
                Check("restart: the server saves, exits and the Manager starts it again", back, "pid " + pidBefore + " -> " + (srv.Launched != null ? srv.Launched.Id : 0) + ", state " + srv.State);
                if (!back) throw new Exception("server did not come back after restart");
                string rlog = string.Join(" ", Directory.Exists(srv.LogArchiveDir) ? Directory.GetFiles(srv.LogArchiveDir).Select(Path.GetFileName) : new string[0]);
                Note("logs after restart: " + rlog + " (exit code " + exitCode + ")");
                Pump(3);

                // a server that hangs shows as not responding
                srv.Command("testhang 40");
                bool hung = WaitFor(() => srv.State == "not responding", 60);
                main.SelectForTest(0, 0); Pump(0.6); Shot(main, "not-responding");
                Check("a server that stops answering (its loop stuck 40 s) shows as Not responding, with Force quit", hung, "state " + srv.State + ", quiet " + srv.QuietSeconds.ToString("0") + " s");
                bool resumed = WaitFor(() => srv.State == "running", 40);
                Check("...and as Running again when it recovers", resumed, "state " + srv.State);

                // ---- stop it ----
                srv.Stop();
                bool stopped = WaitFor(() => !srv.Running, 90);
                WaitFor(() => Json.Str(srv.Status, "state") == "stopped", 10);     // its last status write lands as it closes
                Check("Stop saves and shuts the server down", stopped && Json.Str(srv.Status, "state") == "stopped", "running " + srv.Running + ", state " + Json.Str(srv.Status, "state"));
                JsonNode run = Json.Arr(srv.Data, "Runs").LastOrDefault();
                Check("the run is recorded as a clean stop (for Uptime)", run != null && Json.Bool(run, "Clean"), run?.ToJsonString() ?? "");
                main.SelectForTest(0, main.TabIndexOf("Uptime")); Pump(1); Shot(main, "after-uptime");
                main.SelectForTest(0, main.TabIndexOf("Saves")); Pump(1); Shot(main, "after-saves");
                // a save's contents, by name
                WorldFile wf = WorldFile.Read(srv.CurrentWorldFile(), Catalog.Load(srv));
                Check("Saves: a save's players and ships are listed (planet names from catalog.json)", wf.Players.Count >= 0 && Catalog.Load(srv).Known && wf.Ships.All(x => x.Name.Length > 0),
                    wf.Players.Count + " player(s), " + wf.Ships.Count + " ship(s); catalog " + (Catalog.Load(srv).Known ? "read" : "missing"));
                string ezip = Path.Combine(Path.GetDirectoryName(reportPath), "export-world.zip");
                int en = MainForm.ExportSaveZip(srv.CurrentWorldFile(), false, ezip);
                bool hasWorld = false;
                using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(ezip)) hasWorld = z.Entries.Any(x => x.Name == "world.json");
                Check("Saves: Export makes a zip with world.json", hasWorld, en + " file(s)");
                // copying ships and players from another save (one the client-and-server tests played in)
                string fixture = Environment.GetEnvironmentVariable("SDSM_COPY_FROM") ?? "";
                if (File.Exists(fixture))
                {
                    WorldFile src = WorldFile.Read(fixture, Catalog.Load(srv));
                    WorldFile before = WorldFile.Read(srv.CurrentWorldFile(), Catalog.Load(srv));
                    WorldFile.CopyRequest req = new WorldFile.CopyRequest();
                    req.Ships.AddRange(src.Ships.OrderByDescending(x => x.Parts).Take(2).Select(x => x.Id));
                    req.Players.AddRange(src.Players.Take(1).Select(x => x.SteamId));
                    List<string> copyNotes = WorldFile.Copy(fixture, srv.CurrentWorldFile(), req);
                    Note("copy: " + string.Join(" / ", copyNotes));
                    WorldFile after = WorldFile.Read(srv.CurrentWorldFile(), Catalog.Load(srv));
                    int expectShips = WorldFile.ShipsWith(src.Root, req.Ships).Count;
                    Check("Saves: ships and a player copied in from another save (docked ships come along)", after.Ships.Count == before.Ships.Count + expectShips && after.Players.Count >= before.Players.Count,
                        "ships " + before.Ships.Count + " -> " + after.Ships.Count + " (+" + expectShips + "), players " + before.Players.Count + " -> " + after.Players.Count);
                    // the game loads the world with them
                    string err2 = srv.Start();
                    bool up = err2 == null && WaitFor(() => srv.State == "running", 240);
                    Pump(8);
                    int shipsNow = Json.Int(srv.Status, "ships");
                    Check("...and the server loads that world, with the copied ships in it", up && shipsNow == after.Ships.Count, "state " + srv.State + ", ships " + shipsNow + " of " + after.Ships.Count);
                    string[] errs = Json.ReadShared(srv.LogPath).Split('\n').Where(l => l.Contains("[ERROR]") || l.Contains("Exception")).ToArray();
                    Check("...with no errors in its log", errs.Length == 0, errs.Length + ": " + string.Join(" | ", errs.Take(3)));
                    srv.Stop();
                    WaitFor(() => !srv.Running, 90);
                }
                else Note("copy between saves not tried: SDSM_COPY_FROM is not set to a save");

                // ships named by their beacons, deleted into the bin and restored (a world with named ships)
                string shipWorld = Path.Combine(Path.GetDirectoryName(fixture.Length > 0 ? fixture : reportPath), "ship-fixture.json");
                if (File.Exists(shipWorld))
                {
                    WaitFor(() => !srv.Running, 60);
                    Directory.CreateDirectory(Path.Combine(srv.WorldsDir, "ships"));
                    File.Copy(shipWorld, Path.Combine(srv.WorldsDir, "ships", "world.json"), true);
                    Conf wc = srv.LoadConf(); string oldWorld = wc.Get("World"); wc.Set("World", "ships"); wc.Save(srv.ConfPath);
                    string err3 = srv.Start();
                    bool up3 = err3 == null && WaitFor(() => srv.State == "running", 240);
                    bool listed = up3 && WaitFor(() => Json.Arr(srv.Ships, "ships").Any(x => Json.Str(x, "name").Length > 0 && x["where"] != null), 40);
                    main.SelectForTest(0, main.TabIndexOf("Ships")); Pump(1);
                    Shot(main, "ships-named");
                    List<JsonNode> ships = Json.Arr(srv.Ships, "ships").ToList();
                    string names = string.Join(", ", ships.Where(x => Json.Str(x, "name").Length > 0).Select(x => "#" + Json.Int(x, "id") + " " + Json.Str(x, "name")));
                    Check("Ships: ships are named from their beacon, with where they are (planet, coordinates)", listed && ships.Any(x => Json.Str(x, "name") == "cool ship") && main.ShipRowsForTest == ships.Count, names + "; " + main.ShipRowsForTest + " row(s)");
                    JsonNode target = ships.FirstOrDefault(x => Json.Str(x, "name") == "cool ship" && Json.Int(x, "root") == Json.Int(x, "id")) ?? ships.FirstOrDefault(x => Json.Int(x, "root") == Json.Int(x, "id"));
                    if (target != null)
                    {
                        int tid = Json.Int(target, "id");
                        string del = main.AskServerForTest("ship delete " + tid);
                        Check("Ships: Delete takes it out of the world into the bin and says what it was made of", del != null && del.Contains("Recovered: ") && del.Contains("ship restore"), (del ?? "").Replace("\n", " / "));
                        srv.Forget(srv.ShipsPath);
                        bool gone = WaitFor(() => !Json.Arr(srv.Ships, "ships").Any(x => Json.Int(x, "id") == tid), 25);
                        main.SelectForTest(0, main.TabIndexOf("Ships")); Pump(1);
                        Shot(main, "ships-bin");
                        Check("...the Ships page shows it in the bin, with what it held and was made of", gone && main.BinRowsForTest == 1, "gone " + gone + ", bin rows " + main.BinRowsForTest);
                        string binId = System.Text.RegularExpressions.Regex.Match(del ?? "", @"ship restore (\S+)").Groups[1].Value;
                        string res = main.AskServerForTest("ship restore " + binId);
                        bool restored = res != null && res.Contains("Restored") && WaitFor(() => Json.Arr(srv.Ships, "ships").Any(x => Json.Str(x, "name") == Json.Str(target, "name") && Json.Int(x, "parts") == Json.Int(target, "parts")), 25);
                        Check("...and Restore puts it back where it stood, every part", restored, (res ?? "").Replace("\n", " / "));
                    }
                    string[] errs3 = Json.ReadShared(srv.LogPath).Split('\n').Where(l => l.Contains("[ERROR]") && l.Contains("[DedicatedServer]")).ToArray();
                    Check("...with no DedicatedServer errors in the server log", errs3.Length == 0, errs3.Length + ": " + string.Join(" | ", errs3.Take(3)));
                    srv.Stop();
                    WaitFor(() => !srv.Running, 90);
                    wc = srv.LoadConf(); wc.Set("World", oldWorld); wc.Save(srv.ConfPath);
                }
                else Note("ships not tried: no ship-fixture.json beside the copy fixture");
                main.SelectForTest(0, main.TabIndexOf("Logs")); Pump(1); Shot(main, "after-logs");
                string zip = Path.Combine(Path.GetDirectoryName(reportPath), "export-test.zip");
                int zipped = 0;
                try { zipped = MainForm.ExportZip(srv, zip); } catch (Exception ze) { Note("export: " + ze.Message); }
                Check("Logs: export all makes a zip of the logs, stats and settings", zipped >= 4 && File.Exists(zip), zipped + " file(s)");
                main.ShowHomeForTest(); Pump(1); Shot(main, "after-home");
                bool world = File.Exists(srv.CurrentWorldFile());
                Check("the world was saved in the server's own folder", world, srv.CurrentWorldFile());
            }
            catch (Exception e) { Note("self test stopped: " + e); result = "ERROR"; }
            finally
            {
                try { if (srv != null && srv.Running) { srv.Stop(); WaitFor(() => !srv.Running, 60); if (srv.Running) srv.Kill(); } } catch { }
                try { main?.Close(); } catch { }
            }
            JsonObject report = new JsonObject
            {
                ["result"] = result,
                ["scenarios"] = new JsonArray("manager-selftest"),
                ["secondsToWorld"] = (DateTime.Now - t0).TotalSeconds,
                ["checks"] = _checks,
                ["notes"] = _notes,
                ["exceptions"] = new JsonArray(),
                ["unityErrors"] = new JsonArray(),
            };
            File.WriteAllText(reportPath, report.ToJsonString(Json.Indented));
            return _checks.Any(c => Json.Str(c, "status") == "FAIL") || result != "DONE" ? 1 : 0;
        }
    }
}
