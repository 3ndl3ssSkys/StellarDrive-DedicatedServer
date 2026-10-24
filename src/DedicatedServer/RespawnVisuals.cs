// A player's side of trees and ores growing back (the server decides; see Respawn.cs).
//
// The server sends "respawn" with the planet and what came back. The game's own lists of what is gone
// only ever grow, so this takes the things out of them and makes the planet draw them again:
//   trees, bushes, rocks  drawn by the GPU from the lists: a stand-in grows out of the ground in their
//                         place (with the game's own dust puff), then the real one takes over
//   ore deposits          their chunk is loaded again, and the deposit grows from nothing
//   fixed resources       the planet's few fixed objects are placed again, and grow the same way
//   asteroid ores         the ring's mask of mined ores is worked out again
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Services;
using Core.Values;
using HarmonyLib;
using MelonLoader;
using Planets;
using Planets.EnvironmentGeneration;
using Planets.EnvironmentGeneration.Configs;
using Planets.EnvironmentGeneration.Services;
using Planets.Interface.Model;
using Planets.Network;
using Planets.Network.Model;
using Planets.Objects.Common;
using Planets.Objects.Common.Model;
using Planets.Services;
using Planets.Terrain;
using Planets.Terrain.Evaluation.Services;
using Planets.Terrain.Model;
using UnityEngine;
using WorldEffects.Interface;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;

namespace DedicatedServer
{
    internal static class RespawnVisuals
    {
        public static int Grown, Instant;      // for the probe
        private const float GrowSeconds = 1.6f;
        private const int MaxAnimated = 80;    // more than this at once just appear

        public static void OnEvent(DsEvent e)
        {
            ulong planet;
            if (!ulong.TryParse(e.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out planet)) return;
            PlanetsClientTracker tracker = ServiceLocator.GetService<PlanetsClientTracker>();
            TrackedPlanetClient tp = tracker != null ? tracker.TrackedPlanets.FirstOrDefault(p => p.Planet != null && p.Planet.Id == planet) : null;
            if (tp == null) { Log.Debug("respawn", "planet " + planet + " not here"); return; }
            PlanetNetworkedState state = tp.Planet.GetComponent<PlanetNetworkedState>();
            if (state == null) return;
            int animated = 0;
            bool trees = false, ores = false, fixedOnes = false, asteroids = false;
            List<ChunkObjectId> oreIds = new List<ChunkObjectId>();
            List<int> fixedIds = new List<int>();
            foreach (string line in (e.Data ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 2) continue;
                ChunkObjectId c;
                if (p[0] == Respawn.Tree && Respawn.ParseChunk(p[1], out c))
                {
                    trees = true;
                    if (animated < MaxAnimated && GrowTree(tp, state, c, animated)) { animated++; Grown++; }
                    else { RemoveClient(state._destroyedDenseObjects, c); Instant++; }
                }
                else if (p[0] == Respawn.Ore && Respawn.ParseChunk(p[1], out c)) { RemoveClient(state._destroyedHotspotsObjects, c); oreIds.Add(c); ores = true; }
                else if (p[0] == Respawn.Fixed) { int n; if (int.TryParse(p[1], out n)) { fixedIds.Add(n); fixedOnes = true; } }
                else if (p[0] == Respawn.Asteroid) asteroids = true;
            }
            if (trees) Recompute(tp);
            if (ores) ReloadOres(tp, oreIds);
            if (fixedOnes) ReloadFixed(tp, fixedIds);
            if (asteroids) MelonCoroutines.Start(RemaskRings(tp));
            RefreshLocalColliders();
            Log.Debug("respawn", "planet " + planet + ": " + (e.Data ?? "").Split('\n').Length + " thing(s) back, " + animated + " growing");
        }

        private static void RemoveClient(DestroyedChunkObjectsSync sync, ChunkObjectId c)
        {
            Dictionary<ulong, HashSet<uint>> d = Traverse.Create(sync).Field("_destroyedDenseObjectsClient").GetValue<Dictionary<ulong, HashSet<uint>>>();
            HashSet<uint> set;
            if (d == null || !d.TryGetValue(c.ChunkHash, out set)) return;
            set.Remove(c.IdWithinChunk);
            if (set.Count == 0) d.Remove(c.ChunkHash);
        }

