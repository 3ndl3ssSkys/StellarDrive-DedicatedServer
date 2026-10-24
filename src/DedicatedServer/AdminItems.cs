// Items this mod adds to the game, on the server and in every player's game (the ids must agree).
//
//   Indestructible Build Tool (id 39800): works exactly like the build tool. What is built with it is
//   protected (Protect.cs): nothing can break it and nobody can change it without this tool, and it
//   stays where it is. Admins hand it out with /give.
//   Indestructible Recycler Tool (id 39801): works exactly like the recycler, and also takes apart
//   protected builds (nothing else can). Admins hand it out with /give.
//
// An item is added by cloning the build tool's own item settings into the game's item list.
// Keep in mind: a world whose players carry this item needs this mod to load their inventories.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Items.Model;
using UnityEngine;

namespace DedicatedServer
{
    internal static class AdminItems
    {
        public const uint ProtectToolId = 39800, RecyclerToolId = 39801;
        public const string ProtectToolName = "Indestructible Build Tool", RecyclerToolName = "Indestructible Recycler Tool";
        private static ItemSettings _tool, _recycler;

        public static bool IsProtectTool(uint itemId) { return itemId == ProtectToolId; }
        public static bool IsRecyclerTool(uint itemId) { return itemId == RecyclerToolId; }

