// Mods from the server: a player turned away for missing mods can download them from that server.
//
// The server (ShareMods on) lists the files in its verdict: mod name, file name, size and SHA-256.
// The "turned away" popup then says what they are, how big, and plainly that mods are programs that
// run with the game's full rights, so they should only take them from servers they trust - and offers
// "Download and install". Nothing happens without that press.
//
// Each file comes over the game's own UDP port (Query.GetFile), is checked against the size and
// SHA-256 the server announced, and goes into Mods. A file of the same name already there is moved to
// UserData/DedicatedServer-replaced first (Windows lets a loaded DLL be moved, not overwritten).
// MelonLoader only loads mods when the game starts, so the popup then offers Quit; the player starts
// the game again and joins.
//
// The SHA-256 proves the file arrived as the server meant it; it does not prove the server is honest.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UI.MainMenu.Common;
using UnityEngine;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class ModInstall
    {
        public class Offer { public string Mod, File, Sha; public long Size; }

        public static List<Offer> Offers(DsVerdict v)
        {
            List<Offer> l = new List<Offer>();
            foreach (string line in (v.Missing ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                long size;
                if (p.Length < 5 || p[0] != "#file" || !long.TryParse(p[3], out size)) continue;
                if (!SafeName(p[2]) || size <= 0 || size > 50L * 1024 * 1024 || !Regex.IsMatch(p[4], "^[0-9a-f]{64}$")) continue;
                l.Add(new Offer { Mod = p[1], File = p[2], Size = size, Sha = p[4] });
            }
            return l;
        }

        /// <summary>A plain DLL name: no folders, nothing odd.</summary>
        public static bool SafeName(string f) { return f != null && Regex.IsMatch(f, @"^[A-Za-z0-9 _.+()\-]{1,64}\.dll$") && !f.Contains(".."); }

        public static string Kb(long b) { return b >= 1024 * 1024 ? (b / (1024.0 * 1024.0)).ToString("0.0") + " MB" : Math.Max(1, b / 1024) + " KB"; }

        // ---- the popup --------------------------------------------------------------------

        private static ClosablePopup _popup;
        private static TMP_Text _text;
        private static Button _go, _later;
        private static List<Offer> _offers;
        private static string _address;
        private static readonly List<Query.Fetch> _running = new List<Query.Fetch>();
        private static readonly Dictionary<string, byte[]> _got = new Dictionary<string, byte[]>();
        private static readonly List<string> _errors = new List<string>();
        public static string State = "";            // for the probe: "", "offered", "downloading", "installed", "failed"
        public static List<string> Installed = new List<string>();

        /// <summary>The turned-away popup, with "Download and install" added when the server offers the mods.</summary>
        public static void AddToPopup(ClosablePopup popup, TMP_Text text, DsVerdict v)
        {
            _offers = Offers(v);
            if (_offers.Count == 0) return;
            _popup = popup; _text = text; _address = Client.LastAddress.Value;
            Button ok = popup.GetComponentsInChildren<Button>(true).FirstOrDefault();
            if (ok == null) return;
            long total = _offers.Sum(o => o.Size);
            text.text = ClientUi.RejectionText(v).Replace("<size=75%>Put the same versions in your Mods folder, then join again.</size>", "")
                + "\n<size=85%>The server can send them: " + string.Join(", ", _offers.Select(o => o.File + " (" + Kb(o.Size) + ")").ToArray()) + ", " + Kb(total) + " in all."
                + "\n<color=#FFB84D>Mods are programs: they run with your game's full rights. Only install them from a server you trust.</color></size>";
            RectTransform okr = (RectTransform)ok.transform;
            _later = UnityEngine.Object.Instantiate(ok, okr.parent, false);
            _later.name = "DedicatedServer.Later";
            _later.onClick = new Button.ButtonClickedEvent();
            _later.onClick.AddListener(() => { State = ""; popup.Close(); });
            Label(_later, "Not now");
            _go = ok;
            float gap = okr.sizeDelta.x * 0.55f + 10f;
            ((RectTransform)_later.transform).anchoredPosition = okr.anchoredPosition + new Vector2(gap, 0f);
            okr.anchoredPosition = okr.anchoredPosition - new Vector2(gap, 0f);
            _go.onClick = new Button.ButtonClickedEvent();
            _go.onClick.AddListener(Start);
            Label(_go, "Download and install");
            State = "offered";
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

        /// <summary>What "Download and install" does (and the probe).</summary>
        public static void Start()
        {
            if (_offers == null || _offers.Count == 0 || State == "downloading") return;
            State = "downloading";
            _got.Clear(); _errors.Clear(); _running.Clear(); Installed.Clear();
            if (_go != null) _go.interactable = false;
            foreach (Offer o in _offers)
            {
                Offer offer = o;
                _running.Add(Query.GetFile(_address, "mod/" + o.File, (bytes, err) =>
                {
                    if (bytes == null) { _errors.Add(offer.File + ": " + err); return; }
                    if (bytes.Length != offer.Size || ServerInfo.Hash(bytes) != offer.Sha) { _errors.Add(offer.File + ": it didn't arrive as the server described it"); return; }
                    _got[offer.File] = bytes;
                }));
            }
        }

        /// <summary>Every frame in the menu.</summary>
        public static void Update()
        {
            if (State != "downloading") return;
            float done = _running.Count == 0 ? 1f : _running.Average(f => f.Finished ? 1f : f.Progress);
            if (_text != null) _text.text = "Downloading " + _offers.Count + " mod(s) from the server... " + (done * 100f).ToString("0") + "%";
            if (_running.Any(f => !f.Finished)) return;
            if (_errors.Count > 0) { Finish(false, "Could not download: " + string.Join("; ", _errors.ToArray())); return; }
            try
            {
                string mods = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
                string old = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-replaced");
                foreach (Offer o in _offers)
                {
                    string target = Path.Combine(mods, o.File);
                    string tmp = target + ".download";
                    File.WriteAllBytes(tmp, _got[o.File]);
                    foreach (string other in OtherCopies(o.Mod, target))
                    {
                        Directory.CreateDirectory(old);
                        File.Move(other, Path.Combine(old, Path.GetFileName(other) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".old"));
                        Log.Msg("moved the older copy " + Path.GetFileName(other) + " out of Mods");
                    }
                    if (File.Exists(target))
                    {
                        Directory.CreateDirectory(old);
                        File.Move(target, Path.Combine(old, o.File + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".old"));
                    }
                    File.Move(tmp, target);
                    Installed.Add(o.Mod);
                    Log.Msg("installed " + o.File + " (" + o.Mod + ", " + Kb(o.Size) + ", sha256 " + o.Sha.Substring(0, 12) + "...) from " + _address);
                }
                Finish(true, "Installed " + string.Join(", ", Installed.ToArray()) + ".\nThe game loads mods when it starts: restart it, then join again."
                    + "\n<size=75%>Any older copy was moved to UserData\\DedicatedServer-replaced.</size>");
            }
            catch (Exception e) { Finish(false, "Could not install: " + e.Message); }
        }

        /// <summary>Other files in Mods that are this mod (another version, or the same one under another name).</summary>
        private static List<string> OtherCopies(string mod, string target)
        {
            List<string> l = new List<string>();
            foreach (MelonLoader.MelonMod m in MelonLoader.MelonMod.RegisteredMelons)
            {
                try
                {
                    if (m == null || m.Info == null || m.Info.Name != mod) continue;
                    string path = m.MelonAssembly != null ? m.MelonAssembly.Location : "";
                    if (!string.IsNullOrEmpty(path) && File.Exists(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase)) l.Add(path);
                }
                catch { }
            }
            return l;
        }

        private static void Finish(bool ok, string msg)
        {
            State = ok ? "installed" : "failed";
            if (_text != null) _text.text = msg;
            if (_go != null)
            {
                _go.interactable = true;
                _go.onClick = new Button.ButtonClickedEvent();
                if (ok) { Label(_go, "Quit the game"); _go.onClick.AddListener(() => Application.Quit()); }
                else { Label(_go, "Try again"); _go.onClick.AddListener(Start); }
            }
            if (_later != null) Label(_later, ok ? "Later" : "Close");
        }
    }
}
