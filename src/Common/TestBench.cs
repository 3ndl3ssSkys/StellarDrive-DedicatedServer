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
    using System.Collections;
    using System.IO;
    using System.Linq;
    using Players;
    using Players.LocalClient.Controls;
    using Players.Services;
    using Ships;
    using Ships.Interface.Model;
    using Ships.Interface.Model.Parts;
    using Ships.Interface.Model.Placement;
    using Ships.Interface.Services;
    using Ships.Interface.Settings;
    using Ships.Services;
    using UnityEngine;
    using WorldTracking.Interface.Model;
    using WorldTracking.Interface.Services;

    /// <summary>
    /// For self tests that have to LOOK at something: a small floor platform
    /// built with the game's own ship creation, next to the local player, with
    /// the camera then put at a known place. Host only.
    /// </summary>
    public class TestBench
    {
        public TrackedShipServer Ship;
        public int Size;

        public static PartSettings Part(Func<PartSettings, bool> f) { return new PartSettingsProvider().GetAllPartSettings().FirstOrDefault(f); }

        public static TrackedPlayerServer Me()
        {
            PlayersClientTracker cp = Svc.Get<PlayersClientTracker>();
            return Svc.Get<PlayersServerTracker>().GetTrackedPlayerFromConnectionId(cp.LocalPlayer.ConnectionId);
        }

        /// <summary>
        /// A size x size iron deck, level with gravity, a few metres in front
        /// of the player and a little above the ground, pinned in place (its
        /// rigidbody frozen) so it neither slides downhill nor tips over.
        /// </summary>
        public static IEnumerator Create(int size, Action<TestBench> done, float side = 0f)
        {
            TrackedPlayerServer me = Me();
            ShipCreationService creator = Svc.Get<ShipCreationService>();
            ShipsServerTracker ships = Svc.Get<ShipsServerTracker>();
            IWorldTransposeServer tr = Svc.Get<IWorldTransposeServer>();
            HullPartSettings floor = Part(p => p.name == "IronFloor") as HullPartSettings;
            // where the player is in the world (not in some ship's own space)
            uint space = me.SpaceId;
            Vector3 mePos = me.Position;
            Quaternion meRot = me.Rotation;
            for (int hop = 0; hop < 8; hop++)
            {
                TrackedShipServer carrier = ships.Ships.FirstOrDefault(sh => sh.RelativeSpaceId == space);
                if (carrier == null) break;
                mePos = tr.Position(space, carrier.SpaceId, mePos);
                meRot = tr.Rotation(space, carrier.SpaceId, meRot);
                space = carrier.SpaceId;
            }
            Vector3 g = Vector3.zero;
            try { g = Svc.Get<WorldProvision.Interface.Services.IWorldEnvironmentServerProvider>().GetGravityAtPosition(space, mePos); } catch { }
            Vector3 up = g.sqrMagnitude > 0.01f ? -g.normalized : meRot * Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(meRot * Vector3.forward, up).normalized;
            Vector3 feet = mePos - up * BodyAboveFeet;
            // floor voxels at y=0 have their surface at local y=-0.5
            Vector3 pos = feet + fwd * 3f + up * (0.5f + 0.6f) + Vector3.Cross(up, fwd) * side;   // side: metres to the player's right
            Quaternion rot = Quaternion.LookRotation(fwd, up);
            yield return CreateAt(size, space, pos, rot, done, new LocalPhysicsState { Position = mePos, Rotation = meRot }, "player at " + mePos.ToString("F1") + " space " + space + ", gravity " + g.ToString("F2") + ", player up " + (me.Rotation * Vector3.up).ToString("F2") + ", deck up " + up.ToString("F2") + " at " + pos.ToString("F1"));
        }

        /// <summary>A deck made exactly here (in <paramref name="space"/>), pinned; disposing it puts the player back at <paramref name="home"/>.</summary>
        public static IEnumerator CreateAt(int size, uint space, Vector3 pos, Quaternion rot, Action<TestBench> done, LocalPhysicsState home, string made = "")
        {
            ShipCreationService creator = Svc.Get<ShipCreationService>();
            ShipsServerTracker ships = Svc.Get<ShipsServerTracker>();
            HullPartSettings floor = Part(p => p.name == "IronFloor") as HullPartSettings;
            HullPartPlacement first = new HullPartPlacement(new HullPartPosition { Type = VoxelPartType.Floor, VoxelCoord = Vector3Int.zero }, PartOrientation.Up, PartOrientation.Forward);
            uint id = creator.CreateShip(new HullPart(floor, first), new WorldTransform { SpaceId = space, Position = pos, Rotation = rot });
            TrackedShipServer ship = ships.GetShip(id);
            for (int x = 0; x < size; x++)
                for (int z = 0; z < size; z++)
                {
                    if (x == 0 && z == 0) continue;
                    ship.AddHullPart(floor, new HullPartPlacement(new HullPartPosition { Type = VoxelPartType.Floor, VoxelCoord = new Vector3Int(x, 0, z) }, PartOrientation.Up, PartOrientation.Forward));
                }
            TestBench bench = new TestBench { Ship = ship, Size = size, _pos = pos, _rot = rot, _home = home, _homeSpace = space };
            bench.Made = made;
            for (int i = 0; i < 120 && !ship.IsRigidbodyLoaded; i++) yield return null;
            bench.Pin();
            for (int i = 0; i < 30; i++) yield return null;
            bench.Pin();
            done(bench);
        }

        /// <summary>Use an existing ship as the "deck" (for looking at it; it is not pinned or removed).</summary>
        public static TestBench For(TrackedShipServer ship)
        {
            return new TestBench { Ship = ship, Size = 0, _pos = ship.Position, _rot = ship.Rotation };
        }

        private Vector3 _pos;
        private Quaternion _rot;
        private LocalPhysicsState _home;
        private uint _homeSpace;

        /// <summary>
        /// Put the player back where they stood before the deck was made, then
        /// take the deck apart (the game drops a ship once its last part goes).
        /// </summary>
        public IEnumerator Dispose()
        {
            TrackedPlayerServer sp = Me();
            // whatever a test left you doing (in a seat, at a chest, on a ladder...), you are on your feet
            // before the deck goes: sitting in a seat that is gone leaves the next test stuck
            if (sp.State.Type != Players.Interface.Model.PlayerStateType.Walking)
            {
                sp.SetState(new Players.Interface.Model.PlayerState { Type = Players.Interface.Model.PlayerStateType.Walking });
                for (int i = 0; i < 10; i++) yield return null;
            }
            Svc.Get<ISpacesServerProvider>().SetWorldObjectPhysics(sp, _homeSpace, _home);
            Svc.Get<PlayersClientTracker>().LocalPlayer.Respawn(sp.PhysicsState, _home.Rotation);
            for (int i = 0; i < 10; i++) yield return null;
            foreach (ushort id in Ship.StatefulParts.Select(p => p.Id).ToList()) Ship.RemoveStatefulPart(id);
            foreach (HullPartPosition hp in Ship.GetHullParts().Keys.ToList()) Ship.RemoveHullPart(hp);
            for (int i = 0; i < 10; i++) yield return null;
        }
        public string Made;
        /// <summary>Where the deck is pinned (in <see cref="HomeSpace"/>), and where disposing it puts the player back.</summary>
        public Vector3 PinnedAt { get { return _pos; } }
        public Quaternion PinnedRotation { get { return _rot; } }
        public uint HomeSpace { get { return _homeSpace; } }
        public LocalPhysicsState Home { get { return _home; } }

        /// <summary>Where the camera really is, in deck coordinates (to check a Look landed).</summary>
        public string Where()
        {
            Camera c = Camera.main;
            IShipsClientProvider sc = Svc.Get<IShipsClientProvider>();
            Transform t;
            if (c == null || sc == null || !sc.TryGetShipVisualTransform(Ship.Id, out t) || t == null) return "no camera/ship transform";
            return "camera at deck " + t.InverseTransformPoint(c.transform.position).ToString("F2") + " looking " + t.InverseTransformDirection(c.transform.forward).ToString("F2") + " up " + t.InverseTransformDirection(c.transform.up).ToString("F2")
                + "; server deck at " + Ship.Position.ToString("F1") + " rot " + Ship.Rotation.eulerAngles.ToString("F0") + " (made " + _rot.eulerAngles.ToString("F0") + ")";
        }

        /// <summary>Pin the deck somewhere else from now on (moved there at once).</summary>
        public void MoveTo(Vector3 pos, Quaternion rot)
        {
            _pos = pos; _rot = rot;
            // a pinned deck's body is frozen: it is put there itself (the ship's state alone only sets how it moves)
            Rigidbody rb = null;
            try { rb = Ship.RigidbodyObject != null ? Ship.RigidbodyObject.GetComponent<Rigidbody>() : null; } catch (NullReferenceException) { }
            if (rb != null) { rb.position = pos; rb.rotation = rot; rb.transform.SetPositionAndRotation(pos, rot); }
            Pin();
        }

        /// <summary>Put the deck back where it was made and freeze it there.</summary>
        public void Pin()
        {
            Svc.Get<ISpacesServerProvider>().SetWorldObjectPhysics(Ship, Ship.SpaceId, new LocalPhysicsState { Position = _pos, Rotation = _rot, Velocity = Vector3.zero });
            Ship.NotifyRepositioned();
            Rigidbody rb = null;
            try { rb = Ship.RigidbodyObject != null ? Ship.RigidbodyObject.GetComponent<Rigidbody>() : null; } catch (NullReferenceException) { }   // (physics not loaded yet)
            if (rb == null) return;
            rb.constraints = RigidbodyConstraints.FreezeAll;
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        /// <summary>
        /// Really move the deck: let go of it and set it going at <paramref name="localVel"/>
        /// (deck axes) for <paramref name="seconds"/>, then pin it again. The visuals see a true
        /// lurch - a start and a sudden stop - not just a changed number in the ship's state.
        /// </summary>
        public IEnumerator Shove(Vector3 localVel, float seconds, Action each = null)
        {
            Rigidbody rb = Ship.RigidbodyObject != null ? Ship.RigidbodyObject.GetComponent<Rigidbody>() : null;
            if (rb != null) rb.constraints = RigidbodyConstraints.None;
            float until = Time.time + seconds;
            while (Time.time < until)
            {
                Svc.Get<ISpacesServerProvider>().SetWorldObjectPhysics(Ship, Ship.SpaceId, new LocalPhysicsState { Position = Ship.Position, Rotation = _rot, Velocity = _rot * localVel });
                if (rb != null && !rb.isKinematic) { rb.linearVelocity = _rot * localVel; rb.angularVelocity = Vector3.zero; }
                each?.Invoke();
                yield return null;
            }
            Pin();
        }

        /// <summary>Deck-local position of the middle of the deck surface.</summary>
        public Vector3 Middle { get { return new Vector3((Size - 1) * 0.5f, -0.5f, (Size - 1) * 0.5f); } }

        public ushort Place(PartSettings s, Vector3 local, PartOrientation forward = PartOrientation.Forward, PartOrientation up = PartOrientation.Up)
        {
            return Ship.AddStatefulPart(s, new FloatingPartPlacement(local, forward, up));
        }

        public void Wall(Vector3Int voxel, VoxelPartType type)
        {
            HullPartSettings wall = Part(p => p.name == "IronWall") as HullPartSettings;
            PartOrientation fwd = type == VoxelPartType.RightWall ? PartOrientation.Right : PartOrientation.Forward;
            Ship.AddHullPart(wall, new HullPartPlacement(new HullPartPosition { Type = type, VoxelCoord = voxel }, fwd, PartOrientation.Up));
        }

        public Vector3 World(Vector3 local)
        {
            return Svc.Get<IWorldTransposeServer>().Position(Ship.RelativeSpaceId, Ship.SpaceId, local);
        }

        /// <summary>Player capsule: 1.65 m tall, centred on the body position; eyes 0.65 above that.</summary>
        public const float BodyAboveFeet = 0.825f, EyeAboveBody = 0.65f;

        /// <summary>
        /// Stand the player with their feet at <paramref name="feetLocal"/>
        /// (deck-local) and look at <paramref name="targetLocal"/>.
        /// Returns the camera rotation IN DECK SPACE, for <see cref="Shot"/>
        /// and <see cref="Grab"/> to hold.
        /// </summary>
        public Quaternion Look(Vector3 feetLocal, Vector3 targetLocal)
        {
            ISpacesServerProvider spaces = Svc.Get<ISpacesServerProvider>();
            IWorldTransposeServer tr = Svc.Get<IWorldTransposeServer>();
            PlayersClientTracker cp = Svc.Get<PlayersClientTracker>();
            TrackedPlayerServer sp = Me();
            Vector3 eyeLocal = Eye(feetLocal);
            Vector3 flat = Vector3.ProjectOnPlane(targetLocal - eyeLocal, Vector3.up);
            Quaternion bodyLocal = Quaternion.LookRotation(flat.sqrMagnitude > 1e-4f ? flat : Vector3.forward, Vector3.up);
            Quaternion camLocal = Quaternion.LookRotation(targetLocal - eyeLocal, Vector3.up);
            Vector3 body = World(feetLocal + Vector3.up * BodyAboveFeet);
            spaces.SetWorldObjectPhysics(sp, Ship.SpaceId, new LocalPhysicsState { Position = body, Rotation = tr.Rotation(Ship.RelativeSpaceId, Ship.SpaceId, bodyLocal), Velocity = Vector3.zero });
            cp.LocalPlayer.Respawn(sp.PhysicsState, tr.Rotation(Ship.RelativeSpaceId, sp.SpaceId, camLocal));
            Aim(camLocal);
            return camLocal;
        }

        /// <summary>A camera rotation (deck frame) looking along <paramref name="dirLocal"/>, level.</summary>
        public Quaternion LookDir(Vector3 dirLocal) { return Quaternion.LookRotation(dirLocal, Vector3.up); }

        /// <summary>Deck-local eye position for feet at <paramref name="feetLocal"/>.</summary>
        public static Vector3 Eye(Vector3 feetLocal) { return feetLocal + Vector3.up * (BodyAboveFeet + EyeAboveBody); }

        public void Remove(ushort part) { Ship.RemoveStatefulPart(part); }

        // ---- the player's own way in: what the crosshair finds, what F does ---------------------

        /// <summary>What the game's own interaction detector has under the crosshair right now.</summary>
        public static Players.Interface.Model.IInteractableObject Hovered()
        {
            Players.Services.InteractableHoverTracker t = Svc.Get<Players.Services.InteractableHoverTracker>();
            return t != null ? t.CurrentHoveredInteractable : null;
        }

        /// <summary>Stand at <paramref name="feetLocal"/> looking at <paramref name="targetLocal"/> and let the game's detector run.</summary>
        public IEnumerator HoverAt(Vector3 feetLocal, Vector3 targetLocal, int frames = 25)
        {
            Quaternion q = Look(feetLocal, targetLocal);
            for (int i = 0; i < frames; i++)
            {
                // aim from where the camera really is (the body settles a few cm from where it was put)
                Camera c = Camera.main;
                Transform t;
                if (c != null && Svc.Get<IShipsClientProvider>().TryGetShipVisualTransform(Ship.Id, out t))
                {
                    Vector3 eye = t.InverseTransformPoint(c.transform.position);
                    if ((targetLocal - eye).sqrMagnitude > 1e-4f) q = Quaternion.LookRotation(targetLocal - eye, Vector3.up);
                }
                Aim(q);
                yield return null;
            }
        }

        /// <summary>
        /// Exactly what the Interact key (F) does - PlayerInteractionControl.OnInteract:
        /// if that control is live, interact with whatever the game's tracker holds.
        /// </summary>
        public static bool PressInteract()
        {
            PlayerInteractionControl c = UnityEngine.Object.FindFirstObjectByType<PlayerInteractionControl>();
            Players.Interface.Model.IInteractableObject h = Hovered();
            if (c == null || !c.isActiveAndEnabled || h == null) return false;
            h.Interact(Players.Interface.Model.InteractionType.Interact);
            return true;
        }

        /// <summary>The game's pick-up on the hovered object (what left click does on a part).</summary>
        // ---- keys and buttons through the Input System itself ---------------------------------
        // Queued as device state, the way a real keyboard's report arrives: every action,
        // rebind and "is it pressed" in the game and the mods sees it. The game may be in the
        // background while a test runs, so input is told to keep listening until Restore.

        private static UnityEngine.InputSystem.InputSettings.BackgroundBehavior? _bg;

        public static void ListenUnfocused()
        {
            var st = UnityEngine.InputSystem.InputSystem.settings;
            if (_bg == null) _bg = st.backgroundBehavior;
            st.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            foreach (UnityEngine.InputSystem.InputDevice d in new UnityEngine.InputSystem.InputDevice[] { UnityEngine.InputSystem.Keyboard.current, UnityEngine.InputSystem.Mouse.current })
                if (d != null && !d.enabled) UnityEngine.InputSystem.InputSystem.EnableDevice(d);
        }

        public static void RestoreInput()
        {
            KeysUp();
            if (_bg != null) UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = _bg.Value;
            _bg = null;
        }

        public static void KeysDown(params UnityEngine.InputSystem.Key[] keys)
        {
            ListenUnfocused();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(UnityEngine.InputSystem.Keyboard.current, new UnityEngine.InputSystem.LowLevel.KeyboardState(keys));
        }

        public static void KeysUp()
        {
            if (UnityEngine.InputSystem.Keyboard.current != null)
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(UnityEngine.InputSystem.Keyboard.current, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            if (UnityEngine.InputSystem.Mouse.current != null)
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(UnityEngine.InputSystem.Mouse.current, new UnityEngine.InputSystem.LowLevel.MouseState { position = UnityEngine.InputSystem.Mouse.current.position.ReadValue() });
        }

        public static void MouseDown(UnityEngine.InputSystem.LowLevel.MouseButton button)
        {
            ListenUnfocused();
            UnityEngine.InputSystem.Mouse m = UnityEngine.InputSystem.Mouse.current;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(m, new UnityEngine.InputSystem.LowLevel.MouseState { position = m.position.ReadValue() }.WithButton(button));
        }

        private static bool _crouchKey;

        /// <summary>
        /// Crouch the way a player does: the Crouch key (Left Ctrl) held through the Input System,
        /// which in gravity is what crouches - and the game's own "down" input, which is what zero
        /// gravity reads.
        /// </summary>
        public static void CrouchKey(PlayerWalkingControls w, float v)
        {
            if (w != null) w.crouchInput = v;
            bool on = v > 0f;
            if (on == _crouchKey) return;
            _crouchKey = on;
            if (on) KeysDown(UnityEngine.InputSystem.Key.LeftCtrl); else KeysUp();
        }

        /// <summary>Press a key for a few frames and let it go.</summary>
        public static IEnumerator Tap(UnityEngine.InputSystem.Key key, int frames = 3)
        {
            KeysDown(key);
            for (int i = 0; i < frames; i++) yield return null;
            KeysUp();
            yield return null;
            yield return null;
        }

        public static bool PressPickUp()
        {
            Players.Interface.Model.IInteractableObject h = Hovered();
            if (h == null) return false;
            return h.Interact(Players.Interface.Model.InteractionType.PickUp);
        }

        /// <summary>Every collider the interaction detector's ray passes through (its own mask), nearest first.</summary>
        public static string RayReport(float dist = 5f)
        {
            Camera cam = Camera.main;
            if (cam == null) return "no camera";
            int mask = (1 << LayerMask.NameToLayer("Default")) | (1 << LayerMask.NameToLayer("Ship")) | (1 << LayerMask.NameToLayer("Interactable")) | (1 << LayerMask.NameToLayer("WorldObject"));
            RaycastHit[] hits = new RaycastHit[32];
            int n = WorldTracking.Interface.Values.ClientScene.PhysicsScene.Raycast(cam.transform.position, cam.transform.forward, hits, dist, mask);
            if (n == 0) return "the ray hits nothing within " + dist + " m";
            return string.Join("; ", hits.Take(n).OrderBy(h => h.distance).Select(h => h.collider.name + " (tag " + h.collider.tag + ", layer " + LayerMask.LayerToName(h.collider.gameObject.layer) + ", " + h.distance.ToString("F2") + " m, trigger " + h.collider.isTrigger + ")").ToArray());
        }

        public static string Describe(Players.Interface.Model.IInteractableObject h)
        {
            if (h == null) return "nothing";
            Component c = h as Component;
            string what = c != null ? c.GetType().Name + " '" + c.name + "'" : h.GetType().Name;
            string kinds = h.Interactions != null ? string.Join("/", h.Interactions.Select(i => i.type.ToString()).ToArray()) : "";
            return what + " [" + kinds + "]";
        }

        /// <summary>
        /// Point the local camera. The look controls keep their rotation in
        /// whatever physics space the player is in (the deck's own space once
        /// standing on it), so convert from deck space to that.
        /// </summary>
        public void Aim(Quaternion camLocal)
        {
            PlayerLookControls look = UnityEngine.Object.FindFirstObjectByType<PlayerLookControls>();
            if (look == null) return;
            uint cur = (uint)Refl.Get(look, "_currentSpaceId");
            look.Rotation = cur == Ship.RelativeSpaceId ? camLocal : Svc.Get<IWorldTransposeClient>().Rotation(Ship.RelativeSpaceId, cur, camLocal);
        }

        /// <summary>Hold the aim for a few frames, then save a screenshot to DemoVerify/shots.</summary>
        public IEnumerator Shot(string name, Quaternion aim, int settleFrames = 30)
        {
            Texture2D tex = null;
            yield return Grab(name, aim, t => tex = t, settleFrames);
            UnityEngine.Object.Destroy(tex);
        }

        /// <summary>Like <see cref="Shot"/> but keeps the picture for measuring (caller destroys it).</summary>
        public IEnumerator Grab(string name, Quaternion aim, Action<Texture2D> got, int settleFrames = 30)
        {
            for (int i = 0; i < settleFrames; i++) { Aim(aim); yield return null; }
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (name != null) Save(name, tex);
            got(tex);
        }

        /// <summary>Save what is on screen now (menus included), without touching the camera.</summary>
        public static IEnumerator Screen(string name)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            Save(name, tex);
            UnityEngine.Object.Destroy(tex);
        }

        public static void Save(string name, Texture2D tex)
        {
            string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DemoVerify", "shots");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), ImageConversion.EncodeToPNG(tex));
        }

        /// <summary>
        /// Mean per-channel difference (0..1) between two same-size pictures,
        /// inside a rectangle given in 0..1 screen fractions.
        /// </summary>
        public static float Diff(Texture2D a, Texture2D b, Rect r)
        {
            if (a.width != b.width || a.height != b.height) return 1f;
            Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
            int x0 = Mathf.Clamp((int)(r.xMin * a.width), 0, a.width - 1), x1 = Mathf.Clamp((int)(r.xMax * a.width), x0 + 1, a.width);
            int y0 = Mathf.Clamp((int)(r.yMin * a.height), 0, a.height - 1), y1 = Mathf.Clamp((int)(r.yMax * a.height), y0 + 1, a.height);
            double sum = 0; int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color32 ca = pa[y * a.width + x], cb = pb[y * a.width + x];
                    sum += Math.Abs(ca.r - cb.r) + Math.Abs(ca.g - cb.g) + Math.Abs(ca.b - cb.b);
                    n += 3;
                }
            return n == 0 ? 0f : (float)(sum / n / 255.0);
        }

        /// <summary>
        /// How many pixels inside the rectangle differ clearly (any channel by
        /// more than <paramref name="threshold"/> of 255) - small sharp things
        /// like text show up here even when the mean difference hardly moves.
        /// </summary>
        public static int Changed(Texture2D a, Texture2D b, Rect r, int threshold = 40)
        {
            if (a.width != b.width || a.height != b.height) return int.MaxValue;
            Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
            int x0 = Mathf.Clamp((int)(r.xMin * a.width), 0, a.width - 1), x1 = Mathf.Clamp((int)(r.xMax * a.width), x0 + 1, a.width);
            int y0 = Mathf.Clamp((int)(r.yMin * a.height), 0, a.height - 1), y1 = Mathf.Clamp((int)(r.yMax * a.height), y0 + 1, a.height);
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color32 ca = pa[y * a.width + x], cb = pb[y * a.width + x];
                    if (Math.Abs(ca.r - cb.r) > threshold || Math.Abs(ca.g - cb.g) > threshold || Math.Abs(ca.b - cb.b) > threshold) n++;
                }
            return n;
        }

        /// <summary>Where a deck-local point lands on screen, in 0..1 fractions (z &lt; 0 = behind).</summary>
        public Vector3 OnScreen(Vector3 local)
        {
            Camera c = Camera.main;
            if (c == null) return new Vector3(-1, -1, -1);
            // the camera renders the client's copy; both copies share the ship's pose closely enough to frame a shot
            Vector3 v = c.WorldToViewportPoint(ClientWorld(local));
            return v;
        }

        /// <summary>A deck-local point in the local client's render space.</summary>
        public Vector3 ClientWorld(Vector3 local)
        {
            IShipsClientProvider sc = Svc.Get<IShipsClientProvider>();
            Transform t;
            if (sc != null && sc.TryGetShipVisualTransform(Ship.Id, out t) && t != null) return t.TransformPoint(local);
            return World(local);
        }
    }
}
