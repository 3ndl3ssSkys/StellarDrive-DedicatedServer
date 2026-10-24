// What the server puts in front of a player, over everything else on screen (menus included):
//   announce  a message in the middle-top of the screen: fades in, stays OnScreenSeconds, fades out.
//             Enter closes it early (and does not also open the chat). Several queue up.
//   ping      a short sound when someone @mentions or whispers to this player.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class Overlay
    {
        private class Item { public string Text; public float Seconds; }

        private static readonly Queue<Item> _queue = new Queue<Item>();
        private static GameObject _root;
        private static CanvasGroup _group;
        private static TMP_Text _text, _hint;
        private static Item _current;
        private static float _t;
        private static int _closedFrame = -10;
        public const float FadeIn = 0.25f, FadeOut = 0.6f;
        public static string Showing { get { return _current != null ? _current.Text : ""; } }   // for the probe
        public static int Shown;

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(UI.Chat.ChatMenu), "OpenChat"),
                    prefix: new HarmonyMethod(typeof(Overlay).GetMethod("OpenChatPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("announcements: Enter may also open the chat: " + e.Message); }
        }

        // the Enter that closed an announcement doesn't open the chat as well
        private static bool OpenChatPrefix() { return Time.frameCount - _closedFrame > 1; }

        public static void OnEvent(DsEvent e)
        {
            float s;
            if (!float.TryParse(e.Data, NumberStyles.Float, CultureInfo.InvariantCulture, out s) || s <= 0f) s = 5f;
            _queue.Enqueue(new Item { Text = e.Text ?? "", Seconds = Mathf.Clamp(s, 1f, 120f) });
            if (_queue.Count > 6) _queue.Dequeue();
        }

        public static void ClientReset() { _queue.Clear(); _current = null; if (_root != null) _root.SetActive(false); }

        /// <summary>Every frame on a player's game.</summary>
        public static void Update()
        {
            if (_current == null)
            {
                if (_queue.Count == 0) return;
                _current = _queue.Dequeue();
                _t = 0f;
                Build();
                _text.text = _current.Text;
                _root.SetActive(true);
                Shown++;
            }
            _t += Time.unscaledDeltaTime;
            float total = FadeIn + _current.Seconds + FadeOut;
            if (EnterPressed() && _t > 0.1f && _t < FadeIn + _current.Seconds)
            {
                _t = FadeIn + _current.Seconds;      // straight to the fade out
                _closedFrame = Time.frameCount;
            }
            float a = _t < FadeIn ? _t / FadeIn : _t < FadeIn + _current.Seconds ? 1f : 1f - (_t - FadeIn - _current.Seconds) / FadeOut;
            _group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a));
            ((RectTransform)_group.transform).anchoredPosition = new Vector2(0f, -110f + (1f - Mathf.Clamp01(_t / FadeIn)) * 18f);
            if (_t >= total) { _current = null; _root.SetActive(false); }
        }

#if DSTEST
        /// <summary>For the probe: as if the player pressed Enter.</summary>
        public static void CloseForTest() { if (_current != null) { _t = FadeIn + _current.Seconds; _closedFrame = Time.frameCount; } }

        /// <summary>For the probe: drop what is showing and waiting, so the next message is the one shown.</summary>
        public static void ClearForTest() { ClientReset(); }
#endif

        private static bool EnterPressed()
        {
            try
            {
                UnityEngine.InputSystem.Keyboard k = UnityEngine.InputSystem.Keyboard.current;
                if (k != null) return k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame;
            }
            catch { }
            return false;
        }

        private static void Build()
        {
            if (_root != null) return;
            _root = new GameObject("DedicatedServer.Announcement", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas c = _root.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 32000;             // over every menu
            CanvasScaler sc = _root.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920f, 1080f);
            sc.matchWidthOrHeight = 0.5f;

            GameObject panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(_root.transform, false);
            RectTransform pr = (RectTransform)panel.transform;
            pr.anchorMin = pr.anchorMax = new Vector2(0.5f, 1f);
            pr.pivot = new Vector2(0.5f, 1f);
            pr.anchoredPosition = new Vector2(0f, -110f);
            _group = panel.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false; _group.interactable = false;
            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.07f, 0.11f, 0.86f);
            bg.raycastTarget = false;
            UnityEngine.UI.Outline ol = panel.AddComponent<UnityEngine.UI.Outline>();
            ol.effectColor = new Color(0.35f, 0.78f, 1f, 0.55f);
            ol.effectDistance = new Vector2(2f, -2f);
            VerticalLayoutGroup v = panel.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(44, 44, 22, 16);
            v.spacing = 6f;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            ContentSizeFitter fit = panel.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TMP_FontAsset font = null;
            TMP_Text any = UnityEngine.Object.FindFirstObjectByType<TMP_Text>();
            if (any != null) font = any.font;
            _text = Label(panel.transform, "Text", font, 34f, Color.white);
            LayoutElement le = _text.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 1100f;
            _hint = Label(panel.transform, "Hint", font, 17f, new Color(0.72f, 0.78f, 0.85f, 0.85f));
            _hint.text = "Enter to close";
        }

        private static TMP_Text Label(Transform parent, string name, TMP_FontAsset font, float size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.color = color;
            t.richText = true;
            t.enableWordWrapping = true;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            return t;
        }

        // ---- ping --------------------------------------------------------------------------------

        private static string _pingEvent;
        private static bool _looked;
        public static int Pings;
        public static string PingSound { get { return _pingEvent ?? ""; } }

        public static void OnPing(DsEvent e)
        {
            Pings++;
            if (!Client.PingSound.Value) return;
            try
            {
                if (!_looked) { _looked = true; _pingEvent = PickSound(); }
                if (!string.IsNullOrEmpty(_pingEvent)) FMODUnity.RuntimeManager.PlayOneShot(_pingEvent);
            }
            catch (Exception ex) { Log.Once("ping", "ping sound: " + ex.Message); }
            Log.Debug("chat", (e.Data == "whisper" ? "whisper from " : "mentioned by ") + e.Text);
        }

        /// <summary>A short interface sound from the game's own banks (the first that fits).</summary>
        private static string PickSound()
        {
            List<string> all = new List<string>();
            try
            {
                FMOD.Studio.Bank[] banks;
                FMODUnity.RuntimeManager.StudioSystem.getBankList(out banks);
                foreach (FMOD.Studio.Bank b in banks)
                {
                    FMOD.Studio.EventDescription[] evs;
                    if (b.getEventList(out evs) != FMOD.RESULT.OK) continue;
                    foreach (FMOD.Studio.EventDescription d in evs) { string p; if (d.getPath(out p) == FMOD.RESULT.OK && !string.IsNullOrEmpty(p)) all.Add(p); }
                }
            }
            catch (Exception e) { Log.Debug("ui", "sound list: " + e.Message); }
            Log.Debug("ui", all.Count + " sound event(s): " + string.Join(", ", all.Take(200).ToArray()));
            string[] want = { "notif", "message", "chat", "ping", "beep", "unlock", "complete", "select", "click", "button", "ui/" };
            foreach (string w in want)
            {
                string hit = all.FirstOrDefault(p => p.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("music", StringComparison.OrdinalIgnoreCase) < 0 && p.IndexOf("ambient", StringComparison.OrdinalIgnoreCase) < 0);
                if (hit != null) return hit;
            }
            return all.FirstOrDefault(p => p.IndexOf("InvalidError", StringComparison.OrdinalIgnoreCase) >= 0) ?? "";
        }
    }
}
