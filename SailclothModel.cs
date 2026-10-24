using System.Collections.Generic;
using UnityEngine;

namespace SailDamage
{
    // The sailcloth's own look (Кэп, 0.5.2: the vanilla wool bale is everywhere already): a stack of folded plain grey
    // canvas tied crosswise with a dark rope, like a bundle of fabric from a shop. Few polygons; folds, the creases
    // where the rope bites in and the weave come from generated textures, laid on by the UVs.
    internal static class SailclothModel
    {
        internal const float Length = 0.40f;   // along X
        internal const float Depth = 0.27f;    // along Z, folded edges at +Z
        private const int Layers = 5;
        private const float LayerT = 0.038f;
        internal static float Height => Layers * LayerT;

        // Both ropes run vertically round the bundle, crossing on top (Кэп, 0.5.5: a parcel tie, not a belt round
        // the middle): one across it at RopeX, one along it at RopeZ.
        private const float RopeX = 0.03f;
        private const float RopeZ = -0.01f;
        private const float RopeR = 0.0055f;
        private const float RopeSink = RopeR * 0.4f;
        private const float AcrossCorner = 0.02f, AlongCorner = 0.025f;
        private const float DentV = 0.07f;      // how much the across rope pinches the stack
        private const float DentH = 0.035f;     // and the one along it
        private const float DentWidth = 0.022f;
        private static readonly float[] DentSteps = { -2.2f, -1.2f, -0.5f, 0f, 0.5f, 1.2f, 2.2f };

        private static readonly Color ClothColor = new Color(0.82f, 0.81f, 0.78f);
        private static readonly Color RopeColor = new Color(0.32f, 0.23f, 0.16f);

        // v of the side texture by part of the layer's outline: bottom, folded front, top, back.
        private const float VFrontStart = 0.30f, VFrontEnd = 0.55f, VBackStart = 0.85f;

        private static Texture2D sideTex, capTex;