        private static IEnumerable<object> Levels(TrackedPlanetClient tp)
        {
            object dense = Traverse.Create(tp).Field("_denseEnvironmentVisuals").GetValue();
            if (dense == null) return new object[0];
            Array a = Traverse.Create(dense).Field("_visuals").GetValue() as Array;
            return a != null ? a.Cast<object>() : new object[0];
        }

        /// <summary>Every level draws again from the (now shorter) lists.</summary>
        private static void Recompute(TrackedPlanetClient tp)
        {
            foreach (object level in Levels(tp)) Traverse.Create(level).Field("_forceRecomputeGeneration").SetValue(true);
        }

        // ---- a tree growing ----------------------------------------------------------------------

        /// <summary>Starts a stand-in growing where the tree will be; false if it isn't in view (then it just comes back).</summary>
        private static bool GrowTree(TrackedPlanetClient tp, PlanetNetworkedState state, ChunkObjectId id, int n)
        {
            try
            {
                foreach (object level in Levels(tp))
                {
                    Traverse t = Traverse.Create(level);
                    if (!t.Field("_isAllocated").GetValue<bool>()) continue;
                    List<Chunk> chunks = t.Field("_currentChunks").GetValue<List<Chunk>>();
                    Chunk chunk = chunks != null ? chunks.FirstOrDefault(c => c.TreeSignature.Hash == id.ChunkHash) : null;
                    if (chunk == null) continue;
                    PlanetDensePlacementConfig cfg = t.Field("_environmentConfig").GetValue<PlanetDensePlacementConfig>();
                    if (cfg == null || cfg.pattern == null || id.IdWithinChunk >= cfg.pattern.GetObjectCount()) continue;
                    // only the level whose pattern really has an object there
                    Patterns.Interface.Model.Point2D pt = cfg.pattern.GetPointFromIndex(id.IdWithinChunk);
                    Vector3 onSphere = PlanetSphereUtils.TemplateToSphere(pt.pos, chunk.Transform);
                    if (cfg.EvaluateNoiseAtPosition(onSphere) <= pt.threshold) continue;
                    PlanetDenseEnvironmentTransformCpuCalculator calc = t.Field("_environmentCpuCalculator").GetValue<PlanetDenseEnvironmentTransformCpuCalculator>();
                    PlanetTerrain terrain = t.Field("_terrain").GetValue<PlanetTerrain>();
                    PlanetSurfaceTransform tr = calc.CalculateDenseObjectTransform(id, chunk, cfg.pattern);
                    if (tr.SurfacePosition.Magnitude < tp.Planet.Radius + cfg.elevationMin) continue;
                    GameObject prefab = cfg.GetObjectPrefab(new DenseObjectId { PlanetId = tp.Planet.Id, PlacedObjectId = id }, terrain);
                    GameObject standIn = StandIn(prefab, "respawning tree");
                    if (standIn == null) return false;
                    Grow g = standIn.AddComponent<Grow>();
                    g.Planet = tp.Planet;
                    g.PlanetPos = tr.SurfacePosition;
                    g.PlanetRot = tr.Rotation;
                    g.Target = tr.Scale;
                    g.Delay = (n % 12) * 0.05f + UnityEngine.Random.value * 0.25f;
                    g.Done = () => { RemoveClient(state._destroyedDenseObjects, id); Traverse.Create(level).Field("_forceRecomputeGeneration").SetValue(true); };
                    Puff(prefab, tp.Planet, tr.SurfacePosition, tr.Rotation, tr.Scale.x);
                    return true;
                }
            }
            catch (Exception e) { Log.Once("grow-tree", "respawn: a tree could not grow in place: " + e.Message); }
            return false;
        }

        /// <summary>The prefab's look without its physics or behaviour: renderers only.</summary>
        private static GameObject StandIn(GameObject prefab, string name)
        {
            if (prefab == null) return null;
            GameObject root = new GameObject(name);
            LODGroup lod = prefab.GetComponentInChildren<LODGroup>(true);
            Renderer[] wanted = lod != null && lod.GetLODs().Length > 0 ? lod.GetLODs()[0].renderers : prefab.GetComponentsInChildren<MeshRenderer>(true);
            int made = 0;
            foreach (Renderer r in wanted)
            {
                MeshRenderer mr = r as MeshRenderer;
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mr == null || mf == null || mf.sharedMesh == null) continue;
                GameObject part = new GameObject(r.name);
                part.transform.SetParent(root.transform, false);
                // where the renderer sits in the prefab
                Matrix4x4 m = prefab.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                part.transform.localPosition = m.GetColumn(3);
                part.transform.localRotation = m.rotation;
                part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                MeshRenderer nr = part.AddComponent<MeshRenderer>();
                nr.sharedMaterials = mr.sharedMaterials;
                nr.shadowCastingMode = mr.shadowCastingMode;
                made++;
            }
            if (made == 0) { UnityEngine.Object.Destroy(root); return null; }
            return root;
        }