        /// <summary>Players' games: the toolbar's picture of the tool (rendered from the build tool's model) in gold.</summary>
        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Items.Services.ItemThumbnailTextureCache), "GetThumbnail"),
                    postfix: new HarmonyMethod(typeof(AdminItems).GetMethod("ThumbPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Debug("ui", "tool picture: " + e.Message); }
            try
            {
                // the tool in hand (and in other players' hands) in gold too
                h.Patch(AccessTools.Method(typeof(Players.Services.LocalSelectedToolTracker), "UnsheatheTool"),
                    postfix: new HarmonyMethod(typeof(AdminItems).GetMethod("UnsheathePostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(Players.Visuals.OtherPlayerToolVisual), "OnItemSelectionChange"),
                    postfix: new HarmonyMethod(typeof(AdminItems).GetMethod("OtherToolPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Debug("ui", "gold tools: " + e.Message); }
        }

        private static readonly HashSet<ToolSettings> _goldTools = new HashSet<ToolSettings>();
        public static int Gilded;
        private static readonly Color Gold = new Color(1f, 0.74f, 0.24f, 1f);

        private static void UnsheathePostfix(object __instance, ToolSettings tool)
        {
            if (tool == null || !_goldTools.Contains(tool)) return;
            Component c = Traverse.Create(__instance).Field("_currentTool").GetValue() as Component;
            if (c != null) Gild(c.gameObject);
        }

        private static void OtherToolPostfix(object __instance, Players.Interface.Model.ItemSelection newSelection)
        {
            if (!newSelection.HasItemSelected || (newSelection.ItemId != ProtectToolId && newSelection.ItemId != RecyclerToolId)) return;
            Component c = Traverse.Create(__instance).Field("_currentTool").GetValue() as Component;
            if (c != null) Gild(c.gameObject);
        }

        /// <summary>Every surface of a tool model in polished gold (its own copies of the materials).</summary>
        public static void Gild(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;     // beams and sparks keep their colours
                foreach (Material m in r.materials)
                {
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Gold);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", Gold);
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.9f);
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.75f);
                }
            }
            Gilded++;
        }

        private static readonly HashSet<Texture2D> _tinted = new HashSet<Texture2D>();

        private static void ThumbPostfix(uint itemId, Texture2D __result)
        {
            if ((itemId != ProtectToolId && itemId != RecyclerToolId) || __result == null || _tinted.Contains(__result)) return;
            _tinted.Add(__result);
            MelonLoader.MelonCoroutines.Start(TintLater(__result));
        }

        private static System.Collections.IEnumerator TintLater(Texture2D tex)
        {
            for (int i = 0; i < 4; i++) yield return null;           // the picture is rendered into it after a frame or two
            Texture2D gold = Tinted(tex);
            if (gold == null || gold.width != tex.width || gold.height != tex.height) yield break;
            try { tex.SetPixels32(gold.GetPixels32()); tex.Apply(false, false); } catch (Exception e) { Log.Debug("ui", "tool picture: " + e.Message); }
            UnityEngine.Object.Destroy(gold);
        }

        /// <summary>Adds the items to every item list the game has loaded (once per list).</summary>
        public static void Register()
        {
            try
            {
                foreach (ItemSettingsList list in Resources.FindObjectsOfTypeAll<ItemSettingsList>())
                {
                    Traverse t = Traverse.Create(list).Field("items");
                    ItemSettings[] items = t.GetValue<ItemSettings[]>();
                    if (items == null) continue;
                    List<ItemSettings> l = items.ToList();
                    bool added = false;
                    if (!items.Any(i => i != null && i.id == ProtectToolId))
                    {
                        if (_tool == null) _tool = Copy(items, "Build", "IndestructibleBuildTool", ProtectToolId, ProtectToolName);
                        if (_tool != null) { l.Add(_tool); added = true; }
                    }
                    if (!items.Any(i => i != null && i.id == RecyclerToolId))
                    {
                        if (_recycler == null) _recycler = Copy(items, "Recycl", "IndestructibleRecyclerTool", RecyclerToolId, RecyclerToolName);
                        if (_recycler != null) { l.Add(_recycler); added = true; }
                    }
                    if (!added) continue;
                    t.SetValue(l.ToArray());
                    Traverse.Create(list).Field("_dict").SetValue(null);         // rebuilt with the new items on next use
                }
            }
            catch (Exception e) { Log.Once("admin-items-err", "admin items: " + e.Message); }
        }

        /// <summary>A gold copy of one of the game's tools (the first whose name has `like` in it), as a new item.</summary>
        private static ItemSettings Copy(ItemSettings[] items, string like, string name, uint id, string itemName)
        {
            ItemSettings src = items.FirstOrDefault(i => i != null && i.toolSettings != null && (i.itemName ?? i.name).IndexOf(like, StringComparison.OrdinalIgnoreCase) >= 0);
            if (src == null) { Log.Once("admin-items-" + id, "admin items: no tool like '" + like + "' in the game's items; the " + itemName + " isn't available"); return null; }
            ItemSettings copy = UnityEngine.Object.Instantiate(src);
            copy.name = name;
            copy.id = id;
            copy.itemName = itemName;
            copy.maxStackSize = 1;
            if (!DedicatedServerMod.IsServer)
            {
                copy.icon = Tinted(src.icon) ?? src.icon;
                if (src.toolSettings != null)
                {
                    // its own tool settings (the same model): how the tool in hand is known to be this one, to gild it
                    copy.toolSettings = UnityEngine.Object.Instantiate(src.toolSettings);
                    copy.toolSettings.name = name + "Tool";
                    UnityEngine.Object.DontDestroyOnLoad(copy.toolSettings);
                    _goldTools.Add(copy.toolSettings);
                }
            }
            UnityEngine.Object.DontDestroyOnLoad(copy);
            Log.Debug("ui", "added " + itemName + " (item " + id + ", a copy of " + (src.itemName ?? src.name) + ")");
            return copy;
        }

        /// <summary>The tool's icon in gold, so the copies are easy to tell from the game's own.</summary>
        private static Texture2D Tinted(Texture2D src)
        {
            if (src == null) return null;
            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture keep = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                RenderTexture.active = keep;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] px = t.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    float l = (px[i].r + px[i].g + px[i].b) / (3f * 255f);
                    px[i] = new Color32((byte)Mathf.Clamp(255 * (0.35f + l * 0.75f), 0, 255), (byte)Mathf.Clamp(255 * (0.25f + l * 0.6f), 0, 255), (byte)Mathf.Clamp(255 * (l * 0.25f), 0, 255), px[i].a);
                }
                t.SetPixels32(px);
                t.Apply(false, false);
                t.name = "IndestructibleBuildToolIcon";
                return t;
            }
            catch { return null; }
        }

        // ---- finding items by name (for /give) ------------------------------------------------------

        public class ItemRef { public uint Id; public string Name; public byte Stack; }

        public static List<ItemRef> All()
        {
            Register();
            Dictionary<uint, ItemRef> d = new Dictionary<uint, ItemRef>();
            foreach (ItemSettingsList list in Resources.FindObjectsOfTypeAll<ItemSettingsList>())
            {
                ItemSettings[] items = Traverse.Create(list).Field("items").GetValue<ItemSettings[]>();
                if (items == null) continue;
                foreach (ItemSettings i in items)
                    if (i != null && !d.ContainsKey(i.id)) d[i.id] = new ItemRef { Id = i.id, Name = string.IsNullOrEmpty(i.itemName) ? i.name : i.itemName, Stack = Math.Max((byte)1, i.maxStackSize) };
            }
            return d.Values.OrderBy(x => x.Name).ToList();
        }

        private static string Squash(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        /// <summary>The item a name means: exactly, then by its start, then anywhere in it. Null with the candidates if unclear.</summary>
        public static ItemRef Find(string name, out List<ItemRef> candidates)
        {
            List<ItemRef> all = All();
            string w = Squash(name);
            uint id;
            candidates = new List<ItemRef>();
            if (uint.TryParse(name, out id)) { ItemRef byId = all.FirstOrDefault(i => i.Id == id); if (byId != null) return byId; }
            if (w.Length == 0) return null;
            ItemRef exact = all.FirstOrDefault(i => Squash(i.Name) == w);
            if (exact != null) return exact;
            candidates = all.Where(i => Squash(i.Name).StartsWith(w)).ToList();
            if (candidates.Count == 0) candidates = all.Where(i => Squash(i.Name).Contains(w)).ToList();
            return candidates.Count >= 1 ? candidates.OrderBy(i => i.Name.Length).First() : null;
        }
    }
}
