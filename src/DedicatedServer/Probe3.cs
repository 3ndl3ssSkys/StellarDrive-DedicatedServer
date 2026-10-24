// Probes for round 4: staying above the ground (at spawn, after joining, far from spawn) and
// admin teleports. The client measures its own height above its own terrain; the server measures
// the same player on its side (`ground`), and logs every time anything is under the surface.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Core.Values;
using Planets.Services;
using Players;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Services;

namespace DedicatedServer
{
    internal static partial class Probe
    {
        private static IEnumerator Run3(string probe, Action<string, bool, string> check, Action<string> note)
        {
            switch (probe)
            {
                case "ground": return GroundProbe(check, note);
                case "groundrun": return GroundRunProbe(check, note);
                case "shipground": return ShipGroundProbe(check, note);
                case "savedservers": return SavedServersProbe(check, note);
                case "chatstyle": return ChatStyleProbe(check, note);
                case "servericon": return ServerIconProbe(check, note);
                case "moddownload": return ModDownloadProbe(check, note);
                default: return Run4(probe, check, note);
            }
        }

        /// <summary>This client's height above its own terrain (NaN when not on a planet's surface).</summary>
        private static float ClientHeight(out uint space)
        {
            TrackedPlayerLocalClient me = Me();
            space = me != null ? me.SpaceId : 0;
            if (me == null) return float.NaN;
            ISpacesClientProvider sp = Core.Services.ServiceLocator.GetService<ISpacesClientProvider>();
            PlanetsClientTracker pc = Core.Services.ServiceLocator.GetService<PlanetsClientTracker>();
            PlanetSurfaceSpace s = sp != null ? sp.GetSpace(me.SpaceId) as PlanetSurfaceSpace : null;
            if (s == null || pc == null) return float.NaN;
            Vector3Double pp = s.Center + (Vector3Double)me.Position;
            double r = pp.Magnitude;
            return (float)(r - pc.GetTerrainPointsProvider(s.PlanetId).GetElevationAtPoint((Vector3)(pp / r)));
        }

        /// <summary>Height above this client's terrain of a point in one of its spaces (NaN off a planet's surface).</summary>
        private static float ClientHeightAt(uint spaceId, Vector3 local)
        {
            ISpacesClientProvider sp = Core.Services.ServiceLocator.GetService<ISpacesClientProvider>();
            PlanetsClientTracker pc = Core.Services.ServiceLocator.GetService<PlanetsClientTracker>();
            PlanetSurfaceSpace s = sp != null ? sp.GetSpace(spaceId) as PlanetSurfaceSpace : null;
            if (s == null || pc == null) return float.NaN;
            Vector3Double pp = s.Center + (Vector3Double)local;
            double r = pp.Magnitude;
            return (float)(r - pc.GetTerrainPointsProvider(s.PlanetId).GetElevationAtPoint((Vector3)(pp / r)));
        }

        /// <summary>
        /// Running across the line where the server moves a player into a new space (about 1 km from
        /// the old one's centre), then a ship sliding along the ground for a minute and a half, watched
        /// from this client: neither may ever be drawn in the ground.
        /// </summary>
        private static IEnumerator GroundRunProbe(Action<string, bool, string> check, Action<string> note)
        {
            TrackedPlayerLocalClient me = Me();
            if (me == null) { check("ground run", false, "not in a game"); yield break; }
            string id = MyState().SteamUserId.ToString();
            CheckWrap w = new CheckWrap();
            bool rescue = !(Environment.GetEnvironmentVariable("SDD_DS_GROUND_RESCUE") ?? "").Equals("off", StringComparison.OrdinalIgnoreCase);
            yield return ConsoleText("set GroundRescue " + (rescue ? "on" : "off"), "GroundRescue", w);

            // 1. a player running over the 1 km line
            yield return ConsoleText("tpforward " + id + " 930", "moved", w);
            yield return new WaitForSecondsRealtime(3f);
            uint space = Me().SpaceId;
            int changes = 0, below = 0, frames = 0;
            float min = float.MaxValue, minNearChange = float.MaxValue, lastChange = -99f;
            List<string> log = new List<string>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 45f)
            {
                float t = Time.realtimeSinceStartup - t0;
                Keys(UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.LeftShift);
                uint sp; float h = ClientHeight(out sp);
                if (sp != space) { changes++; lastChange = t; log.Add("space " + space + " -> " + sp + " at " + t.ToString("0.0") + " s, height " + h.ToString("0.00")); space = sp; }
                if (!float.IsNaN(h))
                {
                    frames++;
                    if (h < min) min = h;
                    if (h < -1f) below++;
                    if (t - lastChange < 2f && h < minNearChange) minNearChange = h;
                }
                yield return null;
            }
            Keys();
            note("running over the 1 km line: " + frames + " frames, lowest " + min.ToString("0.00") + " m, " + below + " frame(s) more than 1 m under; " + changes + " space change(s): " + string.Join("; ", log.ToArray())
                 + (changes > 0 ? "; lowest within 2 s of a change " + minNearChange.ToString("0.00") + " m" : ""));
            check("running over the line where the server re-centres a player's space: never in the ground", frames > 100 && below == 0, frames + " frames, lowest " + min.ToString("0.00") + " m, " + changes + " space change(s)");