        /// <summary>The game's own puff of dust, where something comes up.</summary>
        private static void Puff(GameObject prefab, Planet planet, Vector3Double at, Quaternion rot, float scale)
        {
            try
            {
                MinedPlanetObjectEffect fx = prefab != null ? prefab.GetComponentInChildren<MinedPlanetObjectEffect>(true) : null;
                IWorldEffectSpawner spawner = ServiceLocator.GetService<IWorldEffectSpawner>();
                if (fx == null || spawner == null) return;
                spawner.Spawn(new WorldEffectSpawnParams
                {
                    Space = new PlanetSurfaceSpace { Center = at, PlanetId = planet.Id },
                    Rotation = rot,
                    Scale = scale,
                    Effect = fx.worldEffect
                });
            }
            catch { }
        }

        /// <summary>Rises out of the ground with a little overshoot, kept in place on the planet as the world origin moves.</summary>
        internal class Grow : MonoBehaviour
        {
            public Planet Planet;
            public Vector3Double PlanetPos;
            public Quaternion PlanetRot;
            public Vector3 Target = Vector3.one;
            public float Delay;
            public Action Done;
            public bool FollowPlanet = true;      // false: something else places it (the game's own ore visuals)
            private float _t = -1f;
            private int _after = -1;

            private void Start() { transform.localScale = Vector3.zero; Place(); }

            private void Place()
            {
                if (!FollowPlanet || Planet == null) return;
                IWorldTransposeClient tr = ServiceLocator.GetService<IWorldTransposeClient>();
                if (tr == null || ClientOrigin.LocalVisualSpace == null) return;
                TransformOperation op = tr.ToTransformOperation(Planet.SurfaceSpace, ClientOrigin.LocalVisualSpace);
                transform.position = (Vector3)op.MultiplyPoint(PlanetPos);
                transform.rotation = (Quaternion)op.Rotation * PlanetRot;
            }

            private void LateUpdate()
            {
                Place();
                if (_after >= 0)
                {
                    // the real one is drawn by now: the stand-in goes
                    if (--_after <= 0) { if (FollowPlanet) Destroy(gameObject); else Destroy(this); }
                    return;
                }
                if (Delay > 0f) { Delay -= Time.deltaTime; return; }
                if (_t < 0f) _t = 0f;
                _t += Time.deltaTime / GrowSeconds;
                float k = EaseOutBack(Mathf.Clamp01(_t));
                // a slight sway as it settles
                float sway = Mathf.Sin(_t * 9f) * (1f - Mathf.Clamp01(_t)) * 4f;
                transform.localScale = Target * Mathf.Max(0f, k);
                if (FollowPlanet) transform.rotation = transform.rotation * Quaternion.Euler(sway, 0f, sway * 0.5f);
                if (_t < 1f) return;
                transform.localScale = Target;
                try { Done?.Invoke(); } catch { }
                _after = FollowPlanet ? 3 : 0;
            }

            private static float EaseOutBack(float x)
            {
                const float c1 = 1.70158f, c3 = c1 + 1f;
                return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
            }
        }

        // ---- ores and fixed resources ------------------------------------------------------------

        private static void ReloadOres(TrackedPlanetClient tp, List<ChunkObjectId> ids)
        {
            object vis = Traverse.Create(tp).Field("_hotspotsEnvironmentVisuals").GetValue();
            if (vis == null) return;
            foreach (ulong chunk in ids.Select(i => i.ChunkHash).Distinct())
                Traverse.Create(vis).Method("UnloadChunk", new[] { typeof(ulong) }).GetValue(chunk);
            // the chunk is loaded again on the planet's next update; then the deposits grow
            MelonCoroutines.Start(GrowOresLater(tp, vis, ids));
        }

