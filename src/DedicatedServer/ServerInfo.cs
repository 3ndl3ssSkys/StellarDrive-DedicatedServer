// The server's name, description, icon and chat-name style, for its players; and the files it can
// hand out (its icon, and the mods a player needs).
//
//   * In game: on joining, a player is sent DsServerInfo (name, description, the players' name style,
//     the icon PNG). The pause menu shows the icon and name in its top-right corner, the player list
//     gets a heading row with them, and players' names in chat take the server's StylePlayerName.
//   * On the Join page: the status answer (Query) carries the icon's hash; a saved server's icon is
//     fetched once (FileService) and kept in UserData/DedicatedServer-icons.
//   * The icon is <data>/server-icon.png (the Server Manager puts it there, made 128x128), at most 64 KB.
//
// Mods: with ShareMods on, a player turned away for missing mods is also told which files the server
// can send (name, size, SHA-256). Only mods this server runs and requires are offered. Nothing is
// downloaded or installed without the player pressing a button that says what will happen.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FishNet.Broadcast;
using FishNet.Connection;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DedicatedServer
{
    public struct DsServerInfo : IBroadcast
    {
        public string Name, Desc, NameStyle, IconHash;
        public int Players, Max;
        public byte[] Icon;
    }

    internal static class ServerInfo
    {
        public const int MaxIcon = 64 * 1024;

        // ---- the server's side -------------------------------------------------------------

        private static byte[] _icon = new byte[0];
        private static string _iconHash = "";
        private static DateTime _iconTime = DateTime.MinValue;
        private static string _sentKey = "";
        private static float _nextCheck;

        public static string IconPath { get { return Path.Combine(Server.DataDir, "server-icon.png"); } }

        public static byte[] Icon()
        {
            try
            {
                if (!System.IO.File.Exists(IconPath)) { _icon = new byte[0]; _iconHash = ""; return _icon; }
                DateTime t = System.IO.File.GetLastWriteTimeUtc(IconPath);
                if (t != _iconTime)
                {
                    _iconTime = t;
                    byte[] b = System.IO.File.ReadAllBytes(IconPath);
                    if (b.Length > MaxIcon) { Log.Warn("server-icon.png is " + (b.Length / 1024) + " KB; at most " + (MaxIcon / 1024) + " KB is sent (the Server Manager makes a small one)"); b = new byte[0]; }
                    _icon = b;
                    _iconHash = b.Length > 0 ? Hash(b).Substring(0, 16) : "";
                }
            }
            catch (Exception e) { Log.Once("icon", "server icon: " + e.Message); }
            return _icon;
        }

        public static string IconHash { get { Icon(); return _iconHash; } }

        public static string Hash(byte[] b) { using (SHA256 s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        public static DsServerInfo Current()
        {
            return new DsServerInfo
            {
                Name = Server.Cfg.ServerName.Value ?? "", Desc = Server.Cfg.Description.Value ?? "", NameStyle = Server.Cfg.StylePlayerName.Value ?? "",
                IconHash = IconHash, Icon = Icon(), Players = Server.ActivePlayers().Count, Max = Server.MaxPlayers
            };
        }

        public static void SendTo(NetworkConnection conn) { Net.ToClient(conn, Current()); }

        /// <summary>Every frame on the server: if the name, description, style or icon changed, everyone hears.</summary>
        public static void Update()
        {
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 5f;
            DsServerInfo i = Current();
            string key = i.Name + "|" + i.Desc + "|" + i.NameStyle + "|" + i.IconHash + "|" + i.Players;
            if (key == _sentKey) return;
            bool first = _sentKey.Length == 0;
            _sentKey = key;
            if (first) return;
            foreach (Players.TrackedPlayerServer p in Server.ActivePlayers())
            {
                NetworkConnection c = Server.Connection(p.ConnectionId);
                if (c != null) Net.ToClient(c, i);
            }
        }

        /// <summary>The mods this server would send a player: file name -> (mod name, path, size, sha256).</summary>
        public static Dictionary<string, string[]> Shareable()
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (!Server.Cfg.ShareMods.Value) return d;
            HashSet<string> required = new HashSet<string>(Gate.Required().Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase) { "DedicatedServer" };
            foreach (MelonMod m in MelonMod.RegisteredMelons)
            {
                try
                {
                    if (m == null || m.Info == null || !required.Contains(m.Info.Name)) continue;
                    string path = m.MelonAssembly != null ? m.MelonAssembly.Location : m.GetType().Assembly.Location;
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                    d[Path.GetFileName(path)] = new[] { m.Info.Name, path };
                }
                catch { }
            }
            return d;
        }

        private static readonly Dictionary<string, KeyValuePair<DateTime, byte[]>> _fileCache = new Dictionary<string, KeyValuePair<DateTime, byte[]>>();

        /// <summary>A file the server hands out, by name ("icon", or "mod/&lt;file&gt;"); null if it doesn't.</summary>
        public static byte[] FileBytes(string name)
        {
            if (name == "icon") return Icon().Length > 0 ? Icon() : null;
            if (!name.StartsWith("mod/")) return null;
            string[] m;
            if (!Shareable().TryGetValue(name.Substring(4), out m)) return null;
            DateTime t = System.IO.File.GetLastWriteTimeUtc(m[1]);
            KeyValuePair<DateTime, byte[]> c;
            if (_fileCache.TryGetValue(m[1], out c) && c.Key == t) return c.Value;
            byte[] b = System.IO.File.ReadAllBytes(m[1]);
            _fileCache[m[1]] = new KeyValuePair<DateTime, byte[]>(t, b);
            return b;
        }

        /// <summary>"#file" lines for a verdict: the files that would give a player the mods they lack.</summary>
        public static string FileLines(List<KeyValuePair<string, string>> missing)
        {
            Dictionary<string, string[]> share = Shareable();
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string[]> f in share)
            {
                if (!missing.Any(x => string.Equals(x.Key, f.Value[0], StringComparison.OrdinalIgnoreCase))) continue;
                byte[] b = FileBytes("mod/" + f.Key);
                if (b == null) continue;
                sb.Append("\n#file\t").Append(f.Value[0]).Append('\t').Append(f.Key).Append('\t').Append(b.Length).Append('\t').Append(Hash(b));
            }
            return sb.ToString();
        }

        // ---- the client's side -------------------------------------------------------------

        public static DsServerInfo? Joined;          // the server we're on, while on one
        public static Texture2D JoinedIcon;

        public static void InitClient(HarmonyLib.Harmony h)
        {
            Net.OnServerInfo = OnInfo;
            try
            {
                h.Patch(AccessTools.Method(typeof(UI.Chat.ChatMessageObjectUI), "InitializeChatMessage"),
                    prefix: new HarmonyMethod(typeof(ServerInfo).GetMethod("RichTextPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(ServerInfo).GetMethod("ChatPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(UI.Chat.ChatMessageObjectUI), "InitializeSystem"),
                    prefix: new HarmonyMethod(typeof(ServerInfo).GetMethod("RichTextPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(ServerInfo).GetMethod("FitPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(UI.Pause.PauseMenuHandler), "Show"),
                    postfix: new HarmonyMethod(typeof(ServerInfo).GetMethod("PausePostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(UI.Pause.PlayerListMenu), "Start"),
                    postfix: new HarmonyMethod(typeof(ServerInfo).GetMethod("PlayerListPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("server name and icon in the menus won't show: " + e.Message); }
        }

        public static void ClientReset() { Joined = null; }

        private static void OnInfo(DsServerInfo i)
        {
            Joined = i;
            if (i.Icon != null && i.Icon.Length > 0)
            {
                Texture2D t = LoadIcon(i.Icon);
                if (t != null) { if (JoinedIcon != null) UnityEngine.Object.Destroy(JoinedIcon); JoinedIcon = t; }
                SavedServers.KeepIcon(Client.LastAddress.Value, i.IconHash, i.Icon);
            }
            else JoinedIcon = null;
            SavedServers.OnInfo(Client.LastAddress.Value, i);
            Log.Msg("server: " + i.Name + (i.Icon != null && i.Icon.Length > 0 ? " (with an icon)" : ""));
        }

        public static Texture2D LoadIcon(byte[] png)
        {
            try
            {
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(t, png)) { UnityEngine.Object.Destroy(t); return null; }
                t.wrapMode = TextureWrapMode.Clamp;
                return t;
            }
            catch { return null; }
        }

        // The game's chat lines have rich text off, so colour and size tags would show as typed. On a
        // dedicated server (which wraps what players type in <noparse>) they are switched on.
        /// <summary>
        /// The game sizes a chat line as it is made, before the chat's layout has given it its width, so a
        /// line that wraps differently at the real width was drawn over the line before it. Once laid out,
        /// the height is worked out again (and whenever the width changes).
        /// </summary>
        internal class ChatLineFit : MonoBehaviour
        {
            private float _width = -1f;
            private int _frames = 3;
            private void LateUpdate()
            {
                TMP_Text t = Traverse.Create(GetComponent<UI.Chat.ChatMessageObjectUI>()).Field("text").GetValue<TMP_Text>();
                if (t == null) return;
                float w = ((RectTransform)t.transform).rect.width;
                if (_frames > 0) _frames--;
                if (Mathf.Abs(w - _width) < 0.5f && _frames == 0) return;
                _width = w;
                t.ForceMeshUpdate(true, true);
                Traverse.Create(GetComponent<UI.Chat.ChatMessageObjectUI>()).Method("SetDynamicHeight").GetValue();
                if (transform.parent != null) UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform.parent);
            }
        }

        private static void FitPostfix(UI.Chat.ChatMessageObjectUI __instance)
        {
            if (__instance != null && __instance.GetComponent<ChatLineFit>() == null) __instance.gameObject.AddComponent<ChatLineFit>();
        }

        private static void RichTextPrefix(UI.Chat.ChatMessageObjectUI __instance)
        {
            if (!Joined.HasValue) return;
            try { TMP_Text t = Traverse.Create(__instance).Field("text").GetValue<TMP_Text>(); if (t != null) t.richText = true; }
            catch (Exception e) { Log.Once("chat-rich", "chat colours: " + e.Message); }
        }

        private static void ChatPostfix(UI.Chat.ChatMessageObjectUI __instance, Managers.Interface.Model.ChatMessage chatMessage)
        {
            if (!Joined.HasValue || string.IsNullOrEmpty(Joined.Value.NameStyle)) return;
            try
            {
                TMP_Text text = Traverse.Create(__instance).Field("text").GetValue<TMP_Text>();
                string name = (chatMessage.PlayerName ?? "").Replace("<", "‹");
                text.text = Chat.Format(Joined.Value.NameStyle, name) + ": " + chatMessage.Message;
                Traverse.Create(__instance).Method("SetDynamicHeight").GetValue();      // the line's height for the new text
                FitPostfix(__instance);
            }
            catch (Exception e) { Log.Once("chat-name", "chat name style: " + e.Message); }
        }

        /// <summary>The pause menu: the server's icon and name in the top-right corner.</summary>
        private static void PausePostfix(UI.Pause.PauseMenuHandler __instance)
        {
            if (!Joined.HasValue) return;
            try
            {
                GameObject canvas = Traverse.Create(__instance).Field("_pauseCanvas").GetValue<GameObject>();
                if (canvas == null) return;
                TMP_Text font = canvas.GetComponentInChildren<TMP_Text>(true);
                GameObject box = new GameObject("DedicatedServer.ServerBadge", typeof(RectTransform));
                RectTransform r = (RectTransform)box.transform;
                r.SetParent(canvas.transform, false);
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(1f, 1f);
                r.anchoredPosition = new Vector2(-30f, -24f);
                r.sizeDelta = new Vector2(900f, 130f);
                Badge(r, font, 96f, Joined.Value.Name, Joined.Value.Players + " / " + Joined.Value.Max + " players" + (Joined.Value.Desc.Length > 0 ? "\n<size=80%>" + Joined.Value.Desc.Replace("<", "‹") + "</size>" : ""), true);
            }
            catch (Exception e) { Log.Once("pause-badge", "server badge on the pause menu: " + e.Message); }
        }

        /// <summary>Icon and two lines of text in a box, the icon at the right edge (right) or the left.</summary>
        private static void Badge(RectTransform box, TMP_Text font, float iconSize, string title, string sub, bool right)
        {
            float x = 0f;
            if (JoinedIcon != null)
            {
                GameObject ig = new GameObject("Icon", typeof(RectTransform));
                RectTransform ir = (RectTransform)ig.transform;
                ir.SetParent(box, false);
                ir.anchorMin = ir.anchorMax = ir.pivot = right ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
                ir.sizeDelta = new Vector2(iconSize, iconSize);
                ir.anchoredPosition = Vector2.zero;
                RawImage img = ig.AddComponent<RawImage>();
                img.texture = JoinedIcon;
                img.raycastTarget = false;
                x = iconSize + 16f;
            }
            GameObject tg = new GameObject("Text", typeof(RectTransform));
            RectTransform tr = (RectTransform)tg.transform;
            tr.SetParent(box, false);
            tr.anchorMin = new Vector2(0f, 0f); tr.anchorMax = new Vector2(1f, 1f);
            tr.offsetMin = new Vector2(right ? 0f : x, 0f); tr.offsetMax = new Vector2(right ? -x : 0f, 0f);
            TextMeshProUGUI t = tg.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font.font;
            t.fontSize = font != null ? font.fontSize * 0.9f : 30f;
            // shrink to fit rather than cut the name short (the pause canvas is scaled down a lot)
            t.enableAutoSizing = true;
            t.fontSizeMax = t.fontSize;
            t.fontSizeMin = t.fontSize * 0.4f;
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.alignment = right ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft;
            t.text = "<b>" + (title ?? "").Replace("<", "‹") + "</b>\n<size=75%><color=#B8C8D8>" + sub + "</color></size>";
        }

        /// <summary>The player list: a heading row with the server's icon, name and players.</summary>
        private static void PlayerListPostfix(UI.Pause.PlayerListMenu __instance)
        {
            if (!Joined.HasValue) return;
            try
            {
                Transform list = Traverse.Create(__instance).Field("playerListTransform").GetValue<Transform>();
                UI.Pause.PlayerListItem prefab = Traverse.Create(__instance).Field("playerListItemPrefab").GetValue<UI.Pause.PlayerListItem>();
                if (list == null || prefab == null || list.Find("DedicatedServer.ServerRow") != null) return;
                UI.Pause.PlayerListItem row = UnityEngine.Object.Instantiate(prefab, list, false);
                row.name = "DedicatedServer.ServerRow";
                RawImage avatar = Traverse.Create(row).Field("avatarImage").GetValue<RawImage>();
                TMP_Text name = Traverse.Create(row).Field("nameText").GetValue<TMP_Text>();
                TMP_Text ping = Traverse.Create(row).Field("pingText").GetValue<TMP_Text>();
                UnityEngine.Object.DestroyImmediate(row);       // not a player: the list leaves it alone
                if (avatar != null) { if (JoinedIcon != null) avatar.texture = JoinedIcon; else avatar.enabled = false; }
                if (name != null) { name.richText = true; name.text = "<b>" + Joined.Value.Name.Replace("<", "‹") + "</b>"; }
                if (ping != null) ping.text = Joined.Value.Players + " / " + Joined.Value.Max + " players";
                list.Find("DedicatedServer.ServerRow").SetSiblingIndex(0);
            }
            catch (Exception e) { Log.Once("list-badge", "server row on the player list: " + e.Message); }
        }
    }
}
