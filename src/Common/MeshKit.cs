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
    using UnityEngine;

    /// <summary>
    /// Procedural low-poly meshes in the game's style: flat shaded, every face
    /// mapped onto a cell of an ink atlas (a light fill framed by a black line,
    /// optionally hatched), rendered with the game's own Custom/PlanetObject
    /// shader. The game's textures are grayscale hand-inked sheets and colour
    /// comes from a tint, so a tinted ink atlas sits right next to them.
    /// </summary>
    public class MeshKit
    {
        // Atlas cells (4x4 grid). Row 0 = outlined faces, row 1 = outlined and
        // hatched (shade side), row 2 = soft (no outline - curved surfaces get
        // their silhouette from the game's edge-detection pass), row 3 = flat
        // fills for small details.
        public const int CellPlain = 0, CellPlainAlt = 1, CellPanel = 2, CellRivet = 3;
        public const int CellHatch = 4, CellHatchDense = 5, CellHatchPanel = 6, CellDark = 7;
        public const int CellSoft = 8, CellSoftHatch = 9, CellSoftDark = 10, CellSoftLight = 11;
        public const int CellFillWhite = 12, CellFillMid = 13, CellFillDark = 14, CellFillBlack = 15;

        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Vector3> _n = new List<Vector3>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector2> _uv2 = new List<Vector2>();
        private readonly List<int> _t = new List<int>();

        public Matrix4x4 M = Matrix4x4.identity;

        /// <summary>
        /// The base colour of everything added from now on: a texel of the
        /// game's 32x32 ShipColorPalette, picked through the SECOND uv set
        /// exactly like every vanilla part mesh does (the albedo sheet is only
        /// grayscale ink). Without it the shader reads texel 0, which is black.
        /// </summary>
        public Vector2 Pal = Palette.Light;

        public int VertexCount { get { return _v.Count; } }

        // ---- cells ---------------------------------------------------------

        public static Rect Cell(int cell)
        {
            int cx = cell % 4, cy = cell / 4;
            const float pad = 0.004f;
            return new Rect(cx * 0.25f + pad, 1f - (cy + 1) * 0.25f + pad, 0.25f - 2 * pad, 0.25f - 2 * pad);
        }

        // ---- primitives ------------------------------------------------------

        /// <summary>One flat quad a-b-c-d (counter-clockwise seen from the front).</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int cell, Vector2? uvScale = null)
        {
            a = M.MultiplyPoint3x4(a); b = M.MultiplyPoint3x4(b); c = M.MultiplyPoint3x4(c); d = M.MultiplyPoint3x4(d);
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            if (n == Vector3.zero) n = Vector3.Cross(c - b, a - b).normalized;
            Rect r = Cell(cell);
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) { _n.Add(n); _uv2.Add(Pal); }
            _uv.Add(new Vector2(r.xMin, r.yMin)); _uv.Add(new Vector2(r.xMax, r.yMin));
            _uv.Add(new Vector2(r.xMax, r.yMax)); _uv.Add(new Vector2(r.xMin, r.yMax));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, int cell)
        {
            a = M.MultiplyPoint3x4(a); b = M.MultiplyPoint3x4(b); c = M.MultiplyPoint3x4(c);
            Vector3 cr = Vector3.Cross(b - a, c - a);
            if (cr.sqrMagnitude < 1e-16f) return;   // no area: nothing to draw, and no normal to give it
            Vector3 n = cr.normalized;
            Rect r = Cell(cell);
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            for (int k = 0; k < 3; k++) { _n.Add(n); _uv2.Add(Pal); }
            _uv.Add(new Vector2(r.xMin, r.yMin)); _uv.Add(new Vector2(r.xMax, r.yMin)); _uv.Add(new Vector2(r.center.x, r.yMax));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        /// <summary>Axis-aligned box. cells: +y, -y, +x, -x, +z, -z (or one for all).</summary>
        public void Box(Vector3 center, Vector3 size, params int[] cells)
        {
            Vector3 h = size * 0.5f;
            int top = CellAt(cells, 0, CellPlain), bot = CellAt(cells, 1, CellHatch);
            int px = CellAt(cells, 2, CellPlain), nx = CellAt(cells, 3, CellHatch);
            int pz = CellAt(cells, 4, CellPlain), nz = CellAt(cells, 5, CellPlainAlt);
            Vector3 c = center;
            Vector3 p000 = c + new Vector3(-h.x, -h.y, -h.z), p100 = c + new Vector3(h.x, -h.y, -h.z);
            Vector3 p010 = c + new Vector3(-h.x, h.y, -h.z), p110 = c + new Vector3(h.x, h.y, -h.z);
            Vector3 p001 = c + new Vector3(-h.x, -h.y, h.z), p101 = c + new Vector3(h.x, -h.y, h.z);
            Vector3 p011 = c + new Vector3(-h.x, h.y, h.z), p111 = c + new Vector3(h.x, h.y, h.z);
            Quad(p010, p011, p111, p110, top);      // +y
            Quad(p000, p100, p101, p001, bot);      // -y
            Quad(p100, p110, p111, p101, px);       // +x
            Quad(p001, p011, p010, p000, nx);       // -x
            Quad(p101, p111, p011, p001, pz);       // +z
            Quad(p000, p010, p110, p100, nz);       // -z
        }

        /// <summary>
        /// A box with every edge chamfered by <paramref name="bevel"/>: the
        /// game's parts are all softened like this, and the extra faces catch
        /// light the way their hand-painted highlights do.
        /// </summary>
        public void BevelBox(Vector3 center, Vector3 size, float bevel, int faceCell = CellPlain, int bevelCell = CellFillMid, int bottomCell = CellHatch)
        {
            Vector3 h = size * 0.5f;
            float b = Mathf.Min(bevel, Mathf.Min(h.x, Mathf.Min(h.y, h.z)) * 0.95f);
            Vector3 i = h - new Vector3(b, b, b);
            Vector3 c = center;
            // main faces (inset)
            Quad(c + new Vector3(-i.x, h.y, -i.z), c + new Vector3(-i.x, h.y, i.z), c + new Vector3(i.x, h.y, i.z), c + new Vector3(i.x, h.y, -i.z), faceCell);
            Quad(c + new Vector3(-i.x, -h.y, -i.z), c + new Vector3(i.x, -h.y, -i.z), c + new Vector3(i.x, -h.y, i.z), c + new Vector3(-i.x, -h.y, i.z), bottomCell);
            Quad(c + new Vector3(h.x, -i.y, -i.z), c + new Vector3(h.x, i.y, -i.z), c + new Vector3(h.x, i.y, i.z), c + new Vector3(h.x, -i.y, i.z), faceCell);
            Quad(c + new Vector3(-h.x, -i.y, i.z), c + new Vector3(-h.x, i.y, i.z), c + new Vector3(-h.x, i.y, -i.z), c + new Vector3(-h.x, -i.y, -i.z), CellHatch);
            Quad(c + new Vector3(i.x, -i.y, h.z), c + new Vector3(i.x, i.y, h.z), c + new Vector3(-i.x, i.y, h.z), c + new Vector3(-i.x, -i.y, h.z), faceCell);
            Quad(c + new Vector3(-i.x, -i.y, -h.z), c + new Vector3(-i.x, i.y, -h.z), c + new Vector3(i.x, i.y, -h.z), c + new Vector3(i.x, -i.y, -h.z), faceCell);
            // 12 edge bevels
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    // edges along z
                    Vector3 a = c + new Vector3(sx * i.x, sy * h.y, -i.z), bb = c + new Vector3(sx * i.x, sy * h.y, i.z);
                    Vector3 cc = c + new Vector3(sx * h.x, sy * i.y, i.z), d = c + new Vector3(sx * h.x, sy * i.y, -i.z);
                    if (sx * sy > 0) Quad(a, bb, cc, d, bevelCell); else Quad(a, d, cc, bb, bevelCell);
                }
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    // edges along y
                    Vector3 a = c + new Vector3(sx * i.x, -i.y, sz * h.z), bb = c + new Vector3(sx * i.x, i.y, sz * h.z);
                    Vector3 cc = c + new Vector3(sx * h.x, i.y, sz * i.z), d = c + new Vector3(sx * h.x, -i.y, sz * i.z);
                    if (sx * sz < 0) Quad(a, bb, cc, d, bevelCell); else Quad(a, d, cc, bb, bevelCell);
                }
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    // edges along x
                    Vector3 a = c + new Vector3(-i.x, sy * i.y, sz * h.z), bb = c + new Vector3(i.x, sy * i.y, sz * h.z);
                    Vector3 cc = c + new Vector3(i.x, sy * h.y, sz * i.z), d = c + new Vector3(-i.x, sy * h.y, sz * i.z);
                    if (sy * sz > 0) Quad(a, bb, cc, d, bevelCell); else Quad(a, d, cc, bb, bevelCell);
                }
            // 8 corners
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 px = c + new Vector3(sx * h.x, sy * i.y, sz * i.z);
                        Vector3 py = c + new Vector3(sx * i.x, sy * h.y, sz * i.z);
                        Vector3 pz = c + new Vector3(sx * i.x, sy * i.y, sz * h.z);
                        if (sx * sy * sz > 0) Tri(px, py, pz, bevelCell); else Tri(px, pz, py, bevelCell);
                    }
        }

        /// <summary>
        /// Solid of revolution around +y. profile = (radius, height) from the
        /// bottom up. Each ring band is one face row; smooth=true shares a UV
        /// strip (no outline per facet) for round things.
        /// </summary>
        public void Lathe(Vector3 center, IList<Vector2> profile, int segments, int cell = CellSoft, bool capBottom = true, bool capTop = true, float phase = 0f)
        {
            Rect r = Cell(cell);
            for (int s = 0; s < segments; s++)
            {
                float a0 = phase + s * Mathf.PI * 2f / segments, a1 = phase + (s + 1) * Mathf.PI * 2f / segments;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                for (int k = 0; k < profile.Count - 1; k++)
                {
                    Vector2 p = profile[k], q = profile[k + 1];
                    Vector3 a = center + d0 * p.x + Vector3.up * p.y;
                    Vector3 b = center + d1 * p.x + Vector3.up * p.y;
                    Vector3 cc = center + d1 * q.x + Vector3.up * q.y;
                    Vector3 d = center + d0 * q.x + Vector3.up * q.y;
                    if (p.x < 1e-5f) Tri(b, d, cc, cell);
                    else if (q.x < 1e-5f) Tri(a, d, b, cell);
                    else
                    {
                        // band quad, facing outward: a(b) bottom, d(c) top
                        QuadUV(a, d, cc, b, cell, (float)s / segments, (float)(s + 1) / segments, (float)k / (profile.Count - 1), (float)(k + 1) / (profile.Count - 1));
                    }
                }
                if (capBottom && profile[0].x > 1e-5f)
                {
                    Vector3 cb = center + Vector3.up * profile[0].y;
                    Tri(cb, center + d0 * profile[0].x + Vector3.up * profile[0].y, center + d1 * profile[0].x + Vector3.up * profile[0].y, CellHatch);
                }
                Vector2 top = profile[profile.Count - 1];
                if (capTop && top.x > 1e-5f)
                {
                    Vector3 ct = center + Vector3.up * top.y;
                    Tri(ct, center + d1 * top.x + Vector3.up * top.y, center + d0 * top.x + Vector3.up * top.y, CellSoftLight);
                }
            }
        }

        // A quad whose UVs are a sub-rectangle of a cell, for continuous strips.
        private void QuadUV(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int cell, float u0, float u1, float v0, float v1)
        {
            a = M.MultiplyPoint3x4(a); b = M.MultiplyPoint3x4(b); c = M.MultiplyPoint3x4(c); d = M.MultiplyPoint3x4(d);
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            if (n == Vector3.zero) n = Vector3.Cross(c - b, a - b).normalized;
            Rect r = Cell(cell);
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) { _n.Add(n); _uv2.Add(Pal); }
            _uv.Add(new Vector2(Mathf.Lerp(r.xMin, r.xMax, u0), Mathf.Lerp(r.yMin, r.yMax, v0)));
            _uv.Add(new Vector2(Mathf.Lerp(r.xMin, r.xMax, u0), Mathf.Lerp(r.yMin, r.yMax, v1)));
            _uv.Add(new Vector2(Mathf.Lerp(r.xMin, r.xMax, u1), Mathf.Lerp(r.yMin, r.yMax, v1)));
            _uv.Add(new Vector2(Mathf.Lerp(r.xMin, r.xMax, u1), Mathf.Lerp(r.yMin, r.yMax, v0)));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        /// <summary>
        /// Extrude a convex-or-star-shaped 2D outline (x,y in the XY plane,
        /// counter-clockwise) along z from z0 to z1. The front (+z) and back
        /// caps are fanned from the centroid.
        /// </summary>
        public void Extrude(IList<Vector2> outline, float z0, float z1, int capCell = CellPlain, int sideCell = CellFillMid, int backCell = CellHatch)
        {
            Vector2 cen = Vector2.zero;
            foreach (Vector2 p in outline) cen += p;
            cen /= outline.Count;
            int n = outline.Count;
            for (int k = 0; k < n; k++)
            {
                Vector2 p = outline[k], q = outline[(k + 1) % n];
                // front cap (+z faces +z)
                TriUVPlanar(new Vector3(cen.x, cen.y, z1), new Vector3(p.x, p.y, z1), new Vector3(q.x, q.y, z1), capCell, outline);
                // back cap
                TriUVPlanar(new Vector3(cen.x, cen.y, z0), new Vector3(q.x, q.y, z0), new Vector3(p.x, p.y, z0), backCell, outline);
                // side
                Quad(new Vector3(p.x, p.y, z0), new Vector3(q.x, q.y, z0), new Vector3(q.x, q.y, z1), new Vector3(p.x, p.y, z1), sideCell);
            }
        }

        // A triangle whose UVs come from its XY position inside the outline's
        // bounding box, so a cap made of many fan triangles reads as ONE face
        // with one border drawn around the whole shape's box.
        private void TriUVPlanar(Vector3 a, Vector3 b, Vector3 c, int cell, IList<Vector2> outline)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector2 p in outline) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            Rect r = Cell(cell);
            Func<Vector3, Vector2> uv = p => new Vector2(
                Mathf.Lerp(r.xMin, r.xMax, Mathf.InverseLerp(min.x, max.x, p.x)),
                Mathf.Lerp(r.yMin, r.yMax, Mathf.InverseLerp(min.y, max.y, p.y)));
            Vector3 wa = M.MultiplyPoint3x4(a), wb = M.MultiplyPoint3x4(b), wc = M.MultiplyPoint3x4(c);
            Vector3 n = Vector3.Cross(wb - wa, wc - wa).normalized;
            int i = _v.Count;
            _v.Add(wa); _v.Add(wb); _v.Add(wc);
            for (int k = 0; k < 3; k++) { _n.Add(n); _uv2.Add(Pal); }
            _uv.Add(uv(a)); _uv.Add(uv(b)); _uv.Add(uv(c));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        public static List<Vector2> RoundedTopSlab(float width, float height, float archSegments = 8)
        {
            // A headstone outline: square bottom, semicircular top.
            List<Vector2> o = new List<Vector2>();
            float r = width * 0.5f, straight = Mathf.Max(0f, height - r);
            o.Add(new Vector2(-r, 0));
            o.Add(new Vector2(r, 0));
            o.Add(new Vector2(r, straight));
            for (int k = 1; k < archSegments; k++)
            {
                float a = Mathf.PI * k / archSegments;
                o.Add(new Vector2(Mathf.Cos(a) * r, straight + Mathf.Sin(a) * r));
            }
            o.Add(new Vector2(-r, straight));
            return o;
        }

        // ---- output ----------------------------------------------------------

        public Mesh ToMesh(string name)
        {
            Mesh m = new Mesh();
            m.name = name;
            if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(_v);
            // a normal of no length is fine for the game's own shader but turns the standard lit
            // shader (the X-ray's see-through look) into NaN - which the bloom blows up into a
            // blinding disc
            for (int k = 0; k < _n.Count; k++) if (_n[k].sqrMagnitude < 1e-12f || float.IsNaN(_n[k].x)) _n[k] = Vector3.up;
            m.SetNormals(_n);
            m.SetUVs(0, _uv);
            m.SetUVs(1, _uv2);
            m.SetTriangles(_t, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            m.UploadMeshData(false);
            return m;
        }

        public GameObject ToObject(string name, Transform parent, Material mat, int layer = 0)
        {
            GameObject go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = ToMesh(name);
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        private static int CellAt(int[] cells, int i, int dflt)
        {
            if (cells == null || cells.Length == 0) return dflt;
            if (cells.Length == 1) return cells[0];
            return i < cells.Length ? cells[i] : dflt;
        }
    }

    /// <summary>
    /// The game's base colours: row 0 of ShipColorPalette, addressed through a
    /// mesh's second uv set (u = (texel + 0.5) / 32, v = 0.5 / 32).
    /// </summary>
    public static class Palette
    {
        public static readonly Vector2 Gray = P(1), Dark = P(2), Light = P(3), Red = P(4), Yellow = P(5);
        public static readonly Vector2 Green = P(6), Blue = P(7), Beige = P(8), Brown = P(9), Orange = P(10);
        private static Vector2 P(int texel) { return new Vector2((texel + 0.5f) / 32f, 0.5f / 32f); }
    }

    /// <summary>
    /// Materials in the game's look, built from its own shader, and the ink
    /// atlas they sample.
    /// </summary>
    public static class Looks
    {
        private static Texture2D _atlas;
        private static Material _base;
        private static readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();

        /// <summary>
        /// A Custom/PlanetObject material from any vanilla part, with our
        /// atlas as albedo and the given tint. Returns a URP Lit fallback if the
        /// game shader cannot be found (never null).
        /// </summary>
        public static Material Get(string key, Color tint, float roughness = 1f, float metalness = 0f, Color? emission = null)
        {
            Material m;
            if (_cache.TryGetValue(key, out m) && m != null) return m;
            Material b = BaseMaterial();
            if (b != null)
            {
                m = new Material(b);
                m.name = "SDMod_" + key;
                m.DisableKeyword("SHIP_COLOR_PALETTE");
                if (m.HasProperty("_Albedo")) m.SetTexture("_Albedo", Atlas());
                if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
                if (m.HasProperty("_Roughness")) m.SetFloat("_Roughness", roughness);
                if (m.HasProperty("_Metalness")) m.SetFloat("_Metalness", metalness);
                if (emission.HasValue && m.HasProperty("_EmissionColor"))
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", emission.Value);
                }
            }
            else
            {
                Shader s = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(s);
                m.name = "SDMod_" + key + "_fallback";
                m.SetTexture("_BaseMap", Atlas());
                m.SetColor("_BaseColor", tint);
            }
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _cache[key] = m;
            return m;
        }

        public static Material BaseMaterial()
        {
            if (_base != null) return _base;
            foreach (Material mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null || mat.shader == null || mat.shader.name != "Custom/PlanetObject") continue;
                if (mat.IsKeywordEnabled("SHIP_COLOR_PALETTE") || mat.IsKeywordEnabled("_EMISSION")) continue;
                if (!mat.HasProperty("_Albedo")) continue;
                _base = mat;
                if (mat.name == "StorageChest" || mat.name == "Beacon") break;
            }
            return _base;
        }

        /// <summary>
        /// 1024² grayscale ink atlas, 4x4 cells (see MeshKit.Cell*): light
        /// fills framed by a hand-wobbled black line, hatched variants, soft
        /// unframed fills, and flat swatches. Drawn once per session.
        /// </summary>
        public static Texture2D Atlas()
        {
            if (_atlas != null) return _atlas;
            const int S = 1024, C = S / 4;
            Color32[] px = new Color32[S * S];
            System.Random rng = new System.Random(1234);
            for (int cell = 0; cell < 16; cell++)
            {
                int cx = cell % 4, cy = cell / 4;
                int ox = cx * C, oy = S - (cy + 1) * C;
                float baseV;
                bool outline = cy <= 1, hatch = false, dense = false, panel = false, rivets = false;
                switch (cell)
                {
                    case MeshKit.CellPlain: baseV = 0.86f; break;
                    case MeshKit.CellPlainAlt: baseV = 0.80f; break;
                    case MeshKit.CellPanel: baseV = 0.84f; panel = true; break;
                    case MeshKit.CellRivet: baseV = 0.84f; rivets = true; break;
                    case MeshKit.CellHatch: baseV = 0.74f; hatch = true; break;
                    case MeshKit.CellHatchDense: baseV = 0.66f; hatch = true; dense = true; break;
                    case MeshKit.CellHatchPanel: baseV = 0.74f; hatch = true; panel = true; break;
                    case MeshKit.CellDark: baseV = 0.42f; hatch = true; break;
                    case MeshKit.CellSoft: baseV = 0.86f; break;
                    case MeshKit.CellSoftHatch: baseV = 0.72f; hatch = true; break;
                    case MeshKit.CellSoftDark: baseV = 0.45f; break;
                    case MeshKit.CellSoftLight: baseV = 0.95f; break;
                    case MeshKit.CellFillWhite: baseV = 0.97f; break;
                    case MeshKit.CellFillMid: baseV = 0.72f; break;
                    case MeshKit.CellFillDark: baseV = 0.35f; break;
                    default: baseV = 0.04f; break;
                }
                // low-frequency brush variation, like the painted sheets
                float n1 = (float)rng.NextDouble() * 10f, n2 = (float)rng.NextDouble() * 10f;
                for (int y = 0; y < C; y++)
                    for (int x = 0; x < C; x++)
                    {
                        float u = x / (float)C, v = y / (float)C;
                        float val = baseV;
                        if (cell < 12) val += (Mathf.PerlinNoise(n1 + u * 3f, n2 + v * 3f) - 0.5f) * 0.07f + (v - 0.5f) * 0.04f;
                        if (hatch)
                        {
                            // diagonal hatching, strongest toward the bottom-right
                            float period = dense ? 9f : 13f;
                            float d = (x + y) % period;
                            float strength = Mathf.Clamp01(1.2f - v * 1.1f + u * 0.3f);
                            if (d < 2.2f && strength > 0.35f && Mathf.PerlinNoise(n2 + x * 0.05f, n1 + y * 0.05f) > 0.3f) val = Mathf.Min(val, 0.18f);
                        }
                        if (panel)
                        {
                            // an inner panel seam
                            int m = C / 6;
                            bool seam = (Mathf.Abs(x - m) < 2 || Mathf.Abs(x - (C - m)) < 2) && y > m && y < C - m
                                     || (Mathf.Abs(y - m) < 2 || Mathf.Abs(y - (C - m)) < 2) && x > m && x < C - m;
                            if (seam) val = 0.12f;
                        }
                        if (rivets)
                        {
                            int m = C / 7;
                            foreach (int rx in new[] { m, C - m })
                                foreach (int ry in new[] { m, C - m })
                                {
                                    float dd = Mathf.Sqrt((x - rx) * (x - rx) + (y - ry) * (y - ry));
                                    if (dd < 9f) val = dd > 6f ? 0.1f : 0.93f;
                                }
                        }
                        if (outline)
                        {
                            // a slightly wobbly ink border, thicker at the bottom like a drop shadow
                            float wob = Mathf.PerlinNoise(n1 + (x + y) * 0.02f, n2) * 3f;
                            float left = 5f + wob, right = 5f + wob, top = 5f + wob, bottom = 8f + wob;
                            if (x < left || x > C - 1 - right || y < bottom || y > C - 1 - top) val = 0.03f;
                        }
                        byte g = (byte)(Mathf.Clamp01(val) * 255f);
                        px[(oy + y) * S + ox + x] = new Color32(g, g, g, 255);
                    }
            }
            _atlas = new Texture2D(S, S, TextureFormat.RGBA32, true);
            _atlas.name = "SDMod_InkAtlas";
            _atlas.SetPixels32(px);
            _atlas.wrapMode = TextureWrapMode.Clamp;
            _atlas.filterMode = FilterMode.Trilinear;
            _atlas.anisoLevel = 4;
            _atlas.Apply(true, false);
            _atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _atlas;
        }

        /// <summary>A texture drawn by a callback (e.g. a chess board top).</summary>
        public static Texture2D Paint(string name, int w, int h, Func<int, int, Color> fn)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.name = name;
            Color[] px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = fn(x, y);
            t.SetPixels(px);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Trilinear;
            t.Apply(true, false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return t;
        }

        /// <summary>A material like Get() but with its own albedo texture.</summary>
        public static Material WithTexture(string key, Texture2D tex, Color tint, float roughness = 1f)
        {
            Material m;
            if (_cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Get("base-" + key, tint, roughness));
            m.name = "SDMod_" + key;
            if (m.HasProperty("_Albedo")) m.SetTexture("_Albedo", tex);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _cache[key] = m;
            return m;
        }
    }
}
