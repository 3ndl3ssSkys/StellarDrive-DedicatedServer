// The game's TextMeshPro fonts, made usable by name in chat (<font="...">, from a style's font=).
// TextMeshPro finds a font named in a tag through its MaterialReferenceManager (or Resources, which
// this game doesn't use), so every font the game has loaded is registered there.
using System;
using TMPro;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Fonts
    {
        public static int Registered;

        public static void RegisterAll()
        {
            try
            {
                foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    TMP_FontAsset have;
                    if (MaterialReferenceManager.TryGetFontAsset(f.hashCode, out have)) continue;
                    try { MaterialReferenceManager.AddFontAsset(f); Registered++; } catch { }
                }
                Log.Debug("ui", "fonts usable in chat: " + Registered);
            }
            catch (Exception e) { Log.Once("fonts", "chat fonts: " + e.Message); }
        }
    }
}
