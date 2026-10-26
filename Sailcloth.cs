using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Sailcloth: a new item for mending sails by hand (the plan, section 5). Hold it and keep the right mouse button
    // down on board: a bar fills while a needle sews, holes close into patches and the cloth is used up.
    // The item is a copy of the wool bale (model) handled like oakum, registered in PrefabsDirectory the way the
    // HatchTarp mod registers its oiled canvas, so it saves with the boat's items.
    // Sewing is done from the bolt lying in front of you (looked at, right button held), not from your hands
    // (Кэп, 0.5.2): GoPointer calls OnAltHeld on the held item and on the looked-at one alike.
    public class ShipItemSailcloth : ShipItem
    {
        public override void UpdateLookText()
        {
            if (!sold)
            {
                base.UpdateLookText();
                return;
            }
            lookText = held ? "" : $"{name}\n{Mathf.RoundToInt(amount / Sailcloth.FullAmount * 100f)}%\nhold RMB: mend sails";
        }

        public override void OnAltHeld()
        {
            if (!sold || SailMending.Instance == null)
                return;
            if (held)
            {
                SailMending.Instance.Say("Put the sailcloth down and look at it to sew");
                return;
            }
            SailMending.Instance.Work(this);
        }
    }

    internal static class Sailcloth
    {
        internal const string ItemName = "sailcloth";
        internal const float FullAmount = 1f;
        private const int ModelIndex = 222; // wool bale

        internal static GameObject Template;
        internal static bool Ready;
        private static GameObject holder;

        private static readonly FieldInfo HeldField = AccessTools.Field(typeof(GoPointer), "heldItem");
        private static readonly MethodInfo PickUpMethod = AccessTools.Method(typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) });

        internal static PickupableItem HeldItem(GoPointer p) => p && HeldField != null ? HeldField.GetValue(p) as PickupableItem : null;

        internal static GoPointer MousePointer()
        {
            foreach (GoPointer p in UnityEngine.Object.FindObjectsOfType<GoPointer>())
                if (p.type == GoPointer.PointerType.crosshairMouse)
                    return p;
            return null;
        }

        internal static void Inject(PrefabsDirectory pd)
        {
            GameObject[] dir = pd.directory;
            if (dir == null)
                return;
            int idx = Plugin.SailclothPrefabIndex.Value;
            if (Template == null)
            {
                Template = Build(dir, idx);
                if (Template == null)
                    return;
            }
            if (idx >= dir.Length)
            {
                Array.Resize(ref dir, idx + 1);
                pd.directory = dir;
            }
            if (dir[idx] != null && dir[idx] != Template)
            {
                Plugin.Log.LogError($"SAILCLOTH slot {idx} is taken by '{dir[idx].name}', change SailclothPrefabIndex in the config");
                Ready = false;
                return;
            }
            dir[idx] = Template;
            Ready = true;
            Plugin.Debug($"SAILCLOTH registered in slot {idx} (directory length {dir.Length})");
        }

        private static GameObject Build(GameObject[] dir, int idx)
        {
            GameObject oakum = dir.FirstOrDefault(g => g != null && g.GetComponent<ShipItemOakum>() != null);
            if (oakum == null)
            {
                Plugin.Log.LogError("SAILCLOTH oakum prefab not found, no sailcloth");
                return null;
            }
            GameObject src = ModelIndex < dir.Length && dir[ModelIndex] != null && dir[ModelIndex].GetComponent<ShipItem>() != null
                ? dir[ModelIndex] : oakum;

            if (holder == null)
            {
                holder = new GameObject("SailDamage templates");
                holder.SetActive(false); // no Awake inside an inactive object
                UnityEngine.Object.DontDestroyOnLoad(holder);
            }
            GameObject go = UnityEngine.Object.Instantiate(src, holder.transform);
            go.name = idx + " " + ItemName;

            ShipItem old = go.GetComponent<ShipItem>();
            var item = go.AddComponent<ShipItemSailcloth>();
            foreach (FieldInfo f in Fields(typeof(ShipItem)))
                f.SetValue(item, f.GetValue(old));
            ShipItem ob = oakum.GetComponent<ShipItem>();
            item.big = ob.big;
            item.mass = ob.mass;
            item.category = ob.category;
            item.wallAttachment = false;
            item.sold = false;
            item.nailed = false;
            foreach (Component c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c == old || c == item)
                    continue;
                foreach (FieldInfo f in Fields(c.GetType()))
                    if (f.FieldType.IsAssignableFrom(typeof(ShipItemSailcloth)) && ReferenceEquals(f.GetValue(c), old))
                        f.SetValue(c, item);
            }
            UnityEngine.Object.DestroyImmediate(old);
            foreach (Good g in go.GetComponentsInChildren<Good>(true))
                UnityEngine.Object.DestroyImmediate(g);

            item.name = ItemName;
            item.health = 100f;
            item.amount = FullAmount;
            item.value = Plugin.SailclothPrice.Value;
            item.inventoryScale *= 0.25f;

            SaveablePrefab sp = go.GetComponent<SaveablePrefab>();
            if (sp != null)
                Traverse.Create(sp).Field("prefabIndex").SetValue(idx);
            else
                Plugin.Log.LogError("SAILCLOTH the model has no SaveablePrefab, it will not save");

            // Its own model: a tied bundle of folded grey canvas (SailclothModel). The source's material lends the shader.
            Renderer srcRenderer = go.GetComponentInChildren<Renderer>(true);
            Material baseMat = srcRenderer != null ? srcRenderer.sharedMaterial : null;
            Plugin.Debug("SAILCLOTH source components: " + string.Join(", ",
                go.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()) + $"; {go.transform.childCount} children");
            SailclothModel.Apply(go, baseMat);
            Plugin.Debug($"SAILCLOTH template: handling '{oakum.name}', price {item.value}");
            return go;
        }

        private static System.Collections.Generic.IEnumerable<FieldInfo> Fields(Type t)
        {
            for (; t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral && !f.IsInitOnly)
                        yield return f;
        }

        // Test key: a full bolt of sailcloth into the player's hands.
        internal static IEnumerator Give(MonoBehaviour host)
        {
            GoPointer pointer = MousePointer();
            Camera cam = Camera.main;
            if (!Ready || pointer == null || cam == null || HeldItem(pointer) != null)
            {
                Plugin.Log.LogWarning($"SAILCLOTH cannot give: ready {Ready}, pointer {pointer != null}, hands busy {pointer != null && HeldItem(pointer) != null}");
                yield break;
            }
            Vector3 pos = cam.transform.position + cam.transform.forward;
            // Spawn high above, so the item's body cannot push the hull while it starts up.
            GameObject go = UnityEngine.Object.Instantiate(Template, pos + Vector3.up * 500f, Quaternion.identity);
            var item = go.GetComponent<ShipItemSailcloth>();
            Freeze(item, true);
            for (int i = 0; i < 4; i++) // wait for ShipItem.LoadAfterDelay
            {
                yield return null;
                if (item == null)
                    yield break;
                Freeze(item, true);
            }
            item.amount = FullAmount;
            item.Sell();
            item.transform.position = pos;
            try { item.ResetRigidbody(); } catch { }
            if (HeldItem(pointer) == null && PickUpMethod != null)
                PickUpMethod.Invoke(pointer, new object[] { item });
            float until = Time.time + 0.6f;
            while (Time.time < until && item != null)
            {
                Freeze(item, true);
                yield return null;
            }
            Freeze(item, false);
        }

        private static void Freeze(ShipItem item, bool on)
        {
            if (item == null || item.itemRigidbodyC == null)
                return;
            Rigidbody rb = item.itemRigidbodyC.GetComponent<Rigidbody>();
            if (rb == null)
                return;
            rb.detectCollisions = !on;
            if (on && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    [HarmonyPatch(typeof(PrefabsDirectory), "PopulateShipItems")]
    internal static class SailclothRegisterPatch
    {
        private static void Prefix(PrefabsDirectory __instance) => Sailcloth.Inject(__instance);
    }

    // Sewing: while the right mouse button is held with sailcloth in hand on board, the most damaged sail of the boat is
    // mended at MendRate m² per second; FullAmount of cloth covers SailclothArea m² of holes.
    public class SailMending : MonoBehaviour
    {
        internal static SailMending Instance;

        private float lastWork = -10f;
        private float startWork, doneWork;
        private string message;
        private float messageUntil;
        private Transform needle;
        private LineRenderer thread;
        private float phase;

        private void Awake() => Instance = this;

        private void Update()
        {
            if (Plugin.TestKeys.Value && GameState.playing && Input.GetKeyDown(KeyCode.F6))
                StartCoroutine(Sailcloth.Give(this));

            bool sewing = Time.time - lastWork < 0.15f;
            if (!sewing)
                startWork = 0f;
            AnimateNeedle(sewing);
        }

        private static float Work(Sail s) => DamageModel.Instance.HoleArea(s);
        private static float Worst(Sail s) => Mathf.Max(DamageModel.Instance.GetDamage(s), DamageModel.Instance.GetStuddingDamage(s));

        internal bool Work(ShipItemSailcloth cloth)
        {
            if (DamageModel.Instance == null)
                return false;
            Transform boat = GameState.currentBoat;
            if (boat == null)
            {
                Say("Sails can only be mended on board");
                return false;
            }
            if (cloth.amount <= 0.0001f)
            {
                Say("Out of sailcloth");
                return false;
            }
            Sail target = WorstSail(boat, out float left);
            if (target == null)
            {
                Say("Sails are intact");
                return false;
            }
            if (startWork <= 0f || Time.time - lastWork > 0.15f)
            {
                startWork = left;
                doneWork = 0f;
            }
            lastWork = Time.time;

            doneWork += Sew(target, cloth, Plugin.MendRate.Value * Time.deltaTime, "by hand");
            if (cloth == null || cloth.amount <= 0.0001f)
                Say("Out of sailcloth");
            return true;
        }

        /// <summary>The sail of the boat with the worst holes (studding sails included), null if all are whole.</summary>
        internal static Sail WorstSail(Transform boat, out float left)
        {
            Sail target = null;
            left = 0f;
            foreach (Sail s in DamageModel.BoatSails(boat))
            {
                float w = Work(s);
                left += w;
                if (w > 0.001f && (target == null || Worst(s) > Worst(target)))
                    target = s;
            }
            return target;
        }

        /// <summary>Mends up to <paramref name="m2"/> m² of the sail from the bolt; a used-up bolt goes. Returns m² mended.</summary>
        internal static float Sew(Sail target, ShipItemSailcloth cloth, float m2, string who)
        {
            m2 = Mathf.Min(m2, cloth.amount / Sailcloth.FullAmount * Plugin.SailclothArea.Value);
            float done = DamageModel.Instance.PatchArea(target, m2);
            cloth.amount = Mathf.Max(0f, cloth.amount - done / Plugin.SailclothArea.Value * Sailcloth.FullAmount);
            if (Work(target) <= 0.001f)
            {
                Plugin.Debug($"MEND {target.sailName}: patched {who}, sailcloth left {cloth.amount:F2}");
                UISoundPlayer.instance?.PlayUISound(UISounds.oakum, 1f, 1f);
            }
            cloth.UpdateLookText();
            if (cloth.amount <= 0.0001f)
            {
                // The bolt is used up: it goes (Кэп, 0.5.2).
                Plugin.Debug("MEND sailcloth used up");
                cloth.DestroyItem();
            }
            return done;
        }

        internal void Say(string text)
        {
            if (Time.time < messageUntil && message == text)
                return;
            message = text;
            messageUntil = Time.time + 2.5f;
        }

        private void OnGUI()
        {
            bool sewing = Time.time - lastWork < 0.15f && startWork > 0f;
            float w = Mathf.Min(420f, Screen.width * 0.4f), h = 22f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.72f, w, h);
            if (sewing)
            {
                float k = Mathf.Clamp01(doneWork / startWork);
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = new Color(0.93f, 0.86f, 0.68f, 0.95f);
                GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * k, rect.height - 4f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(rect.x, rect.y - h - 2f, rect.width, h), $"Mending sails: {Mathf.RoundToInt(k * 100f)}%", style);
            }
            else if (Time.time < messageUntil && message != null)
            {
                var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(rect.x, rect.y - h - 2f, rect.width, h), message, style);
            }
        }

        // ------------------------------------------------------------ needle and thread
        // Кэп (0.5.1): a sharp needle and a soft thread, and a stitch like real sewing: the needle tips down and goes
        // away under the bottom edge of the screen at an angle, comes back up from below a little to the right a
        // second later (point first, as if through the cloth), turns over and is back where it started.
        // All in camera space; the needle mesh runs along +Y from the point (y = 0) to the eye.

        private const float NeedleLength = 0.075f;
        private const float Cycle = 2.6f;
        private const int ThreadPoints = 24;
        private const float ThreadLength = 0.42f;

        private struct Key
        {
            internal float T;
            internal Vector3 Pos, Eye; // where the point is, and which way the eye lies from it
            internal Key(float t, Vector3 pos, Vector3 eye) { T = t; Pos = pos; Eye = eye.normalized; }
        }

        private static readonly Key[] Keys =
        {
            new Key(0.00f, new Vector3(0.050f, -0.120f, 0.40f), new Vector3(0.30f, 0.75f, -0.55f)), // ready, point down
            new Key(0.25f, new Vector3(0.045f, -0.150f, 0.41f), new Vector3(0.15f, 0.55f, -0.80f)), // tips down
            new Key(0.65f, new Vector3(0.020f, -0.360f, 0.47f), new Vector3(0.05f, 0.40f, -0.90f)), // gone under
            new Key(1.65f, new Vector3(0.020f, -0.360f, 0.47f), new Vector3(0.05f, 0.40f, -0.90f)), // (out of sight)
            new Key(1.66f, new Vector3(0.120f, -0.380f, 0.44f), new Vector3(0.10f, -0.85f, -0.45f)), // below, to the right
            new Key(2.10f, new Vector3(0.090f, -0.100f, 0.40f), new Vector3(0.20f, -0.70f, -0.65f)), // up, point first
            new Key(2.60f, new Vector3(0.050f, -0.120f, 0.40f), new Vector3(0.30f, 0.75f, -0.55f)), // turned over
        };

        private Vector3[] knots, knotsOld;

        private void AnimateNeedle(bool sewing)
        {
            Camera cam = Camera.main;
            if (!sewing || cam == null)
            {
                if (needle != null && needle.gameObject.activeSelf)
                {
                    needle.gameObject.SetActive(false);
                    thread.gameObject.SetActive(false);
                }
                phase = 0f;
                knots = null;
                return;
            }
            if (needle == null)
                MakeNeedle();
            if (needle.parent != cam.transform)
                needle.SetParent(cam.transform, false);
            needle.gameObject.SetActive(true);
            thread.gameObject.SetActive(true);

            phase = Mathf.Repeat(phase + Time.deltaTime, Cycle);
            int k = 0;
            while (k < Keys.Length - 2 && phase >= Keys[k + 1].T)
                k++;
            Key a = Keys[k], b = Keys[k + 1];
            float f = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a.T, b.T, phase));
            needle.localPosition = Vector3.Lerp(a.Pos, b.Pos, f);
            needle.localRotation = Quaternion.Slerp(Quaternion.FromToRotation(Vector3.up, a.Eye),
                                                     Quaternion.FromToRotation(Vector3.up, b.Eye), f);

            // Thread: a little rope of knots from the eye down to the cloth below the screen, under gravity.
            Vector3 eye = cam.transform.InverseTransformPoint(needle.TransformPoint(new Vector3(0f, NeedleLength * 0.93f, 0f)));
            Vector3 anchor = new Vector3(0.0f, -0.40f, 0.50f);
            if (knots == null)
            {
                knots = new Vector3[ThreadPoints];
                knotsOld = new Vector3[ThreadPoints];
                for (int i = 0; i < ThreadPoints; i++)
                    knots[i] = knotsOld[i] = Vector3.Lerp(eye, anchor, i / (ThreadPoints - 1f));
            }
            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            Vector3 g = cam.transform.InverseTransformDirection(Vector3.down) * 9.8f * dt * dt;
            for (int i = 1; i < ThreadPoints - 1; i++)
            {
                Vector3 v = (knots[i] - knotsOld[i]) * 0.96f;
                knotsOld[i] = knots[i];
                knots[i] += v + g;
            }
            float seg = ThreadLength / (ThreadPoints - 1);
            for (int it = 0; it < 20; it++)
            {
                knots[0] = eye;
                knots[ThreadPoints - 1] = anchor;
                for (int i = 0; i < ThreadPoints - 1; i++)
                {
                    Vector3 d = knots[i + 1] - knots[i];
                    float len = d.magnitude;
                    if (len < 1e-6f)
                        continue;
                    Vector3 fix = d * (1f - seg / len) * 0.5f;
                    if (i > 0) knots[i] += fix; else knots[i + 1] -= fix;
                    if (i + 1 < ThreadPoints - 1) knots[i + 1] -= fix; else knots[i] += fix;
                }
            }
            thread.positionCount = ThreadPoints;
            for (int i = 0; i < ThreadPoints; i++)
                thread.SetPosition(i, cam.transform.TransformPoint(knots[i]));
        }

        private void MakeNeedle()
        {
            var n = new GameObject("SailDamage needle");
            n.AddComponent<MeshFilter>().sharedMesh = NeedleMesh();
            var r = n.AddComponent<MeshRenderer>();
            Shader standard = Shader.Find("Standard");
            if (standard != null)
            {
                r.material = new Material(standard) { color = new Color(0.72f, 0.73f, 0.76f) };
                r.material.SetFloat("_Metallic", 0.85f);
                r.material.SetFloat("_Glossiness", 0.75f);
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            needle = n.transform;
            if (thread != null)
                return;

            var t = new GameObject("SailDamage thread");
            DontDestroyOnLoad(t);
            thread = t.AddComponent<LineRenderer>();
            thread.useWorldSpace = true;
            thread.widthMultiplier = 0.0011f;
            thread.numCapVertices = 2;
            thread.numCornerVertices = 2;
            Shader sprite = Shader.Find("Sprites/Default");
            if (sprite != null)
                thread.material = new Material(sprite);
            thread.startColor = thread.endColor = new Color(0.86f, 0.8f, 0.64f);
            thread.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // A lathed needle: sharp point, long thin shank, a slightly flattened, wider eye end.
        private static Mesh NeedleMesh()
        {
            float L = NeedleLength;
            // (height, radius) from the point up.
            Vector2[] profile =
            {
                new Vector2(0f, 0f), new Vector2(0.004f * L / 0.075f, 0.00035f), new Vector2(0.012f * L / 0.075f, 0.0008f),
                new Vector2(0.02f * L / 0.075f, 0.001f), new Vector2(0.86f * L, 0.001f), new Vector2(0.9f * L, 0.0013f),
                new Vector2(0.97f * L, 0.0013f), new Vector2(L, 0.0007f), new Vector2(L, 0f),
            };
            const int Sides = 12;
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            for (int p = 0; p < profile.Length; p++)
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s / (float)Sides * Mathf.PI * 2f;
                    // The eye end is flattened, like a real needle's.
                    float flat = profile[p].x > 0.86f * L ? 0.6f : 1f;
                    verts.Add(new Vector3(Mathf.Cos(a) * profile[p].y, profile[p].x, Mathf.Sin(a) * profile[p].y * flat));
                }
            for (int p = 0; p < profile.Length - 1; p++)
                for (int s = 0; s < Sides; s++)
                {
                    int i0 = p * (Sides + 1) + s, i1 = i0 + 1, j0 = i0 + Sides + 1, j1 = j0 + 1;
                    tris.Add(i0); tris.Add(j0); tris.Add(i1);
                    tris.Add(i1); tris.Add(j0); tris.Add(j1);
                }
            var mesh = new Mesh { name = "SailDamage needle" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