        private static IEnumerator GrowOresLater(TrackedPlanetClient tp, object vis, List<ChunkObjectId> ids)
        {
            yield return null; yield return null;
            int n = 0;
            foreach (ChunkObjectId id in ids)
            {
                PlanetObjectVisuals v = null;
                try
                {
                    object[] args = { id, null };
                    bool ok = (bool)AccessTools.Method(vis.GetType(), "TryGetObjectVisuals").Invoke(vis, args);
                    v = ok ? args[1] as PlanetObjectVisuals : null;
                }
                catch { }
                if (v == null) { Instant++; continue; }
                GrowExisting(v.gameObject, tp.Planet, (Vector3Double)v.planetRelativePos, v.planetRelativeRot, n++);
                Grown++;
            }
        }

        private static void ReloadFixed(TrackedPlanetClient tp, List<int> ids)
        {
            object vis = Traverse.Create(tp).Field("_staticEnvironmentVisuals").GetValue();
            if (vis == null) return;
            MelonCoroutines.Start(ReloadFixedLater(tp, vis, ids));
        }

        private static IEnumerator ReloadFixedLater(TrackedPlanetClient tp, object vis, List<int> ids)
        {
            // the server's list change reaches this game on its own; wait for it, then place them again
            yield return new WaitForSecondsRealtime(0.5f);
            try
            {
                Traverse t = Traverse.Create(vis);
                if (t.Field("_isSpawned").GetValue<bool>()) { t.Method("DespawnObjects").GetValue(); t.Field("_isSpawned").SetValue(false); }
            }
            catch (Exception e) { Log.Once("fixed-reload", "respawn: fixed resources: " + e.Message); yield break; }
            yield return null; yield return null;
            int n = 0;
            foreach (int id in ids)
            {
                PlanetObjectVisuals v = null;
                try
                {
                    object[] args = { id, null };
                    bool ok = (bool)AccessTools.Method(vis.GetType(), "TryGetObjectVisuals").Invoke(vis, args);
                    v = ok ? args[1] as PlanetObjectVisuals : null;
                }
                catch { }
                if (v == null) continue;
                GrowExisting(v.gameObject, tp.Planet, (Vector3Double)v.planetRelativePos, v.planetRelativeRot, n++);
                Grown++;
            }
        }

        /// <summary>An object the game placed itself grows from nothing (the game keeps placing it).</summary>
        private static void GrowExisting(GameObject go, Planet planet, Vector3Double at, Quaternion rot, int n)
        {
            Grow g = go.AddComponent<Grow>();
            g.FollowPlanet = false;
            g.Target = go.transform.localScale;
            g.Delay = (n % 12) * 0.06f;
            Puff(go, planet, at, rot, go.transform.lossyScale.x);
        }

        private static IEnumerator RemaskRings(TrackedPlanetClient tp)
        {
            // after the server's list change arrives
            for (int i = 0; i < 2; i++)
            {
                yield return new WaitForSecondsRealtime(i == 0 ? 0.5f : 2f);
                try
                {
                    object rings = Traverse.Create(tp).Field("_ringAsteroidVisuals").GetValue();
                    IDictionary segs = rings != null ? Traverse.Create(rings).Field("_ringSegments").GetValue() as IDictionary : null;
                    if (segs == null) yield break;
                    foreach (object seg in segs.Values) Traverse.Create(seg).Method("UpdateDestroyedResourcesMask").GetValue();
                }
                catch (Exception e) { Log.Once("ring-remask", "respawn: asteroid ores: " + e.Message); yield break; }
            }
        }

        /// <summary>The local player's planet colliders are worked out again (so they can bump into what came back).</summary>
        private static void RefreshLocalColliders()
        {
            try
            {
                Managers.Client.WorldPhysicsClientUpdater u = UnityEngine.Object.FindFirstObjectByType<Managers.Client.WorldPhysicsClientUpdater>();
                object pc = u != null ? Traverse.Create(u).Field("_planetColliders").GetValue() : null;
                IDictionary passes = pc != null ? Traverse.Create(pc).Field("_generationPasses").GetValue() as IDictionary : null;
                if (passes != null) passes.Clear();
            }
            catch (Exception e) { Log.Once("respawn-local-colliders", "respawn: local colliders: " + e.Message); }
        }
    }
}
