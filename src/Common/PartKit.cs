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
    using System.Linq;
    using System.Reflection;
    using HarmonyLib;
    using Items.Model;
    using Players.Interface.Model;
    using Ships.Interface.Model;
    using Ships.Interface.Model.Parts;
    using Ships.Interface.Services;
    using Ships.Interface.Settings;
    using Ships.Parts.Common;
    using TMPro;
    using UnityEngine;
    using UnityEngine.Localization;
    using UnityEngine.Localization.Components;

    /// <summary>
    /// What a new ship part is. Everything the game needs to build, save, sync,
    /// render and interact with it comes from here.
    /// </summary>
    public class PartDef
    {
        public ushort Id;
        public string AssetName;                 // PartSettings.name, also the prefab name
        public string Label;                     // build menu / tooltips
        public string Description = "";
        public string StateClass;                // e.g. "StorageState" - picks the network/save format
        public SnappingStyle Snap = SnappingStyle.PreciseOnFloor;
        public float Mass = 10f;
        public Vector3 Size = Vector3.one;       // PartSettings.size (snapping footprint)
        public List<KeyValuePair<uint, int>> Cost = new List<KeyValuePair<uint, int>>();
        public Vector3 BoundsCenter = new Vector3(0, 0.5f, 0);
        public Vector3 BoundsSize = Vector3.one; // collider + destruction hardness
        public Type VisualType = typeof(DefaultShipPartVisuals);
        public Action<Transform> BuildVisual;    // add renderers under the Visual transform
        public Type InteractionType;             // a PartInteraction subclass, or null for none
        public Vector3 InteractCenter = new Vector3(0, 0.5f, 0);
        public Vector3 InteractSize = Vector3.one;
        public Action<GameObject> PostBuild;     // last chance to add anything to the prefab root
        public ushort MenuAfter;                 // list it in the build menu right after this part (and nowhere else)
        public string[] Research;                // the research that unlocks it, by name - the first of these that exists (else: whatever unlocks MenuAfter)
        public string MenuTab;                   // with no MenuAfter: list it at the end of this build menu tab, if there is one
        public PartSettings Settings;            // filled in once built
        public GameObject Prefab;
    }

    /// <summary>
    /// Registers new ship parts with the game.
    ///
    /// The game resolves part ids through PartSettingsProvider, which snapshots
    /// Resources.FindObjectsOfTypeAll&lt;PartSettings&gt;() the first time each
    /// instance is asked - and there are a dozen instances (MasterSync, the
    /// save converter, the build tool, the thumbnail cache ...). Rather than race
    /// them, a postfix on the snapshot itself adds our parts to every one, the
    /// moment it is taken. The parts are built lazily at that point, because
    /// that is also the first moment the vanilla part assets (whose materials
    /// and fonts we borrow) are guaranteed to be loaded.
    /// </summary>
    public static class PartKit
    {
        public static readonly List<PartDef> Defs = new List<PartDef>();
        private static readonly Dictionary<ushort, PartDef> _byId = new Dictionary<ushort, PartDef>();
        private static GameObject _holder;
        private static bool _patched;
        private static FieldInfo _dictField, _costField;

        public static void Register(PartDef def)
        {
            Defs.Add(def);
            _byId[def.Id] = def;
        }

        public static bool IsOurs(ushort id) { return _byId.ContainsKey(id); }
        public static PartDef Get(ushort id) { PartDef d; _byId.TryGetValue(id, out d); return d; }
        public static bool IsOurs(PartSettings s) { return s != null && _byId.ContainsKey(s.id); }

        public static void Patch(HarmonyLib.Harmony h)
        {
            if (_patched) return;
            _patched = true;
            _dictField = typeof(PartSettingsProvider).GetField("_partsDict", Refl.Any);
            _costField = typeof(PartSettings).GetField("buildingCost", Refl.Any);
            MethodInfo ensure = typeof(PartSettingsProvider).GetMethod("EnsureDictIsCached", Refl.Any);
            h.Patch(ensure, postfix: new HarmonyMethod(typeof(PartKit).GetMethod(nameof(EnsurePostfix), Refl.Any)));
            MethodInfo tab = Refl.Method(typeof(UI.BuildMenu.BuildMenu), "LoadTabPanel");
            if (tab != null) h.Patch(tab, prefix: new HarmonyMethod(typeof(PartKit).GetMethod(nameof(MenuPrefix), Refl.Any)));
            MethodInfo tip = Refl.Method(typeof(UI.Interaction.InteractionTooltipsActivator), "InstantiateTooltip");
            if (tip != null) h.Patch(tip, postfix: new HarmonyMethod(typeof(PartKit).GetMethod(nameof(TooltipPostfix), Refl.Any)));
        }

        // ---- adjusting vanilla parts --------------------------------------------

        private static readonly List<KeyValuePair<Func<PartSettings, bool>, Action<PartSettings>>> _vanillaHooks = new List<KeyValuePair<Func<PartSettings, bool>, Action<PartSettings>>>();
        private static readonly HashSet<PartSettings> _hooked = new HashSet<PartSettings>();

        /// <summary>
        /// Run <paramref name="modify"/> once on every vanilla part matching
        /// <paramref name="match"/>, the first time the game snapshots its parts -
        /// which is before any ship (and so any copy of the prefab) exists.
        /// </summary>
        public static void OnVanilla(Func<PartSettings, bool> match, Action<PartSettings> modify)
        {
            _vanillaHooks.Add(new KeyValuePair<Func<PartSettings, bool>, Action<PartSettings>>(match, modify));
        }

        private static void RunVanillaHooks(Dictionary<ushort, PartSettings> dict)
        {
            if (_vanillaHooks.Count == 0) return;
            foreach (PartSettings s in dict.Values)
            {
                if (s == null || _byId.ContainsKey(s.id) || _hooked.Contains(s)) continue;
                _hooked.Add(s);
                foreach (var h in _vanillaHooks)
                {
                    try { if (h.Key(s)) h.Value(s); }
                    catch (Exception e) { Log.Once("vanilla-" + s.id, "could not adjust part " + s.fullLabel + ": " + e.Message); }
                }
            }
        }

        // EnsureDictIsCached runs on EVERY part lookup, so the common case -
        // a provider whose snapshot we already completed - must cost one lookup.
        private static readonly Dictionary<object, int> _done = new Dictionary<object, int>();

        private static void EnsurePostfix(PartSettingsProvider __instance)
        {
            try
            {
                var dict = _dictField.GetValue(__instance) as Dictionary<ushort, PartSettings>;
                if (dict == null) return;
                int seen;
                if (_done.TryGetValue(dict, out seen) && seen == dict.Count) return;
                RunVanillaHooks(dict);
                if (Defs.Count == 0) { _done[dict] = dict.Count; return; }
                if (dict.ContainsKey(Defs[Defs.Count - 1].Id) && dict.ContainsKey(Defs[0].Id)) { _done[dict] = dict.Count; return; }
                foreach (PartDef d in Defs)
                {
                    if (dict.ContainsKey(d.Id))
                    {
                        if (dict[d.Id] != d.Settings) Log.Once("partid-" + d.Id, "part id " + d.Id + " is already taken by '" + dict[d.Id].name + "' - " + d.Label + " not added.");
                        continue;
                    }
                    PartSettings s = Build(d);
                    if (s != null) dict[d.Id] = s;
                }
                _done[dict] = dict.Count;
            }
            catch (Exception e) { Log.Once("partkit-ensure", "PartKit: " + e); }
        }

        /// <summary>Build (once) the settings asset and prefab for a part.</summary>
        public static PartSettings Build(PartDef d)
        {
            if (d.Settings != null) return d.Settings;
            try
            {
                if (_holder == null)
                {
                    // Prefabs must stay ACTIVE: the game finds a part's visual
                    // with GetComponentInChildren, which skips inactive objects.
                    // They are parked far below the world and never rendered.
                    _holder = new GameObject("SDMod_PartPrefabs_" + typeof(PartKit).Namespace);
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                    _holder.transform.position = new Vector3(0f, -90000f, 0f);
                }

                GameObject root = new GameObject(d.AssetName);
                root.transform.SetParent(_holder.transform, false);
                ShipPartBounds bounds = root.AddComponent<ShipPartBounds>();
                bounds.center = d.BoundsCenter;
                bounds.bounds = d.BoundsSize;

                GameObject visual = new GameObject("Visual");
                visual.transform.SetParent(root.transform, false);
                visual.AddComponent(d.VisualType ?? typeof(DefaultShipPartVisuals));
                d.BuildVisual?.Invoke(visual.transform);

                GameObject inter = new GameObject("Interactions");
                inter.transform.SetParent(root.transform, false);
                inter.AddComponent<DefaultShipPartInteractions>();
                if (d.InteractionType != null)
                {
                    GameObject col = new GameObject(d.AssetName);
                    col.layer = LayerMask.NameToLayer("Interactable");
                    col.tag = "Interactable";   // the game's detector only looks at colliders tagged so
                    col.transform.SetParent(inter.transform, false);
                    BoxCollider bc = col.AddComponent<BoxCollider>();
                    bc.center = d.InteractCenter;
                    bc.size = d.InteractSize;
                    col.AddComponent(d.InteractionType);
                }
                d.PostBuild?.Invoke(root);

                PartSettings s = ScriptableObject.CreateInstance<PartSettings>();
                s.name = d.AssetName;
                s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                s.id = d.Id;
                s.fullLabel = d.Label;
                s.description = d.Description;
                s.internalStateClass = d.StateClass;
                s.snappingStyle = d.Snap;
                s.localizedDescription = new LocalizedString();
                s.part = root;
                s.mass = d.Mass;
                s.size = d.Size;
                s.thumbnailRotation = Quaternion.identity;
                List<ItemInstance> cost = new List<ItemInstance>();
                ItemSettings[] items = Resources.FindObjectsOfTypeAll<ItemSettings>();
                foreach (var kv in d.Cost)
                {
                    ItemSettings it = items.FirstOrDefault(i => i.id == kv.Key);
                    if (it != null) cost.Add(new ItemInstance(it, kv.Value));
                }
                _costField.SetValue(s, cost);
                d.Settings = s;
                d.Prefab = root;
                Log.Msg("registered part " + d.Id + " '" + d.Label + "' (" + (d.StateClass ?? "stateless") + ")");
                return s;
            }
            catch (Exception e)
            {
                Log.Error("could not build part " + d.Label + ": " + e);
                return null;
            }
        }

        // ---- build menu ------------------------------------------------------

        /// <summary>
        /// Parts with a MenuAfter go into the build menu beside that vanilla
        /// part, and are unlocked by whatever research unlocks it (the menu
        /// only lists researched parts; the host does not check research when
        /// building, so this is all a part needs).
        /// </summary>
        private static void MenuPrefix(UI.BuildMenu.BuildMenu __instance)
        {
            try { PlaceAll(); }
            catch (Exception e) { Log.Once("partkit-menu", "PartKit build menu: " + e.Message); }
        }

        // ---- research and the build menu -------------------------------------------------

        private static int _placeScene = -1;
        private static float _placeUntil, _placeNext;
        private static Research.Interface.Values.ResearchSettingsList[] _lists;
        private static UI.BuildMenu.BuildMenu[] _menus;

        /// <summary>
        /// Call every frame from the mod's OnUpdate. For the first 75 seconds in a world it
        /// keeps each part in exactly one research node (the first of PartDef.Research that
        /// exists) and in exactly one place in the build menu (right after MenuAfter). Other
        /// mods may shuffle parts about while a world loads - Demo Unlock All sweeps parts no
        /// research unlocks into its "Spare Parts" node and its "Extra" tab, and makes nodes
        /// such as "Spaceship Necessities" a moment after the world appears - so this looks
        /// again every second rather than once, for longer than Unlock All goes on looking.
        ///
        /// A dedicated server has no local player, but it needs the same research tree as its
        /// players: the host is what decides whether a part is researched, and a node a player
        /// researches has to exist there. So with a world up on a server that has no client of
        /// its own, the research half runs too (a server has no build menu to place in).
        /// </summary>
        public static void Tick()
        {
            if (Defs.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _placeNext) return;
            _placeNext = now + 1f;
            Players.Services.PlayersClientTracker cp = Svc.Get<Players.Services.PlayersClientTracker>();
            bool server = (cp == null || cp.LocalPlayer == null) && IsDedicatedServerWorld();
            if (!server && (cp == null || cp.LocalPlayer == null)) return;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _placeScene) { _placeScene = scene; _placeUntil = now + 75f; _lists = null; _menus = null; }
            if (now > _placeUntil) return;
            try { if (server) PlaceResearch(); else PlaceAll(); }
            catch (Exception e) { Log.Once("partkit-place", "PartKit research and menu placement: " + e.Message); }
        }

        /// <summary>A world running on a server with no client of its own (a dedicated server).</summary>
        public static bool IsDedicatedServerWorld()
        {
            try
            {
                return FishNet.InstanceFinder.IsServerStarted && !FishNet.InstanceFinder.IsClientStarted
                    && Svc.Get<Ships.Services.ShipsServerTracker>() != null;
            }
            catch { return false; }
        }

        /// <summary>The research node a part belongs in (null: none found).</summary>
        public static Research.Interface.Values.ResearchSettings ResearchFor(PartDef d)
        {
            if (_lists == null || _lists.Length == 0) _lists = Resources.FindObjectsOfTypeAll<Research.Interface.Values.ResearchSettingsList>();
            if (d.Research != null)
                foreach (string name in d.Research)
                    foreach (var list in _lists)
                    {
                        if (list == null || list.list == null) continue;
                        var r = list.list.FirstOrDefault(x => x != null && x.techName == name);
                        if (r != null) return r;
                    }
            if (d.MenuAfter != 0)
                foreach (var list in _lists)
                {
                    if (list == null || list.list == null) continue;
                    var r = list.list.FirstOrDefault(x => x != null && x.unlockedParts != null && x.unlockedParts.Any(p => p != null && p.id == d.MenuAfter) && !x.techName.StartsWith("Spare Parts"));
                    if (r != null) return r;
                }
            return null;
        }

        public static void PlaceAll()
        {
            PlaceResearch();
            if (_menus == null || _menus.Length == 0 || _menus.Any(m => m == null)) _menus = Resources.FindObjectsOfTypeAll<UI.BuildMenu.BuildMenu>();
            foreach (var menu in _menus) PlaceInMenu(menu);
        }

        /// <summary>Each part into exactly one research node (the research half of PlaceAll).</summary>
        public static void PlaceResearch()
        {
            if (_lists == null || _lists.Length == 0) _lists = Resources.FindObjectsOfTypeAll<Research.Interface.Values.ResearchSettingsList>();
            foreach (PartDef d in Defs)
            {
                if (d.Settings == null) continue;
                var target = ResearchFor(d);
                if (target == null) continue;
                foreach (var list in _lists)
                {
                    if (list == null || list.list == null) continue;
                    foreach (var r in list.list)
                        if (r != null && r != target && r.unlockedParts != null && r.unlockedParts.Contains(d.Settings))
                            r.unlockedParts = r.unlockedParts.Where(p => p != d.Settings).ToArray();
                }
                if (target.unlockedParts == null) target.unlockedParts = new PartSettings[0];
                if (!target.unlockedParts.Contains(d.Settings)) target.unlockedParts = target.unlockedParts.Concat(new[] { d.Settings }).ToArray();
            }
        }

        private static void PlaceInMenu(UI.BuildMenu.BuildMenu menu)
        {
            UI.BuildMenu.BuildMenuTabConfig[] tabs = menu != null ? Refl.Get(menu, "tabsConfig") as UI.BuildMenu.BuildMenuTabConfig[] : null;
            if (tabs == null) return;
            foreach (PartDef d in Defs)
            {
                if (d.MenuAfter == 0 && d.MenuTab != null && d.Settings != null)
                {
                    var tab = tabs.FirstOrDefault(t => t != null && t.name == d.MenuTab);
                    if (tab == null)
                    {
                        // the tab is not there yet (Demo Unlock All makes one of the same name only
                        // when it has parts of its own for it): make it, after the game's own tabs
                        tab = new UI.BuildMenu.BuildMenuTabConfig { name = d.MenuTab, rows = new UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow[0] };
                        tab.icon = tabs.Length > 0 && tabs[tabs.Length - 1] != null ? tabs[tabs.Length - 1].icon : null;
                        tabs = tabs.Concat(new[] { tab }).ToArray();
                        Refl.Set(menu, "tabsConfig", tabs);
                    }
                    if (tab.rows == null) tab.rows = new UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow[0];
                    // out of any other tab; into this one (eight to a row, like Unlock All's)
                    foreach (var t in tabs)
                        if (t != tab && t != null && t.rows != null)
                            foreach (var row in t.rows) if (row.parts.Contains(d.Settings)) row.parts = row.parts.Where(p => p != d.Settings).ToArray();
                    if (!tab.rows.Any(r => r.parts.Contains(d.Settings)))
                    {
                        var last = tab.rows.Length > 0 ? tab.rows[tab.rows.Length - 1] : null;
                        if (last == null || last.parts.Length >= 8)
                        {
                            last = new UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow { parts = new PartSettings[0] };
                            tab.rows = tab.rows.Concat(new[] { last }).ToArray();
                        }
                        last.parts = last.parts.Concat(new[] { d.Settings }).ToArray();
                    }
                    continue;
                }
                if (d.MenuAfter == 0 || d.Settings == null) continue;
                UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow home = null;
                foreach (var t in tabs)
                    foreach (var row in t.rows)
                        if (home == null && row.parts.Any(p => p != null && p.id == d.MenuAfter && p != d.Settings)) home = row;
                if (home == null) { Log.Once("menu-" + d.Id, "no build menu row has part " + d.MenuAfter + " - " + d.Label + " is not listed."); continue; }
                foreach (var t in tabs)
                    foreach (var row in t.rows)
                        if (row != home && row.parts.Contains(d.Settings)) row.parts = row.parts.Where(p => p != d.Settings).ToArray();
                if (!home.parts.Contains(d.Settings))
                {
                    List<PartSettings> l = new List<PartSettings>(home.parts);
                    l.Insert(l.FindIndex(p => p != null && p.id == d.MenuAfter) + 1, d.Settings);
                    home.parts = l.ToArray();
                }
            }
        }

        /// <summary>Has this world researched what unlocks the part? (True when research can't be read.)</summary>
        public static bool IsUnlocked(PartSettings s)
        {
            try
            {
                Research.Interface.Services.IResearchProvider rp = Svc.Get<Research.Interface.Services.IResearchProvider>();
                return rp == null || s == null || rp.IsPartUnlocked(s);
            }
            catch { return true; }
        }

        // ---- interaction labels ---------------------------------------------

        private static readonly Dictionary<LocalizedString, string> _labels = new Dictionary<LocalizedString, string>();

        /// <summary>
        /// A LocalizedString that shows literal text in the interaction tips.
        /// The tip normally resolves it through a string table; the postfix
        /// below spots ours, switches that off and writes the text instead.
        /// </summary>
        public static LocalizedString Label(string text)
        {
            foreach (var kv in _labels) if (kv.Value == text) return kv.Key;
            LocalizedString ls = new LocalizedString();
            _labels[ls] = text;
            return ls;
        }

        private static void TooltipPostfix(object __instance, LocalizedString localizedString)
        {
            string text;
            if (localizedString == null || !_labels.TryGetValue(localizedString, out text)) return;
            try
            {
                object canvas = Refl.Get(__instance, "_instantiatedCanvas");
                Transform wrapper = Refl.Get(canvas, "tipsWrapper") as Transform;
                if (wrapper == null || wrapper.childCount == 0) return;
                Transform tip = wrapper.GetChild(wrapper.childCount - 1);
                LocalizeStringEvent ev = tip.GetComponentInChildren<LocalizeStringEvent>();
                if (ev != null) ev.enabled = false;
                TMP_Text t = ev != null ? ev.GetComponent<TMP_Text>() : tip.GetComponentInChildren<TMP_Text>();
                if (t != null) t.text = text;
            }
            catch (Exception e) { Log.Once("tooltip", "tooltip label: " + e.Message); }
        }

        public static PartInteractionConfig Interact(string label)
        {
            return new PartInteractionConfig { type = InteractionType.Interact, localizedLabel = Label(label) };
        }

        private static PartInteractionConfig? _pickUp;

        /// <summary>The game's own "pick up" (left click), label and all, copied from a vanilla part that has one.</summary>
        public static PartInteractionConfig PickUp()
        {
            if (_pickUp.HasValue) return _pickUp.Value;
            foreach (PartSettings ps in Resources.FindObjectsOfTypeAll<PartSettings>())
            {
                if (ps == null || ps.part == null) continue;
                foreach (PartInteraction pi in ps.part.GetComponentsInChildren<PartInteraction>(true))
                {
                    // only the game's own: another mod's part asks its own copy of this, which asks ours...
                    if (pi.GetType().Assembly != typeof(PartInteraction).Assembly || pi.Interactions == null) continue;
                    foreach (PartInteractionConfig c in pi.Interactions)
                        if (c.type == InteractionType.PickUp) { _pickUp = c; return c; }
                }
            }
            return new PartInteractionConfig { type = InteractionType.PickUp, localizedLabel = Label("Pick up") };
        }

        // ---- text ------------------------------------------------------------

        private static TMP_FontAsset _font;

        /// <summary>The game's own title font (falls back to any TMP font).</summary>
        public static TMP_FontAsset Font()
        {
            if (_font != null) return _font;
            TMP_FontAsset[] all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            _font = all.FirstOrDefault(f => f.name.Contains("Anta")) ?? all.FirstOrDefault(f => f.name.Contains("Mulish")) ?? all.FirstOrDefault();
            return _font;
        }

        /// <summary>World-space text, depth tested like any other surface.</summary>
        public static TextMeshPro Text(Transform parent, string name, Vector3 localPos, Quaternion localRot, float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            TextMeshPro t = go.AddComponent<TextMeshPro>();
            TMP_FontAsset f = Font();
            if (f != null) t.font = f;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = true;
            t.text = "";
            return t;
        }
    }

    /// <summary>
    /// Base for our parts' interaction colliders. The game's PartInteraction
    /// keeps its settings in private serialized fields, which Unity will not
    /// copy for a component type from a mod assembly - so everything here is
    /// an override instead.
    /// </summary>
    public abstract class ModPartInteraction : PartInteraction
    {
        public PartContext Context { get { return PartContext; } }
        public override bool IsActive { get { return true; } }
        public override float DetectionDistance { get { return 2f; } }
        public abstract override IReadOnlyCollection<PartInteractionConfig> Interactions { get; }
        public abstract override bool Interact(InteractionType type);

        /// <summary>The game's own pick-up request (what left click on a vanilla part sends).</summary>
        protected bool PickUpThis()
        {
            try
            {
                var svc = Svc.Get<Managers.Interface.Services.IShipInteractionNetworkService>();
                svc?.PickUpPart(new Managers.Interface.Model.PartInteractNetworkRequest { ShipId = PartContext.ShipId, PartId = PartContext.PartId });
                return svc != null;
            }
            catch (Exception e) { Log.Warn("pick up: " + e.Message); return false; }
        }

        /// <summary>The same request the game's own interaction sends.</summary>
        protected void StartVanillaInteraction()
        {
            try
            {
                var svc = Svc.Get<Managers.Interface.Services.IShipInteractionNetworkService>();
                svc?.StartInteractWithPart(new Managers.Interface.Model.PartInteractNetworkRequest { ShipId = PartContext.ShipId, PartId = PartContext.PartId });
            }
            catch (Exception e) { Log.Warn("interact: " + e.Message); }
        }
    }
}
