// Finishing names in the chat box: player names (in commands that take one, and after @) and item
// names (in /give). While typing, the best match for what has been typed so far shows above the box:
// the shortest name that starts with it, else one that contains it. Tab fills it in; so does a space,
// where a player's name is expected (a command's player, or @name).
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Players;
using Players.Services;
using TMPro;
using UnityEngine;

namespace DedicatedServer
{
    internal static class ChatComplete
    {
        // command -> which words after it are a player's name (1 = the first)
        private static readonly Dictionary<string, int[]> PlayerArgs = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "give", new[] { 1 } }, { "tp", new[] { 1, 2 } }, { "bring", new[] { 1 } }, { "kick", new[] { 1 } }, { "ban", new[] { 1 } }, { "unban", new[] { 1 } },
            { "op", new[] { 1 } }, { "deop", new[] { 1 } }, { "admin", new[] { 1 } }, { "deadmin", new[] { 1 } }, { "perm", new[] { 1 } }, { "unlink", new[] { 1 } },
            { "banip", new[] { 1 } }, { "whisper", new[] { 1 } }, { "w", new[] { 1 } }, { "msg", new[] { 1 } }, { "tell", new[] { 1 } }, { "pm", new[] { 1 } }, { "dm", new[] { 1 } },
            { "friend", new[] { 1 } }, { "unfriend", new[] { 1 } }, { "setspawn", new[] { 1 } }, { "clearspawn", new[] { 1 } }, { "tpforward", new[] { 1 } }, { "ship", new[] { 2 } },
        };

        private static TMP_InputField _input;
        private static TMP_Text _hint;
        private static string _lastText = "";
        public static string Suggestion = "";                // for the probe

        private enum Slot { None, Player, Item }

        public static void Update()
        {
            if (!Net.ClientStarted) return;
            if (_input == null)
            {
                UI.Chat.ChatMenu menu = UnityEngine.Object.FindFirstObjectByType<UI.Chat.ChatMenu>(FindObjectsInactive.Include);
                if (menu == null) return;
                _input = Traverse.Create(menu).Field("inputField").GetValue<TMP_InputField>();
                if (_input == null) return;
                _input.onValueChanged.AddListener(OnChanged);
            }
            // the chat box is open while it is interactable (the game turns that off when it closes)
            if (!_input.interactable || !_input.gameObject.activeInHierarchy) { ShowHint(""); return; }
            bool tab = false;
            try { UnityEngine.InputSystem.Keyboard k = UnityEngine.InputSystem.Keyboard.current; tab = k != null && k.tabKey.wasPressedThisFrame; } catch { }
            if (tab) Complete(false);
            Slot slot; string token; int start;
            string best = Best(_input.text.Replace("\t", ""), out slot, out token, out start);
            ShowHint(best != null && !string.Equals(best, token, StringComparison.OrdinalIgnoreCase) ? "Tab: " + best : "");
        }

        /// <summary>Fills in the best match for the word being typed. True if it changed anything.</summary>
        public static bool Complete(bool spaceTyped)
        {
            if (_input == null) return false;
            string text = _input.text.Replace("\t", "");
            if (spaceTyped && text.EndsWith(" ")) text = text.Substring(0, text.Length - 1);
            Slot slot; string token; int start;
            string best = Best(text, out slot, out token, out start);
            if (best == null || (spaceTyped && slot != Slot.Player) || string.Equals(best, token, StringComparison.OrdinalIgnoreCase) && !spaceTyped) { if (_input.text.Contains("\t")) Set(text); return false; }
            Set(text.Substring(0, start) + best + " ");
            return true;
        }

        private static void Set(string text)
        {
            _lastText = text;
            _input.SetTextWithoutNotify(text);
            _input.caretPosition = _input.stringPosition = text.Length;
        }

        private static void OnChanged(string now)
        {
            string was = _lastText;
            _lastText = now;
            if (now.Length == was.Length + 1 && now.EndsWith(" ") && now.StartsWith(was)) Complete(true);
            else if (now.Contains("\t")) Complete(false);
        }

        /// <summary>The best match for the last word of the text, and where that word starts.</summary>
        private static string Best(string text, out Slot slot, out string token, out int start)
        {
            slot = Slot.None; token = ""; start = text.Length;
            int sp = text.LastIndexOf(' ');
            start = sp + 1;
            token = text.Substring(start);
            if (token.StartsWith("@")) { slot = Slot.Player; start++; token = token.Substring(1); return Match(token, PlayerNames()); }
            if (!text.StartsWith("/")) return null;
            string[] words = text.Substring(1).Split(new[] { ' ' }, StringSplitOptions.None);
            string cmd = words[0];
            int index = words.Length - 1;                    // the word being typed: 0 = the command itself
            if (index == 0) return null;
            int[] players;
            if (PlayerArgs.TryGetValue(cmd, out players) && players.Contains(index) && !(cmd.Equals("friend", StringComparison.OrdinalIgnoreCase) && token.Equals("remove", StringComparison.OrdinalIgnoreCase)))
            { slot = Slot.Player; return Match(token, PlayerNames()); }
            if (cmd.Equals("give", StringComparison.OrdinalIgnoreCase) && index >= 2)
            {
                // an item's name may be several words: match all the words after the player's name
                int itemStart = text.IndexOf(' ', text.IndexOf(' ') + 1) + 1;
                string typed = text.Substring(itemStart);
                string hit = Match(typed, AdminItems.All().Select(i => i.Name));
                if (hit == null) return null;
                slot = Slot.Item; start = itemStart; token = typed;
                return hit;
            }
            return null;
        }

        private static string Match(string typed, IEnumerable<string> names)
        {
            if (string.IsNullOrEmpty(typed)) return null;
            List<string> l = names.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            return l.Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).ThenBy(n => n).FirstOrDefault()
                ?? l.Where(n => n.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n.Length).ThenBy(n => n).FirstOrDefault();
        }

        private static IEnumerable<string> PlayerNames()
        {
            PlayersClientTracker t = Core.Services.ServiceLocator.GetService<PlayersClientTracker>();
            if (t == null) yield break;
            if (t.LocalPlayer != null) yield return t.LocalPlayer.Name;
            foreach (TrackedPlayerOtherClient o in t.OtherPlayers) if (o != null) yield return o.Name;
        }

        private static void ShowHint(string s)
        {
            Suggestion = s;
            if (_input == null) return;
            if (_hint == null)
            {
                if (s.Length == 0) return;
                GameObject go = new GameObject("DedicatedServer.ChatHint", typeof(RectTransform));
                go.transform.SetParent(_input.transform, false);
                RectTransform r = (RectTransform)go.transform;
                r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 1f);
                r.offsetMin = new Vector2(8f, 0f); r.offsetMax = new Vector2(-10f, 0f);
                TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
                t.font = _input.textComponent != null ? _input.textComponent.font : t.font;
                t.fontSize = 17f;
                t.alignment = TextAlignmentOptions.MidlineRight;
                t.color = new Color(0.62f, 0.75f, 0.9f, 0.8f);
                t.raycastTarget = false;
                t.enableWordWrapping = false;
                _hint = t;
            }
            if (_hint.text != s) _hint.text = s;
            _hint.gameObject.SetActive(s.Length > 0);
        }

#if DSTEST
        /// <summary>For the probe: type into the chat box as a player would, then press Tab (or not).</summary>
        public static string TypeForTest(string text, bool tab)
        {
            if (_input == null) return "no chat box";
            _lastText = "";
            _input.text = text;
            if (tab) Complete(false);
            return _input.text;
        }
#endif
    }
}