        internal static void Apply(GameObject go, Material baseMat)
        {
            for (int i = go.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(go.transform.GetChild(i).gameObject);

            MeshFilter mf = go.GetComponent<MeshFilter>() ?? go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.GetComponent<MeshRenderer>() ?? go.AddComponent<MeshRenderer>();
            // ShipItem requires a Collider, so the source's box cannot be removed (Unity refuses): reuse it. A second,
            // added box stayed solid (ShipItem.Awake makes only the first one a trigger) and fought the item's body
            // (Кэп, 0.5.3: the bolt flew off and threw the boat).
            BoxCollider box = go.GetComponent<BoxCollider>() ?? go.AddComponent<BoxCollider>();
            foreach (Collider c in go.GetComponents<Collider>())
                if (c != box)
                    Object.DestroyImmediate(c);
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(Length, Height, Depth + 0.01f);

            mf.sharedMesh = ClothMesh();
            mr.sharedMaterials = new[] { MakeMat(baseMat, ClothColor, SideTexture()), MakeMat(baseMat, ClothColor, CapTexture()) };

            var rope = new GameObject("sailcloth rope");
            rope.layer = go.layer;
            rope.transform.SetParent(go.transform, false);
            rope.AddComponent<MeshFilter>().sharedMesh = RopeMesh();
            Material ropeMat = VanillaRopeMaterial();
            Material rm = ropeMat != null ? new Material(ropeMat) : MakeMat(baseMat, RopeColor, null);
            if (rm.HasProperty("_Color"))
                rm.color = RopeColor;
            rm.name = "sailcloth rope";
            rope.AddComponent<MeshRenderer>().sharedMaterial = rm;
            Plugin.Log.LogInfo($"SAILCLOTH model: {mf.sharedMesh.vertexCount} cloth verts, rope material '{(ropeMat != null ? ropeMat.name : "none, plain")}'");
        }

        // ------------------------------------------------------------ cloth

        // How the ropes pinch the bundle at a point.
        private static Vector3 Pinch(Vector3 p)
        {
            float dx = (p.x - RopeX) / DentWidth;
            float v = DentV * Mathf.Exp(-dx * dx);
            float dz = (p.z - RopeZ) / DentWidth;
            float h = DentH * Mathf.Exp(-dz * dz);
            var q = new Vector3(p.x * (1f - h), p.y * (1f - v) * (1f - h), p.z * (1f - v));

            // Under the ropes the cloth is pulled inside their inner edge, so no fold, shifted layer or square corner
            // pokes through them (Кэп, 0.5.3: the rope clipped at the side). Flat-topped weights, so the rings next
            // to the rope are pulled in too.
            float wx = Mathf.Exp(-dx * dx * dx * dx);
            Vector2 yz = Inside(new Vector2(q.z, q.y), Depth / 2f * (1f - DentV), Height / 2f * (1f - DentV), AcrossCorner - RopeR + RopeSink);
            q.z = Mathf.Lerp(q.z, yz.x, wx);
            q.y = Mathf.Lerp(q.y, yz.y, wx);
            float wz = Mathf.Exp(-dz * dz * dz * dz);
            Vector2 xy = Inside(new Vector2(q.x, q.y), Length / 2f * (1f - DentH), Height / 2f * (1f - DentH), AlongCorner - RopeR + RopeSink);
            q.x = Mathf.Lerp(q.x, xy.x, wz);
            q.y = Mathf.Lerp(q.y, xy.y, wz);
            return q;
        }

        // A point pulled inside a rounded rectangle of half sizes (ha, hb) and corner radius r.
        private static Vector2 Inside(Vector2 p, float ha, float hb, float r)
        {
            r = Mathf.Max(r, 0.001f);
            var q = new Vector2(Mathf.Clamp(p.x, -ha + r, ha - r), Mathf.Clamp(p.y, -hb + r, hb - r));
            Vector2 d = p - q;
            return d.magnitude > r ? q + d.normalized * r : p;
        }

        // One layer's outline in (z, y) with its texture v; folded round at the front (+z), nearly square at the back.
        private static void Outline(float depth, float t, List<Vector2> pts, List<float> vs)
        {
            float zf = depth / 2f - t / 2f, zb = -depth / 2f, rb = 0.005f, h = t / 2f;
            // bottom: back to front
            // Top and bottom get points across the rope along the bundle, so it can press in.
            float z0 = zb + rb;
            pts.Add(new Vector2(z0, -h)); vs.Add(0f);
            foreach (float d in DentSteps)
            {
                float z = RopeZ + d * DentWidth;
                if (z > z0 && z < zf)
                {
                    pts.Add(new Vector2(z, -h)); vs.Add((z - z0) / (zf - z0) * VFrontStart);
                }
            }
            pts.Add(new Vector2(zf, -h)); vs.Add(VFrontStart);
            // folded front
            const int Fold = 8;
            for (int i = 1; i < Fold; i++)
            {
                float a = -Mathf.PI / 2f + Mathf.PI * i / Fold;
                pts.Add(new Vector2(zf + Mathf.Cos(a) * h, Mathf.Sin(a) * h));
                vs.Add(Mathf.Lerp(VFrontStart, VFrontEnd, i / (float)Fold));
            }
            // top: front to back
            pts.Add(new Vector2(zf, h)); vs.Add(VFrontEnd);
            for (int i = DentSteps.Length - 1; i >= 0; i--)
            {
                float z = RopeZ + DentSteps[i] * DentWidth;
                if (z > z0 && z < zf)
                {
                    pts.Add(new Vector2(z, h)); vs.Add(VFrontEnd + (zf - z) / (zf - z0) * (VBackStart - VFrontEnd));
                }
            }
            pts.Add(new Vector2(z0, h)); vs.Add(VBackStart);
            // back
            pts.Add(new Vector2(zb, h - rb)); vs.Add(VBackStart + 0.03f);
            // points down the back, so the rope round the middle can press into it
            foreach (float k in new[] { 0.4f, 0f, -0.4f })
            {
                pts.Add(new Vector2(zb, h * k)); vs.Add(Mathf.Lerp(VBackStart + 0.03f, 0.97f, (1f - k) / 2f));
            }
            pts.Add(new Vector2(zb, -h + rb)); vs.Add(0.97f);
            pts.Add(new Vector2(zb + rb, -h)); vs.Add(1f);
        }

        private static Mesh ClothMesh()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var sides = new List<int>();
            var caps = new List<int>();
            // Rings along the length, closer together where the rope bites.
            var xs = new List<float>();
            for (int i = 0; i <= 10; i++)
                xs.Add(-Length / 2f + Length * i / 10f);
            foreach (float d in DentSteps)
                xs.Add(RopeX + d * DentWidth);
            xs.Sort();

            var rnd = new System.Random(7);
            for (int k = 0; k < Layers; k++)
            {
                // Layers are not stacked dead even.
                float depth = Depth - 0.012f * (float)rnd.NextDouble();
                float shiftZ = 0.006f * ((float)rnd.NextDouble() - 0.5f);
                float shiftX = 0.008f * ((float)rnd.NextDouble() - 0.5f);
                float y0 = -Height / 2f + LayerT * (k + 0.5f);
                var outline = new List<Vector2>();
                var vs = new List<float>();
                Outline(depth, LayerT * 0.98f, outline, vs);
                int n = outline.Count;

                int start = verts.Count;
                foreach (float x in xs)
                    for (int j = 0; j < n; j++)
                    {
                        verts.Add(Pinch(new Vector3(x + shiftX, y0 + outline[j].y, outline[j].x + shiftZ)));
                        uvs.Add(new Vector2((x + Length / 2f) / Length, vs[j]));
                    }
                for (int r = 0; r < xs.Count - 1; r++)
                    for (int j = 0; j < n - 1; j++)
                    {
                        int a = start + r * n + j, b = a + 1, c = a + n, d = c + 1;
                        sides.Add(a); sides.Add(c); sides.Add(b);
                        sides.Add(b); sides.Add(c); sides.Add(d);
                    }

                // End caps: a fan with planar UVs over the layer's outline, centred under the rope along the bundle
                // so the whole strip under it is pulled in.
                for (int end = 0; end < 2; end++)
                {
                    float x = end == 0 ? xs[0] : xs[xs.Count - 1];
                    int centre = verts.Count;
                    verts.Add(Pinch(new Vector3(x + shiftX, y0, RopeZ)));
                    uvs.Add(new Vector2((RopeZ - shiftZ) / Depth + 0.5f, 0.5f));
                    for (int j = 0; j < n; j++)
                    {
                        verts.Add(Pinch(new Vector3(x + shiftX, y0 + outline[j].y, outline[j].x + shiftZ)));
                        uvs.Add(new Vector2(outline[j].x / Depth + 0.5f, outline[j].y / LayerT + 0.5f));
                    }
                    for (int j = 0; j < n - 1; j++)
                    {
                        if (end == 0) { caps.Add(centre); caps.Add(centre + 1 + j); caps.Add(centre + 2 + j); }
                        else { caps.Add(centre); caps.Add(centre + 2 + j); caps.Add(centre + 1 + j); }
                    }
                }
            }

            var mesh = new Mesh { name = "sailcloth bundle" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sides, 0);
            mesh.SetTriangles(caps, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------ rope

        private static Mesh RopeMesh()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float sink = RopeSink;

            // Across the bundle: round the (y, z) outline of the pinched stack at RopeX.
            var across = new List<Vector3>();
            float hy = Height / 2f * (1f - DentV) + RopeR - sink, hz = Depth / 2f * (1f - DentV) + RopeR - sink;
            RoundRect(across, hz, hy, AcrossCorner,(a, b) => new Vector3(RopeX, b, a));
            Tube(across, verts, uvs, tris);

            // Along the bundle: round its (x, y) outline at RopeZ, lying over the across rope where they cross.
            var along = new List<Vector3>();
            float hx = Length / 2f * (1f - DentH) + RopeR - sink, hy2 = Height / 2f * (1f - DentH) + RopeR - sink;
            RoundRect(along, hx, hy2, AlongCorner, (a, b) => new Vector3(a, b, RopeZ));
            along = Subdivide(along, RopeR);
            float over = Mathf.Max(0f, hy + RopeR * 1.9f - hy2);
            for (int i = 0; i < along.Count; i++)
            {
                Vector3 p = along[i];
                float k = (p.x - RopeX) / (RopeR * 3f);
                p.y += Mathf.Sign(p.y) * over * Mathf.Exp(-k * k) * Mathf.Clamp01((Mathf.Abs(p.y) - hy2 * 0.8f) / (hy2 * 0.2f));
                along[i] = p;
            }
            Tube(along, verts, uvs, tris);

            // The knot on top where they cross: a coil round the crossing, and two cut ends lying on the cloth
            // (Кэп, 0.5.5: the ring of 0.5.4 did not read as a knot).
            float top = hy2 + over;
            var centre = new Vector3(RopeX, top + RopeR * 0.6f, RopeZ);
            Vector3 axis = new Vector3(1f, 0f, -1f).normalized, side = Vector3.Cross(axis, Vector3.up);
            const float CoilR = 0.006f, CoilLength = 0.014f, Turns = 1.5f;
            var coil = new List<Vector3>();
            for (int i = 0; i <= 24; i++)
            {
                float t = i / 24f, a = t * Turns * Mathf.PI * 2f;
                coil.Add(centre + axis * (CoilLength * (t - 0.5f)) + (Vector3.up * Mathf.Cos(a) + side * Mathf.Sin(a)) * CoilR);
            }
            OpenTube(coil, 1f, 1f, verts, uvs, tris);

            float rest = Height / 2f + RopeR * 0.8f;
            for (int e = 0; e < 2; e++)
            {
                Vector3 dir = e == 0 ? axis : -axis;
                Vector3 from = e == 0 ? coil[coil.Count - 1] : coil[0];
                float length = e == 0 ? 0.034f : 0.027f, bend = e == 0 ? 0.006f : -0.005f;
                var tail = new List<Vector3>();
                for (int i = 0; i <= 10; i++)
                {
                    float t = i / 10f;
                    Vector3 p = from + dir * (0.004f + length * t) + side * (bend * Mathf.Sin(t * Mathf.PI * 0.8f));
                    p.y = Mathf.Lerp(from.y, rest, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2.2f)));
                    tail.Add(p);
                }
                tail.Insert(0, from);
                OpenTube(tail, 1f, 0.8f, verts, uvs, tris);
            }

