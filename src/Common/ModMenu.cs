#if MOD_GRAVES
namespace Graves.Common
#elif MOD_CHESS
namespace Chess.Common
#elif MOD_PERF
namespace PerformancePlus.Common
#elif MOD_TWEAKS
namespace GameTweaks.Common
#elif MOD_HARNESS
namespace DemoVerifyHarness.Common
#elif MOD_MOMENTUM
namespace Momentum.Common
#elif MOD_CAMERAS
namespace ExtraCameras.Common
#elif MOD_GRAVITY
namespace Gravity.Common
#elif MOD_RAILS
namespace SafetyRails.Common
#elif MOD_SOLAR
namespace SolarPanels.Common
#elif MOD_STATION
namespace StationModels.Common
#elif MOD_VR
namespace StellarDriveVR.Common
#else
namespace SDMod.Common
#endif
{
    using System;
    using System.Collections.Generic;
    using Players.LocalClient;
    using Players.LocalClient.Controls;
    using Players.LocalClient.Interaction;
    using Core.Values;
    using UnityEngine;

    /// <summary>
    /// An IMGUI window drawn in the game's own palette that behaves like the
    /// game's menus: looking, firing, interacting and the other menus are held
    /// off while it is open, walking is left alone (and only stopped while a
    /// text field has focus), and closing always hands every control back.
    /// </summary>
    public class ModMenu
    {
        public string Title;
        public Rect Window;
        public bool IsOpen { get; private set; }
        public Action<ModMenu> Draw;
        public Action OnClosed;
        public bool TextFocused;
        /// <summary>Keep walking stopped the whole time the window is open (not only while typing).</summary>
        public bool HoldMovement;
        /// <summary>
        /// Leave the game's controls, cursor and ESC alone: for a panel that sits
        /// beside something the game itself is running (the game shows the cursor).
        /// </summary>
        public bool Passive;
        public string HintText = "ESC to close";
        public float Width = 460f, Height = 360f;

        private readonly int _id;
        private readonly List<MonoBehaviour> _suppressed = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour> _movement = new List<MonoBehaviour>();
        private CursorLockMode _prevLock;
        private bool _prevVisible;
        private static GUISkin _skin;
        private static GUIStyle _title, _hint, _note;
        private static Color _line = new Color(0.35f, 0.78f, 1f, 1f);
        private static readonly List<Texture2D> _textures = new List<Texture2D>();

        public ModMenu(string title, int id)
        {
            Title = title;
            _id = id;
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            float w = Mathf.Min(Width, Screen.width - 60f), h = Mathf.Min(Height, Screen.height - 60f);
            Window = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            if (Passive) return;
            _prevLock = Cursor.lockState;
            _prevVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Suppress(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            TextFocused = false;
            GUIUtility.keyboardControl = 0;
            if (Passive) { try { OnClosed?.Invoke(); } catch (Exception e) { Log.Warn("menu close: " + e.Message); } return; }
            Suppress(false);
            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
            ReloadGameControls();
            try { OnClosed?.Invoke(); } catch (Exception e) { Log.Warn("menu close: " + e.Message); }
        }

        /// <summary>Call from OnUpdate.</summary>
        public void Update()
        {
            if (!IsOpen || Passive) return;
            foreach (MonoBehaviour c in _suppressed) if (c != null && c.enabled) c.enabled = false;
            bool stop = TextFocused || HoldMovement;
            foreach (MonoBehaviour c in _movement) if (c != null && c.enabled == stop) c.enabled = !stop;
            if (Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
                Close();
        }

        /// <summary>Call from OnGUI (every frame, open or not: the first frames get the look ready).</summary>
        public void OnGUI()
        {
            WarmUp();
            if (!IsOpen) return;
            GUI.depth = -100;
            GUISkin prev = GUI.skin;
            EnsureSkin();
            GUI.skin = _skin;
            Window = GUI.Window(_id, Window, DrawWindow, GUIContent.none);
            GUI.skin = prev;
        }

        private void DrawWindow(int id)
        {
            Outline(new Rect(0, 0, Window.width, Window.height), 2f, _line);
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Title, _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(HintText, _hint);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            try { Draw?.Invoke(this); }
            catch (Exception e) { GUILayout.Label("error: " + e.Message); Log.Once("menu-" + Title, "menu draw: " + e); }
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        /// <summary>A text field that stops walking while it has focus.</summary>
        public string TextField(string name, string value, int max)
        {
            GUI.SetNextControlName(name);
            string r = GUILayout.TextField(value ?? "", max);
            TextFocused = GUI.GetNameOfFocusedControl() == name;
            return r;
        }

        // ---- ready before it is needed ---------------------------------------------------

        private static bool _warm, _paletteFound;
        private static int _warmScene = -1;
        private const string Glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:;!?()[]{}-+=*/%'\"<>_#@&";

        /// <summary>
        /// Build the skin and have the font draw every glyph at every size the menus use, on a
        /// frame where nothing is open - so the first menu opened in a world does not stall on
        /// it. The game's colours are looked up once per world until they are found.
        /// </summary>
        public static void WarmUp()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_warm && (_paletteFound || scene == _warmScene)) return;
            _warmScene = scene;
            if (!_paletteFound && _skin != null)
            {
                foreach (Texture2D t in _textures) if (t != null) UnityEngine.Object.Destroy(t);
                _textures.Clear();
                _skin = null;
            }
            EnsureSkin();
            GUIContent all = new GUIContent(Glyphs);
            foreach (GUIStyle st in new[] { _skin.label, _skin.button, _skin.textField, _skin.box, _title, _hint, _note })
                if (st != null) st.CalcSize(all);
            _warm = true;
        }

        // the local player's controls: found once per player, not searched for on every open
        private static Transform _player;

        private static Transform PlayerRoot()
        {
            if (_player != null) return _player;
            PlayerLookControls look = UnityEngine.Object.FindFirstObjectByType<PlayerLookControls>();
            _player = look != null ? look.transform.root : null;
            return _player;
        }

        private void Suppress(bool on)
        {
            if (!on)
            {
                foreach (MonoBehaviour c in _suppressed) if (c != null) c.enabled = true;
                foreach (MonoBehaviour c in _movement) if (c != null) c.enabled = true;
                _suppressed.Clear();
                _movement.Clear();
                return;
            }
            _suppressed.Clear();
            _movement.Clear();
            Off<PlayerLookControls>();
            Off<PlayerPrimaryActionControl>();
            Off<PlayerSecondaryActionControl>();
            Off<PlayerTertiaryControl>();
            Off<PlayerScrollControl>();
            Off<PlayerToolSelectionControls>();
            Off<PlayerInteractionControl>();
            Off<PlayerObjectInteractionDetector>();
            Off<PlayerChatControl>();
            Off<PlayerInventoryControl>();
            Off<PlayerGuideControl>();
            Off<PlayerEscapeControl>();
            Transform root = PlayerRoot();
            if (root != null)
            {
                _movement.AddRange(root.GetComponentsInChildren<PlayerWalkingControls>(true));
                _movement.AddRange(root.GetComponentsInChildren<PlayerCheatFlyControls>(true));
            }
            else
            {
                foreach (PlayerWalkingControls c in UnityEngine.Object.FindObjectsByType<PlayerWalkingControls>(FindObjectsSortMode.None)) _movement.Add(c);
                foreach (PlayerCheatFlyControls c in UnityEngine.Object.FindObjectsByType<PlayerCheatFlyControls>(FindObjectsSortMode.None)) _movement.Add(c);
            }
        }

        private void Off<T>() where T : MonoBehaviour
        {
            Transform root = PlayerRoot();
            T[] found = root != null ? root.GetComponentsInChildren<T>(true) : UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            foreach (T c in found)
            {
                if (c == null || !c.enabled) continue;
                c.enabled = false;
                _suppressed.Add(c);
            }
        }

        /// <summary>
        /// PlayerControlsActivator switches controls on and off per player
        /// state. Ask it to re-derive its own set, in case a state change landed
        /// while this menu had things off.
        /// </summary>
        public static void ReloadGameControls()
        {
            try
            {
                var m = typeof(PlayerControlsActivator).GetMethod("ReloadEnabledScripts", Refl.Any);
                if (m == null) return;
                foreach (PlayerControlsActivator a in UnityEngine.Object.FindObjectsByType<PlayerControlsActivator>(FindObjectsSortMode.None))
                    m.Invoke(a, m.GetParameters().Length == 0 ? null : new object[m.GetParameters().Length]);
            }
            catch (Exception e) { Log.Once("reload-controls", "could not reload controls: " + e.Message); }
        }

        // ---- look ----------------------------------------------------------

        public static Color LineColor { get { EnsureSkin(); return _line; } }

        private static void EnsureSkin()
        {
            if (_skin != null) return;
            Color back = new Color(0.04f, 0.07f, 0.10f, 0.95f), text = new Color(0.85f, 0.92f, 1f, 1f), textSel = Color.white;
            try
            {
                GameColorPalette[] pal = Resources.FindObjectsOfTypeAll<GameColorPalette>();
                if (pal != null && pal.Length > 0)
                {
                    _paletteFound = true;
                    _line = pal[0].uiLineColor; back = pal[0].uiBackgroundColor; text = pal[0].uiTextColor; textSel = pal[0].uiTextColorSelected;
                }
            }
            catch { }
            back.a = Mathf.Max(back.a, 0.94f);
            _skin = UnityEngine.Object.Instantiate(GUI.skin);
            _skin.hideFlags = HideFlags.DontSave;
            _skin.window.normal.background = Tex(back);
            _skin.window.onNormal.background = _skin.window.normal.background;
            _skin.window.padding = new RectOffset(12, 12, 10, 10);
            _skin.box.normal.background = Tex(Fade(_line, 0.09f));
            _skin.box.normal.textColor = text;
            _skin.button.normal.background = Tex(Fade(_line, 0.22f));
            _skin.button.hover.background = Tex(Fade(_line, 0.45f));
            _skin.button.active.background = Tex(Fade(_line, 0.70f));
            _skin.button.onNormal.background = _skin.button.active.background;
            _skin.button.normal.textColor = text;
            _skin.button.hover.textColor = textSel;
            _skin.button.active.textColor = textSel;
            _skin.button.onNormal.textColor = textSel;
            _skin.button.padding = new RectOffset(10, 10, 6, 6);
            _skin.button.fontSize = 14;
            _skin.label.normal.textColor = text;
            _skin.label.wordWrap = true;
            _skin.label.fontSize = 14;
            _skin.textField.normal.background = Tex(Fade(Color.black, 0.45f));
            _skin.textField.focused.background = _skin.textField.normal.background;
            _skin.textField.normal.textColor = textSel;
            _skin.textField.focused.textColor = textSel;
            _skin.textField.padding = new RectOffset(6, 6, 5, 5);
            _skin.textField.fontSize = 14;
            _skin.textArea.normal.background = _skin.textField.normal.background;
            _skin.textArea.focused.background = _skin.textField.normal.background;
            _skin.textArea.normal.textColor = textSel;
            _skin.textArea.focused.textColor = textSel;
            _skin.toggle.normal.textColor = text;
            _skin.toggle.onNormal.textColor = textSel;
            _skin.scrollView.normal.background = Tex(Fade(Color.black, 0.18f));
            _title = new GUIStyle(_skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = false };
            _title.normal.textColor = Color.Lerp(_line, Color.white, 0.55f);
            _hint = new GUIStyle(_skin.label) { fontSize = 11, wordWrap = false, alignment = TextAnchor.MiddleRight };
            _hint.normal.textColor = Fade(text, 0.6f);
            _note = new GUIStyle(_skin.label) { fontSize = 12, wordWrap = true, alignment = TextAnchor.UpperLeft };
            _note.normal.textColor = Fade(text, 0.7f);
        }

        /// <summary>A one-line, right-aligned aside ("ESC to close").</summary>
        public static GUIStyle Hint { get { EnsureSkin(); return _hint; } }
        /// <summary>Small faded explanatory text that wraps to the window width.</summary>
        public static GUIStyle Note { get { EnsureSkin(); return _note; } }
        public static GUIStyle TitleStyle { get { EnsureSkin(); return _title; } }

        public static Color Fade(Color c, float a) { c.a = a; return c; }

        public static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.DontSave;
            Color[] px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels(px);
            t.Apply();
            _textures.Add(t);
            return t;
        }

        public static void Outline(Rect r, float w, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            Texture2D t = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), t);
            GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), t);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), t);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), t);
            GUI.color = prev;
        }
    }
}
