// Player markers: the dot (or edge arrow) the game draws for every other player further than 20 m.
//
// Server: works out, for each player, whose markers they see and in what colour, and sends it to them
// ("markers") when anything changes.
//   PlayerMarkers = everyone | friends | nobody   (friends: both players have added each other with /friend)
//   AdminsSeeAllMarkers                           admins see everyone whatever the rule
//   ColoredMarkers                                each player's own colour (/color), or one picked from their id
// Player: hides the markers it was told to, colours the rest, and shows a player's name (and how far)
// next to their marker while looking straight at it.
//
// The game sends every player's position to every player either way; hiding a marker is a rule for
// the players' games, not a secret.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FishNet.Connection;
using HarmonyLib;
using Players;
using Players.Interface.Model;
using Players.Interface.Services;
using TMPro;
using UI.Targeting;
using UnityEngine;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class Markers
    {
        public static readonly string[] Palette = { "#4FC3F7", "#FFB84D", "#9BE7A0", "#FF9BD2", "#C79BFF", "#FFE16B", "#5FE0D0", "#FF6B5E", "#8FD3FF", "#B8E986", "#F5A3FF", "#FFD1A1" };

        // ---- server ------------------------------------------------------------------------------

        private static readonly Dictionary<int, string> _sent = new Dictionary<int, string>();
        private static float _next;

        public static string ColorOf(PlayerRecord r)
        {
            if (r == null) return "#FFFFFF";
            if (!string.IsNullOrEmpty(r.MarkerColor)) return r.MarkerColor;
            return Palette[(int)(r.Id % (ulong)Palette.Length)];
        }

        public static bool AreFriends(PlayerRecord a, PlayerRecord b)
        {
            return a != null && b != null && a.Friends.Contains(b.Id) && b.Friends.Contains(a.Id);
        }

        /// <summary>Every few seconds, and at once after a change (Changed()).</summary>
        public static void Update()
        {
            if (!Server.Ready || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 3f;
            try { SendAll(false); } catch (Exception e) { Log.Once("markers", "markers: " + e.Message); }
        }

        public static void Changed() { _next = 0f; }

        public static void SendAll(bool force)
        {
            IReadOnlyList<TrackedPlayerServer> on = Server.ActivePlayers();
            string mode = Server.Cfg.PlayerMarkers.Value;
            bool colors = Server.Cfg.ColoredMarkers.Value;
            foreach (int gone in _sent.Keys.Where(k => !on.Any(p => p.ConnectionId == k)).ToList()) _sent.Remove(gone);
            foreach (TrackedPlayerServer me in on)
            {
                PlayerRecord mine = Server.Data.Player(me.SteamUserId);
                bool admin = mine != null && mine.Op && Server.Cfg.AdminsSeeAllMarkers.Value;
                List<string> lines = new List<string>();
                foreach (TrackedPlayerServer other in on)
                {
                    if (other.ConnectionId == me.ConnectionId) continue;
                    PlayerRecord theirs = Server.Data.Player(other.SteamUserId);
                    bool see = admin || mode == "everyone" || (mode == "friends" && AreFriends(mine, theirs));
                    lines.Add(other.Name + "\t" + (colors ? ColorOf(theirs) : "") + "\t" + (see ? "1" : "0"));
                }
                string data = string.Join("\n", lines.ToArray());
                string was;
                if (!force && _sent.TryGetValue(me.ConnectionId, out was) && was == mode + "|" + data) continue;
                _sent[me.ConnectionId] = mode + "|" + data;
                NetworkConnection conn = Server.Connection(me.ConnectionId);
                if (conn != null) Net.Event(conn, "markers", mode, data);
                Log.Debug("markers", me.Name + ": " + lines.Count(l => l.EndsWith("\t1")) + " of " + lines.Count + " marker(s) shown");
            }
        }

        // ---- player ------------------------------------------------------------------------------

        private class Rule { public Color? Color; public bool Visible = true; }
        private static readonly Dictionary<string, Rule> _rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        private static bool _active;
        public static string LookedAt = "";          // for the probe: the name tag showing now
        public static int Hidden, Shown;
        private static TMP_FontAsset _font;

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PlayersTargetingUpdater), "UpdatePlayerTargets"),
                    postfix: new HarmonyMethod(typeof(Markers).GetMethod("TargetsPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("player markers won't be coloured: " + e.Message); }
        }

        public static void ClientReset() { _rules.Clear(); _active = false; LookedAt = ""; }

        public static void OnEvent(DsEvent e)
        {
            _rules.Clear();
            foreach (string line in (e.Data ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3 || p[0].Length == 0) continue;
                Color c;
                _rules[p[0]] = new Rule { Color = p[1].Length > 0 && ColorUtility.TryParseHtmlString(p[1], out c) ? c : (Color?)null, Visible = p[2] == "1" };
            }
            _active = true;
            Received++;
            Mode = e.Text ?? "";
        }

        private static void TargetsPostfix(PlayersTargetingUpdater __instance)
        {
            if (!_active && NetHealth.Lost.Count == 0) return;
            try
            {
                IList list = Traverse.Create(__instance).Field("_indicators").GetValue() as IList;
                if (list == null) return;
                ScreenPositions screens = Core.Services.ServiceLocator.GetService<ScreenPositions>();
                IPlayersTargetingDataProvider provider = Core.Services.ServiceLocator.GetService<IPlayersTargetingDataProvider>();
                Dictionary<string, Vector3> where = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
                if (provider != null) foreach (PlayerTargetingData d in provider.GetPlayersTargetingData()) where[d.PlayerName ?? ""] = d.VisualPosition;
                string looking = "";
                float best = float.MaxValue;
                int hidden = 0, shown = 0;
                foreach (object item in list)
                {
                    Traverse t = Traverse.Create(item);
                    string name = t.Field("PlayerName").GetValue<string>() ?? "";
                    PlayerIndicator ind = t.Field("Indicator").GetValue<PlayerIndicator>();
                    TargetArrow arrow = t.Field("Arrow").GetValue<TargetArrow>();
                    if (ind == null) continue;
                    // looked at: in front, drawn (further than the game's 20 m), near the middle of the screen
                    float off = float.MaxValue, dist = 0f;
                    Vector3 pos;
                    if (ind.gameObject.activeSelf && screens != null && where.TryGetValue(name, out pos))
                    {
                        ScreenPositions.ScreenPosition sp = screens.CalculateScreenPosFromWorldPos(pos);
                        RawImage img = Traverse.Create(ind).Field("image").GetValue<RawImage>();
                        if (sp.IsInFront && img != null && img.color.a > 0.05f) { off = sp.Position.magnitude; dist = sp.Distance; }
                    }
                    bool look = off < 70f;
                    if (NetHealth.Lost.Contains(name))
                    {
                        // their position couldn't be worked out this frame: no dot rather than one left where it was
                        ind.gameObject.SetActive(false);
                        if (arrow != null) arrow.gameObject.SetActive(false);
                        hidden++;
                        continue;
                    }
                    if (!Apply(name, ind, arrow, look, dist)) { hidden++; continue; }
                    shown++;
                    if (look && off < best) { best = off; looking = name; }
                }
                LookedAt = looking; Hidden = hidden; Shown = shown;
            }
            catch (Exception e) { Log.Once("markers-client", "player markers: " + e.Message); }
        }

        /// <summary>One marker: hidden, or coloured with its name tag on while looked at. False when hidden.</summary>
        private static bool Apply(string name, PlayerIndicator ind, TargetArrow arrow, bool looking, float distance)
        {
            Rule r;
            if (!_rules.TryGetValue(name, out r)) r = null;
            if (r != null && !r.Visible)
            {
                ind.gameObject.SetActive(false);
                if (arrow != null) arrow.gameObject.SetActive(false);
                return false;
            }
            if (r != null && r.Color.HasValue)
            {
                Tint(ind.gameObject, r.Color.Value);
                if (arrow != null) Tint(arrow.gameObject, r.Color.Value);
            }
            TMP_Text tag = Tag(ind, r);
            if (looking) tag.text = name + (distance > 0f ? "  <size=75%><color=#B8C8D8>" + Mathf.RoundToInt(distance) + " m</color></size>" : "");
            tag.gameObject.SetActive(looking);
            return true;
        }

        public static int Received;
        public static string Mode = "";

#if DSTEST
        /// <summary>For the probe (one player, so nobody else's marker): the rules on two stand-in markers made from the game's own.</summary>
        public static string TestApply(string a, string colorA, bool showA, string b, string colorB, bool showB)
        {
            PlayersTargetingUpdater u = UnityEngine.Object.FindFirstObjectByType<PlayersTargetingUpdater>(FindObjectsInactive.Include);
            if (u == null) return "no targeting updater";
            Traverse t = Traverse.Create(u);
            PlayerIndicator prefab = t.Field("playerIndicator").GetValue<PlayerIndicator>();
            TargetArrow arrowPrefab = t.Field("targetArrow").GetValue<TargetArrow>();
            Transform canvas = t.Field("targetingCanvas").GetValue<Transform>();
            Dictionary<string, Rule> keep = new Dictionary<string, Rule>(_rules);
            bool wasActive = _active;
            List<GameObject> made = new List<GameObject>();
            try
            {
                OnEvent(new DsEvent { Kind = "markers", Text = Mode, Data = a + "\t" + colorA + "\t" + (showA ? "1" : "0") + "\n" + b + "\t" + colorB + "\t" + (showB ? "1" : "0") });
                PlayerIndicator ia = UnityEngine.Object.Instantiate(prefab, canvas), ib = UnityEngine.Object.Instantiate(prefab, canvas);
                TargetArrow aa = UnityEngine.Object.Instantiate(arrowPrefab, canvas), ab = UnityEngine.Object.Instantiate(arrowPrefab, canvas);
                made.AddRange(new[] { ia.gameObject, ib.gameObject, aa.gameObject, ab.gameObject });
                bool sa = Apply(a, ia, aa, false, 0f);
                bool sb = Apply(b, ib, ab, true, 42f);
                RawImage img = Traverse.Create(ib).Field("image").GetValue<RawImage>();
                Transform tag = ib.transform.Find("DedicatedServer.NameTag");
                string col = img != null ? "#" + ColorUtility.ToHtmlStringRGB(img.color) : "?";
                return a + (sa || ia.gameObject.activeSelf || aa.gameObject.activeSelf ? " SHOWN" : " hidden") + "; " + b + (sb ? " shown " + col : " HIDDEN")
                    + (tag != null && tag.gameObject.activeSelf ? " tag " + tag.GetComponent<TMP_Text>().text.Split(' ')[0] : " no tag");
            }
            finally
            {
                foreach (GameObject g in made) UnityEngine.Object.Destroy(g);
                _rules.Clear(); foreach (KeyValuePair<string, Rule> kv in keep) _rules[kv.Key] = kv.Value;
                _active = wasActive;
            }
        }
#endif

        private static void Tint(GameObject go, Color c)
        {
            foreach (Graphic g in go.GetComponentsInChildren<Graphic>(true))
            {
                if (g is TMP_Text) continue;
                Color now = g.color;
                g.color = new Color(c.r, c.g, c.b, now.a);
            }
        }

        private static TMP_Text Tag(PlayerIndicator ind, Rule r)
        {
            Transform t = ind.transform.Find("DedicatedServer.NameTag");
            TMP_Text text;
            if (t == null)
            {
                GameObject go = new GameObject("DedicatedServer.NameTag", typeof(RectTransform));
                go.transform.SetParent(ind.transform, false);
                RectTransform rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(18f, 0f);
                rt.sizeDelta = new Vector2(320f, 40f);
                TextMeshProUGUI tm = go.AddComponent<TextMeshProUGUI>();
                if (_font == null) { TMP_Text any = UnityEngine.Object.FindFirstObjectByType<TMP_Text>(); _font = any != null ? any.font : TMP_Settings.defaultFontAsset; }
                if (_font != null) tm.font = _font;
                tm.fontSize = 20f;
                tm.richText = true;
                tm.enableWordWrapping = false;
                tm.alignment = TextAlignmentOptions.MidlineLeft;
                tm.raycastTarget = false;
                tm.outlineWidth = 0.2f;
                tm.outlineColor = new Color32(0, 0, 0, 200);
                text = tm;
                go.SetActive(false);
            }
            else text = t.GetComponent<TMP_Text>();
            text.color = r != null && r.Color.HasValue ? r.Color.Value : Color.white;
            return text;
        }
    }
}
