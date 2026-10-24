// Mods a server doesn't allow, turned off just for that server.
//
// A server with RejectExtraMods turns away players with mods it neither runs nor allows. The turned-away
// popup then offers to turn those mods off for this server: their files are moved out of Mods (to
// UserData/DedicatedServer-ModsOff), the game restarts without them and joins the server by itself, and
// as soon as the player leaves that server (or the next time the game starts, if it was closed in the
// server) the files go back. MelonLoader only loads mods when the game starts, so they are back in the
// game from the start after that; the player is told, with a button to restart straight away.
//   "Always for this server" remembers the choice (saved servers): the next time, it happens by itself,
//   with a notice saying what is being turned off.
// DedicatedServer itself (and the test harness) are never turned off. Nothing is deleted: a file is only
// ever moved between Mods and that folder, and what was moved is written down first
// (UserData/DedicatedServer-modsoff.json), so a crash part-way can always be put right.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UI.MainMenu.Common;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class ModsOff
    {
        public class Item { public string Name = "", File = ""; }

        public class Plan
        {
            public string Address = "", Server = "";
            public List<Item> Mods = new List<Item>();
            public string Stage = "";            // "joining": restart, then join; "in": joined (put back on leaving)
            public DateTime At;
        }

        private static readonly string[] Never = { "DedicatedServer", "Demo Verify Harness" };

        private static string PlanFile { get { return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-modsoff.json"); } }
        private static string OffDir { get { return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-ModsOff"); } }
        private static string ModsDir { get { return MelonLoader.Utils.MelonEnvironment.ModsDirectory; } }

        public static string State = "";        // for the probe: "", "offered", "auto", "restarting", "joining", "in", "back"
        public static string LastNotice = "";

        private static Plan Load()
        {
            try { return File.Exists(PlanFile) ? JsonConvert.DeserializeObject<Plan>(File.ReadAllText(PlanFile)) : null; }
            catch (Exception e) { Log.Warn("mods off: could not read " + PlanFile + ": " + e.Message); return null; }
        }

        private static void Save(Plan p)
        {
            if (p == null) { try { File.Delete(PlanFile); } catch { } return; }
            File.WriteAllText(PlanFile, JsonConvert.SerializeObject(p, Formatting.Indented));
        }

        /// <summary>The mods a verdict says this server doesn't allow, with this game's file for each (only those that can be turned off).</summary>
        public static List<Item> Turnable(DsVerdict v)
        {
            List<Item> l = new List<Item>();
            foreach (string line in (v.Missing ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 2 || p[0] != Gate.CodeExtraMods || Never.Contains(p[1])) continue;
                foreach (MelonLoader.MelonMod m in MelonLoader.MelonMod.RegisteredMelons)
                {
                    try
                    {
                        if (m == null || m.Info == null || m.Info.Name != p[1]) continue;
                        string path = m.MelonAssembly != null ? m.MelonAssembly.Location : "";
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ModsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!l.Any(x => x.Name == p[1])) l.Add(new Item { Name = p[1], File = Path.GetFileName(path) });
                    }
                    catch { }
                }
            }
            return l;
        }

        private static string Names(IEnumerable<Item> l) { return string.Join(", ", l.Select(x => x.Name).ToArray()); }

        // ---- the turned-away popup ----------------------------------------------------------------

        private static Button _go, _always;
        private static List<Item> _offer;
        private static string _address, _server;

        /// <summary>The turned-away popup: "Turn them off and join" and "Always for this server" added, or straight on with "always" set.</summary>
        public static void AddToPopup(ClosablePopup popup, TMP_Text text, DsVerdict v)
        {
            _offer = Turnable(v);
            if (_offer.Count == 0) return;
            _address = Client.LastAddress.Value;
            SavedServer s = SavedServers.Get(_address, false);
            _server = s != null && s.Name.Length > 0 ? s.Name : _address;
            if (s != null && s.AutoModsOff)
            {
                text.text = "Turning off " + Names(_offer) + " for " + _server + " (you chose to always do this here).\nThe game restarts without them in a few seconds and joins; they come back on when you leave.";
                State = "auto";
                _cancelled = false;
                Button stop = popup.GetComponentsInChildren<Button>(true).FirstOrDefault();
                if (stop != null)
                {
                    stop.onClick = new Button.ButtonClickedEvent();
                    stop.onClick.AddListener(() => StopAlways(popup));
                    Label(stop, "Don't, and stop doing this");
                }
                MelonLoader.MelonCoroutines.Start(GoLater(5f));
                return;
            }
            Button ok = popup.GetComponentsInChildren<Button>(true).FirstOrDefault();
            if (ok == null) return;
            text.text = ClientUi.RejectionText(v).Replace("<size=75%>Take them out of your Mods folder (or ask the server's admin to allow them), then join again.</size>", "")
                + "\n<size=85%>You can turn them off just for this server: the game restarts without " + Names(_offer) + " and joins, and they are turned back on when you leave.</size>";
            RectTransform okr = (RectTransform)ok.transform;
            _always = UnityEngine.Object.Instantiate(ok, okr.parent, false);
            _always.name = "DedicatedServer.AlwaysModsOff";
            _always.onClick = new Button.ButtonClickedEvent();
            _always.onClick.AddListener(() => Go(true));
            Label(_always, "Always for this server");
            _go = ok;
            Button later = UnityEngine.Object.Instantiate(ok, okr.parent, false);
            later.name = "DedicatedServer.NotNow";
            later.onClick = new Button.ButtonClickedEvent();
            later.onClick.AddListener(() => { State = ""; popup.Close(); });
            Label(later, "Not now");
            // three in a row inside the popup, centred where the game's one button was: the two choices, then a narrower "Not now"
            // (the game's button isn't pivoted at its middle, so everything is worked out from its real centre)
            float w = okr.sizeDelta.x, h = okr.sizeDelta.y, big = w * 0.72f, small = w * 0.48f, gap = 10f;
            float centre = okr.anchoredPosition.x + (0.5f - okr.pivot.x) * w, y = okr.anchoredPosition.y + (0.5f - okr.pivot.y) * h;
            float left = centre - (big * 2f + small + gap * 2f) / 2f;
            RectTransform ar = (RectTransform)_always.transform, lr = (RectTransform)later.transform;
            foreach (RectTransform r in new[] { okr, ar, lr }) r.pivot = new Vector2(0.5f, 0.5f);
            okr.sizeDelta = ar.sizeDelta = new Vector2(big, h);
            lr.sizeDelta = new Vector2(small, h);
            okr.anchoredPosition = new Vector2(left + big / 2f, y);
            ar.anchoredPosition = new Vector2(left + big + gap + big / 2f, y);
            lr.anchoredPosition = new Vector2(left + big * 2f + gap * 2f + small / 2f, y);
            _go.onClick = new Button.ButtonClickedEvent();
            _go.onClick.AddListener(() => Go(false));
            Label(_go, "Turn them off and join");
            State = "offered";
        }

        private static System.Collections.IEnumerator GoLater(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (!_cancelled) Go(false);
        }

        private static bool _cancelled;

        /// <summary>The automatic turning off called off, and not done by itself for this server any more.</summary>
        public static void StopAlways(ClosablePopup popup)
        {
            _cancelled = true;
            State = "";
            SavedServer s = SavedServers.Get(_address, false);
            if (s != null) { s.AutoModsOff = false; SavedServers.Save(); }
            Log.Msg("mods off: no longer turned off by themselves for " + _server);
            if (popup != null) popup.Close();
        }

        private static void Label(Button b, string t)
        {
            TMP_Text x = b.GetComponentInChildren<TMP_Text>(true);
            if (x == null) return;
            UnityEngine.Localization.Components.LocalizeStringEvent l = x.GetComponent<UnityEngine.Localization.Components.LocalizeStringEvent>();
            if (l != null) l.enabled = false;
            x.enableAutoSizing = true; x.fontSizeMin = 12;
            x.text = t;
        }

        /// <summary>What the buttons do (and the probe): move the files out, write it down, restart and join.</summary>
        public static string Go(bool always)
        {
            if (_offer == null || _offer.Count == 0) return "nothing to turn off";
            try
            {
                if (always)
                {
                    SavedServer s = SavedServers.Get(_address, true);
                    s.AutoModsOff = true;
                    SavedServers.Save();
                }
                Plan p = new Plan { Address = _address, Server = _server, Mods = _offer, Stage = "joining", At = DateTime.UtcNow };
                Save(p);                                        // written down before anything moves
                Directory.CreateDirectory(OffDir);
                foreach (Item i in _offer)
                {
                    string from = Path.Combine(ModsDir, i.File), to = Path.Combine(OffDir, i.File);
                    if (File.Exists(to)) File.Delete(to);       // a copy left from before: the one in Mods is the current one
                    File.Move(from, to);
                }
                Log.Msg("mods off: turned off " + Names(_offer) + " for " + _server + (always ? " (always for this server)" : "") + "; restarting to join without them");
                State = "restarting";
                Restart();
                return "restarting without " + Names(_offer);
            }
            catch (Exception e)
            {
                Log.Warn("mods off: could not turn the mods off: " + e.Message);
                PutBack("could not turn them off (" + e.Message + ")");
                return "failed: " + e.Message;
            }
        }

        private static void Restart()
        {
#if DSTEST
            if (Environment.GetEnvironmentVariable("SDD_DS_REAL_RESTART") != "1") { Log.Msg("mods off: (test build) not restarting"); return; }
#endif
            string exe = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "StellarDrive.exe");
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(" ") ? "\"" + a + "\"" : a).ToArray());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = MelonLoader.Utils.MelonEnvironment.GameRootDirectory });
            Application.Quit();
        }

        // ---- after the restart, and after leaving ---------------------------------------------------

        private static bool _started, _wasIn;
        private static float _menuSince = -1f;

        /// <summary>Every frame on a player's game.</summary>
        public static void Update()
        {
            bool inGame = SceneManager.GetActiveScene().name == "MainGame";
            if (!_started)
            {
                _started = true;
                Plan p = Load();
                if (p != null && p.Stage == "in") PutBack("the game was closed while on " + p.Server);   // closed in the server: put back now
                else if (p != null && p.Stage == "joining") { State = "joining"; Log.Msg("mods off: started without " + Names(p.Mods) + "; joining " + p.Server); }
            }
            if (State == "joining" && !inGame)
            {
                if (_menuSince < 0f) _menuSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _menuSince < 3f) return;           // the main menu settles first
                Plan p = Load();
                if (p == null) { State = ""; return; }
                p.Stage = "in";
                Save(p);
                State = "in";
                string err = Client.Join(p.Address);
                if (err != null) PutBack("could not join " + p.Server + ": " + err);
                return;
            }
            if (State == "in")
            {
                if (inGame) _wasIn = true;
                else if (_wasIn) { _wasIn = false; PutBack("you left " + Load()?.Server); }
            }
        }

        /// <summary>The files back in Mods, the record gone, and the player told.</summary>
        public static void PutBack(string why)
        {
            Plan p = Load();
            State = "";
            if (p == null) return;
            List<string> back = new List<string>(), failed = new List<string>();
            foreach (Item i in p.Mods)
            {
                string from = Path.Combine(OffDir, i.File), to = Path.Combine(ModsDir, i.File);
                try
                {
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) { failed.Add(i.Name + " (a file of that name is in Mods again; the turned-off copy stays in " + OffDir + ")"); continue; }
                    File.Move(from, to);
                    back.Add(i.Name);
                }
                catch (Exception e) { failed.Add(i.Name + " (" + e.Message + ")"); }
            }
            Save(null);
            State = "back";
            LastNotice = (back.Count > 0 ? string.Join(", ", back.ToArray()) + (back.Count == 1 ? " is" : " are") + " turned back on (" + why + "). " + (back.Count == 1 ? "It loads" : "They load") + " the next time the game starts." : "")
                + (failed.Count > 0 ? (back.Count > 0 ? "\n" : "") + "Could not put back: " + string.Join("; ", failed.ToArray()) : "");
            Log.Msg("mods off: " + LastNotice.Replace("\n", " "));
            Overlay.OnEvent(new DsEvent { Kind = "announce", Text = LastNotice, Data = "8" });
        }

#if DSTEST
        public static List<Item> OfferForTest { get { return _offer; } }
        public static void StartAgainForTest() { _started = false; _menuSince = -1f; }
#endif
    }
}
