// Joining a server, and staying on one, when the server stops answering.
//
// While the loading screen is up after joining by address there is a "Cancel and leave" button, and a
// line saying whether the server is answering. Anything arriving on the game connection counts; only when
// that has been quiet for a few seconds is the server asked (the Join page's question, over the game port),
// so a player on a server sends no extra packets of their own. Once in the world, if the server stops answering for 15 seconds
// (its process is up but frozen, say), a notice says so, with a "Leave" button.
// While either is up the mouse is free to click it (in game the mouse is held for looking around), and
// F10 leaves too.
using System;
using Core.Services;
using Managers.Interface;
using Managers.Interface.Services;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class JoinWatch
    {
        private static GameObject _root;
        private static TMP_Text _line;
        private static Button _leave;
        private static TMP_Text _leaveLabel;
        private static float _nextAsk, _lastAnswer, _since;
        private static bool _asking;
        private static string _address;
        public static string State = "";           // for the probe: "", "loading", "not responding"
        private static bool _freed;                 // we let the mouse go (and take it back when the notice goes)

        // when the game connection last brought anything from the server (a clock that's safe off the main thread)
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static long _lastDataMs = -1;
        public static int Asked;                    // for the probe: status questions sent while joined

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(HarmonyLib.AccessTools.Method(typeof(FishNet.Transporting.Tugboat.Tugboat), "HandleClientReceivedDataArgs"),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(JoinWatch).GetMethod("ReceivedPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("join watch: can't see the game connection's own traffic (it asks the server instead): " + e.Message); }
        }

        private static void ReceivedPostfix() { _lastDataMs = _clock.ElapsedMilliseconds; }

        /// <summary>Seconds since the game connection last brought something (a large number when nothing yet).</summary>
        private static float DataQuiet { get { return _lastDataMs < 0 ? 1e6f : (_clock.ElapsedMilliseconds - _lastDataMs) / 1000f; } }

        private static bool JoinedByAddress { get { return MainGameSceneLoadData.Mode == MainGameSceneLoadData.GameMode.JoinGameIp; } }

        /// <summary>Every frame on a player's game.</summary>
        public static void Update()
        {
            bool inGame = SceneManager.GetActiveScene().name == "MainGame";
            if (!inGame || !JoinedByAddress) { Hide(); _since = 0f; return; }
            float now = Time.realtimeSinceStartup;
            if (_since == 0f) { _since = now; _lastAnswer = now; _address = Client.LastAddress.Value; }
            float dataQuiet = DataQuiet;
            if (now - dataQuiet > _lastAnswer) _lastAnswer = now - dataQuiet;
            if (now >= _nextAsk && !_asking && dataQuiet > 3f)      // only when the connection itself has gone quiet
            {
                _nextAsk = now + 4f;
                _asking = true;
                Asked++;
                Query.AskServer(_address, (info, ms) =>
                {
                    _asking = false;
                    if (info != null) _lastAnswer = Time.realtimeSinceStartup;
                    Log.Debug("net", "server " + (info != null ? "answered in " + ms + " ms" : "did not answer"));
                });
            }
            float quiet = now - _lastAnswer;
            UI.Loading.LoadingScreenDisabler loading = UnityEngine.Object.FindFirstObjectByType<UI.Loading.LoadingScreenDisabler>();
            if (loading != null && loading.gameObject.activeInHierarchy)
            {
                State = "loading";
                Show("Leave", (quiet > 9f ? "<color=#FFB84D>The server hasn't answered for " + Mathf.RoundToInt(quiet) + " s. It may be stuck.</color>"
                                          : "Joining " + _address + "... (" + Mathf.RoundToInt(now - _since) + " s)") + " <size=75%><color=#B8C8D8>(F10)</color></size>");
                return;
            }
            if (quiet > 15f)
            {
                State = "not responding";
                Show("Leave", "<color=#FFB84D>The server isn't responding</color> <size=80%>(" + Mathf.RoundToInt(quiet) + " s)</size> <size=75%><color=#B8C8D8>F10 or click to leave</color></size>");
                return;
            }
            State = "";
            Hide();
        }

        /// <summary>Stops the connection and goes back to the main menu, as Quit to Title does.</summary>
        public static void Leave()
        {
            Log.Msg("left " + _address + " from the " + (State == "loading" ? "loading screen" : "game") + " (the player chose to)");
            Hide();
            try
            {
                INetworkConnectionStopService stop = ServiceLocator.GetService<INetworkConnectionStopService>();
                if (stop != null) stop.StopConnection();
                else if (FishNet.InstanceFinder.ClientManager != null && FishNet.InstanceFinder.ClientManager.Started) FishNet.InstanceFinder.ClientManager.StopConnection();   // still on the loading screen
            }
            catch (Exception e) { Log.Warn("leaving: " + e.Message); }
            SceneManager.LoadScene(0, LoadSceneMode.Single);
        }

        private static void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            if (_freed)
            {
                _freed = false;
                if (SceneManager.GetActiveScene().name == "MainGame" && State == "") { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            }
        }

        /// <summary>After the game's own frame: while the notice is up the mouse stays free, and F10 leaves.</summary>
        public static void LateUpdate()
        {
            // the turned-away popup: it came up from the loading screen, where the game held the mouse, so it couldn't be clicked
            if (ClientUi.Shown != null && ClientUi.Shown.gameObject.activeInHierarchy)
            {
                if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            }
            if (_root == null || !_root.activeSelf) return;
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; _freed = true; }
            UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame) Leave();
        }

        private static void Show(string button, string line)
        {
            Build();
            if (!_root.activeSelf) _root.SetActive(true);
            _line.text = line;
            _leaveLabel.text = State == "loading" ? "Cancel and leave" : button;
        }

        private static void Build()
        {
            if (_root != null) return;
            _root = new GameObject("DedicatedServer.JoinWatch", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas c = _root.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 31990;
            CanvasScaler sc = _root.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920f, 1080f);
            sc.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                GameObject es = new GameObject("DedicatedServer.EventSystem", typeof(UnityEngine.EventSystems.EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                es.transform.SetParent(_root.transform, false);
            }

            GameObject panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(_root.transform, false);
            RectTransform pr = (RectTransform)panel.transform;
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0.5f, 0f);
            pr.anchoredPosition = new Vector2(0f, 150f);
            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.07f, 0.11f, 0.85f);
            HorizontalLayoutGroup hl = panel.AddComponent<HorizontalLayoutGroup>();
            hl.padding = new RectOffset(22, 14, 10, 10);
            hl.spacing = 20f;
            hl.childAlignment = TextAnchor.MiddleCenter;
            hl.childControlWidth = hl.childControlHeight = true;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;
            ContentSizeFitter fit = panel.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TMP_FontAsset font = null;
            TMP_Text any = UnityEngine.Object.FindFirstObjectByType<TMP_Text>();
            if (any != null) font = any.font;

            GameObject lg = new GameObject("Line", typeof(RectTransform));
            lg.transform.SetParent(panel.transform, false);
            TextMeshProUGUI line = lg.AddComponent<TextMeshProUGUI>();
            if (font != null) line.font = font;
            line.fontSize = 22f; line.color = Color.white; line.richText = true; line.enableWordWrapping = false; line.raycastTarget = false;
            line.alignment = TextAlignmentOptions.MidlineLeft;
            _line = line;

            GameObject bgo = new GameObject("Leave", typeof(RectTransform));
            bgo.transform.SetParent(panel.transform, false);
            Image bimg = bgo.AddComponent<Image>();
            bimg.color = new Color(0.08f, 0.2f, 0.3f, 1f);
            _leave = bgo.AddComponent<Button>();
            ColorBlock cb = _leave.colors;
            cb.highlightedColor = new Color(0.55f, 0.8f, 1f, 1f);
            cb.pressedColor = new Color(0.35f, 0.6f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            _leave.colors = cb;
            _leave.onClick.AddListener(Leave);
            LayoutElement le = bgo.AddComponent<LayoutElement>();
            le.minWidth = 210f; le.minHeight = 44f;
            GameObject tgo = new GameObject("Label", typeof(RectTransform));
            tgo.transform.SetParent(bgo.transform, false);
            RectTransform tr = (RectTransform)tgo.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(12f, 4f); tr.offsetMax = new Vector2(-12f, -4f);
            TextMeshProUGUI label = tgo.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            label.fontSize = 22f; label.color = Color.white; label.alignment = TextAlignmentOptions.Center; label.raycastTarget = false;
            _leaveLabel = label;
            _root.SetActive(false);
        }

        /// <summary>For the probe: the button, as a player presses it.</summary>
        public static bool PressLeave() { if (_root == null || !_root.activeSelf) return false; _leave.onClick.Invoke(); return true; }
    }
}
