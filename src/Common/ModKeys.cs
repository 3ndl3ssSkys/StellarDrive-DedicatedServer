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
#elif MOD_DZOOM
namespace DemoZoom.Common
#elif MOD_DSHIP
namespace DemoShipTools.Common
#elif MOD_DPICK
namespace DemoBuildPick.Common
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
    using System.Linq;
    using System.Reflection;
    using Core.Services;
    using HarmonyLib;
    using MelonLoader;
    using Players.Interface.Services;
    using TMPro;
    using UI.Common;
    using UI.Options;
    using UnityEngine;
    using UnityEngine.Events;
    using UnityEngine.InputSystem;
    using UnityEngine.UI;

    /// <summary>One mod key: an input action of its own, shown and rebindable under "Mods" in the game's Keybindings page.</summary>
    public sealed class ModKey
    {
        public readonly string Name, Label, Default;
        /// <summary>Set: the row goes right after this row of the game's own (its object name, e.g. "Sprint"), not under Mods.</summary>
        public string After;
        public readonly InputAction Action;
        internal readonly MelonPreferences_Entry<string> Saved;

        internal ModKey(string name, string label, string def, InputAction action, MelonPreferences_Entry<string> saved)
        {
            Name = name; Label = label; Default = def; Action = action; Saved = saved;
        }

        /// <summary>
        /// Pressed this frame / held / let go this frame (never while it is being rebound).
        /// Edges are found here, from IsPressed frame to frame: the game runs input in its own
        /// update, and WasPressedThisFrame missed presses (Build Pick found the same). A key
        /// looked at again after frames of not being looked at starts fresh rather than firing.
        /// </summary>
        public bool Down { get { Sample(); return _downFrame == Time.frameCount; } }
        public bool Held { get { Sample(); return _was; } }
        public bool Up { get { Sample(); return _upFrame == Time.frameCount; } }

        private int _sampled = -9, _downFrame = -9, _upFrame = -9;
        private bool _was;

        private void Sample()
        {
            int f = Time.frameCount;
            if (_sampled == f) return;
            ModKeys.KeepOn();
            bool now = Action.enabled && Action.IsPressed();
            if (_sampled == f - 1)
            {
                if (now && !_was) _downFrame = f;
                if (!now && _was) _upFrame = f;
            }
            _sampled = f;
            _was = now;
        }
        public string Path { get { return Action.bindings[0].effectivePath; } }
        public string Shown { get { return Action.GetBindingDisplayString(0, InputBinding.DisplayStringOptions.DontIncludeInteractions); } }

        /// <summary>Bind it to a path ("&lt;Keyboard&gt;/g") and remember that, as the Keybindings page does.</summary>
        public void Bind(string path)
        {
            bool on = Action.enabled;
            Action.Disable();
            if (string.IsNullOrEmpty(path) || path == Default) Action.RemoveAllBindingOverrides();
            else Action.ApplyBindingOverride(0, path);
            if (on) Action.Enable();
            if (Saved.Value != Path) { Saved.Value = Path; MelonPreferences.Save(); }
        }
    }

    /// <summary>
    /// The keys of every mod, in one "Mods" section at the end of the game's Keybindings
    /// page. Each mod DLL has its own copy of this class (compiled in under its own
    /// namespace) with its own keys; they meet only in the page itself, where a copy adds
    /// the section heading if nobody has yet, puts its rows under it and sorts the lot by
    /// name - so the section looks the same whichever mods are installed or ran first.
    /// Rows are the game's own rows, cloned (layout, font, the box you click to rebind),
    /// with the game's translation hook taken off so the caption stays ours. Bindings live
    /// in [ModKeybinds] in MelonPreferences.cfg, one readable path per key; the page's
    /// "Reset to Default" resets them too.
    /// </summary>
    public static class ModKeys
    {
        public const string Category = "ModKeybinds";
        public const string HeaderName = "___Mods";
        public const string RowPrefix = "SDModKey:";      // rows in the Mods section
        public const string PlacedPrefix = "SDModKey@";   // rows placed among the game's own

        private static readonly List<ModKey> _keys = new List<ModKey>();
        private static InputActionMap _map;
        private static MelonPreferences_Category _cat;
        private static IPlayerKeybinds _hooked;
        private static InputActionRebindingExtensions.RebindingOperation _rebinding;
        private static bool _patched;

        public static IReadOnlyList<ModKey> All { get { return _keys; } }

        /// <summary>
        /// Add a key. <paramref name="name"/> is its entry in [ModKeybinds] (unique across all
        /// mods); <paramref name="defaultPath"/> an Input System path such as "&lt;Keyboard&gt;/c"
        /// or "&lt;Mouse&gt;/middleButton". <paramref name="carried"/> is a binding a mod had
        /// saved somewhere else before this section existed: used once, if [ModKeybinds] has
        /// nothing yet.
        /// </summary>
        public static ModKey Add(string name, string label, string defaultPath, string carried = null, string after = null)
        {
            ModKey have = _keys.FirstOrDefault(k => k.Name == name);
            if (have != null) return have;
            if (_cat == null)
            {
                _cat = MelonPreferences.GetCategory(Category) ?? MelonPreferences.CreateCategory(Category, "Mod keybinds");
            }
            if (_map == null)
            {
                InputActionAsset asset = ScriptableObject.CreateInstance<InputActionAsset>();
                asset.hideFlags = HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
                asset.name = "SDModKeys " + typeof(ModKeys).Namespace;
                _map = asset.AddActionMap("SDMods");
            }
            MelonPreferences_Entry<string> saved = _cat.GetEntry<string>(name) ?? _cat.CreateEntry(name, defaultPath, label,
                description: label + " - the key, as an Input System path (<Keyboard>/c, <Mouse>/middleButton...). Also rebindable in game: Options > Keybindings > Mods.");
            bool on = _map.enabled;
            if (on) _map.Disable();
            InputAction action = _map.AddAction(name, InputActionType.Button, defaultPath, expectedControlLayout: "Button");
            ModKey key = new ModKey(name, label, defaultPath, action, saved) { After = after };
            _keys.Add(key);
            if (!string.IsNullOrEmpty(carried) && carried != defaultPath && saved.Value == defaultPath) saved.Value = carried;
            string want = (saved.Value ?? "").Trim();
            if (want.Length > 0 && want != defaultPath)
            {
                try { action.ApplyBindingOverride(0, want); }
                catch (Exception e) { MelonLogger.Warning("[" + Category + "] " + name + " = '" + want + "' is not a usable binding (" + e.Message + "); using " + defaultPath); }
            }
            _map.Enable();
            return key;
        }

        /// <summary>
        /// A mod's key settings from before [ModKeybinds]: the binding they held (null if it was
        /// the default) - a key name ("C") or a path in <paramref name="keyEntry"/>, and a rebind
        /// the Keybindings page had saved as JSON in <paramref name="jsonEntry"/> - and then they
        /// (and the other old entries in <paramref name="drop"/>) are taken out of the mod's section.
        /// </summary>
        public static string CarryOld(MelonPreferences_Category cat, string keyEntry, string keyDefault, bool keyName, string jsonEntry, params string[] drop)
        {
            string k = (cat.CreateEntry(keyEntry, keyDefault).Value ?? keyDefault).Trim();
            string j = cat.CreateEntry(jsonEntry, "").Value ?? "";
            string path = null;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(j, @"""path""\s*:\s*""([^""]+)""");
            if (m.Success) path = m.Groups[1].Value;
            else if (k.Length > 0 && k != keyDefault)
            {
                if (!keyName) path = k;
                else
                {
                    string control = null;
                    try { if (Keyboard.current != null) control = Keyboard.current[(Key)Enum.Parse(typeof(Key), k, true)].name; } catch (Exception) { }
                    path = "<Keyboard>/" + (control ?? char.ToLowerInvariant(k[0]) + k.Substring(1));
                }
            }
            cat.DeleteEntry(keyEntry);
            cat.DeleteEntry(jsonEntry);
            foreach (string d in drop) cat.DeleteEntry(d);
            return path;
        }

        /// <summary>Hook the page (once per DLL). Call from OnInitializeMelon with the mod's Harmony.</summary>
        public static void Patch(HarmonyLib.Harmony h)
        {
            if (_patched) return;
            _patched = true;
            h.Patch(AccessTools.Method(typeof(MenuNavigation), "InitMenu"), postfix: new HarmonyMethod(typeof(ModKeys), nameof(MenuOpened)));
        }

        /// <summary>
        /// Call every frame: follows the game's "Reset to Default". The keybinds service is
        /// made again with each scene (the title screen has one, the world another), so the
        /// hook moves with it.
        /// </summary>
        public static void Update()
        {
            if (_keys.Count == 0) return;
            KeepOn();
            IPlayerKeybinds k = ServiceLocator.GetService<IPlayerKeybinds>();
            if (ReferenceEquals(k, _hooked) && (!(k is UnityEngine.Object) || (UnityEngine.Object)k != null)) return;
            if (_hooked != null && (!(_hooked is UnityEngine.Object) || (UnityEngine.Object)_hooked != null))
                _hooked.OnKeybindsReset = (Action)Delegate.Remove(_hooked.OnKeybindsReset, new Action(ResetAll));
            _hooked = k;
            if (k != null) k.OnKeybindsReset = (Action)Delegate.Combine(k.OnKeybindsReset, new Action(ResetAll));
        }

        /// <summary>
        /// Switch the keys back on if something switched them off behind our back. Since Unity
        /// 6000.3.24 (Input System 1.20) the Input System's own start-up
        /// (InputActionState.InitializeGlobalActionState, BeforeSceneLoad) runs after the mods'
        /// OnInitializeMelon and wipes every action map's state: the keys stayed bound but off
        /// until a rebind switched them on again. Left alone while a key is being rebound.
        /// </summary>
        internal static void KeepOn()
        {
            if (_map == null || _rebinding != null) return;
            foreach (InputAction a in _map.actions) if (!a.enabled) { a.Enable(); }
        }

        private static void ResetAll()
        {
            CancelRebind();
            foreach (ModKey k in _keys) k.Bind(k.Default);
            RefreshRows();
        }

        // ---- the page -------------------------------------------------------------------

        private static readonly List<KeyValuePair<ModKey, TMP_Text>> _shown = new List<KeyValuePair<ModKey, TMP_Text>>();

        private static void MenuOpened(GameObject ____currentMenu)
        {
            try { if (____currentMenu != null) Build(____currentMenu); }
            catch (Exception e) { MelonLogger.Warning("[ModKeys] could not add the Mods keybinds: " + e); }
        }

        /// <summary>Put this DLL's keys (and, if missing, the "Mods" heading) on a Keybindings page.</summary>
        public static void Build(GameObject page)
        {
            if (_keys.Count == 0) return;
            ControlKeybindInput[] rows = page.GetComponentsInChildren<ControlKeybindInput>(true);
            if (rows.Length == 0) return;    // not the Keybindings page
            Transform list = rows.Where(r => !Ours(r.transform) && r.transform.parent != null)
                .GroupBy(r => r.transform.parent).OrderByDescending(g => g.Count()).First().Key;
            ControlKeybindInput template = rows.LastOrDefault(r => r.transform.parent == list && !Ours(r.transform));
            if (template == null) return;

            Transform header = list.Find(HeaderName);
            if (header == null && _keys.Any(k => k.After == null))
            {
                Transform game = null;
                for (int i = 0; i < list.childCount; i++)
                {
                    Transform c = list.GetChild(i);
                    if (c.name.StartsWith("___") && c.name != HeaderName) game = c;
                }
                if (game == null) return;
                header = Clone(game.gameObject, list, HeaderName).transform;
                foreach (TMP_Text t in header.GetComponentsInChildren<TMP_Text>(true)) t.text = "Mods";
            }

            _shown.RemoveAll(p => p.Value == null);
            foreach (ModKey key in _keys)
            {
                string rowName = (key.After == null ? RowPrefix : PlacedPrefix) + key.Name;
                if (list.Find(rowName) != null) continue;
                GameObject row = Clone(template.gameObject, list, rowName, go =>
                {
                    ControlKeybindInput input = go.GetComponent<ControlKeybindInput>();
                    input.actionRef = InputActionReference.Create(key.Action);
                    input.mirroredActions = new List<InputActionReference>();
                    Set(input, "useBindingIndex", true);
                    Set(input, "bindingIndex", 0);
                    Set(input, "ignoreMouse", false);
                });
                ControlKeybindInput ci = row.GetComponent<ControlKeybindInput>();
                TMP_Text keyText = Get(ci, "keyText") as TMP_Text;
                foreach (TMP_Text t in row.GetComponentsInChildren<TMP_Text>(true))
                    if (t != keyText) { t.text = key.Label; break; }
                if (keyText != null) keyText.text = key.Shown;
                Button button = row.GetComponentInChildren<Button>(true);
                if (button != null)
                {
                    for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++) button.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
                    ModKey k = key;
                    button.onClick.AddListener(() => StartRebind(k, keyText));
                }
                _shown.Add(new KeyValuePair<ModKey, TMP_Text>(key, keyText));
            }

            // keys that belong with the game's own: right after their row
            foreach (ModKey key in _keys.Where(k => k.After != null))
            {
                Transform row = list.Find(PlacedPrefix + key.Name), anchor = null;
                for (int i = 0; i < list.childCount && anchor == null; i++) if (list.GetChild(i).name == key.After) anchor = list.GetChild(i);
                if (row != null && anchor != null) row.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
                else if (row != null && header != null) row.name = RowPrefix + key.Name;   // no such row in this game: under Mods after all
            }
            if (header == null) return;
            // the heading, then every mod's rows by caption: the same order whoever built it
            header.SetAsLastSibling();
            List<Transform> mine = new List<Transform>();
            for (int i = 0; i < list.childCount; i++) if (list.GetChild(i).name.StartsWith(RowPrefix)) mine.Add(list.GetChild(i));
            foreach (Transform r in mine.OrderBy(r => Caption(r), StringComparer.OrdinalIgnoreCase)) r.SetAsLastSibling();
        }

        private static bool Ours(Transform t) { return t.name.StartsWith(RowPrefix) || t.name.StartsWith(PlacedPrefix); }

        private static string Caption(Transform row)
        {
            TMP_Text t = row.GetComponentInChildren<TMP_Text>(true);
            return t != null ? t.text : row.name;
        }

        /// <summary>
        /// A copy of one of the page's own children, made while switched off so none of its
        /// scripts run before it is ours; the translation hook (which would put the original's
        /// caption back) is removed before it ever wakes.
        /// </summary>
        private static GameObject Clone(GameObject original, Transform parent, string name, Action<GameObject> setUp = null)
        {
            GameObject hold = new GameObject("SDModKeysHold");
            hold.SetActive(false);
            GameObject go = UnityEngine.Object.Instantiate(original, hold.transform);
            go.name = name;
            foreach (Component c in go.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "LocalizeStringEvent") UnityEngine.Object.DestroyImmediate(c);
            setUp?.Invoke(go);
            go.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(hold);
            return go;
        }

        private static void RefreshRows()
        {
            foreach (var p in _shown) if (p.Value != null) p.Value.text = p.Key.Shown;
        }

        // ---- rebinding --------------------------------------------------------------------

        public static bool Rebinding { get { return _rebinding != null; } }

        /// <summary>Wait for the next key or mouse button and bind it (Escape cancels).</summary>
        public static void StartRebind(ModKey key, TMP_Text shown)
        {
            if (_rebinding != null) CancelRebind();
            key.Action.Disable();
            if (shown != null) shown.text = "...";
            _rebinding = key.Action.PerformInteractiveRebinding(0)
                .WithExpectedControlType("Button")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithControlsExcluding("<Pointer>/press")
                .WithControlsExcluding("<Touchscreen>")
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(op => Finish(key, shown, true))
                .OnCancel(op => Finish(key, shown, false));
            _rebinding.Start();
        }

        private static void Finish(ModKey key, TMP_Text shown, bool done)
        {
            try { _rebinding?.Dispose(); } catch (Exception) { }
            _rebinding = null;
            key.Action.Enable();
            if (done)
            {
                key.Saved.Value = key.Path;
                MelonPreferences.Save();
                MelonLogger.Msg("[ModKeybinds] " + key.Label + " is now " + key.Shown);
            }
            if (shown != null) shown.text = key.Shown;
        }

        private static void CancelRebind()
        {
            if (_rebinding == null) return;
            try { _rebinding.Cancel(); } catch (Exception) { }
            try { _rebinding?.Dispose(); } catch (Exception) { }
            _rebinding = null;
        }

        /// <summary>The key display of one key's row on the open page ("" if the page is not open).</summary>
        public static string RowText(ModKey key)
        {
            foreach (var p in _shown) if (p.Key == key && p.Value != null) return p.Value.text;
            return "";
        }

        private static void Set(object o, string field, object v)
        {
            FieldInfo f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) f.SetValue(o, v);
        }

        private static object Get(object o, string field)
        {
            FieldInfo f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? f.GetValue(o) : null;
        }
    }
}