            var mesh = new Mesh { name = "sailcloth rope" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void RoundRect(List<Vector3> path, float ha, float hb, float r, System.Func<float, float, Vector3> to)
        {
            const int Corner = 5;
            var centres = new[] { new Vector2(ha - r, hb - r), new Vector2(-ha + r, hb - r), new Vector2(-ha + r, -hb + r), new Vector2(ha - r, -hb + r) };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i <= Corner; i++)
                {
                    float a = (c + i / (float)Corner) * Mathf.PI / 2f;
                    path.Add(to(centres[c].x + Mathf.Cos(a) * r, centres[c].y + Mathf.Sin(a) * r));
                }
            path.Add(path[0]);
        }

        // The path with no step longer than max, so a bend can be laid on its straight runs.
        private static List<Vector3> Subdivide(List<Vector3> path, float max)
        {
            var result = new List<Vector3> { path[0] };
            for (int i = 1; i < path.Count; i++)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt((path[i] - path[i - 1]).magnitude / max));
                for (int s = 1; s <= steps; s++)
                    result.Add(Vector3.Lerp(path[i - 1], path[i], s / (float)steps));
            }
            return result;
        }

        // A rope piece with two ends: the frame is carried along the path (no twist where it turns upright), the
        // radius goes from r0 to r1 times RopeR, and the end is closed with a short cone.
        private static void OpenTube(List<Vector3> path, float r0, float r1, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            const int Sides = 6;
            int start = verts.Count;
            float along = 0f;
            Vector3 side = Vector3.zero;
            int count = path.Count + 1;
            for (int i = 0; i < count; i++)
            {
                int k = Mathf.Min(i, path.Count - 1);
                Vector3 dir = (path[Mathf.Min(k + 1, path.Count - 1)] - path[Mathf.Max(k - 1, 0)]).normalized;
                if (i == 0)
                    side = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                else
                    side = Vector3.ProjectOnPlane(side, dir).normalized;
                Vector3 up = Vector3.Cross(side, dir);
                Vector3 p = path[k];
                float r;
                if (i == count - 1)
                {
                    p += dir * RopeR * 0.4f;
                    r = 0f;
                }
                else
                {
                    if (i > 0)
                        along += (path[k] - path[k - 1]).magnitude;
                    r = Mathf.Lerp(r0, r1, k / (float)Mathf.Max(1, path.Count - 1)) * RopeR;
                }
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s / (float)Sides * Mathf.PI * 2f;
                    verts.Add(p + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * r);
                    uvs.Add(new Vector2(along / (RopeR * 12f), s / (float)Sides));
                }
            }
            for (int i = 0; i < count - 1; i++)
                for (int s = 0; s < Sides; s++)
                {
                    int a = start + i * (Sides + 1) + s, b = a + 1, c = a + Sides + 1, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
        }

        private static void Tube(List<Vector3> path, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            const int Sides = 6;
            int start = verts.Count;
            float along = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 prev = path[Mathf.Max(0, i - 1)], next = path[Mathf.Min(path.Count - 1, i + 1)];
                if (i == 0 || i == path.Count - 1)
                {
                    prev = path[path.Count - 2];
                    next = path[1];
                }
                Vector3 dir = (next - prev).normalized;
                Vector3 side = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 up = Vector3.Cross(side, dir);
                if (i > 0)
                    along += (path[i] - path[i - 1]).magnitude;
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s / (float)Sides * Mathf.PI * 2f;
                    verts.Add(path[i] + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * RopeR);
                    uvs.Add(new Vector2(along / (RopeR * 12f), s / (float)Sides));
                }
            }
            for (int i = 0; i < path.Count - 1; i++)
                for (int s = 0; s < Sides; s++)
                {
                    int a = start + i * (Sides + 1) + s, b = a + 1, c = a + Sides + 1, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
        }

        // ------------------------------------------------------------ textures

        // Sides: u along the bundle, v round one layer (bottom, folded front, top, back). Weave, the crease lines where
        // the fold turns, soft lengthwise folds, and shading with creases fanning out from the rope.
        private static Texture2D SideTexture()
        {
            if (sideTex != null)
                return sideTex;
            const int W = 512, H = 256;
            sideTex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "sailcloth side", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            var px = new Color[W * H];
            float ropeU = (RopeX + Length / 2f) / Length;
            var rnd = new System.Random(11);
            for (int y = 0; y < H; y++)
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float val = 1f;
                    // Weave: fine threads both ways, a little noise.
                    val *= 0.97f + 0.025f * Mathf.Sin(x * 2.2f) * Mathf.Sin(y * 2.4f) + 0.02f * (float)rnd.NextDouble();
                    // Creases where the cloth turns over the fold, and the shadowed seam between layers.
                    float fold = Mathf.Min(Mathf.Abs(v - VFrontStart), Mathf.Abs(v - VFrontEnd));
                    val *= 1f - 0.22f * Mathf.Exp(-fold * fold / 0.0004f);
                    float back = Mathf.Min(v, 1f - v);
                    val *= 1f - 0.15f * Mathf.Exp(-back * back / 0.002f);
                    // The folded front: lit in the middle, soft wavy lengthwise folds.
                    if (v > VFrontStart && v < VFrontEnd)
                    {
                        float m = (v - VFrontStart) / (VFrontEnd - VFrontStart);
                        val *= 0.94f + 0.1f * Mathf.Sin(m * Mathf.PI);
                        val *= 1f + 0.05f * Mathf.Sin(u * 23f + 3f * Mathf.Sin(m * 5f));
                    }
                    // Where the rope bites in: a dark groove and creases fanning out from it.
                    float d = (u - ropeU) * Length;
                    float groove = Mathf.Exp(-d * d / 0.00004f);
                    float fan = Mathf.Exp(-d * d / 0.0012f);
                    val *= 1f - 0.35f * groove;
                    val *= 1f - 0.12f * fan * Mathf.Max(0f, Mathf.Sin(v * 60f + d * 140f));
                    // And where the rope along the bundle crosses the top and the bottom.
                    float dz = AlongGroove(v);
                    val *= 1f - 0.3f * Mathf.Exp(-dz * dz / 0.00004f);
                    val *= 1f - 0.1f * Mathf.Exp(-dz * dz / 0.0012f) * Mathf.Max(0f, Mathf.Sin(u * 70f + dz * 140f));
                    // The ends of the folds are pressed and a bit darker.
                    float e = Mathf.Min(u, 1f - u);
                    val *= 1f - 0.12f * Mathf.Exp(-e * e / 0.0006f);
                    val = Mathf.Clamp01(val);
                    px[y * W + x] = new Color(val, val, val, 1f);
                }
            }
            sideTex.SetPixels(px);
            sideTex.Apply(true);
            return sideTex;
        }

        // Distance in metres from the rope along the bundle at texture v, on the bottom or top of a layer (as Outline
        // lays v out); far away elsewhere.
        private static float AlongGroove(float v)
        {
            float z0 = -Depth / 2f + 0.005f, zf = Depth / 2f - LayerT * 0.49f;
            if (v < VFrontStart)
                return z0 + v / VFrontStart * (zf - z0) - RopeZ;
            if (v > VFrontEnd && v < VBackStart)
                return zf - (v - VFrontEnd) / (VBackStart - VFrontEnd) * (zf - z0) - RopeZ;
            return 1f;
        }

        // Ends: the cut through a folded layer, with the fold lines of the cloth inside and darker edges.
        private static Texture2D CapTexture()
        {
            if (capTex != null)
                return capTex;
            const int S = 128;
            capTex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "sailcloth end", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                    float val = 0.9f;
                    float edge = Mathf.Min(v, 1f - v);
                    val *= 1f - 0.3f * Mathf.Exp(-edge * edge / 0.004f);
                    foreach (float line in new[] { 0.34f, 0.67f })
                    {
                        float d = v - line - 0.03f * Mathf.Sin(u * 9f);
                        val *= 1f - 0.25f * Mathf.Exp(-d * d / 0.0008f) * Mathf.Clamp01((0.92f - u) * 6f);
                    }
                    val *= 1f - 0.15f * Mathf.Clamp01((u - 0.85f) * 6f);
                    px[y * S + x] = new Color(val, val, val, 1f);
                }
            capTex.SetPixels(px);
            capTex.Apply(true);
            return capTex;
        }

        private static Material MakeMat(Material baseMat, Color color, Texture2D tex)
        {
            Material m;
            if (baseMat != null)
                m = new Material(baseMat);
            else
                m = new Material(Shader.Find("Standard"));
            m.name = "sailcloth";
            if (m.HasProperty("_Color"))
                m.color = color;
            // Only our own picture: the model's normal and detail maps would be stretched over our UVs.
            foreach (string t in m.GetTexturePropertyNames())
                if (m.GetTexture(t) != null)
                    m.SetTexture(t, null);
            if (tex != null && m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", tex);
                m.mainTextureScale = Vector2.one;
                m.mainTextureOffset = Vector2.zero;
            }
            return m;
        }

        // The rope of the game: the cloth rope prefab's material, or any rope line's.
        private static Material VanillaRopeMaterial()
        {
            RefsDirectory refs = RefsDirectory.instance;
            if (refs != null)
            {
                var f = typeof(RefsDirectory).GetField("clothRopePrefab",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var prefab = f != null ? f.GetValue(refs) as GameObject : null;
                SkinnedMeshRenderer smr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                if (smr != null && smr.sharedMaterial != null)
                    return smr.sharedMaterial;
            }
            foreach (RopeEffect re in Resources.FindObjectsOfTypeAll<RopeEffect>())
            {
                LineRenderer lr = re != null ? re.GetComponent<LineRenderer>() : null;
                if (lr != null && lr.sharedMaterial != null)
                    return lr.sharedMaterial;
            }
            return null;
        }
    }
}