            // 2. a ship sliding along the ground (a minute and a half: over a kilometre)
            yield return ConsoleText("testship ground 18", "test ship #", w);
            Match m = Regex.Match(w.Detail ?? "", "#(\\d+)");
            if (!m.Success) { check("a test ship slides along the ground", false, w.Detail); yield break; }
            uint shipId = uint.Parse(m.Groups[1].Value);
            Ships.Services.ShipsClientTracker ct = Core.Services.ServiceLocator.GetService<Ships.Services.ShipsClientTracker>();
            Ships.TrackedShipClient ship = null;
            float until = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < until && !(ct.TryGetTrackedShip(shipId, out ship) && ship.IsVisualLoaded)) yield return null;
            if (ship == null) { check("a test ship slides along the ground", false, "never reached this client"); yield break; }
            int sFrames = 0, sBelow = 0, sChanges = 0; float sMin = float.MaxValue, jumpMax = 0f; uint sSpace = ship.SpaceId;
            Vector3 lastPos = ship.VisualPhysicsState.Position; List<string> slog = new List<string>();
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 90f)
            {
                var vs = ship.VisualPhysicsState;
                float h = ClientHeightAt(vs.SpaceId, vs.Position);
                if (vs.SpaceId != sSpace) { sChanges++; slog.Add("space " + sSpace + " -> " + vs.SpaceId + " at " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s, height " + h.ToString("0.00")); sSpace = vs.SpaceId; lastPos = vs.Position; }
                else { float jump = Vector3.Distance(vs.Position, lastPos); if (jump > jumpMax) jumpMax = jump; lastPos = vs.Position; }
                if (!float.IsNaN(h)) { sFrames++; if (h < sMin) sMin = h; if (h < -2f) sBelow++; }
                yield return null;
            }
            yield return ConsoleText("testship stop", "stopped", w);
            note("ship sliding along the ground, as drawn here: " + sFrames + " frames, origin lowest " + sMin.ToString("0.00") + " m, " + sBelow + " frame(s) more than 2 m under; biggest jump in one frame " + jumpMax.ToString("0.00") + " m; " + sChanges + " space change(s): " + string.Join("; ", slog.ToArray()));
            yield return ConsoleText("ground", "lowest", w);
            foreach (string l in w.Detail.Split('\n')) if (l.Trim().Length > 0) note("server: " + l.Trim());
            Match g = Regex.Match(w.Detail, @"(\d+) time\(s\) a player was under the ground, (\d+) time\(s\) a ship");
            check("the ship is never drawn in the ground", sFrames > 100 && sBelow == 0, sFrames + " frames, lowest " + sMin.ToString("0.00") + " m");
            check("the server never had a player or ship under the ground", g.Success && g.Groups[1].Value == "0" && g.Groups[2].Value == "0", g.Success ? g.Value : w.Detail);
        }

        /// <summary>
        /// Ethan's two cases: players shuffling about on a ship parked on the ground (it sank), and a
        /// ship flown into the ground and thrust into it (it went through). SDD_DS_GROUND_RESCUE and
        /// SDD_DS_SHIP_SWEEP (on/off) set the server's two defences, to compare with and without.
        /// </summary>
        private static IEnumerator ShipGroundProbe(Action<string, bool, string> check, Action<string> note)
        {
            TrackedPlayerLocalClient me = Me();
            if (me == null) { check("ship ground", false, "not in a game"); yield break; }
            string id = MyState().SteamUserId.ToString();
            CheckWrap w = new CheckWrap();
            bool rescue = !(Environment.GetEnvironmentVariable("SDD_DS_GROUND_RESCUE") ?? "").Equals("off", StringComparison.OrdinalIgnoreCase);
            yield return ConsoleText("set GroundRescue " + (rescue ? "on" : "off"), "GroundRescue", w);
            note("server: GroundRescue " + (rescue ? "on" : "off"));

            string spot = Environment.GetEnvironmentVariable("SDD_DS_SPOT");      // "x y z": go there first (a place that went wrong before)
            if (!string.IsNullOrEmpty(spot))
            {
                yield return ConsoleText("tpat " + id + " " + spot, "moved", w);
                note("went to " + spot + ": " + w.Detail.Trim());
                yield return new WaitForSecondsRealtime(4f);
            }
            yield return ConsoleText("testship park 7", "test ship #", w);
            Match m = Regex.Match(w.Detail ?? "", "#(\\d+)");
            if (!m.Success) { check("a test ship is parked", false, w.Detail); yield break; }
            uint shipId = uint.Parse(m.Groups[1].Value);
            Ships.Services.ShipsClientTracker ct = Core.Services.ServiceLocator.GetService<Ships.Services.ShipsClientTracker>();
            Ships.TrackedShipClient ship = null;
            float until = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < until && !(ct.TryGetTrackedShip(shipId, out ship) && ship.IsVisualLoaded)) yield return null;
            if (ship == null) { check("a test ship is parked", false, "never reached this client"); yield break; }
            yield return new WaitForSecondsRealtime(5f);
            yield return ConsoleText("ground", "lowest", w);
            int ev0 = ShipEventsIn(w.Detail);

            // 1. on the ship: walking every way and jumping for 40 s
            yield return ConsoleText("tpship " + id + " " + shipId, "put ", w);
            yield return new WaitForSecondsRealtime(3f);
            float sMin = float.MaxValue, pMin = float.MaxValue; int frames = 0, onShip = 0;
            UnityEngine.InputSystem.Key[] dirs = { UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.D, UnityEngine.InputSystem.Key.S, UnityEngine.InputSystem.Key.A };
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 40f)
            {
                float t = Time.realtimeSinceStartup - t0;
                UnityEngine.InputSystem.Key k = dirs[((int)(t / 0.9f)) % 4];
                if ((t % 2.3f) < 0.12f) Keys(k, UnityEngine.InputSystem.Key.Space); else Keys(k);
                var vs = ship.VisualPhysicsState;
                float h = ClientHeightAt(vs.SpaceId, vs.Position);
                if (!float.IsNaN(h)) { frames++; if (h < sMin) sMin = h; }
                uint sp; float ph = ClientHeight(out sp);
                if (float.IsNaN(ph)) onShip++; else if (ph < pMin) pMin = ph;
                yield return null;
            }
            Keys();
            yield return ConsoleText("ground", "lowest", w);
            int ev1 = ShipEventsIn(w.Detail);
            note("40 s walking and jumping on the parked ship: ship origin lowest " + sMin.ToString("0.00") + " m above the ground here (" + frames + " frames); player on the ship " + onShip + " frames, off it lowest " + (pMin == float.MaxValue ? "-" : pMin.ToString("0.00")) + " m; server ship-in-ground events " + (ev1 - ev0));
            foreach (string l in w.Detail.Split('\n')) if (l.Contains("ground:") || l.Contains("last:")) note("server: " + l.Trim());
            check("players walking and jumping on a parked ship: it never sinks into the ground", ev1 - ev0 == 0 && sMin > -0.5f, (ev1 - ev0) + " server event(s), drawn lowest " + sMin.ToString("0.00") + " m");

            // 1b. a hole: the ground pieces under it vanish behind the server's back (what went wrong in a long session)
            yield return ConsoleText("testhole", "destroyed", w);
            note("hole: " + w.Detail.Trim());
            float hMin = float.MaxValue; t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 8f)
            {
                var vs = ship.VisualPhysicsState;
                float h = ClientHeightAt(vs.SpaceId, vs.Position);
                if (!float.IsNaN(h) && h < hMin) hMin = h;
                yield return null;
            }
            yield return ConsoleText("ground", "lowest", w);
            bool remade = Regex.Match(w.Detail, @"ground remade [1-9]").Success;
            note("after the hole: ship drawn lowest " + hMin.ToString("0.00") + " m; " + w.Detail.Split('\n').FirstOrDefault(l => l.Contains("ground:")));
            check("with the ground gone from under a ship, the server remakes it and the ship doesn't sink away", !rescue || (remade && hMin > -3f), "remade " + remade + ", ship drawn lowest " + hMin.ToString("0.00") + " m");

            // 2. off it, then the ship rammed into the ground and thrust into it
            yield return ConsoleText("tpforward " + id + " 0 -30", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testship ram 35", "ramming", w);
            float rMin = float.MaxValue; t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 7f)
            {
                var vs = ship.VisualPhysicsState;
                float h = ClientHeightAt(vs.SpaceId, vs.Position);
                if (!float.IsNaN(h) && h < rMin) rMin = h;
                yield return null;
            }
            yield return ConsoleText("ground", "lowest", w);
            int ev2 = ShipEventsIn(w.Detail);
            yield return ConsoleText("testship stop", "stopped", w);
            note("rammed at 35 m/s, then 3 s of thrust into the ground: drawn lowest " + rMin.ToString("0.00") + " m; server ship-in-ground events " + (ev2 - ev1));
            foreach (string l in w.Detail.Split('\n')) if (l.Contains("ground:") || l.Contains("last:")) note("server: " + l.Trim());
            check("a ship flown and thrust into the ground stays on top of it", rMin > -1.5f, "drawn lowest " + rMin.ToString("0.00") + " m; " + (ev2 - ev1) + " server event(s)");
        }

        // ---- the Join page: saved servers, the password popup, a ban ------------------------------

        private static IEnumerator WaitUntil(Func<bool> cond, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!cond() && Time.realtimeSinceStartup < until) yield return null;
        }

        private static IEnumerator SavedServersProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string id = Me() != null ? MyState().SteamUserId.ToString() : "";
            yield return Console("set Description A test server: building, flying and chess.", "Description", w);
            yield return Console("deop " + id, "", w);                // admins skip the password
            // this test game's list starts empty (servers saved by earlier tests would sit above ours)
            foreach (SavedServer o in SavedServers.All.ToList()) { o.Pinned = false; o.Pass = ""; o.Banned = false; }
            SavedServers.All.RemoveAll(o => !o.Pinned);
            SavedServers.Save();
            yield return Leave();
            yield return new WaitForSecondsRealtime(3f);
            yield return OpenJoinPage(note);
            yield return WaitUntil(() => ClientUi.PanelPresent(), 10f);

            // Save: pinned, asked, and shown at the top of the list
            check("the Join page has the Save button", ClientUi.PressSave(_address), "address box " + (ClientUi.Panel() != null));
            yield return WaitUntil(() => ClientUi.Rows.Any(r => r.Server.Address == SavedServers.Key(_address) && r.Server.State == "online"), 10f);
            ServerRow row = ClientUi.Rows.FirstOrDefault(r => r.Server.Address == SavedServers.Key(_address));
            SavedServer s = row != null ? row.Server : null;
            yield return new WaitForSecondsRealtime(0.5f);
            Shot("saved-servers"); yield return new WaitForSecondsRealtime(0.5f);
            check("a saved server is listed at the top of the list (under its heading, above any Steam friends)", row != null && row.transform.GetSiblingIndex() <= 1 && !row.transform.parent.GetComponentsInChildren<UI.MainMenu.FriendGameButton>(true).Any(f => f.transform.parent == row.transform.parent && f.transform.GetSiblingIndex() < row.transform.GetSiblingIndex()), row != null ? "row " + row.transform.GetSiblingIndex() + " of " + row.transform.parent.childCount : "no row");
            check("...with its name, description and players, from the server itself (through the game port)", s != null && s.State == "online" && s.Name.Length > 0 && s.Desc.StartsWith("A test server") && s.Players == 0 && s.Max > 0,
                s != null ? s.State + ": '" + s.Name + "' / '" + s.Desc + "' " + s.Players + "/" + s.Max + ", " + s.PingMs + " ms" : "none");
            TMPro.TMP_Text rowText = row != null ? row.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
            note("row text: " + (rowText != null ? rowText.text : "-"));

            // Refresh asks again
            bool asked = false;
            UnityEngine.UI.Button refresh = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(b => b.name == "DedicatedServer.Refresh");
            if (refresh != null) { refresh.onClick.Invoke(); asked = s.State == "asking"; }
            yield return WaitUntil(() => s.State == "online", 8f);
            check("Refresh asks the saved servers again", refresh != null && asked && s.State == "online", refresh == null ? "no Refresh button" : "asking " + asked + ", then " + s.State);

            // an address that answers nothing shows as offline
            SavedServers.Pin("127.0.0.1:7999");
            yield return WaitUntil(() => SavedServers.Get("127.0.0.1:7999", false).State == "offline", 10f);
            SavedServer dead = SavedServers.Get("127.0.0.1:7999", false);
            yield return new WaitForSecondsRealtime(0.3f);
            Shot("saved-servers-offline"); yield return new WaitForSecondsRealtime(0.5f);
            check("a saved server that doesn't answer shows as offline", dead.State == "offline", dead.State);

            // pointing at a row: Play and Settings over its icon at once; the x inside the box, at its right end
            yield return RowLooks(check, row, "1280x720");
            int sw = Screen.width, sh = Screen.height;
            foreach (Vector2Int res in new[] { new Vector2Int(1920, 1200), new Vector2Int(2560, 1080), new Vector2Int(1024, 768) })
            {
                Screen.SetResolution(res.x, res.y, FullScreenMode.Windowed);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return RowLooks(check, ClientUi.Rows.FirstOrDefault(r => r.Server == s), res.x + "x" + res.y);
            }
            Screen.SetResolution(sw, sh, FullScreenMode.Windowed);
            yield return new WaitForSecondsRealtime(1.5f);

            // Settings: change a saved server's address in its row
            ServerRow deadRow = ClientUi.Rows.FirstOrDefault(r => r.Server == dead);
            if (deadRow != null) deadRow.Press(RowButton.Kind.Settings);
            yield return null;
            check("Settings opens an address box in the row", deadRow != null && ClientUi.Editing == deadRow, ClientUi.Editing != null ? "editing" : "no editor");
            ClientUi.TypeInEditor("127.0.0.1:7998");
            yield return new WaitForSecondsRealtime(0.5f);
            Shot("saved-servers-edit"); yield return new WaitForSecondsRealtime(0.5f);
            string edited = ClientUi.SaveEdit();
            yield return new WaitForSecondsRealtime(0.5f);
            check("...Save puts the new address in place of the old one (same entry, asked again)", edited == null && dead.Address == "127.0.0.1:7998" && SavedServers.Get("127.0.0.1:7999", false) == null
                && ClientUi.Rows.Count(r => r.Server == dead) == 1 && ClientUi.Editing == null, (edited ?? "saved") + "; now " + dead.Address + ", state " + dead.State);
            SavedServers.Unpin(dead);

            // a password: asked for only when the server wants one, then kept
            yield return Console("set Password hunter22", "Password = (set)", w);
            s.Pass = ""; SavedServers.Typed = null; SavedServers.Save();
            int seen = Client.VerdictsSeen;
            yield return new WaitForSecondsRealtime(0.5f);
            row = ClientUi.Rows.FirstOrDefault(r => r.Server == s);
            if (row != null) row.Press(RowButton.Kind.Play);          // the row's Play button
            yield return WaitUntil(() => Client.VerdictsSeen > seen, 40f);
            yield return WaitUntil(() => ClientUi.PopupState == "password", 30f);
            yield return new WaitForSecondsRealtime(1f);
            Shot("password-popup"); yield return new WaitForSecondsRealtime(0.5f);
            check("joining a server with a password: the game asks for it", ClientUi.PopupState == "password", "popup " + ClientUi.PopupState + "; verdict " + (Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "-"));
            seen = Client.VerdictsSeen;
            ClientUi.TypePasswordInPopup("hunter22");
            yield return WaitUntil(() => Client.VerdictsSeen > seen, 40f);
            yield return WaitUntil(() => Me() != null, 60f);
            check("...typing it there joins", Me() != null && Client.LastVerdict.Value.Accepted, Client.LastVerdict.HasValue ? Client.LastVerdict.Value.Message : "-");
            check("...and it's kept for that server", SavedServers.Typed == null && SavedServers.Get(_address, false).Pass.Length > 0 && SavedServers.PasswordFor(_address) == "hunter22", SavedServers.Get(_address, false).Pass.Length > 0 ? "kept" : "not kept");
            yield return new WaitForSecondsRealtime(2f);

            // banned: thrown out, the kept password erased, the row says so
            id = MyState().SteamUserId.ToString();
            seen = Client.VerdictsSeen;
            yield return Console("ban " + id + " testing bans", "", w);
            yield return WaitUntil(() => Client.VerdictsSeen > seen, 20f);
            yield return WaitUntil(() => SceneManager.GetActiveScene().name != "MainGame" && !ClientStarted(), 30f);
            yield return new WaitForSecondsRealtime(3f);
            yield return OpenJoinPage(note);
            yield return WaitUntil(() => ClientUi.Rows.Count > 0, 10f);
            yield return new WaitForSecondsRealtime(1f);
            Shot("saved-servers-banned"); yield return new WaitForSecondsRealtime(0.5f);
            SavedServer after = SavedServers.Get(_address, false);
            check("a ban erases the kept password and marks the server banned", after != null && after.Banned && after.Pass.Length == 0, after != null ? "banned " + after.Banned + ", password " + (after.Pass.Length > 0 ? "kept" : "erased") : "no entry");

            // put things back and join again for the probes after this one
            yield return Console("unban " + id, "", w);
            yield return Console("set Password \"\"", "Password = (none)", w);
            after.Banned = false; SavedServers.Save();
            yield return Rejoin(w);
            check("unbanned, without a password: joins again", w.Ok, w.Detail);
        }

        /// <summary>Is `inner` inside `outer` on screen (to a pixel)?</summary>
        private static bool Inside(RectTransform inner, RectTransform outer)
        {
            Vector3[] a = new Vector3[4], b = new Vector3[4];
            inner.GetWorldCorners(a); outer.GetWorldCorners(b);
            return a[0].x >= b[0].x - 1f && a[0].y >= b[0].y - 1f && a[2].x <= b[2].x + 1f && a[2].y <= b[2].y + 1f;
        }

        private static Rect ScreenRect(RectTransform r)
        {
            Vector3[] c = new Vector3[4]; r.GetWorldCorners(c);
            return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
        }

        /// <summary>What a click at the middle of `r` would land on (the top thing the pointer hits).</summary>
        private static GameObject HitAt(RectTransform r)
        {
            EventSystem es = EventSystem.current;
            if (es == null) return null;
            Rect sr = ScreenRect(r);
            PointerEventData pe = new PointerEventData(es) { position = sr.center };
            List<RaycastResult> hits = new List<RaycastResult>();
            es.RaycastAll(pe, hits);
            return hits.Count > 0 ? hits[0].gameObject : null;
        }

        private static IEnumerator RowLooks(Action<string, bool, string> check, ServerRow row, string res)
        {
            if (row == null) { check(res + ": the saved server's row is there", false, "no row"); yield break; }
            yield return null;
            row.Hover(true);
            bool atOnce = row.HoverShown;
            yield return null;
            Rect icon = ScreenRect(row.Icon);
            check(res + ": pointing at a row shows Play and Settings over its icon at once", atOnce && row.HoverShown && Inside(row.PlayButton, row.Icon) && Inside(row.SettingsButton, row.Icon),
                "shown " + atOnce + ", play in icon " + Inside(row.PlayButton, row.Icon) + ", settings in icon " + Inside(row.SettingsButton, row.Icon));
            GameObject onPlay = HitAt(row.PlayButton), onGear = HitAt(row.SettingsButton), onX = HitAt(row.RemoveButton);
            check(res + ": ...each can be clicked (nothing on top of it), and so can the x", onPlay == row.PlayButton.gameObject && onGear == row.SettingsButton.gameObject && onX == row.RemoveButton.gameObject,
                "play hits " + (onPlay != null ? onPlay.name : "-") + ", settings " + (onGear != null ? onGear.name : "-") + ", x " + (onX != null ? onX.name : "-"));
            Rect box = ScreenRect(row.BoxRect), x = ScreenRect(row.RemoveButton);
            check(res + ": the x is inside the box, at its right end", Inside(row.RemoveButton, row.BoxRect) && box.xMax - x.xMax < box.height * 0.3f,
                "box " + box + ", x " + x);
            check(res + ": the icon is square, inside the box", Mathf.Abs(icon.width - icon.height) <= 1.5f && Inside(row.Icon, row.BoxRect), "icon " + icon);
            yield return new WaitForSecondsRealtime(0.3f);
            Shot("saved-servers-hover-" + res); yield return new WaitForSecondsRealtime(0.5f);
            row.Hover(false);
            yield return null;
            check(res + ": ...and they go when the pointer leaves", !row.HoverShown, "shown " + row.HoverShown);
        }

        // ---- chat styles, announcements, timed restarts ---------------------------------------------

        private static readonly List<string> _chatSeen = new List<string>();
        private static bool _chatHooked;

        private static void ChatSeenPrefix(string systemMessage) { lock (_chatSeen) _chatSeen.Add("system|" + systemMessage); }
        private static void ChatPlayerSeenPostfix(UI.Chat.ChatMessageObjectUI __instance)
        {
            try { lock (_chatSeen) _chatSeen.Add("player|" + HarmonyLib.Traverse.Create(__instance).Field("text").GetValue<TMPro.TMP_Text>().text); } catch { }
        }

        private static IEnumerator SeeChat(Func<string, bool> match, float seconds, CheckWrap w)
        {
            float until = Time.realtimeSinceStartup + seconds;
            w.Ok = false; w.Detail = "";
            while (Time.realtimeSinceStartup < until)
            {
                lock (_chatSeen) { string hit = _chatSeen.FirstOrDefault(match); if (hit != null) { w.Ok = true; w.Detail = hit; yield break; } }
                yield return null;
            }
            lock (_chatSeen) w.Detail = "seen: " + string.Join(" || ", _chatSeen.Skip(System.Math.Max(0, _chatSeen.Count - 4)).ToArray());
        }

        private static IEnumerator ChatStyleProbe(Action<string, bool, string> check, Action<string> note)
        {
            if (!_chatHooked)
            {
                _chatHooked = true;
                HarmonyLib.Harmony h = new HarmonyLib.Harmony("DedicatedServer.probe.chat");
                h.Patch(HarmonyLib.AccessTools.Method(typeof(UI.Chat.ChatMenu), "ReceiveSystemMessage"), prefix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("ChatSeenPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(HarmonyLib.AccessTools.Method(typeof(UI.Chat.ChatMessageObjectUI), "InitializeChatMessage"), postfix: new HarmonyLib.HarmonyMethod(typeof(Probe).GetMethod("ChatPlayerSeenPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            CheckWrap w = new CheckWrap();
            lock (_chatSeen) _chatSeen.Clear();
            yield return ConsoleText("say Hello from the console", "said", w);
            yield return SeeChat(m => m.Contains("Hello from the console"), 8f, w);
            check("say: in the announce style (blue, bold, bigger, [Server])", w.Ok && w.Detail.Contains("<color=#4FC3F7>") && w.Detail.Contains("<b>") && w.Detail.Contains("<size=115%>") && w.Detail.Contains("[Server] Hello"), w.Detail);
            yield return ConsoleText("say {color=red italic size=130} Red and slanted", "said", w);
            yield return SeeChat(m => m.Contains("Red and slanted"), 8f, w);
            check("say {style}: a message's own colour, italic and size", w.Ok && w.Detail.Contains("<color=#FF6B5E>") && w.Detail.Contains("<i>") && w.Detail.Contains("<size=130%>"), w.Detail);
            yield return ConsoleText("alert Something important", "alerted", w);
            yield return SeeChat(m => m.Contains("Something important"), 8f, w);
            check("alert: in the alert style (orange, bold)", w.Ok && w.Detail.Contains("<color=#FFB84D>") && w.Detail.Contains("<b>"), w.Detail);
            yield return ConsoleText("restart 5 maintenance", "restart in", w);
            yield return SeeChat(m => m.Contains("restart in 5 minutes: maintenance"), 8f, w);
            check("restart 5: everyone is warned, with the reason", w.Ok, w.Detail);
            yield return ConsoleText("restart cancel", "Called off", w);
            yield return SeeChat(m => m.Contains("called off"), 8f, w);
            check("restart cancel: everyone hears it's off", w.Ok, w.Detail);
            yield return ConsoleText("set StyleServer color=green bold", "StyleServer", w);
            Core.Services.ServiceLocator.GetService<Managers.Server.Services.ChatMessageBroadcaster>().ClientSendChatMessage("/whoami");
            yield return SeeChat(m => m.Contains("<color=#9BE7A0><b>") && m.Contains("operator"), 8f, w);
            check("a changed style (StyleServer) applies at once, to command replies", w.Ok, w.Detail);
            yield return ConsoleText("set StyleServer color=#8FD3FF", "StyleServer", w);
            // a player's own chat: tags shown as typed; their name in the name style
            Core.Services.ServiceLocator.GetService<Managers.Server.Services.ChatMessageBroadcaster>().ClientSendChatMessage("<color=#4FC3F7><b>[Server] I am the server</b></color>");
            yield return SeeChat(m => m.StartsWith("player|") && m.Contains("I am the server"), 8f, w);
            check("a player can't pass for the server: their tags are shown as typed", w.Ok && w.Detail.Contains("<noparse>"), w.Detail);
            check("...and their name is in the server's name style (yellow, bold)", w.Ok && w.Detail.Contains("<color=#FFE16B><b>"), w.Detail);
            // what the chat really draws: tags must be read, not shown (the game's chat has rich text off)
            yield return new WaitForSecondsRealtime(0.5f);
            List<string> drawn = UnityEngine.Object.FindObjectsByType<UI.Chat.ChatMessageObjectUI>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(o => HarmonyLib.Traverse.Create(o).Field("text").GetValue<TMPro.TMP_Text>()).Where(t => t != null)
                .Select(t => { t.ForceMeshUpdate(true, true); return t.GetParsedText(); }).ToList();
            List<string> raw = drawn.Where(t => t.Contains("<color=") || t.Contains("<b>") || t.Contains("<size=")).Where(t => !t.Contains("I am the server")).ToList();
            string styled = drawn.FirstOrDefault(t => t.Contains("Red and slanted")) ?? "";
            check("the chat draws the styles: no tags shown as text (except what a player typed)", drawn.Count > 0 && raw.Count == 0 && styled.StartsWith("[Server] Red"),
                drawn.Count + " line(s); showing tags: " + string.Join(" || ", raw.Take(3).ToArray()) + "; styled line drawn as: " + styled);
            string player = drawn.FirstOrDefault(t => t.Contains("I am the server")) ?? "";
            check("...and a player's typed tags are drawn as typed, after their name", player.Contains(": <color=#4FC3F7>") && !player.StartsWith("<"), player);
            yield return new WaitForSecondsRealtime(1.5f);
            Shot("chat-styles");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        // ---- the server's icon and name in game ------------------------------------------------------

        private static IEnumerator ServerIconProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            // an icon as the Server Manager writes it: a 128x128 PNG in the data folder
            Texture2D t = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(64, 64));
                    t.SetPixel(x, y, d < 52 ? Color.Lerp(new Color(0.3f, 0.75f, 1f), new Color(0.1f, 0.2f, 0.6f), d / 52f) : (d < 60 ? new Color(1f, 0.85f, 0.3f) : new Color(0, 0, 0, 0)));
                }
            t.Apply();
            byte[] png = ImageConversion.EncodeToPNG(t);
            File.WriteAllBytes(Path.Combine(SrvData, "server-icon.png"), png);
            string hash = ServerInfo.Hash(png).Substring(0, 16);
            yield return ConsoleText("set Description Ships, chess and good company.", "Description", w);
            yield return WaitUntil(() => ServerInfo.Joined.HasValue && ServerInfo.Joined.Value.IconHash == hash && ServerInfo.JoinedIcon != null, 15f);
            check("the server's icon reaches the players in game (a new one within seconds)", ServerInfo.JoinedIcon != null && ServerInfo.Joined.Value.IconHash == hash,
                ServerInfo.Joined.HasValue ? "icon " + ServerInfo.Joined.Value.IconHash + ", " + (ServerInfo.Joined.Value.Icon != null ? ServerInfo.Joined.Value.Icon.Length : 0) + " bytes" : "no server info");
            UI.Pause.PauseMenuHandler pause = UnityEngine.Object.FindFirstObjectByType<UI.Pause.PauseMenuHandler>(FindObjectsInactive.Include);
            pause.Show();
            yield return new WaitForSecondsRealtime(1f);
            GameObject badge = GameObject.Find("DedicatedServer.ServerBadge");
            Shot("pause-badge");
            yield return new WaitForSecondsRealtime(0.5f);
            check("the pause menu shows the server's icon and name in its top-right corner", badge != null && badge.GetComponentInChildren<UnityEngine.UI.RawImage>() != null && badge.GetComponentInChildren<TMPro.TMP_Text>().text.Contains(ServerInfo.Joined.Value.Name),
                badge != null ? badge.GetComponentInChildren<TMPro.TMP_Text>().text : "no badge");
            UI.Pause.PauseButtonsActivator act = UnityEngine.Object.FindFirstObjectByType<UI.Pause.PauseButtonsActivator>(FindObjectsInactive.Include);
            GameObject btnGo = act != null ? HarmonyLib.Traverse.Create(act).Field("playerListButton").GetValue<GameObject>() : null;
            UnityEngine.UI.Button btn = btnGo != null ? btnGo.GetComponentInChildren<UnityEngine.UI.Button>(true) : null;
            if (btn != null) btn.onClick.Invoke();
            yield return new WaitForSecondsRealtime(1f);
            GameObject row = GameObject.Find("DedicatedServer.ServerRow");
            Shot("playerlist-server");
            yield return new WaitForSecondsRealtime(0.5f);
            check("the player list has a row for the server at the top: icon, name, players", row != null && row.transform.GetSiblingIndex() == 0 && row.GetComponentsInChildren<TMPro.TMP_Text>().Any(x => x.text.Contains(ServerInfo.Joined.Value.Name)),
                row != null ? string.Join(" / ", row.GetComponentsInChildren<TMPro.TMP_Text>().Select(x => x.text).ToArray()) : "no row");
            pause.Hide();
            // the Join page: a saved server shows the icon too (fetched in pieces over the game port)
            SavedServer s = SavedServers.Pin(_address);
            s.Icon = ""; s.IconTexture = null;
            try { File.Delete(SavedServers.IconFile(hash)); } catch { }
            SavedServers.Ask(s);
            yield return WaitUntil(() => s.Icon == hash && SavedServers.IconOf(s) != null, 15f);
            check("a saved server's icon is fetched for the Join page", s.Icon == hash && SavedServers.IconOf(s) != null, "icon " + s.Icon);
        }

        // ---- mods from the server -------------------------------------------------------------------

        private static IEnumerator ModDownloadProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string mod = Environment.GetEnvironmentVariable("SDD_DS_DOWNLOAD_MOD") ?? "SolarPanels";
            MelonLoader.MelonMod mine = MelonLoader.MelonMod.RegisteredMelons.FirstOrDefault(m => m.Info.Name == mod);
            if (mine == null) { check("mod download", false, mod + " isn't loaded here"); yield break; }
            string file = Path.GetFileName(mine.MelonAssembly.Location);
            string target = Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, file);
            string before = ServerInfo.Hash(File.ReadAllBytes(target));
            yield return ConsoleText("set ShareMods on", "ShareMods", w);
            Client.HideForTest.Add(mod);                          // as if this game didn't have it
            yield return Rejoin(w);
            check("a player without a mod the server needs is turned away...", !w.Ok && Client.LastVerdict.HasValue && Client.LastVerdict.Value.Message.Contains(mod), w.Detail);
            yield return WaitUntil(() => ModInstall.State == "offered", 30f);
            yield return new WaitForSecondsRealtime(1f);
            Shot("mods-offered");
            yield return new WaitForSecondsRealtime(0.5f);
            List<ModInstall.Offer> offers = Client.LastVerdict.HasValue ? ModInstall.Offers(Client.LastVerdict.Value) : new List<ModInstall.Offer>();
            check("...and offered it from the server: file, size, SHA-256, and a warning", ModInstall.State == "offered" && offers.Any(o => o.File == file),
                string.Join(", ", offers.Select(o => o.File + " " + ModInstall.Kb(o.Size) + " " + o.Sha.Substring(0, 12)).ToArray()) + " (state " + ModInstall.State + ")");
            float t0 = Time.realtimeSinceStartup;
            ModInstall.Start();                                   // what "Download and install" does
            yield return WaitUntil(() => ModInstall.State == "installed" || ModInstall.State == "failed", 60f);
            yield return new WaitForSecondsRealtime(0.5f);
            Shot("mods-installed");
            yield return new WaitForSecondsRealtime(0.5f);
            string after = File.Exists(target) ? ServerInfo.Hash(File.ReadAllBytes(target)) : "";
            ModInstall.Offer offer = offers.FirstOrDefault(o => o.File == file);
            string replaced = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DedicatedServer-replaced");
            check("Download and install: the file arrives whole (SHA-256 as announced) and goes into Mods", ModInstall.State == "installed" && offer != null && after == offer.Sha,
                ModInstall.State + " in " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s; " + (offer != null ? ModInstall.Kb(offer.Size) : "-") + "; now " + (after.Length > 12 ? after.Substring(0, 12) : after));
            check("...the copy it replaced is kept (UserData/DedicatedServer-replaced)", Directory.Exists(replaced) && Directory.GetFiles(replaced, file + ".*.old").Length > 0, replaced);
            note("before " + before.Substring(0, 12) + ", after " + (after.Length > 12 ? after.Substring(0, 12) : after) + " (the same build on both sides here, so the same bytes)");
            Client.HideForTest.Clear();
            ClosablePopupClose();
            yield return Rejoin(w);
            check("with the mod, it joins", w.Ok, w.Detail);
        }

        private static void ClosablePopupClose()
        {
            foreach (UI.MainMenu.Common.ClosablePopup p in UnityEngine.Object.FindObjectsByType<UI.MainMenu.Common.ClosablePopup>(FindObjectsSortMode.None)) p.Close();
        }

        private static int ShipEventsIn(string reply)
        {
            Match m = Regex.Match(reply ?? "", @"(\d+) time\(s\) a ship");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private class GroundStats { public float Min = float.MaxValue, Last; public int Below, Samples; public string Spaces = ""; }

        private static IEnumerator SampleGround(float seconds, GroundStats g, UnityEngine.InputSystem.Key[] keys)
        {
            float until = Time.realtimeSinceStartup + seconds;
            HashSet<uint> spaces = new HashSet<uint>();
            while (Time.realtimeSinceStartup < until)
            {
                if (keys != null) Keys(keys);
                uint sp;
                float h = ClientHeight(out sp);
                if (!float.IsNaN(h))
                {
                    g.Samples++; g.Last = h; spaces.Add(sp);
                    if (h < g.Min) g.Min = h;
                    if (h < -1f) g.Below++;
                }
                yield return null;
            }
            if (keys != null) Keys();
            g.Spaces = string.Join(",", spaces.Select(x => x.ToString()).ToArray());
        }

        private static string Show(GroundStats g)
        {
            return g.Samples + " frames, lowest " + g.Min.ToString("0.00", CultureInfo.InvariantCulture) + " m, " + g.Below + " frame(s) more than 1 m under, now " + g.Last.ToString("0.00", CultureInfo.InvariantCulture) + " m (space " + g.Spaces + ")";
        }

        private static IEnumerator GroundProbe(Action<string, bool, string> check, Action<string> note)
        {
            TrackedPlayerLocalClient me = Me();
            if (me == null) { check("ground", false, "not in a game"); yield break; }
            string id = MyState().SteamUserId.ToString();
            CheckWrap w = new CheckWrap();
            bool rescue = !(Environment.GetEnvironmentVariable("SDD_DS_GROUND_RESCUE") ?? "").Equals("off", StringComparison.OrdinalIgnoreCase);
            yield return ConsoleText("set GroundRescue " + (rescue ? "on" : "off"), "GroundRescue", w);
            note("server GroundRescue " + (rescue ? "on" : "off"));

            GroundStats spawn = new GroundStats();
            yield return SampleGround(8f, spawn, null);
            note("at spawn, standing: " + Show(spawn));

            List<GroundStats> far = new List<GroundStats>();
            float[][] hops = { new[] { 3000f, 0f }, new[] { 0f, 2500f }, new[] { 1200f, 0f }, new[] { -4000f, 0f }, new[] { 150f, 0f }, new[] { 0f, 900f } };
            foreach (float[] hop in hops)
            {
                Vector3 before = Me().Position; uint sp0 = Me().SpaceId;
                yield return ConsoleText("tpforward " + id + " " + hop[0].ToString(CultureInfo.InvariantCulture) + " " + hop[1].ToString(CultureInfo.InvariantCulture), "moved", w);
                float t = Time.realtimeSinceStartup + 5f;
                while (Time.realtimeSinceStartup < t && Me().SpaceId == sp0 && Vector3.Distance(Me().Position, before) < 50f) yield return null;
                GroundStats g = new GroundStats();
                yield return SampleGround(6f, g, null);
                GroundStats run = new GroundStats();
                yield return SampleGround(12f, run, new[] { UnityEngine.InputSystem.Key.W, UnityEngine.InputSystem.Key.LeftShift });
                note("after moving " + hop[0] + " m / " + hop[1] + " m sideways: standing " + Show(g) + "; running " + Show(run));
                far.Add(g); far.Add(run);
            }
            yield return ConsoleText("ground", "  last:|m above the ground", w);
            yield return new WaitForSecondsRealtime(0.5f);
            yield return ConsoleText("ground", "lowest", w);
            foreach (string l in w.Detail.Split('\n')) if (l.Trim().Length > 0) note("server: " + l.Trim());
            Match m = Regex.Match(w.Detail, @"(\d+) time\(s\) a player was under the ground, (\d+) time\(s\) a ship; (\d+) player\(s\) and \d+ ship\(s\) put back");
            int sEvents = m.Success ? int.Parse(m.Groups[1].Value) : -1, rescued = m.Success ? int.Parse(m.Groups[3].Value) : -1;
            check("at spawn the player stands on the ground (not in it)", spawn.Samples > 30 && spawn.Below == 0, Show(spawn));
            check("far from spawn the player never sinks into the ground (client's view)", far.All(g => g.Samples > 30 && g.Below == 0),
                string.Join(" | ", far.Select(g => g.Min.ToString("0.00", CultureInfo.InvariantCulture) + " m / " + g.Below).ToArray()));
            check("the server never had the player under the ground", sEvents == 0, m.Success ? sEvents + " time(s), " + rescued + " put back" : "no `ground` reply: " + w.Detail);
        }
    }
}
