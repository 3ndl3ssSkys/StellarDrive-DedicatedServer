// Menu highlights that stick (players' games, main menu and pause menu).
//
// The game's menu buttons (MenuButtonElement, and ButtonBase for world and part buttons) light up on
// a "pointer entered" event and go dark on "pointer left". When a menu closes or changes under the
// mouse the second event never comes, so the button stays lit the next time it is shown. The menu
// also only updates which button is selected on the buttons visible at that moment, so a hidden one
// keeps an old "selected" look, and clicking empty space never closes an open sub-menu.
//
// So, a few times a second, a lit button the mouse isn't over is put back to normal; when the menu
// updates its selection, every button gets it (visible or not); and a click on nothing closes the
// open sub-menus. A button whose image isn't set up yet is left alone instead of throwing (which
// left it drawn wrong).
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UI.Common;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DedicatedServer
{
    internal static class MenuFix
    {
        private static float _next;
        public static int Unstuck, ClosedByClickOff;

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(MenuNavigation), "UpdateList"),
                    postfix: new HarmonyMethod(typeof(MenuFix).GetMethod("UpdateListPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(MenuButtonElement), "UpdateSelectionColor"),
                    prefix: new HarmonyMethod(typeof(MenuFix).GetMethod("ColorPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("menu highlight fix: " + e.Message); }
        }

        // every button gets the menu's selection, hidden ones too
        private static void UpdateListPostfix(MenuNavigation __instance)
        {
            try
            {
                Traverse t = Traverse.Create(__instance);
                MenuButtonElement selected = t.Field("_currentMainMenuSelectedButton").GetValue<MenuButtonElement>();
                MenuButtonElement[] all = t.Field("_mainButtons").GetValue<MenuButtonElement[]>();
                if (all == null) return;
                foreach (MenuButtonElement b in all)
                {
                    if (b == null || b.isActiveAndEnabled) continue;
                    Traverse.Create(b).Field("_isSelected").SetValue(b == selected);
                    Traverse.Create(b).Field("_isHovered").SetValue(false);
                }
            }
            catch { }
        }

        private static bool ColorPrefix(MenuButtonElement __instance)
        {
            Traverse t = Traverse.Create(__instance);
            if (t.Field("_image").GetValue<UnityEngine.UI.Image>() != null) return true;
            UnityEngine.UI.Image img = __instance.GetComponent<UnityEngine.UI.Image>();
            if (img == null) return false;
            t.Field("_image").SetValue(img);
            return true;
        }

        /// <summary>Every frame on a player's game.</summary>
        public static void Update()
        {
            ClickOff();
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.15f;
            Vector2 mouse;
            try { UnityEngine.InputSystem.Mouse m = UnityEngine.InputSystem.Mouse.current; if (m == null) return; mouse = m.position.ReadValue(); }
            catch { return; }
            foreach (MenuButtonElement b in UnityEngine.Object.FindObjectsByType<MenuButtonElement>(FindObjectsSortMode.None))
            {
                Traverse t = Traverse.Create(b);
                if (!t.Field("_isHovered").GetValue<bool>() || Over(b.transform as RectTransform, mouse)) continue;
                t.Field("_isHovered").SetValue(false);
                t.Method("UpdateSelectionColor").GetValue();
                Unstuck++;
            }
            foreach (ButtonBase b in UnityEngine.Object.FindObjectsByType<ButtonBase>(FindObjectsSortMode.None))
            {
                Traverse t = Traverse.Create(b);
                if (!t.Field("_isHovered").GetValue<bool>() || Over(b.transform as RectTransform, mouse)) continue;
                t.Field("_isHovered").SetValue(false);
                t.Method("UpdateSelectionColors").GetValue();
                Unstuck++;
            }
        }

        private static bool Over(RectTransform r, Vector2 mouse)
        {
            if (r == null) return false;
            Canvas c = r.GetComponentInParent<Canvas>();
            Camera cam = c != null && c.renderMode != RenderMode.ScreenSpaceOverlay ? c.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(r, mouse, cam);
        }

        // a click on nothing closes the open sub-menus
        private static void ClickOff()
        {
            bool clicked;
            try { UnityEngine.InputSystem.Mouse m = UnityEngine.InputSystem.Mouse.current; clicked = m != null && m.leftButton.wasPressedThisFrame; }
            catch { return; }
            if (!clicked || EventSystem.current == null || EventSystem.current.IsPointerOverGameObject()) return;
            CloseMenus();
        }

        public static void CloseMenus()
        {
            foreach (MenuNavigation n in UnityEngine.Object.FindObjectsByType<MenuNavigation>(FindObjectsSortMode.None))
            {
                Traverse t = Traverse.Create(n);
                if (t.Field("_currentMainMenuSelectedButton").GetValue() == null && t.Field("_currentMenu").GetValue() == null) continue;
                t.Method("CloseSelectorAMenu").GetValue();
                t.Method("CloseSelectorBMenu").GetValue();
                t.Method("CloseCurrentMenu").GetValue();
                t.Field("_currentMainMenuSelectedButton").SetValue(null);
                t.Field("_currentSelectorASelectedButton").SetValue(null);
                t.Field("_currentSelectorBSelectedButton").SetValue(null);
                t.Method("UpdateList").GetValue();
                ClosedByClickOff++;
            }
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }
    }
}
