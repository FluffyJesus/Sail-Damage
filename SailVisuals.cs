using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Step 4 of the plan: tears drawn into the sail cloth (SailDamage-design.md, section 3).
    // The vanilla canvas uses "Double Sided/Standard/Diffuse Bump"; the game also ships its cutout twin
    // "Double Sided/Transparent/Diffuse Bump Cutout", so a damaged sail gets a copy of its material on that
    // shader and its own main texture: the canvas baked once (tiling included) with alpha = hole mask.
    // At 100% the sail tears off below: the lower part is cut away and its cloth vertices are let loose,
    // so the rag left on the upper yard flaps in the wind.
    public class SailVisuals : MonoBehaviour
    {
        // Full name in the build is "Ciconia Studio/Double Sided/..."; Shader.Find needs it exactly, so match the tail.
        private const string CutoutShader = "Double Sided/Transparent/Diffuse Bump Cutout";
        private static readonly string[] Fallbacks =
        {
            "Legacy Shaders/Transparent/Cutout/Bumped Diffuse",
            "Legacy Shaders/Transparent/Cutout/Diffuse",
        };
        // Redraw the mask when damage moved this much since the last drawing.
        private const float Step = 0.02f;
        // Hand mending leaves a crumb of damage (Sailcloth stops below 0.001 m² of work): below this the sail counts as mended.
        private const float Mended = 0.002f;

        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, Material> UnfurledMaterial =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, Material>("unfurledMaterial");
        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, bool> IsFurled =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, bool>("isFurled");

        // Cloth of torn-off sails: TornClothPatch lets them flap.
        internal static readonly HashSet<Cloth> TornCloths = new HashSet<Cloth>();
        // Torn cloth that blew up once: it gets half the gusts.
        internal static readonly HashSet<Cloth> GentleCloths = new HashSet<Cloth>();

        private readonly Dictionary<Sail, Holes> holes = new Dictionary<Sail, Holes>();
        private readonly HashSet<Sail> failed = new HashSet<Sail>();
        private Shader cutout;
        private bool noShader;
        private float timer;
        private static SailVisuals instance;

        private void Awake() => instance = this;

        // StuddingCompat: the studding canvas of this sail is about to wear a copy of the sail's material. With our torn
        // material on the sail it would show the sail's holes; it gets the studding sails' own texture instead.
        internal static void DressStudding(Sail sail, Material m)
        {
            if (instance == null || !instance.holes.TryGetValue(sail, out Holes h))
                return;
            h.WantsStudding = true;
            Texture2D t = h.StuddingTexture();
            // Our canvas has the sail colour baked in. While the sail is furled StuddingSails tints its canvas with the
            // colour of the furled bundle, which coloured it twice, patches too (Кэп, 0.6.3): keep our material's.
            if (m.HasProperty("_Color") && h.Material.HasProperty("_Color") && m.color != h.Material.color)
                m.color = h.Material.color;
            if (m.mainTexture == t)
                return;
            m.shader = h.Material.shader;
            m.mainTexture = t;
            m.mainTextureScale = h.Material.mainTextureScale;
            m.mainTextureOffset = h.Material.mainTextureOffset;
            if (m.HasProperty("_Cutoff"))
                m.SetFloat("_Cutoff", 0.5f);
        }

        private void Update()
        {
            if (!GameState.playing || DamageModel.Instance == null)
                return;
            Transform boat = GameState.currentBoat != null ? GameState.currentBoat : GameState.lastBoat;

            if (Plugin.TestKeys.Value && boat != null && Input.GetKeyDown(KeyCode.F9))
            {
                bool reset = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                foreach (Sail sail in DamageModel.BoatSails(boat))
                {
                    if (reset)
                    {
                        DamageModel.Instance.Mend(sail);
                        continue;
                    }
                    if (sail.currentUnroll >= 0.04f)
                        DamageModel.Instance.SetDamage(sail, DamageModel.Instance.GetDamage(sail) + 0.1f);
                    // Studding sails on a winch can be set while their sail is furled (Кэп, 0.6.2).
                    for (int side = 0; side < 2; side++)
                        if (StuddingCompat.SetShare(sail, side) > 0.01f)
                            DamageModel.Instance.AddStuddingDamageTest(sail, side, 0.1f);
                }
                PressureLogger.Instance?.ShowMessage(reset ? "Sail damage reset" : "Sails: +10% damage");
                timer = 0f;
            }

            if (Plugin.TestKeys.Value && boat != null && Input.GetKeyDown(KeyCode.F10))
                DumpSails(boat);

            foreach (Holes done in holes.Values)
            {
                done.FinishDraw();
                done.ApplyCanvas();
            }
            ReleaseMended();

            if (!Plugin.ShowHoles.Value || noShader || boat == null)
                return;
            timer -= Time.deltaTime;
            if (timer > 0f)
                return;
            timer = 0.5f;
            ForgetDestroyed();

            foreach (Sail sail in DamageModel.BoatSails(boat))
            {
                if (noShader)
                    return;
                float d = DamageModel.Instance.GetDamage(sail);
                float p = DamageModel.Instance.GetPatched(sail);
                var sd = new Vector2(DamageModel.Instance.GetStuddingDamage(sail, 0), DamageModel.Instance.GetStuddingDamage(sail, 1));
                var sp = new Vector2(DamageModel.Instance.GetStuddingPatched(sail, 0), DamageModel.Instance.GetStuddingPatched(sail, 1));
                holes.TryGetValue(sail, out Holes h);
                if (h == null)
                {
                    if (d < Step && p < Step && Mathf.Max(sd.x, sd.y, sp.x, sp.y) < Step || failed.Contains(sail) || !Prepare(sail, out h))
                        continue;
                    holes[sail] = h;
                }
                h.KeepApplied();
                h.CheckCloth();
                if (h.WantsStudding)
                    h.SetStuddingPanels(StuddingCompat.Panels(sail));
                if (h.NeedsDraw(d, p) || h.StuddingNeedsDraw(sd, sp))
                    h.Draw(d, p, sd, sp);
            }
        }

        // ---------------------------------------------------------------- SailEmblems (Кэп's painted sails)
        // The contract, both sides, is written down once in SailEmblems' Compat.cs (SailDamageLink): Draws, TakeCanvas,
        // SetFrontOnly, FrontTexture, and Release putting back what it was given. SailEmblems finds these by
        // reflection: keep the names and signatures.

        // What Sail Emblems last handed over for each painted sail, kept for a sail torn later: the painted canvas (a torn
        // canvas is made from it; with Front side only the cloth wears the plain one, and a sail torn then showed
        // unpainted for a moment, Кэп 1.0.3) and the same canvas plain (studding sails, patches, the back).
        private sealed class Given
        {
            internal Material Painted;
            internal Texture Clean;
        }

        private readonly Dictionary<Sail, Given> given = new Dictionary<Sail, Given>();

        /// <summary>
        /// SailEmblems: the sail has a new painted canvas (and the same canvas unpainted, for its studding sails; null
        /// without them). If we draw this sail, its torn canvas is read again from the painted one and worn at once;
        /// returns whether we do. Called by reflection, keep the signature.
        /// </summary>
        public static bool TakeCanvas(Sail sail, Material painted, Texture clean)
        {
            if (instance == null || sail == null)
                return false;
            if (painted != null || clean != null)
                instance.given[sail] = new Given { Painted = painted, Clean = clean };
            else
                instance.given.Remove(sail);
            if (!instance.holes.TryGetValue(sail, out Holes h) || painted == null)
                return false;
            h.SetCanvas(painted, clean);
            instance.Wear(sail, h);
            instance.timer = 0f; // draw the tears over it on the next frame
            return true;
        }

        /// <summary>
        /// The sail was mended as new (shipyard): no holes and no patches left to draw, so the torn canvas goes and the
        /// canvas it was made from comes back. Up to 1.0.2 it stayed, and Sail Emblems kept taking the sail for torn
        /// (its Front side only stayed off after a shipyard repair, Кэп). A drawing on its way is let finish first.
        /// </summary>
        internal static void Release(Sail sail)
        {
            if (instance != null && sail != null && instance.holes.ContainsKey(sail))
                instance.releasing.Add(sail);
        }

        private readonly HashSet<Sail> releasing = new HashSet<Sail>();

        private void ReleaseMended()
        {
            if (releasing.Count == 0)
                return;
            List<Sail> done = null;
            foreach (Sail s in releasing)
            {
                if (s == null || !holes.TryGetValue(s, out Holes h))
                {
                    (done ??= new List<Sail>()).Add(s);
                    continue;
                }
                if (h.Busy)
                    continue;
                // damaged again meanwhile: keep drawing it
                if (DamageModel.Instance != null && (DamageModel.Instance.GetDamage(s) > 0f || DamageModel.Instance.GetPatched(s) > 0f || DamageModel.Instance.HasStuddingMarks(s)))
                {
                    (done ??= new List<Sail>()).Add(s);
                    continue;
                }
                h.Unloose();
                Material back = h.Source;
                Renderer r = s.cloth != null ? s.cloth.GetComponent<Renderer>() : null;
                ReefEffectAnimUniversal reef = s.GetComponentInChildren<ReefEffectAnimUniversal>();
                if (back != null && r != null)
                {
                    if (reef != null && IsFurled(reef))
                    {
                        if (UnfurledMaterial(reef) == h.Material || UnfurledMaterial(reef)?.mainTexture == h.Texture)
                            UnfurledMaterial(reef) = back;
                    }
                    else if (r.sharedMaterial == h.Material || r.sharedMaterial?.mainTexture == h.Texture)
                        r.sharedMaterial = back;
                }
                h.Dispose();
                holes.Remove(s);
                (done ??= new List<Sail>()).Add(s);
                Plugin.Debug($"VISUAL {s.sailName}: mended as new, '{back?.name}' back on the sail");
            }
            if (done != null)
                foreach (Sail s in done)
                    releasing.Remove(s);
        }

        /// <summary>
        /// SailEmblems' Front side only on a sail we draw: our material shows the plain canvas with the holes, and the
        /// painted canvas with the same holes is drawn as a one-sided layer over it (FrontTexture). Returns whether it is
        /// on (it needs the plain canvas from TakeCanvas). Called by reflection, keep the signature.
        /// </summary>
        public static bool SetFrontOnly(Sail sail, bool on)
        {
            if (instance == null || sail == null || !instance.holes.TryGetValue(sail, out Holes h))
                return false;
            bool was = h.Split;
            bool now = h.SetSplit(on);
            if (now != was)
                instance.timer = 0f;
            return now;
        }

        /// <summary>SailEmblems: the painted canvas with holes and its tiling, for the one-sided layer; null if not split.</summary>
        public static Texture FrontTexture(Sail sail, out Vector2 scale, out Vector2 offset)
        {
            scale = Vector2.one;
            offset = Vector2.zero;
            if (instance == null || sail == null || !instance.holes.TryGetValue(sail, out Holes h) || !h.Split)
                return null;
            scale = h.Material.mainTextureScale;
            offset = h.Material.mainTextureOffset;
            return h.FrontTexture;
        }

        /// <summary>SailEmblems: we wear our torn canvas on this sail. Called by reflection, keep the signature.</summary>
        public static bool Draws(Sail sail) => instance != null && sail != null && instance.holes.ContainsKey(sail);

        private void Wear(Sail sail, Holes h)
        {
            Renderer r = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
            if (r == null)
                return;
            ReefEffectAnimUniversal reef = sail.GetComponentInChildren<ReefEffectAnimUniversal>();
            if (reef != null && IsFurled(reef))
                UnfurledMaterial(reef) = h.Material;
            else
                r.sharedMaterial = h.Material;
        }

        // Leaving the shipyard builds every sail again (SaveableBoatCustomization.LoadData): the old ones' canvases
        // stayed here, one torn texture per damaged sail and visit.
        private void ForgetDestroyed()
        {
            List<Sail> gone = null;
            foreach (Sail s in holes.Keys)
                if (s == null)
                    (gone ??= new List<Sail>()).Add(s);
            if (gone == null)
                return;
            foreach (Sail s in gone)
            {
                if (holes.TryGetValue(s, out Holes h) && !h.Busy)
                {
                    h.Dispose();
                    holes.Remove(s);
                    given.Remove(s);
                }
            }
            failed.RemoveWhere(s => s == null);
            TornCloths.RemoveWhere(c => c == null);
            GentleCloths.RemoveWhere(c => c == null);
        }

        // The furl placeholder, or a copy of it: Furl() saves renderer.material, which is an "(Instance)" copy.
        internal static bool IsEmpty(Material m)
        {
            Material empty = Refs.emptyMaterial;
            return m != null && empty != null && (m == empty || m.name.StartsWith(empty.name, StringComparison.Ordinal));
        }

        // F10: what every sail of the boat is drawn with right now, to see which renderer shows what.
        private void DumpSails(Transform boat)
        {
            Plugin.Log.LogInfo($"===== DUMP {boat.name} =====");
            foreach (Sail sail in DamageModel.BoatSails(boat))
            {
                holes.TryGetValue(sail, out Holes h);
                Renderer r = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
                Material m = r != null ? r.sharedMaterial : null;
                ReefEffectAnimUniversal reef = sail.GetComponentInChildren<ReefEffectAnimUniversal>();
                Material un = reef != null ? UnfurledMaterial(reef) : null;
                Plugin.Log.LogInfo($"DUMP {sail.sailName} ({sail.category}) unroll={sail.currentUnroll:F2} dmg={DamageModel.Instance.GetDamage(sail):F2} " +
                                   $"holes={(h != null ? h.State : "none")} renderer={(r != null ? r.name + (r.enabled ? "" : " OFF") + (r.isVisible ? "" : " unseen") + " bounds " + r.bounds.size : "none")} " +
                                   $"cloth={(sail.cloth != null && sail.cloth.enabled ? "on" : "off")} " +
                                   $"mat='{m?.name}' tex='{m?.mainTexture?.name}' shader='{m?.shader?.name}' ours={(h != null && m != null && m.mainTexture == h.Texture)} " +
                                   $"empty={IsEmpty(m)} ('{Refs.emptyMaterial?.name}') furled={(reef != null && IsFurled(reef))} unfurledMat='{un?.name}' unfurledOurs={(h != null && un != null && un.mainTexture == h.Texture)}");
                foreach (Renderer other in sail.GetComponentsInChildren<Renderer>(true))
                    if (other != r && !(other is LineRenderer))
                        Plugin.Log.LogInfo($"DUMP   also {other.GetType().Name} {other.name}{(other.enabled ? "" : " OFF")} mat='{other.sharedMaterial?.name}' tex='{other.sharedMaterial?.mainTexture?.name}'");
            }
        }

        private bool Prepare(Sail sail, out Holes h)
        {
            h = null;
            if (cutout == null)
            {
                cutout = FindCutout();
                if (cutout == null)
                {
                    noShader = true;
                    Plugin.Log.LogError($"VISUAL no cutout shader ('{CutoutShader}' or legacy cutout) found, holes are off");
                    return false;
                }
                Plugin.Debug($"VISUAL cutout shader '{cutout.name}', supported={cutout.isSupported}");
            }

            Renderer r = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
            if (r == null)
            {
                failed.Add(sail);
                return false;
            }
            ReefEffectAnimUniversal reef = sail.GetComponentInChildren<ReefEffectAnimUniversal>();
            bool furled = reef != null && IsFurled(reef);
            Material src = furled ? UnfurledMaterial(reef) : r.sharedMaterial;
            given.TryGetValue(sail, out Given g);
            if (g?.Painted != null && g.Painted.mainTexture != null)
                src = g.Painted;
            if (src == null || src.mainTexture == null)
                return false; // furled before the game saved its canvas, try again later
            // While furled the cloth wears Refs.emptyMaterial (a white particle cloud). 0.4.x sometimes copied that
            // instead of the canvas, and the sail turned into a white blob; wait until the real canvas is back.
            if (IsEmpty(src) || IsEmpty(r.sharedMaterial) && !furled)
                return false;

            try
            {
                h = new Holes(sail, src, cutout, r as SkinnedMeshRenderer);
            }
            catch (Exception e)
            {
                failed.Add(sail);
                Plugin.Log.LogError($"VISUAL {sail.sailName}: {e}");
                return false;
            }

            if (furled)
                UnfurledMaterial(reef) = h.Material;
            else
                r.sharedMaterial = h.Material;
            if (g?.Clean != null)
                h.SetCanvas(src, g.Clean); // a painted sail: its studding sails get the plain canvas
            Plugin.Debug($"VISUAL {sail.sailName} ({sail.category}{(DamageModel.IsBattened(sail) ? ", battened" : "")}): '{src.shader.name}' -> '{cutout.name}', canvas {src.mainTexture.name} " +
                               $"{src.mainTexture.width}x{src.mainTexture.height} tiling {src.mainTextureScale} -> {h.Info}");
            return true;
        }

        private static Shader FindCutout()
        {
            Shader found = Shader.Find("Ciconia Studio/" + CutoutShader);
            if (found != null)
                return found;
            foreach (Shader s in Resources.FindObjectsOfTypeAll<Shader>())
                if (s.name.EndsWith(CutoutShader, StringComparison.Ordinal))
                    return s;
            foreach (string name in Fallbacks)
            {
                found = Shader.Find(name);
                if (found != null)
                {
                    Plugin.Log.LogWarning($"VISUAL '{CutoutShader}' not loaded, using single-sided '{name}'");
                    return found;
                }
            }
            return null;
        }

        // One sail: its torn material, the baked canvas and the tears (same seed -> same tears after loading).
        private sealed class Holes
        {
            // Normalized height (0 = foot, 1 = head) below which a fully torn sail is gone, before the ragged edge.
            private const float CutHeight = 0.5f;
            // A lateen tears away at its tack, the lower corner held by the rope (Кэп, 0.4.11): no cloth is cut off,
            // the whole sail hangs from the yard and flaps.
            private bool KeepsWholeCloth => sail.category == SailCategory.lateen;
            // Above this height the cloth is at the yard: no holes, and a torn rag is pinned there.
            private const float HeadBand = 0.94f;

            internal readonly Material Material;
            internal float Shown = -1f;
            internal readonly string Info;
            // what the torn material shows: the canvas, or with Front side only the plain back (Split)
            internal Texture Texture => Material.mainTexture;
            internal bool Busy => jobRunning || jobDone;

            // The material the torn one was made from (the game's canvas, or Sail Emblems' painted one), put back when the
            // sail is mended as new.
            internal Material Source;

            // Mended as new at the shipyard: cloth held as before, ropes back; the caller puts Source back on.
            internal void Unloose()
            {
                SetClothLoose(false);
                HideLowerRopes(false);
                tornShown = false;
            }

            // The sail is gone: free what we made for it.
            internal void Dispose()
            {
                UnityEngine.Object.Destroy(Material);
                UnityEngine.Object.Destroy(texture);
                if (backTexture != null)
                    UnityEngine.Object.Destroy(backTexture);
                if (studTexture != null)
                    UnityEngine.Object.Destroy(studTexture);
            }
            internal string State => $"shown {Shown:F2}, frame {(hasFrame ? "yes" : frameFailed ? "failed" : "no")}, torn {tornShown}";

            private readonly Sail sail;
            private readonly SkinnedMeshRenderer smr;
            private readonly Texture2D texture;
            private readonly Color32[] canvas;
            private readonly Color32[] work;
            private readonly int width, height;
            private readonly Rect uv;
            private readonly Tear[] tears;
            private readonly Bite[] bites;
            private readonly int seed;

            // Height across the sail as a linear function of UV, fitted to the rendered (skinned) set sail;
            // vertexHeight is each vertex's own height 0..1, worldLow/worldSpan map world Y onto it.
            private bool hasFrame, frameFailed;
            private float fa, fb, fc;
            // The same height per canvas pixel, drawn from the mesh triangles (-1 off the cloth). The linear fit alone
            // missed a lateen, whose distance from the mast folds over at the mast: 0.4.7 cut nothing off it.
            private float[] heightMap;
            private float[] vertexHeight;
            private float worldLow, worldSpan;
            private ClothSkinningCoefficient[] original;
            private bool originalGravity;
            private float originalBending;
            private Vector3 originalRandom;
            private bool originalTethers;
            private float originalSolver;
            private readonly List<KeyValuePair<RopeEffect, Transform>> hiddenRopes = new List<KeyValuePair<RopeEffect, Transform>>();
            private bool tornShown;

            internal Holes(Sail sail, Material src, Shader cutout, SkinnedMeshRenderer smr)
            {
                this.sail = sail;
                this.smr = smr;
                string flat = FlattenDegenerateUv(smr, out Vector2 oldUv);
                uv = UvBounds(smr);
                Texture main = src.mainTexture;
                Vector2 sc = src.mainTextureScale, of = src.mainTextureOffset;
                width = Size(main.width * Mathf.Abs(sc.x) * uv.width);
                height = Size(main.height * Mathf.Abs(sc.y) * uv.height);
                if (flat != null)
                    width = height = 1024; // a plain colour, so resolution is only for the tears
                else if (main.width <= 16 && main.height <= 16)
                    width = height = 1024; // a plain texture (MoreSailwindSails' Texture2D.whiteTexture): resolution is for the tears
                flatUv = flat;
                this.oldUv = oldUv;

                texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
                {
                    name = "SailDamage canvas", wrapMode = TextureWrapMode.Clamp, filterMode = main.filterMode, anisoLevel = main.anisoLevel,
                };
                canvas = ReadCanvas(src, out string plain);
                flat = flat ?? plain;
                work = new Color32[canvas.Length];
                rawCanvas = (Color32[])canvas.Clone();
                rawLum = Luminance(rawCanvas);

                Source = src;
                Material = new Material(src) { shader = cutout, name = src.name + " (torn)" };
                Material.mainTexture = texture;
                // The sail colour (Sail.ChangeSailColor sets material.color) is baked into the canvas, so patches can
                // stay plain undyed grey on any sail (Кэп, 0.5.2).
                BakeColour();
                // The whole canvas shows until the first drawing comes back from its thread.
                texture.SetPixels32(canvas);
                texture.Apply(true);
                Material.mainTextureScale = new Vector2(1f / uv.width, 1f / uv.height);
                Material.mainTextureOffset = new Vector2(-uv.xMin / uv.width, -uv.yMin / uv.height);
                if (Material.HasProperty("_Cutoff"))
                    Material.SetFloat("_Cutoff", 0.5f);

                string seams = SeamAxis(out Vector2 along);
                seamAlong = along;
                string key = DamageModel.SaveKey(sail) ?? sail.sailName;
                seed = Hash(key);
                Outline outline = Outline.FromMesh(smr != null ? smr.sharedMesh : null, uv);
                tears = Tear.Make(seed, along, outline);
                bites = Bite.Make(seed, outline);
                Info = $"{width}x{height}, uv {uv.xMin:F2}..{uv.xMax:F2} x {uv.yMin:F2}..{uv.yMax:F2}, cutoff={Material.HasProperty("_Cutoff")}, " +
                       $"seams {seams}, outline {outline.Info}, seed key {key}{(flat != null ? ", " + flat : "")}";
            }

            private readonly Color32[] rawCanvas;
            private float rawLum;
            private readonly Vector2 seamAlong;
            private readonly string flatUv;
            private readonly Vector2 oldUv;

            // Bake a canvas over the mesh UV rectangle, with the material tiling, at our size.
            private Color32[] ReadCanvas(Material src, out string plain)
            {
                plain = null;
                Color32[] px = Grab(src.mainTexture, src.mainTextureScale, src.mainTextureOffset);
                Vector2 sc = src.mainTextureScale, of = src.mainTextureOffset;
                if (flatUv != null)
                {
                    // The old UVs all sat on one texel, so the sail showed that one colour: keep it everywhere.
                    int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(oldUv.x * sc.x + of.x, 1f) * width), 0, width - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(oldUv.y * sc.y + of.y, 1f) * height), 0, height - 1);
                    Color32 one = px[y * width + x];
                    for (int i = 0; i < px.Length; i++)
                        px[i] = one;
                }
                else if (src.mainTexture != null && src.mainTexture.name.StartsWith("ParticleCloud", StringComparison.Ordinal) && MeanUv(smr, out Vector2 mean))
                {
                    // Junk-family sails (junk jib, fin sail) use a soft particle blob as their "canvas" and look plain
                    // in the game; baked as is, the blob showed as a white cloud. Keep the colour at the cloth's middle.
                    int x = Mathf.Clamp(Mathf.FloorToInt((mean.x - uv.xMin) / uv.width * width), 0, width - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt((mean.y - uv.yMin) / uv.height * height), 0, height - 1);
                    Color32 one = px[y * width + x];
                    for (int i = 0; i < px.Length; i++)
                        px[i] = one;
                    plain = "plain canvas (particle texture)";
                }
                for (int i = 0; i < px.Length; i++)
                    px[i].a = 255; // the canvas is opaque; only the mask makes holes
                return px;
            }

            private Color32[] Grab(Texture main, Vector2 sc, Vector2 of)
            {
                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(main, rt, new Vector2(sc.x * uv.width, sc.y * uv.height),
                    new Vector2(of.x + sc.x * uv.xMin, of.y + sc.y * uv.yMin));
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = rt;
                var read = new Texture2D(width, height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] px = read.GetPixels32();
                UnityEngine.Object.Destroy(read);
                return px;
            }

            private static float Luminance(Color32[] px)
            {
                long lum = 0;
                for (int i = 0; i < px.Length; i++)
                    lum += px[i].r * 3 + px[i].g * 6 + px[i].b;
                return Mathf.Max(1f, lum / (float)px.Length);
            }

            // ------------------------------------------------------------ SailEmblems

            // Sail Emblems paints its emblem into a copy of the canvas and hands each new one over (TakeCanvas): the
            // canvas is read again from it, tears and patches drawn over it again. The same canvas without the emblem
            // (rawStud) goes to the studding sails (studBase), else they showed a strip of it, and gives the patches
            // their weave.
            private Material pendingCanvas;
            private Texture pendingClean;
            private bool hasPending;
            private Color32[] rawStud, studBase;
            private float plainLum;

            internal void SetCanvas(Material painted, Texture clean)
            {
                if (painted != null)
                    Source = painted;
                pendingCanvas = painted;
                pendingClean = clean;
                hasPending = true;
                ApplyCanvas();
            }

            /// <summary>Every frame: the canvas Sail Emblems handed over, once no drawing is on its way.</summary>
            internal void ApplyCanvas()
            {
                if (!hasPending || jobRunning || jobDone)
                    return;
                hasPending = false;
                Material src = pendingCanvas;
                if (src == null || src.mainTexture == null)
                    return;
                Array.Copy(ReadCanvas(src, out _), rawCanvas, rawCanvas.Length);
                rawLum = Luminance(rawCanvas);
                rawStud = pendingClean != null ? Grab(pendingClean, src.mainTextureScale, src.mainTextureOffset) : null;
                studBase = rawStud != null ? new Color32[rawStud.Length] : null;
                plainLum = rawStud != null ? Luminance(rawStud) : rawLum;
                if (src.HasProperty("_BumpMap") && Material.HasProperty("_BumpMap"))
                    Material.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
                if (Material.HasProperty("_Color"))
                    Material.color = src.HasProperty("_Color") ? src.color : Color.white;
                BakeColour();
                Shown = -1f;
                StudShown = -1f;
                if (studTexture != null)
                {
                    studTexture.SetPixels32(StudBase);
                    studTexture.Apply(true);
                }
                Plugin.Debug($"VISUAL {sail.sailName}: new canvas '{src.name}' from Sail Emblems{(studBase != null ? ", plain one for the studding sails" : "")}");
            }

            private Color32[] StudBase => studBase ?? canvas;

            // ------------------------------------------------------------ studding sails (StuddingCompat)

            // Their canvas samples a strip of this sail's canvas (its UVs are in the sail's layout), so they get a
            // texture of the same layout: the clean canvas with their own tears, laid along their own outline.
            // The two sides sample strips on either side of the sail's middle, so one texture holds both, each side's
            // tears cut at its own damage (Кэп, 0.6.1).
            internal bool WantsStudding;
            private Vector2 studShown = new Vector2(-1f, -1f), studShownPatched = new Vector2(-1f, -1f);
            private Texture2D studTexture;
            private Color32[] studWork, studSide;
            private Color32[][] studMips;
            private readonly Tear[][] studTears = new Tear[2][];
            private readonly Bite[][] studBites = new Bite[2][];
            private Vector4 studUvKey = new Vector4(float.NaN, 0f, 0f, 0f);
            private bool jobStud;

            internal float StudShown
            {
                set => studShown = new Vector2(value, value);
            }

            internal Texture2D StuddingTexture()
            {
                if (studTexture != null)
                    return studTexture;
                studTexture = new Texture2D(width, height, TextureFormat.RGBA32, true)
                {
                    name = "SailDamage studding canvas", wrapMode = TextureWrapMode.Clamp,
                    filterMode = texture.filterMode, anisoLevel = texture.anisoLevel,
                };
                studTexture.SetPixels32(StudBase);
                studTexture.Apply(true);
                studWork = new Color32[canvas.Length];
                studSide = new Color32[canvas.Length];
                return studTexture;
            }

            // The studding canvases (both sides): tears along their outline, made again when they are laid out anew.
            internal void SetStuddingPanels(Mesh[] panels)
            {
                if (panels == null)
                    return;
                var key = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                foreach (Mesh m in panels)
                    if (m != null)
                        foreach (Vector2 u in m.uv)
                            key = new Vector4(Mathf.Min(key.x, u.x), Mathf.Min(key.y, u.y), Mathf.Max(key.z, u.x), Mathf.Max(key.w, u.y));
                if (key.x > key.z || (key - studUvKey).sqrMagnitude < 1e-6f)
                    return;
                studUvKey = key;
                var info = new System.Text.StringBuilder();
                for (int side = 0; side < 2; side++)
                {
                    Mesh m = side < panels.Length ? panels[side] : null;
                    if (m == null)
                    {
                        studTears[side] = null;
                        studBites[side] = null;
                        continue;
                    }
                    Outline outline = Outline.FromMeshes(new[] { m }, uv);
                    int s = seed ^ (0x2c1b3c6d + side * 7919);
                    studTears[side] = Tear.Make(s, seamAlong, outline);
                    studBites[side] = Bite.Make(s, outline);
                    info.Append(side == 0 ? " port " : " starboard ").Append(outline.Info);
                }
                StudShown = -1f;
                Plugin.Debug($"VISUAL {sail.sailName}: studding sail tears laid out,{info}");
            }

            internal bool StuddingNeedsDraw(Vector2 damage, Vector2 patched)
            {
                if (studTexture == null || studTears[0] == null && studTears[1] == null)
                    return false;
                for (int side = 0; side < 2; side++)
                {
                    float d = damage[side], p = patched[side], shown = studShown[side], shownP = studShownPatched[side];
                    if (shown < 0f || Mathf.Abs(d - shown) >= Step || Mathf.Abs(p - shownP) >= Step || p <= 0f && shownP > 0f)
                        return true;
                    if (d < Mended && shown >= Mended && p > 0f)
                        return true;
                }
                return false;
            }

            // Each side on its own copy of the canvas (its patches close its own holes), then both laid together.
            private void ComputeStudding(Vector2 damage, Vector2 patched)
            {
                Color32[] stud = StudBase;
                Array.Copy(stud, studWork, stud.Length);
                for (int side = 0; side < 2; side++)
                {
                    if (studTears[side] == null)
                        continue;
                    float d = damage[side], p = patched[side];
                    Color32[] buf = side == 0 ? studWork : studSide;
                    if (side == 1)
                        Array.Copy(stud, studSide, stud.Length);
                    if (p > 0f)
                    {
                        Cut(buf, Mathf.Min(d + p, 0.999f), studTears[side], studBites[side]);
                        DrawPatches(buf, d < Mended ? 1f : p / (d + p));
                    }
                    else
                        Cut(buf, Mathf.Min(d, 0.999f), studTears[side], studBites[side]);
                }
                if (studTears[1] != null)
                    for (int i = 0; i < studWork.Length; i++)
                    {
                        Color32 b = studSide[i];
                        if (b.a == 0)
                            studWork[i].a = 0;
                        else if (studWork[i].a != 0 && (b.r != stud[i].r || b.g != stud[i].g || b.b != stud[i].b))
                            studWork[i] = b; // a starboard patch
                    }
                BuildMips(studWork, ref studMips);
            }

            private void BakeColour()
            {
                Color tint = Material.HasProperty("_Color") ? Material.color : Color.white;
                for (int i = 0; i < canvas.Length; i++)
                {
                    Color32 c = rawCanvas[i];
                    canvas[i] = new Color32((byte)(c.r * tint.r), (byte)(c.g * tint.g), (byte)(c.b * tint.b), 255);
                }
                if (rawStud != null)
                    for (int i = 0; i < rawStud.Length; i++)
                    {
                        Color32 c = rawStud[i];
                        studBase[i] = new Color32((byte)(c.r * tint.r), (byte)(c.g * tint.g), (byte)(c.b * tint.b), 255);
                    }
                if (Material.HasProperty("_Color"))
                    Material.color = new Color(1f, 1f, 1f, tint.a);
            }

            // Something put a canvas without our mask back on the set sail (a stale unfurl material): wear ours again.
            internal void KeepApplied()
            {
                // Renderer.material made an instance of ours to recolour it: take its colour and wear ours again.
                Material worn = smr != null ? smr.sharedMaterial : null;
                if (worn != null && worn != Material && worn.mainTexture == Material.mainTexture && worn.HasProperty("_Color") && Material.HasProperty("_Color"))
                {
                    Material.color = worn.color;
                    smr.sharedMaterial = Material;
                }
                // Recoloured at the shipyard: bake the new colour and draw again.
                if (Material.HasProperty("_Color") && !jobRunning && !jobDone)
                {
                    Color c = Material.color;
                    if (c.r < 0.999f || c.g < 0.999f || c.b < 0.999f)
                    {
                        BakeColour();
                        Shown = -1f;
                        StudShown = -1f;
                        Plugin.Debug($"VISUAL {sail.sailName}: sail colour {c} baked into the canvas");
                    }
                }
                Material m = smr != null ? smr.sharedMaterial : null;
                if (m == null || m.mainTexture == Material.mainTexture)
                    return;
                // The furl placeholder belongs on a furled sail only; on a set one it shows as a white cloud.
                if (IsEmpty(m) && sail.currentUnroll < 0.04f)
                    return;
                smr.sharedMaterial = Material;
                Plugin.Debug($"VISUAL {sail.sailName}: '{m.name}' ({m.mainTexture?.name}) was on the sail, torn canvas put back");
            }

            // F10 in 0.4.8: the torn balanced lug had NaN bounds, its cloth simulation had blown up and drew as a white
            // cloud while wearing the right material. Put the cloth back, restart it and loosen it again more gently.
            internal void CheckCloth()
            {
                if (smr == null || sail.cloth == null || !sail.cloth.enabled)
                    return;
                Vector3 size = smr.bounds.size;
                if (!float.IsNaN(size.x + size.y + size.z) && !float.IsInfinity(size.x + size.y + size.z) && size.magnitude < 500f)
                    return;
                blowups++;
                Plugin.Log.LogWarning($"TEAROFF {sail.sailName}: cloth blew up (bounds {size}), torn {tornShown}, gentle {gentle}, time {blowups}");
                bool wasLoose = original != null;
                SetClothLoose(false);
                sail.cloth.enabled = false;
                sail.cloth.enabled = true;
                if (!wasLoose)
                    return;
                if (blowups >= 3)
                {
                    Plugin.Log.LogWarning($"TEAROFF {sail.sailName}: cloth keeps blowing up, the rag stays held as it was");
                    return;
                }
                gentle = true;
                SetClothLoose(true);
            }

            private int blowups;
            private bool gentle;
            private float originalSleep;
            private CapsuleCollider[] originalCapsules;
            private ClothSphereColliderPair[] originalSpheres;

            internal bool NeedsDraw(float damage, float patched)
            {
                if (Mathf.Abs(damage - Shown) >= Step || (damage >= 1f && Shown < 1f) || (damage < 1f && Shown >= 1f))
                    return true;
                if (Mathf.Abs(patched - ShownPatched) >= Step || patched <= 0f && ShownPatched > 0f)
                    return true;
                // Mending done: the last hole closes then, however small the final step was.
                if (damage < Mended && Shown >= Mended && patched > 0f)
                    return true;
                // Damaged or torn off while furled: the frame needs the set sail, so finish when it is set again.
                if (!hasFrame && !frameFailed && Shown > 0f && sail.currentUnroll >= 0.9f)
                    return true;
                return damage >= 1f && !tornShown && !frameFailed && sail.currentUnroll >= 0.9f;
            }

            // Drawing a 1024² mask on the main thread halved the frame rate every few seconds while sails took damage
            // or were mended (Кэп, 0.5.1). The pixels and their mipmaps are now worked out on a pool thread; the main
            // thread only uploads them and does the Unity side (cloth, ropes) when the job is done.
            private volatile bool jobRunning, jobDone;
            private bool jobTornOff;
            private Exception jobError;
            private Color32[][] mips;
            private int mipLevels;

            internal void Draw(float damage, float patched, Vector2 studDamage, Vector2 studPatched)
            {
                if (jobRunning || jobDone)
                    return;
                EnsureFrame(); // Unity calls: here, not on the worker
                bool tornOff = damage >= 1f && Plugin.TearOff.Value && hasFrame;
                bool cutLower = tornOff && !KeepsWholeCloth;
                Shown = damage;
                ShownPatched = patched;
                jobTornOff = tornOff;
                jobStud = studTexture != null && (studTears[0] != null || studTears[1] != null);
                if (jobStud)
                {
                    studShown = studDamage;
                    studShownPatched = studPatched;
                }
                mipLevels = texture.mipmapCount;
                jobBack = Split && backTexture != null && studBase != null;
                jobRunning = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Compute(damage, patched, cutLower);
                        if (jobStud)
                            ComputeStudding(studDamage, studPatched);
                    }
                    catch (Exception e)
                    {
                        jobError = e;
                    }
                    jobDone = true;
                    jobRunning = false;
                });
            }

            // Every frame on the main thread: hand a finished drawing to the GPU.
            internal void FinishDraw()
            {
                if (!jobDone)
                    return;
                jobDone = false;
                if (jobError != null)
                {
                    Plugin.Log.LogError($"VISUAL {sail.sailName}: drawing failed: {jobError}");
                    jobError = null;
                    return;
                }
                SetClothLoose(jobTornOff);
                HideLowerRopes(jobTornOff);
                tornShown = jobTornOff;
                for (int l = 0; l < mips.Length; l++)
                    texture.SetPixels32(mips[l], l);
                texture.Apply(false);
                if (jobStud && studTexture != null)
                {
                    for (int l = 0; l < studMips.Length; l++)
                        studTexture.SetPixels32(studMips[l], l);
                    studTexture.Apply(false);
                }
                if (jobBack && backTexture != null)
                {
                    for (int l = 0; l < backMips.Length; l++)
                        backTexture.SetPixels32(backMips[l], l);
                    backTexture.Apply(false);
                }
            }

            private void Compute(float damage, float patched, bool cutLower)
            {
                Array.Copy(canvas, work, canvas.Length);
                if (patched > 0f)
                {
                    Cut(work, Mathf.Min(damage + patched, 0.999f));
                    DrawPatches(work, damage < Mended ? 1f : patched / (damage + patched));
                }
                else
                    Cut(work, damage);
                if (cutLower)
                    CutLowerPart();
                BuildMips(work, ref mips);
                if (jobBack)
                    ComputeBack();
            }

            // ------------------------------------------------------------ Sail Emblems' Front side only

            // A painted sail shown on its front face only: Sail Emblems lays the painted canvas as a one-sided layer over
            // ours, so ours shows the plain canvas (both faces) and the painted one goes to that layer, both with the same
            // holes and patches (Кэп, 1.0.3: before, a torn sail was painted on both faces).
            internal bool Split { get; private set; }
            private Texture2D backTexture;
            private Color32[] backWork;
            private Color32[][] backMips;
            private bool jobBack;

            /// <summary>The painted canvas with its holes, for the one-sided layer; null unless split.</summary>
            internal Texture FrontTexture => Split ? texture : null;

            internal bool SetSplit(bool on)
            {
                if (on && studBase == null)
                    on = false; // no plain canvas to show behind
                if (on == Split)
                    return Split;
                Split = on;
                if (on && backTexture == null)
                {
                    backTexture = new Texture2D(width, height, TextureFormat.RGBA32, true)
                    {
                        name = "SailDamage back canvas", wrapMode = TextureWrapMode.Clamp, filterMode = texture.filterMode, anisoLevel = texture.anisoLevel,
                    };
                    backTexture.SetPixels32(studBase);
                    backTexture.Apply(true);
                    backWork = new Color32[canvas.Length];
                }
                Material.mainTexture = on ? (Texture)backTexture : texture;
                Shown = -1f; // draw the holes into it
                Plugin.Debug($"VISUAL {sail.sailName}: front side only {(on ? "on" : "off")} (Sail Emblems)");
                return Split;
            }

            // The back: holes as on the front, patches as on the front (plain sailcloth either way), elsewhere the plain canvas.
            private void ComputeBack()
            {
                Color32[] plain = studBase;
                for (int i = 0; i < work.Length; i++)
                {
                    Color32 f = work[i];
                    if (f.a == 0)
                    {
                        Color32 p = plain[i];
                        p.a = 0;
                        backWork[i] = p;
                    }
                    else if (f.r != canvas[i].r || f.g != canvas[i].g || f.b != canvas[i].b)
                        backWork[i] = f; // a patch
                    else
                        backWork[i] = plain[i];
                }
                BuildMips(backWork, ref backMips);
            }

            // Box-filtered mip chain, as Texture2D.Apply(true) would make it, but off the main thread.
            private void BuildMips(Color32[] work, ref Color32[][] mips)
            {
                int levels = mipLevels;
                if (mips == null || mips.Length != levels)
                {
                    mips = new Color32[levels][];
                    mips[0] = work;
                    for (int l = 1; l < levels; l++)
                        mips[l] = new Color32[Mathf.Max(1, width >> l) * Mathf.Max(1, height >> l)];
                }
                for (int l = 1; l < levels; l++)
                {
                    Color32[] src = mips[l - 1], dst = mips[l];
                    int sw = Mathf.Max(1, width >> (l - 1)), sh = Mathf.Max(1, height >> (l - 1));
                    int dw = Mathf.Max(1, width >> l), dh = Mathf.Max(1, height >> l);
                    for (int y = 0; y < dh; y++)
                    {
                        int y0 = Mathf.Min(y * 2, sh - 1) * sw, y1 = Mathf.Min(y * 2 + 1, sh - 1) * sw;
                        for (int x = 0; x < dw; x++)
                        {
                            int x0 = Mathf.Min(x * 2, sw - 1), x1 = Mathf.Min(x * 2 + 1, sw - 1);
                            Color32 a = src[y0 + x0], b = src[y0 + x1], c = src[y1 + x0], d = src[y1 + x1];
                            dst[y * dw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                                                          (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                        }
                    }
                }
            }

            private void Cut(Color32[] pixels, float damage) => Cut(pixels, damage, tears, bites);

            private void Cut(Color32[] pixels, float damage, Tear[] tears, Bite[] bites)
            {
                foreach (Tear t in tears)
                    t.Cut(pixels, width, height, damage);
                foreach (Bite b in bites)
                    b.Cut(pixels, width, height, damage);
                // The head is laced to the yard: holes there left the lateen rag hanging in the air (Кэп, 0.4.9).
                if (heightMap != null)
                    for (int i = 0; i < pixels.Length; i++)
                        if (heightMap[i] >= HeadBand)
                            pixels[i].a = 255;
            }

            internal float ShownPatched = -1f;
            private int[] patchLabel, patchQueue;

            // Hand-mended holes. The holes are those of (damage + patched), already cut in work. 0.5.1-0.5.6 closed
            // all of them from the edge inwards at once; Кэп asked for one hole at a time, each covered whole by its
            // own patch: smallest first, a hole is closed once the mended share covers it and all smaller ones.
            // New damage takes patches off in the reverse order. Each patch is a convex octagon around its hole (its
            // extremes along four directions, turned a little per patch) cut from plain grey sailcloth, with a dark
            // hem and a dashed stitch line inside it. Patches laid later overlap earlier ones; open holes stay open.
            private void DrawPatches(Color32[] work, float share)
            {
                int n = canvas.Length;
                if (patchLabel == null)
                {
                    patchLabel = new int[n];
                    patchQueue = new int[n];
                }
                Array.Clear(patchLabel, 0, n);

                // Flood fill every hole; patchQueue[first[h] .. first[h] + count[h]) are the pixels of hole h.
                var first = new List<int>();
                var count = new List<int>();
                var area = new List<int>();
                int tail = 0;
                for (int start = 0; start < n; start++)
                {
                    if (patchLabel[start] != 0 || work[start].a != 0)
                        continue;
                    int label = first.Count + 1, head = tail, onCloth = 0;
                    first.Add(head);
                    patchQueue[tail++] = start;
                    patchLabel[start] = label;
                    while (head < tail)
                    {
                        int i = patchQueue[head++];
                        int x = i % width, y = i / width;
                        if (heightMap == null || heightMap[i] >= 0f)
                            onCloth++;
                        if (x > 0) Visit(work, i - 1, label, ref tail);
                        if (x < width - 1) Visit(work, i + 1, label, ref tail);
                        if (y > 0) Visit(work, i - width, label, ref tail);
                        if (y < height - 1) Visit(work, i + width, label, ref tail);
                    }
                    count.Add(tail - first[label - 1]);
                    area.Add(onCloth);
                }

                var order = new List<int>(first.Count);
                long total = 0;
                for (int h = 0; h < first.Count; h++)
                {
                    order.Add(h);
                    total += area[h];
                }
                order.Sort((a, b) => area[a] != area[b] ? area[a].CompareTo(area[b]) : first[a].CompareTo(first[b]));
                var closed = new bool[first.Count + 1]; // by label
                double target = share * total + 0.5, sum = 0;
                foreach (int h in order)
                {
                    sum += area[h];
                    if (sum > target)
                        break;
                    closed[h + 1] = true;
                }

                int r = Mathf.Max(2, width / 256);
                float margin = r * 2.5f;
                var lo = new float[4];
                var hi = new float[4];
                var cs = new float[4];
                var sn = new float[4];
                foreach (int h in order)
                {
                    if (!closed[h + 1])
                        break;
                    int from = first[h], size = count[h];
                    if (size < 3)
                    {
                        for (int q = from; q < from + size; q++)
                            work[patchQueue[q]] = canvas[patchQueue[q]]; // a pinhole: just darned
                        continue;
                    }
                    long sx = 0, sy = 0;
                    for (int q = from; q < from + size; q++)
                    {
                        sx += patchQueue[q] % width;
                        sy += patchQueue[q] / width;
                    }
                    float cx = (float)sx / size, cy = (float)sy / size;
                    // Turn of this patch, steady while the hole grows: from the sail seed and a coarse cell of its centre.
                    int cell = Mathf.FloorToInt(cx / 48f) * 7919 + Mathf.FloorToInt(cy / 48f);
                    float turn = (Rand01(seed, cell, 1) - 0.5f) * 0.5f;
                    for (int k = 0; k < 4; k++)
                    {
                        float a = turn + k * Mathf.PI / 4f;
                        cs[k] = Mathf.Cos(a);
                        sn[k] = Mathf.Sin(a);
                        lo[k] = float.MaxValue;
                        hi[k] = float.MinValue;
                    }
                    for (int q = from; q < from + size; q++)
                    {
                        int i = patchQueue[q];
                        float x = i % width + 0.5f, y = i / width + 0.5f;
                        for (int k = 0; k < 4; k++)
                        {
                            float s = x * cs[k] + y * sn[k];
                            if (s < lo[k]) lo[k] = s;
                            if (s > hi[k]) hi[k] = s;
                        }
                    }
                    // Corners not cut square: the diagonal sides sit a little further out.
                    for (int k = 0; k < 4; k++)
                    {
                        float m = k % 2 == 0 ? margin : margin * 1.6f;
                        lo[k] -= m;
                        hi[k] += m;
                    }
                    float tint = 0.96f + 0.08f * Rand01(seed, cell, 2);
                    // Bounding box of the patch: around its turned rectangle (directions 0 and 2).
                    float bx0 = float.MaxValue, by0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue;
                    for (int c = 0; c < 4; c++)
                    {
                        float u = (c & 1) == 0 ? lo[0] : hi[0], v = (c & 2) == 0 ? lo[2] : hi[2];
                        float x = u * cs[0] + v * cs[2], y = u * sn[0] + v * sn[2];
                        bx0 = Mathf.Min(bx0, x); bx1 = Mathf.Max(bx1, x);
                        by0 = Mathf.Min(by0, y); by1 = Mathf.Max(by1, y);
                    }
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(bx0)), x1 = Mathf.Min(width - 1, Mathf.CeilToInt(bx1));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(by0)), y1 = Mathf.Min(height - 1, Mathf.CeilToInt(by1));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int i = y * width + x;
                            if (work[i].a == 0 && !closed[patchLabel[i]])
                                continue; // a hole not mended yet
                            float px = x + 0.5f, py = y + 0.5f, edge = float.MaxValue, along = 0f;
                            for (int k = 0; k < 4; k++)
                            {
                                float s = px * cs[k] + py * sn[k];
                                float e = Mathf.Min(s - lo[k], hi[k] - s);
                                if (e < edge)
                                {
                                    edge = e;
                                    along = px * -sn[k] + py * cs[k]; // position along that side, for the dashes
                                }
                            }
                            if (edge < 0f)
                                continue;
                            // Plain undyed sailcloth, light grey, with the weave of the sail's own canvas (unpainted:
                            // the weave of a painted canvas showed the emblem on the patches, Кэп).
                            Color32 w = rawStud != null ? rawStud[i] : rawCanvas[i];
                            float weave = Mathf.Clamp((w.r * 3 + w.g * 6 + w.b) / (rawStud != null ? plainLum : rawLum), 0.85f, 1.12f);
                            var c = new Color(214f, 212f, 204f) * weave;
                            float kk;
                            if (edge < r * 0.9f)
                                kk = 0.6f;                                                     // hem
                            else if (edge > r * 1.7f && edge < r * 2.5f)
                                kk = Mathf.Repeat(along / (r * 2.2f), 1f) < 0.55f ? 0.68f : 1.05f; // stitches
                            else
                                kk = 1.05f;
                            kk *= tint;
                            work[i] = new Color32((byte)Mathf.Min(255f, c.r * kk), (byte)Mathf.Min(255f, c.g * kk),
                                                  (byte)Mathf.Min(255f, c.b * kk), 255);
                        }
                }
            }

            private void Visit(Color32[] work, int i, int label, ref int tail)
            {
                if (patchLabel[i] != 0 || work[i].a != 0)
                    return;
                patchLabel[i] = label;
                patchQueue[tail++] = i;
            }

            private static float Rand01(int seed, int a, int b)
            {
                unchecked
                {
                    uint h = (uint)(seed * 668265263 + a * 374761393 + b * 2246822519);
                    h = (h ^ (h >> 13)) * 1274126177;
                    return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
                }
            }

            // ------------------------------------------------------------ torn off at 100%

            private bool EnsureFrame()
            {
                if (hasFrame)
                    return true;
                if (frameFailed || sail.currentUnroll < 0.9f)
                    return false;

                Cloth cloth = sail.cloth;
                Mesh mesh = smr != null ? smr.sharedMesh : null;
                Vector2[] uvs = mesh != null && mesh.isReadable ? mesh.uv : null;
                // 0.4.0 read Cloth.vertices through the cloth transform and got the sides as "down" (gaff: 0.13 m tall).
                // The rendered sail is what the player sees, so take the skinned mesh as drawn. BakeMesh in this Unity
                // already applies the renderer's scale, so only position and rotation are added.
                Vector3[] verts = null;
                if (smr != null)
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked);
                    verts = baked.vertices;
                    UnityEngine.Object.Destroy(baked);
                }
                if (verts == null || uvs == null || verts.Length != uvs.Length || verts.Length < 3 ||
                    cloth == null)
                {
                    frameFailed = true;
                    Plugin.Log.LogWarning($"TEAROFF {sail.sailName}: no frame (baked verts {verts?.Length}, uvs {uvs?.Length}, " +
                                          $"coefficients {cloth?.coefficients.Length}, readable {mesh?.isReadable})");
                    return false;
                }
                Matrix4x4 toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);

                // Least squares h = a*u + b*v + c over the rendered vertices.
                double su = 0, sv = 0, suu = 0, svv = 0, suv = 0, sh = 0, suh = 0, svh = 0;
                int n = verts.Length;
                float hMin = float.MaxValue, hMax = float.MinValue;
                var h = new float[n];
                for (int i = 0; i < n; i++)
                {
                    h[i] = Measure(toWorld.MultiplyPoint3x4(verts[i]));
                    hMin = Mathf.Min(hMin, h[i]);
                    hMax = Mathf.Max(hMax, h[i]);
                    double u = uvs[i].x, v = uvs[i].y;
                    su += u; sv += v; suu += u * u; svv += v * v; suv += u * v;
                    sh += h[i]; suh += u * h[i]; svh += v * h[i];
                }
                // Solve [suu suv su; suv svv sv; su sv n] * [a b c] = [suh svh sh] by Cramer's rule.
                double det = suu * (svv * n - sv * sv) - suv * (suv * n - sv * su) + su * (suv * sv - svv * su);
                float span = hMax - hMin;
                if (Math.Abs(det) < 1e-12 || span < 0.5f)
                {
                    frameFailed = true;
                    Plugin.Log.LogWarning($"TEAROFF {sail.sailName}: degenerate frame (det {det:E2}, height span {span:F2} m)");
                    return false;
                }
                double a = (suh * (svv * n - sv * sv) - suv * (svh * n - sv * sh) + su * (svh * sv - svv * sh)) / det;
                double b = (suu * (svh * n - sh * sv) - suh * (suv * n - sv * su) + su * (suv * sh - svh * su)) / det;
                double c = (suu * (svv * sh - svh * sv) - suv * (suv * sh - svh * su) + suh * (suv * sv - svv * su)) / det;
                fa = (float)(a / span);
                fb = (float)(b / span);
                fc = (float)((c - hMin) / span);
                worldLow = hMin;
                worldSpan = span;
                hasFrame = true;
                heightMap = RasterHeights(mesh.triangles, uvs, h, hMin, span);

                // Cloth particles are the mesh vertices with duplicates (UV seams) welded, in order of first
                // appearance: the lateen has 309 vertices but 269 particles. Without a match the mask is still cut,
                // only the cloth is not let loose.
                ClothSkinningCoefficient[] co = cloth.coefficients;
                int[] first = WeldedFirstVertex(mesh.vertices, co.Length);
                if (first == null)
                {
                    Plugin.Log.LogWarning($"TEAROFF {sail.sailName}: {co.Length} cloth particles do not match the {n} vertices, " +
                                          "cloth stays as it is");
                    return true;
                }
                vertexHeight = new float[co.Length];
                int pinned = 0, pinnedLow = 0, pinnedHigh = 0;
                float mdMin = float.MaxValue, mdMax = 0f;
                for (int i = 0; i < co.Length; i++)
                {
                    float t = (h[first[i]] - hMin) / span;
                    vertexHeight[i] = t;
                    // How the cloth is held, for tuning: how many particles are pinned (maxDistance ~0) by height band.
                    float md = co[i].maxDistance;
                    mdMin = Mathf.Min(mdMin, md);
                    mdMax = Mathf.Max(mdMax, md);
                    if (md > 0.01f)
                        continue;
                    pinned++;
                    if (t < 0.2f) pinnedLow++;
                    if (t > 0.8f) pinnedHigh++;
                }
                Plugin.Debug($"TEAROFF {sail.sailName}: {n} verts, {co.Length} particles, height span {span:F2} m, up in uv ({fa:F2},{fb:F2}), " +
                                   $"maxDistance {mdMin:F2}..{mdMax:F2}, pinned {pinned} (low {pinnedLow}, high {pinnedHigh})");
                return true;
            }

            // For each cloth particle, the first mesh vertex at its position; null if the count does not come out.
            private static int[] WeldedFirstVertex(Vector3[] verts, int particles)
            {
                if (verts.Length == particles)
                {
                    var same = new int[particles];
                    for (int i = 0; i < particles; i++)
                        same[i] = i;
                    return same;
                }
                var seen = new Dictionary<Vector3, int>();
                var first = new List<int>();
                for (int i = 0; i < verts.Length; i++)
                {
                    if (seen.ContainsKey(verts[i]))
                        continue;
                    seen[verts[i]] = first.Count;
                    first.Add(i);
                }
                return first.Count == particles ? first.ToArray() : null;
            }

            // What "up" means for tearing off, measured from the rig so it holds while the boat moves on: height for
            // most sails; for a lateen, nearness to its yard, so the rag stays hanging from the yard. 0.4.8 measured
            // from the mast: the cut came out a trapezoid and the pins at the mast foot held the rag flat.
            private float Measure(Vector3 world)
            {
                if (sail.category == SailCategory.lateen && FindYard())
                {
                    Vector3 c = yard.TransformPoint(yardCenter);
                    Vector3 dir = yard.TransformDirection(yardAxis).normalized;
                    return -Vector3.Cross(world - c, dir).magnitude;
                }
                Transform mast = sail.transform.parent != null ? sail.transform.parent : sail.transform;
                Vector3 d = world - mast.position;
                return sail.category == SailCategory.lateen ? -new Vector2(d.x, d.z).magnitude : d.y;
            }

            private Transform yard;
            private Vector3 yardCenter, yardAxis;
            private bool yardSearched;

            // The yard is the longest plain spar mesh on the sail (on the dhow lateen "Cylinder_004"); cloth, colliders,
            // rope attachments and the furled roll are skipped.
            private bool FindYard()
            {
                if (yardSearched)
                    return yard != null;
                yardSearched = true;
                float best = 0f;
                foreach (MeshFilter mf in sail.GetComponentsInChildren<MeshFilter>(true))
                {
                    string n = mf.name;
                    if (mf.sharedMesh == null || n.StartsWith("col", StringComparison.Ordinal) || n.StartsWith("rope", StringComparison.Ordinal) ||
                        n.StartsWith("furled", StringComparison.Ordinal) || n.StartsWith("wind", StringComparison.Ordinal) ||
                        n.StartsWith("SAIL", StringComparison.Ordinal) || n.StartsWith("sail", StringComparison.Ordinal))
                        continue;
                    Bounds b = mf.sharedMesh.bounds;
                    Vector3 s = mf.transform.lossyScale;
                    Vector3 size = new Vector3(b.size.x * Mathf.Abs(s.x), b.size.y * Mathf.Abs(s.y), b.size.z * Mathf.Abs(s.z));
                    int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                    if (size[axis] <= best)
                        continue;
                    best = size[axis];
                    yard = mf.transform;
                    yardCenter = b.center;
                    yardAxis = Vector3.zero;
                    yardAxis[axis] = 1f;
                }
                Plugin.Debug(yard != null
                    ? $"TEAROFF {sail.sailName}: yard '{yard.name}', {best:F1} m long"
                    : $"TEAROFF {sail.sailName}: no yard found, measuring from the mast");
                return yard != null;
            }

            // Height 0..1 of a UV point on the set sail.
            private float HeightAt(float u, float v) => fa * u + fb * v + fc;

            private float[] RasterHeights(int[] tris, Vector2[] uvs, float[] h, float hMin, float span)
            {
                var map = new float[width * height];
                for (int i = 0; i < map.Length; i++)
                    map[i] = -1f;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int ia = tris[i], ib = tris[i + 1], ic = tris[i + 2];
                    Vector2 a = ToPixel(uvs[ia]), b = ToPixel(uvs[ib]), c = ToPixel(uvs[ic]);
                    float ta = (h[ia] - hMin) / span, tb = (h[ib] - hMin) / span, tc = (h[ic] - hMin) / span;
                    float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                    if (Mathf.Abs(area) < 1e-6f)
                        continue;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                    int x1 = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                    int y1 = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = x + 0.5f, py = y + 0.5f;
                            float wa = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                            float wb = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                            float wc = 1f - wa - wb;
                            // A little slack so pixels on shared edges are not left out.
                            if (wa < -0.02f || wb < -0.02f || wc < -0.02f)
                                continue;
                            map[y * width + x] = wa * ta + wb * tb + wc * tc;
                        }
                }
                return map;
            }

            private Vector2 ToPixel(Vector2 p) =>
                new Vector2((p.x - uv.xMin) / uv.width * width, (p.y - uv.yMin) / uv.height * height);

            private void CutLowerPart()
            {
                // Position along the edge, perpendicular to "up" in UV, drives the ragged line. A weak fit (a lateen
                // measured from the mast on both sides) gives no direction, then any steady UV mix will do.
                float g = Mathf.Sqrt(fa * fa + fb * fb);
                for (int y = 0; y < height; y++)
                {
                    float v = uv.yMin + (y + 0.5f) / height * uv.height;
                    for (int x = 0; x < width; x++)
                    {
                        float u = uv.xMin + (x + 0.5f) / width * uv.width;
                        float mapped = heightMap != null ? heightMap[y * width + x] : -1f;
                        float t = mapped >= 0f ? mapped : HeightAt(u, v);
                        float s = g > 0.5f ? (-fb * u + fa * v) / g : u * 0.8f + v * 0.6f;
                        float line = CutHeight + 0.14f * (Noise.Smooth(s * 5f, seed, 7) - 0.5f)
                                               + 0.06f * (Noise.Smooth(s * 23f, seed, 8) - 0.5f)
                                               + 0.02f * (Noise.Smooth(s * 90f, seed, 9) - 0.5f);
                        if (t < line)
                            work[y * width + x].a = 0;
                    }
                }
            }

            // Below the tear line the cloth is set free; just above it the hold eases off, so the rag swings.
            private void SetClothLoose(bool loose)
            {
                Cloth cloth = sail.cloth;
                if (cloth == null)
                    return;
                if (!loose)
                {
                    if (original != null)
                    {
                        cloth.coefficients = original;
                        original = null;
                        TornCloths.Remove(cloth);
                        GentleCloths.Remove(cloth);
                        cloth.useGravity = originalGravity;
                        cloth.bendingStiffness = originalBending;
                        cloth.randomAcceleration = originalRandom;
                        cloth.useTethers = originalTethers;
                        cloth.clothSolverFrequency = originalSolver;
                        cloth.sleepThreshold = originalSleep;
                        cloth.capsuleColliders = originalCapsules;
                        cloth.sphereColliders = originalSpheres;
                        Plugin.Debug($"TEAROFF {sail.sailName}: cloth restored");
                    }
                    return;
                }
                if (original != null || vertexHeight == null || blowups >= 3)
                    return;

                // Most cloth vertices are already free (maxDistance = float.MaxValue); only a few are pinned (0) to the
                // yards and corners. Pins in the lower part let go, pins just above the tear get slack, and the head
                // along the yard is pinned all the way (0.4.9: the lateen rag sagged off its yard and looked afloat).
                // After a blow-up (the balanced lug, three times in a row in 0.4.9) free particles get a finite reach.
                // The balanced lugs come with tethers on, and with the full treatment their cloth blew up every time,
                // flashing a white cloud before CheckCloth caught it (Кэп, 0.4.12). Gentle from the start for them.
                if (cloth.useTethers && !gentle)
                    gentle = true;
                original = cloth.coefficients;
                var co = (ClothSkinningCoefficient[])original.Clone();
                const float slack = 4f; // how far (m) the loose cloth may move away from its normal shape
                float free = blowups >= 2 ? worldSpan * 1.5f : float.MaxValue;
                int freed = 0, eased = 0, headPinned = 0;
                for (int i = 0; i < co.Length && i < vertexHeight.Length; i++)
                {
                    float t = vertexHeight[i];
                    if (t < (KeepsWholeCloth ? HeadBand : CutHeight + 0.1f))
                    {
                        if (co[i].maxDistance < float.MaxValue)
                            freed++;
                        co[i].maxDistance = free;
                    }
                    else if (t >= HeadBand)
                    {
                        if (co[i].maxDistance > 0f)
                            headPinned++;
                        co[i].maxDistance = 0f;
                    }
                    else if (!gentle && t < CutHeight + 0.35f && co[i].maxDistance < slack)
                    {
                        co[i].maxDistance = slack;
                        eased++;
                    }
                    else if (co[i].maxDistance > free)
                        co[i].maxDistance = free;
                }
                cloth.coefficients = co;
                Plugin.Debug($"TEAROFF {sail.sailName}: cloth before: tethers {cloth.useTethers}, self collision {cloth.selfCollisionDistance:F2}, " +
                                   $"capsules {cloth.capsuleColliders?.Length}, spheres {cloth.sphereColliders?.Length}, virtual {cloth.useVirtualParticles:F2}, " +
                                   $"sleep {cloth.sleepThreshold:F3}, friction {cloth.friction:F2}, world velocity {cloth.worldVelocityScale:F2}, " +
                                   $"world acceleration {cloth.worldAccelerationScale:F2}, continuous {cloth.enableContinuousCollision}");
                originalSleep = cloth.sleepThreshold;
                originalCapsules = cloth.capsuleColliders;
                originalSpheres = cloth.sphereColliders;
                // A cloth at rest sleeps and did not notice new coefficients: the torn lateen only came loose after
                // being furled and set again (Кэп, 0.4.9).
                cloth.sleepThreshold = Mathf.Min(originalSleep, 0.001f);
                if (gentle)
                {
                    cloth.capsuleColliders = new CapsuleCollider[0];
                    cloth.sphereColliders = new ClothSphereColliderPair[0];
                }

                // The rag hangs under its own weight and folds easily; damping and gusts are set per frame by TornClothPatch.
                // In a storm 0.4.2 stretched the rag badly: the freed lower part (invisible, but still simulated) hangs
                // on a few pins. Tethers keep every particle within its rest distance of the pins, and more solver
                // steps hold the cloth together; TornClothPatch also eases the wind on it.
                originalGravity = cloth.useGravity;
                originalBending = cloth.bendingStiffness;
                originalRandom = cloth.randomAcceleration;
                originalTethers = cloth.useTethers;
                originalSolver = cloth.clothSolverFrequency;
                cloth.useGravity = true;
                if (!gentle)
                {
                    cloth.bendingStiffness = Mathf.Min(originalBending, 0.2f);
                    cloth.useTethers = true;
                    cloth.clothSolverFrequency = Mathf.Max(originalSolver, 360f);
                }
                else
                {
                    cloth.useTethers = false;
                    GentleCloths.Add(cloth);
                }
                TornCloths.Add(cloth);
                cloth.enabled = false;
                cloth.enabled = true;
                Plugin.Debug($"TEAROFF {sail.sailName}: {freed} pins let go, {eased} given {slack:F1} m slack, {headPinned} pinned at the yard, " +
                                   $"free reach {(free < float.MaxValue ? free.ToString("F1") + " m" : "unlimited")}, gentle {gentle}; " +
                                   $"gravity {originalGravity}, bending {originalBending:F2}, stretching {cloth.stretchingStiffness:F2}, " +
                                   $"random {originalRandom}, tethers {originalTethers}, solver {originalSolver:F0} Hz");
            }

            // Ropes tied to the part that is gone (sheets on the lower corners) are hidden until the sail is mended.
            private void HideLowerRopes(bool hide)
            {
                if (!hide)
                {
                    foreach (KeyValuePair<RopeEffect, Transform> kv in hiddenRopes)
                    {
                        if (kv.Key == null)
                            continue;
                        kv.Key.attachment = kv.Value;
                        kv.Key.ForceDisplay();
                    }
                    if (hiddenRopes.Count > 0)
                        Plugin.Debug($"TEAROFF {sail.sailName}: {hiddenRopes.Count} ropes shown again");
                    hiddenRopes.Clear();
                    return;
                }
                if (hiddenRopes.Count > 0 || !hasFrame)
                    return;

                Transform boat = sail.shipRigidbody != null ? sail.shipRigidbody.transform : sail.transform.root;
                // Either end may be the sail's: sheets run from the boat to an attachment on the sail, reef lines
                // (0.4.2 missed them) start on the sail's lower corner and run up to the yard.
                foreach (RopeEffect rope in boat.GetComponentsInChildren<RopeEffect>(true))
                {
                    Transform at = rope.attachment;
                    if (at == null)
                        continue;
                    bool fromSail = rope.transform.IsChildOf(sail.transform), toSail = at.IsChildOf(sail.transform);
                    if (!fromSail && !toSail)
                        continue;
                    // The studding sails' own rig (StuddingSails) hangs from the yard and stays with them.
                    if (rope.name.StartsWith("Studding", StringComparison.Ordinal) || at.name.StartsWith("Studding", StringComparison.Ordinal))
                        continue;
                    float t = float.MaxValue;
                    if (fromSail)
                        t = (Measure(rope.transform.position) - worldLow) / worldSpan;
                    if (toSail)
                        t = Mathf.Min(t, (Measure(at.position) - worldLow) / worldSpan);
                    bool lower = t < CutHeight + 0.15f;
                    Plugin.Debug($"TEAROFF {sail.sailName}: rope {rope.name} -> {at.name}, lower end at height {t:F2}{(lower ? ", hidden" : "")}");
                    if (!lower)
                        continue;
                    hiddenRopes.Add(new KeyValuePair<RopeEffect, Transform>(rope, at));
                    rope.attachment = null;
                }
            }

            // ------------------------------------------------------------ helpers

            // Sail canvas is sewn from cloths; their seams show as stripes. Stripes along V vary the column means.
            private string SeamAxis(out Vector2 along)
            {
                var col = new float[width];
                var row = new float[height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Color32 c = canvas[y * width + x];
                        float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                        col[x] += l / height;
                        row[y] += l / width;
                    }
                float dc = MeanStep(col), dr = MeanStep(row);
                if (dc > dr * 1.2f)
                {
                    along = new Vector2(0f, 1f);
                    return $"along V ({dc:F2} vs {dr:F2})";
                }
                if (dr > dc * 1.2f)
                {
                    along = new Vector2(1f, 0f);
                    return $"along U ({dc:F2} vs {dr:F2})";
                }
                along = new Vector2(0f, 1f);
                return $"unclear, V assumed ({dc:F2} vs {dr:F2})";
            }

            private static float MeanStep(float[] a)
            {
                float sum = 0f;
                for (int i = 1; i < a.Length; i++)
                    sum += Mathf.Abs(a[i] - a[i - 1]);
                return sum / Mathf.Max(1, a.Length - 1);
            }

            // Junk sail cloth has no UV unwrap (every vertex on the same UV point, a plain-coloured sail), so a hole
            // mask has nowhere to land. Give the mesh a flat projection on its two longest axes. The mesh is shared
            // by all junk sails of that prefab, which all had the same useless UVs; the change lasts until the game
            // restarts.
            private static bool MeanUv(SkinnedMeshRenderer smr, out Vector2 mean)
            {
                mean = Vector2.zero;
                Mesh mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || !mesh.isReadable)
                    return false;
                Vector2[] uvs = mesh.uv;
                if (uvs.Length == 0)
                    return false;
                foreach (Vector2 p in uvs)
                    mean += p;
                mean /= uvs.Length;
                return true;
            }

            private static readonly Dictionary<Mesh, Vector2> Flattened = new Dictionary<Mesh, Vector2>();

            private static string FlattenDegenerateUv(SkinnedMeshRenderer smr, out Vector2 oldUv)
            {
                oldUv = Vector2.zero;
                Mesh mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || !mesh.isReadable)
                    return null;
                if (Flattened.TryGetValue(mesh, out oldUv))
                    return "flat UVs (mesh shared with another junk sail)";
                Vector2[] uvs = mesh.uv;
                Vector3[] verts = mesh.vertices;
                if (uvs.Length == 0 || uvs.Length != verts.Length)
                    return null;
                Vector2 min = uvs[0], max = uvs[0];
                foreach (Vector2 p in uvs)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                if (max.x - min.x >= 1e-3f && max.y - min.y >= 1e-3f)
                    return null;
                oldUv = uvs[0];

                Bounds b = mesh.bounds;
                Vector3 size = b.size;
                int a0 = 0, a1 = 1, a2 = 2; // axes sorted by extent, largest first
                if (size[a1] > size[a0]) { int t = a0; a0 = a1; a1 = t; }
                if (size[a2] > size[a0]) { int t = a0; a0 = a2; a2 = t; }
                if (size[a2] > size[a1]) { int t = a1; a1 = a2; a2 = t; }
                if (size[a1] < 1e-4f)
                    return null;
                var flat = new Vector2[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                    flat[i] = new Vector2((verts[i][a0] - b.min[a0]) / size[a0], (verts[i][a1] - b.min[a1]) / size[a1]);
                mesh.uv = flat;
                Flattened[mesh] = oldUv;
                return $"flat UVs made on mesh axes {a0},{a1} (old UV range {max.x - min.x:F4} x {max.y - min.y:F4})";
            }

            private static int Size(float pixels)
            {
                return Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.RoundToInt(pixels)), 256, 1024);
            }

            private static Rect UvBounds(SkinnedMeshRenderer smr)
            {
                Mesh mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || !mesh.isReadable)
                    return new Rect(0f, 0f, 1f, 1f);
                Vector2[] uv = mesh.uv;
                if (uv == null || uv.Length == 0)
                    return new Rect(0f, 0f, 1f, 1f);
                Vector2 min = uv[0], max = uv[0];
                foreach (Vector2 p in uv)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                if (max.x - min.x < 1e-3f || max.y - min.y < 1e-3f)
                    return new Rect(0f, 0f, 1f, 1f);
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }

            // FNV-1a: string.GetHashCode is not guaranteed to be stable.
            private static int Hash(string s)
            {
                uint h = 2166136261;
                foreach (char c in s)
                    h = (h ^ c) * 16777619;
                return (int)h;
            }
        }

        // A tear: a wandering slit, mostly along the cloths (seams give way first), some slanting.
        // It starts at the foot or head, or in the middle and runs both ways; some fork.
        // It opens once damage passes its threshold, then grows in length and, near full damage, in width.
        private sealed class Tear
        {
            private const int Count = 11;
            private const float StepLen = 0.003f; // UV units between points; below the smallest hole radius

            private sealed class Path
            {
                internal Vector2[] Points;
                internal float[] Widths;
                internal int OpensAt;   // main path point index where a branch starts (0 for main paths)
                internal Path Parent;
            }

            private readonly List<Path> paths = new List<Path>();
            private float threshold, halfWidth;

            internal static Tear[] Make(int seed, Vector2 along, Outline outline)
            {
                var rnd = new System.Random(seed);
                var tears = new Tear[Count];
                for (int i = 0; i < Count; i++)
                {
                    var t = new Tear();
                    // Thresholds spread over 0..0.85: the first tear shows at a few percent, all are open by 85%.
                    t.threshold = i == 0 ? 0f : (i + (float)rnd.NextDouble() - 0.5f) / Count * 0.85f;
                    t.halfWidth = 0.004f + 0.006f * (float)rnd.NextDouble();

                    // Mostly along the cloths, a few degrees off; one in eight slants a little more.
                    float bias = rnd.NextDouble() < 0.12
                        ? (rnd.NextDouble() < 0.5 ? -1f : 1f) * (0.2f + 0.25f * (float)rnd.NextDouble())
                        : ((float)rnd.NextDouble() + (float)rnd.NextDouble() - 1f) * 0.2f;
                    outline.Sample(rnd, out Vector2 edgePoint, out Vector2 inward);

                    if (rnd.NextDouble() < 0.35)
                    {
                        // Starts in the open cloth and runs both ways.
                        Vector2 start = Vector2.Lerp(outline.Center, edgePoint, 0.15f + 0.55f * (float)rnd.NextDouble());
                        float len = 0.1f + 0.2f * (float)rnd.NextDouble();
                        t.paths.Add(Walk(rnd, start, Rotate(along, bias), len, t.halfWidth));
                        t.paths.Add(Walk(rnd, start, Rotate(-along, bias), len * (0.6f + 0.4f * (float)rnd.NextDouble()), t.halfWidth));
                    }
                    else
                    {
                        // Starts at the sail's edge and runs inward along the cloths. 0.4.1 ran tears from the side
                        // edges straight across the cloths, which read as unnatural crosses; a side edge instead
                        // splits a seam a little in from it, drifting slightly inward.
                        float facing = Vector2.Dot(along, inward);
                        Vector2 start = edgePoint - inward * 0.01f;
                        Vector2 dir;
                        if (Mathf.Abs(facing) >= 0.4f)
                            dir = facing > 0f ? along : -along;
                        else
                        {
                            dir = rnd.NextDouble() < 0.5 ? along : -along;
                            start = edgePoint + inward * (0.03f + 0.07f * (float)rnd.NextDouble());
                            dir = (dir + inward * 0.15f).normalized;
                        }
                        float len = 0.2f + 0.35f * (float)rnd.NextDouble();
                        t.paths.Add(Walk(rnd, start, Rotate(dir, bias), len, t.halfWidth));
                    }

                    // A fork off the main path, slanting away.
                    if (rnd.NextDouble() < 0.35)
                    {
                        Path main = t.paths[0];
                        int at = (int)(main.Points.Length * (0.35f + 0.35f * (float)rnd.NextDouble()));
                        Vector2 dir = main.Points[Mathf.Min(at + 1, main.Points.Length - 1)] - main.Points[at];
                        float turn = (rnd.NextDouble() < 0.5 ? -1f : 1f) * (0.3f + 0.25f * (float)rnd.NextDouble());
                        float len = (main.Points.Length - at) * StepLen * (0.3f + 0.3f * (float)rnd.NextDouble());
                        Path branch = Walk(rnd, main.Points[at], Rotate(dir.normalized, turn), len, t.halfWidth * 0.7f);
                        branch.OpensAt = at;
                        branch.Parent = main;
                        t.paths.Add(branch);
                    }
                    tears[i] = t;
                }
                return tears;
            }

            // A random walk that keeps returning to its heading: a slit wandering a little, never a straight cut.
            private static Path Walk(System.Random rnd, Vector2 start, Vector2 heading, float length, float hw)
            {
                int n = Mathf.Max(2, Mathf.CeilToInt(length / StepLen));
                var p = new Path { Points = new Vector2[n], Widths = new float[n] };
                float baseAngle = Mathf.Atan2(heading.y, heading.x), angle = baseAngle, turn = 0f;
                float wobble = 1f, wobbleTarget = 1f;
                Vector2 pos = start;
                for (int i = 0; i < n; i++)
                {
                    p.Points[i] = pos;
                    if (i % 8 == 0)
                        wobbleTarget = 0.45f + 1.1f * (float)rnd.NextDouble();
                    wobble = Mathf.Lerp(wobble, wobbleTarget, 0.15f);
                    p.Widths[i] = hw * wobble;

                    turn = turn * 0.8f + ((float)rnd.NextDouble() - 0.5f) * 0.035f;
                    angle += turn;
                    angle = Mathf.Lerp(angle, baseAngle, 0.08f);
                    // Small zigzag across the heading: thread ends, not a smooth curve.
                    float jag = ((float)rnd.NextDouble() - 0.5f) * 0.6f;
                    pos += new Vector2(Mathf.Cos(angle + jag), Mathf.Sin(angle + jag)) * StepLen;
                }
                return p;
            }

            internal void Cut(Color32[] pixels, int w, int h, float damage)
            {
                if (damage <= threshold || damage < 0.01f)
                    return;
                float grow = Mathf.Clamp01((damage - threshold) / 0.35f);
                // Late damage: the slits open up into gaps.
                float scale = (0.6f + 0.6f * grow) * (1f + 3f * Mathf.Max(0f, damage - 0.6f));

                foreach (Path p in paths)
                {
                    int shown;
                    if (p.Parent == null)
                        shown = Mathf.Max(8, Mathf.RoundToInt(p.Points.Length * grow));
                    else
                    {
                        // A fork opens once the main slit has run past it.
                        int parentShown = Mathf.RoundToInt(p.Parent.Points.Length * grow);
                        float f = (float)(parentShown - p.OpensAt) / Mathf.Max(1, p.Parent.Points.Length - p.OpensAt);
                        shown = Mathf.RoundToInt(p.Points.Length * Mathf.Clamp01(f));
                    }
                    shown = Mathf.Min(shown, p.Points.Length);
                    for (int i = 0; i < shown; i++)
                    {
                        float taper = Mathf.Max(0.25f, Mathf.Sqrt(1f - (float)i / shown));
                        Stamp(pixels, w, h, p.Points[i], p.Widths[i] * scale * taper);
                    }
                }
            }

            internal static void Stamp(Color32[] pixels, int w, int h, Vector2 c, float r)
            {
                float rx = Mathf.Max(1.2f, r * w), ry = Mathf.Max(1.2f, r * h);
                float cx = c.x * w, cy = c.y * h;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - rx)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(cx + rx));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - ry)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(cy + ry));
                for (int y = y0; y <= y1; y++)
                {
                    float dy = (y + 0.5f - cy) / ry;
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = (x + 0.5f - cx) / rx;
                        if (dx * dx + dy * dy < 1f)
                            pixels[y * w + x].a = 0;
                    }
                }
            }

            private static Vector2 Rotate(Vector2 v, float a)
            {
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
            }
        }

        // The sail's edge in canvas coordinates (0..1 over the baked UV rectangle): mesh edges used by one triangle.
        // A jib is a triangle inside its rectangle, so its rectangle edges are not cloth.
        private sealed class Outline
        {
            private readonly List<Vector2> from = new List<Vector2>(), to = new List<Vector2>();
            private readonly List<float> upTo = new List<float>();
            private float total;
            internal Vector2 Center;
            internal string Info;

            internal static Outline FromMesh(Mesh mesh, Rect uv) => FromMeshes(new[] { mesh }, uv);

            // Several cloths (the two studding sails) make one outline.
            internal static Outline FromMeshes(Mesh[] meshes, Rect uv)
            {
                var o = new Outline();
                foreach (Mesh mesh in meshes)
                {
                    if (mesh == null || !mesh.isReadable)
                        continue;
                    Vector2[] uvs = mesh.uv;
                    int[] tris = mesh.triangles;
                    var uses = new Dictionary<long, int>();
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int p = tris[i + k], q = tris[i + (k + 1) % 3];
                            long key = Math.Min(p, q) * 1000000L + Math.Max(p, q);
                            uses.TryGetValue(key, out int c);
                            uses[key] = c + 1;
                        }
                    foreach (KeyValuePair<long, int> kv in uses)
                    {
                        if (kv.Value != 1)
                            continue;
                        Vector2 a = uvs[(int)(kv.Key / 1000000L)], b = uvs[(int)(kv.Key % 1000000L)];
                        o.Add(new Vector2((a.x - uv.xMin) / uv.width, (a.y - uv.yMin) / uv.height),
                              new Vector2((b.x - uv.xMin) / uv.width, (b.y - uv.yMin) / uv.height));
                    }
                }
                if (o.total <= 0f)
                {
                    o.Add(new Vector2(0f, 0f), new Vector2(1f, 0f));
                    o.Add(new Vector2(1f, 0f), new Vector2(1f, 1f));
                    o.Add(new Vector2(1f, 1f), new Vector2(0f, 1f));
                    o.Add(new Vector2(0f, 1f), new Vector2(0f, 0f));
                    o.Info = "rectangle (mesh not readable)";
                }
                else
                    o.Info = $"{o.from.Count} edges, length {o.total:F2}";

                Vector2 sum = Vector2.zero;
                for (int i = 0; i < o.from.Count; i++)
                    sum += (o.from[i] + o.to[i]) * 0.5f * (o.to[i] - o.from[i]).magnitude;
                o.Center = sum / o.total;
                return o;
            }

            private void Add(Vector2 a, Vector2 b)
            {
                from.Add(a);
                to.Add(b);
                total += (b - a).magnitude;
                upTo.Add(total);
            }

            // A random point on the edge (even along its length) and the direction into the cloth.
            internal void Sample(System.Random rnd, out Vector2 point, out Vector2 inward)
            {
                float at = (float)rnd.NextDouble() * total;
                int i = upTo.BinarySearch(at);
                if (i < 0)
                    i = ~i;
                i = Mathf.Min(i, from.Count - 1);
                Vector2 a = from[i], b = to[i];
                point = Vector2.Lerp(a, b, (float)rnd.NextDouble());
                Vector2 d = (b - a).normalized;
                inward = new Vector2(-d.y, d.x);
                if (Vector2.Dot(inward, Center - point) < 0f)
                    inward = -inward;
            }
        }

        // A bite out of the edge: a ragged notch of a few overlapping blobs, opening and widening with damage.
        private sealed class Bite
        {
            private const int Count = 18;

            private Vector2 edge, inward;
            private float threshold, radius, width;
            private int noiseSeed;

            internal static Bite[] Make(int seed, Outline outline)
            {
                var rnd = new System.Random(seed ^ 0x5bd1e995);
                var bites = new Bite[Count];
                for (int i = 0; i < Count; i++)
                {
                    var b = new Bite { noiseSeed = rnd.Next() };
                    outline.Sample(rnd, out b.edge, out b.inward);
                    b.threshold = 0.04f + (i + (float)rnd.NextDouble()) / Count * 0.85f;
                    b.radius = 0.02f + 0.035f * (float)rnd.NextDouble();
                    b.width = 0.8f + 1.2f * (float)rnd.NextDouble(); // along the edge, in radii
                    bites[i] = b;
                }
                return bites;
            }

            // The notch outline is a jagged star: its reach into the cloth at each angle comes from noise at two
            // scales plus rare sharp spikes (threads pulled out), so it reads as torn, not as round scoops.
            internal void Cut(Color32[] pixels, int w, int h, float damage)
            {
                if (damage <= threshold)
                    return;
                float grow = Mathf.Clamp01((damage - threshold) / 0.3f);
                float r = radius * (0.35f + 0.65f * grow) * (1f + 1.5f * Mathf.Max(0f, damage - 0.7f));
                float reach = r * 1.8f * Mathf.Max(1f, width);
                Vector2 along = new Vector2(inward.y, -inward.x);

                int x0 = Mathf.Max(0, Mathf.FloorToInt((edge.x - reach) * w)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((edge.x + reach) * w));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((edge.y - reach) * h)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((edge.y + reach) * h));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 d = new Vector2((x + 0.5f) / w, (y + 0.5f) / h) - edge;
                        float a = Vector2.Dot(d, along) / width, b = Vector2.Dot(d, inward);
                        float dist = Mathf.Sqrt(a * a + b * b);
                        if (dist > reach)
                            continue;
                        float angle = Mathf.Atan2(a, b) + Mathf.PI; // 0..2pi; the seam at pi..-pi points out of the cloth
                        float spike = Noise.Smooth(angle * 5f, noiseSeed, 2);
                        float lim = r * (0.4f + 0.5f * Noise.Smooth(angle * 2.5f, noiseSeed, 1)
                                              + 0.15f * Noise.Smooth(angle * 24f, noiseSeed, 3)
                                              + 0.5f * spike * spike * spike);
                        if (dist < lim)
                            pixels[y * w + x].a = 0;
                    }
                }
            }
        }

        private static class Noise
        {
            // Smooth value noise in 0..1.
            internal static float Smooth(float x, int seed, int channel)
            {
                int i = Mathf.FloorToInt(x);
                float f = x - i;
                f = f * f * (3f - 2f * f);
                return Mathf.Lerp(Lattice(i, seed, channel), Lattice(i + 1, seed, channel), f);
            }

            private static float Lattice(int i, int seed, int channel)
            {
                unchecked
                {
                    uint h = (uint)(i * 374761393 + seed * 668265263 + channel * 2147483647);
                    h = (h ^ (h >> 13)) * 1274126177;
                    return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
                }
            }
        }
    }
}
