using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SailDamage
{
    // Steps 2-3 of the plan: the damage model, thrust loss (SailPatches.cs) and saving; no visuals yet.
    // Two rules (see SailDamage-design.md, section 2):
    //  - overload: damage builds up while pressure P is above OverloadStart;
    //  - swing hit: a fore-and-aft sail swinging on its hinge stops dead at the sheet limit
    //    (an uncontrolled gybe or a slack sheet). Test log 2026-10-22, wind 12-17: hauled sheets
    //    gave 66 deg/s, fully eased gaff and jib 242-251 deg/s, square sails never above 7.
    public class DamageModel : MonoBehaviour
    {
        // A hit is a stop within one physics step: speed falls below this share of the previous one.
        private const float StopRatio = 0.3f;

        internal static DamageModel Instance;

        // Damage of every sail seen so far (0 included, so the save key is looked up only once).
        private readonly Dictionary<Sail, float> damage = new Dictionary<Sail, float>();
        // Share of each sail mended by hand: drawn as patches over the holes it closed.
        private readonly Dictionary<Sail, float> patched = new Dictionary<Sail, float>();
        // The same for each side of the sail's studding sails (StuddingCompat; 0 port, 1 starboard): a side tears only
        // while it is set (Кэп, 0.6.1: in 0.6.0 one value made a furled side tear with the set one).
        private readonly Dictionary<Sail, Stud> studs = new Dictionary<Sail, Stud>();
        // Damage and patches of the sail, then of each studding sail side, read from the save and not yet matched to a
        // sail (that boat was not seen yet).
        private readonly Dictionary<string, float[]> pending = new Dictionary<string, float[]>();

        private sealed class Stud
        {
            internal readonly float[] Damage = new float[2], Patched = new float[2], Reported = new float[2];
        }
        private readonly Dictionary<Sail, float> lastSpeed = new Dictionary<Sail, float>();
        private readonly Dictionary<Sail, float> lastReported = new Dictionary<Sail, float>();
        private Sail[] sails = new Sail[0];
        private float refreshTimer;
        private float lastOverload = -100f;

        /// <summary>A set sail (or studding sail) of the boat is tearing from overload right now.</summary>
        internal bool Tearing => Time.unscaledTime - lastOverload < 2f;

        private void Awake()
        {
            Instance = this;
        }

        private void FixedUpdate()
        {
            if (!GameState.playing)
                return;
            StuddingCompat.TryInit();

            // At the shipyard the boat stands in the yard while the wind still fills the sails on show: a storm in port
            // tore them (Кэп, 1.0.1). The swing speeds are dropped too, so leaving the yard is not taken for a hit.
            if (GameState.currentShipyard != null)
            {
                lastSpeed.Clear();
                return;
            }

            refreshTimer -= Time.fixedDeltaTime;
            if (refreshTimer <= 0f)
            {
                refreshTimer = 1f;
                Transform boat = GameState.currentBoat != null ? GameState.currentBoat : GameState.lastBoat;
                sails = BoatSails(boat);
            }

            foreach (Sail sail in sails)
            {
                if (sail == null || sail.shipRigidbody == null)
                    continue;
                CheckSwingHit(sail);
                ApplyOverload(sail);
                if (StuddingCompat.Present)
                    ApplyStuddingOverload(sail);
            }
        }

        // Studding sails take the same pressure per square metre as their sail set full (StuddingSails computes their
        // force that way), and wear in proportion to how far they are set.
        private void ApplyStuddingOverload(Sail sail)
        {
            // On a winch they can be set while the sail is furled: appliedWindForce then carries the game's furled
            // factor (debugYardMult) instead of the unroll, so divide by the same one.
            float yard = Debugger.instance != null ? Debugger.instance.debugYardMult : 0.015f;
            float full = sail.appliedWindForce / Mathf.Max(yard, sail.currentUnroll, 0.001f);
            float excess = full - Plugin.OverloadStart.Value;
            if (excess <= 0f)
                return;
            float range = Mathf.Max(1f, Plugin.OverloadFull.Value - Plugin.OverloadStart.Value);
            for (int side = 0; side < 2; side++)
            {
                float set = StuddingCompat.SetShare(sail, side);
                if (set <= 0.01f)
                    continue;
                Stud st = StudOf(sail);
                float before = st.Damage[side];
                float total = AddStuddingDamage(sail, side, Plugin.OverloadRate.Value * (excess / range) * set * Time.fixedDeltaTime);
                if (total > before)
                    lastOverload = Time.unscaledTime;
                if (total - st.Reported[side] >= 0.01f)
                {
                    st.Reported[side] = total;
                    string name = side == 0 ? "port" : "starboard";
                    Plugin.Log.LogInfo($"OVERLOAD {sail.sailName} {name} studding sail (set {F(set * 100f)}%) P={F(full)} total={F(total * 100f)}%");
                    if (Plugin.OverloadMessages.Value)
                        PressureLogger.Instance?.ShowMessage($"{sail.sailName}, {name} studding sail: overloaded, damage {total * 100f:F0}%");
                }
            }
        }

        /// <summary>
        /// Every sail of a boat. GameState.currentBoat is the boat's model (BoatRefs.boatModel) with the masts, but
        /// MoreSailwindSails hangs its Fisherman's sails on stays under the boat itself (Кэп, 1.0.0: a player's
        /// junk showed them never damaged), so look from the boat's root.
        /// </summary>
        internal static Sail[] BoatSails(Transform boat)
        {
            if (boat == null)
                return new Sail[0];
            BoatRefs refs = boat.GetComponentInParent<BoatRefs>();
            return (refs != null ? refs.transform : boat).GetComponentsInChildren<Sail>();
        }

        private Stud StudOf(Sail sail)
        {
            GetDamage(sail);
            if (!studs.TryGetValue(sail, out Stud st))
                studs[sail] = st = new Stud();
            return st;
        }

        private float AddStuddingDamage(Sail sail, int side, float amount)
        {
            Stud st = StudOf(sail);
            float before = st.Damage[side];
            float current = Mathf.Clamp01(before + amount);
            st.Damage[side] = current;
            if (st.Patched[side] > 0f)
                st.Patched[side] = Mathf.Clamp(st.Patched[side] - Mathf.Max(0f, current - before), 0f, 1f - current);
            return current;
        }

        /// <summary>Damage 0..1 of one side of the sail's studding sails (0 port, 1 starboard).</summary>
        internal float GetStuddingDamage(Sail sail, int side) => sail != null ? StudOf(sail).Damage[side] : 0f;

        /// <summary>Share of one side of the studding sails mended by hand, 0..1.</summary>
        internal float GetStuddingPatched(Sail sail, int side) => sail != null ? StudOf(sail).Patched[side] : 0f;

        /// <summary>Worse of the two sides.</summary>
        internal float GetStuddingDamage(Sail sail) => Mathf.Max(GetStuddingDamage(sail, 0), GetStuddingDamage(sail, 1));

        internal bool HasStuddingMarks(Sail sail)
        {
            Stud st = sail != null ? StudOf(sail) : null;
            return st != null && (st.Damage[0] > 0f || st.Damage[1] > 0f || st.Patched[0] > 0f || st.Patched[1] > 0f);
        }

        /// <summary>Holes to mend on this sail and its studding sails, in square metres.</summary>
        internal float HoleArea(Sail sail)
        {
            float half = StuddingCompat.FullArea(sail) * 0.5f;
            return GetDamage(sail) * Mathf.Max(1f, sail.sailArea) + (GetStuddingDamage(sail, 0) + GetStuddingDamage(sail, 1)) * half;
        }

        /// <summary>Hand mending of up to <paramref name="m2"/> square metres: the sail first, then its studding sails.</summary>
        internal float PatchArea(Sail sail, float m2)
        {
            float area = Mathf.Max(1f, sail.sailArea);
            float done = Patch(sail, m2 / area) * area;
            float half = StuddingCompat.FullArea(sail) * 0.5f;
            Stud st = StudOf(sail);
            for (int side = 0; side < 2 && half > 0f && m2 - done > 0f; side++)
            {
                float d = st.Damage[side];
                float mend = Mathf.Clamp((m2 - done) / half, 0f, d);
                st.Damage[side] = st.Reported[side] = d - mend;
                st.Patched[side] += mend;
                done += mend * half;
            }
            return done;
        }

        private void CheckSwingHit(Sail sail)
        {
            HingeJoint hinge = sail.GetComponent<HingeJoint>();
            if (hinge == null)
                return;

            float speed = Mathf.Abs(hinge.velocity);
            lastSpeed.TryGetValue(sail, out float before);
            lastSpeed[sail] = speed;

            // A furled sail swinging around is just a bare yard: no cloth to tear.
            if (sail.currentUnroll < 0.04f || before < Plugin.SwingSafe.Value || speed > before * StopRatio)
                return;

            float t = Mathf.InverseLerp(Plugin.SwingSafe.Value, Plugin.SwingFull.Value, before);
            float hit = t * t * Plugin.SwingMaxDamage.Value * sail.currentUnroll;
            float total = AddDamage(sail, hit);
            lastReported[sail] = total;

            string text = $"{sail.sailName}: jolt {before:F0}°/s, damage +{hit * 100f:F1}% (total {total * 100f:F1}%)";
            Plugin.Log.LogInfo($"HIT {sail.sailName} speed={F(before)} dmg+={F(hit * 100f)}% total={F(total * 100f)}%");
            PressureLogger.Instance?.ShowMessage(text);
        }

        private void ApplyOverload(Sail sail)
        {
            if (sail.currentUnroll < 0.04f)
                return;
            float excess = sail.appliedWindForce - Plugin.OverloadStart.Value;
            if (excess <= 0f)
                return;

            float range = Mathf.Max(1f, Plugin.OverloadFull.Value - Plugin.OverloadStart.Value);
            float before = GetDamage(sail);
            float total = AddDamage(sail, Plugin.OverloadRate.Value * (excess / range) * Time.fixedDeltaTime);
            if (total > before)
                lastOverload = Time.unscaledTime;

            // Overload grows smoothly, so report it every full percent instead of every step.
            lastReported.TryGetValue(sail, out float reported);
            if (total - reported >= 0.01f)
            {
                lastReported[sail] = total;
                Plugin.Log.LogInfo($"OVERLOAD {sail.sailName} P={F(sail.appliedWindForce)} total={F(total * 100f)}%");
                if (Plugin.OverloadMessages.Value)
                    PressureLogger.Instance?.ShowMessage($"{sail.sailName}: overloaded, damage {total * 100f:F0}%");
            }
        }

        private float AddDamage(Sail sail, float amount)
        {
            float before = GetDamage(sail);
            float current = Mathf.Clamp(before + amount, 0f, MaxDamage(sail));
            damage[sail] = current;
            KeepPatchesInRange(sail, before);
            return current;
        }

        // Patches sit where the holes of (damage + patched) would be; new damage tears them open again first.
        // 0.5.4 left patched as it was, so (damage + patched) grew with every hit and the patches spread over
        // the sail (Кэп): the new damage now comes out of the patches, and their outer edge stays put.
        private void KeepPatchesInRange(Sail sail, float before)
        {
            if (patched.TryGetValue(sail, out float p) && p > 0f)
                patched[sail] = Mathf.Clamp(p - Mathf.Max(0f, damage[sail] - before), 0f, MaxDamage(sail) - damage[sail]);
        }

        /// <summary>Share of the sail mended by hand (shown as patches), 0..1.</summary>
        internal float GetPatched(Sail sail)
        {
            GetDamage(sail);
            return patched.TryGetValue(sail, out float p) ? p : 0f;
        }

        /// <summary>Hand mending: moves up to <paramref name="amount"/> from damage to patches; returns what was mended.</summary>
        internal float Patch(Sail sail, float amount)
        {
            float d = GetDamage(sail);
            float done = Mathf.Clamp(amount, 0f, d);
            damage[sail] = d - done;
            lastReported[sail] = damage[sail];
            patched[sail] = GetPatched(sail) + done;
            return done;
        }

        /// <summary>Damage and patches of the sail, then of each studding sail side; null for a whole sail (ShipyardKeep).</summary>
        internal float[] Snapshot(Sail sail)
        {
            float d = GetDamage(sail), p = GetPatched(sail);
            if (d <= 0f && p <= 0f && !HasStuddingMarks(sail))
                return null;
            Stud st = StudOf(sail);
            return new[] { d, p, st.Damage[0], st.Patched[0], st.Damage[1], st.Patched[1] };
        }

        /// <summary>The same sail built again by the game (ShipyardKeep): it takes over the old one's damage.</summary>
        internal void Restore(Sail sail, float[] v)
        {
            damage[sail] = lastReported[sail] = Mathf.Min(v[0], MaxDamage(sail));
            patched[sail] = v[1];
            var st = new Stud();
            for (int side = 0; side < 2; side++)
            {
                st.Damage[side] = st.Reported[side] = v[2 + side * 2];
                st.Patched[side] = v[3 + side * 2];
            }
            studs[sail] = st;
        }

        /// <summary>Shipyard: a new sail, no holes and no patches.</summary>
        internal void Mend(Sail sail)
        {
            GetDamage(sail);
            damage[sail] = 0f;
            patched[sail] = 0f;
            lastReported[sail] = 0f;
            studs[sail] = new Stud();
            Plugin.Log.LogInfo($"MEND {sail.sailName}: as new");
        }

        // Junk and fin sails are battened: they never tear away, they keep at least JunkMinHealth.
        // (Few players pick them; tearing them would be an indirect buff to them, Кэп 2026-10-23.)
        internal static bool IsBattened(Sail sail)
        {
            // "junk square", "junk jib": the junk's other sails count too.
            return sail.junkType || sail.category == SailCategory.junk ||
                   (sail.sailName != null && (sail.sailName.IndexOf("fin", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              sail.sailName.IndexOf("junk", System.StringComparison.OrdinalIgnoreCase) >= 0));
        }

        internal static float MaxDamage(Sail sail)
        {
            return IsBattened(sail) ? 1f - Mathf.Clamp01(Plugin.JunkMinHealth.Value) : 1f;
        }

        /// <summary>Test keys: set damage directly (logged, no message spam from overload).</summary>
        internal void SetDamage(Sail sail, float value)
        {
            float before = GetDamage(sail);
            damage[sail] = Mathf.Clamp(value, 0f, MaxDamage(sail));
            lastReported[sail] = damage[sail];
            KeepPatchesInRange(sail, before);
            Plugin.Log.LogInfo($"TEST {sail.sailName} damage set to {F(damage[sail] * 100f)}%");
        }

        /// <summary>Test keys: add damage to one side of the studding sails.</summary>
        internal void AddStuddingDamageTest(Sail sail, int side, float amount)
        {
            float total = AddStuddingDamage(sail, side, amount);
            StudOf(sail).Reported[side] = total;
            Plugin.Log.LogInfo($"TEST {sail.sailName} {(side == 0 ? "port" : "starboard")} studding sail damage set to {F(total * 100f)}%");
        }

        /// <summary>Damage 0..1 of a sail; on first sight it picks up its value from the save.</summary>
        internal float GetDamage(Sail sail)
        {
            if (damage.TryGetValue(sail, out float d))
                return d;

            string key = SaveKey(sail);
            if (key != null && pending.TryGetValue(key, out float[] saved))
            {
                pending.Remove(key);
                d = Mathf.Min(saved[0], MaxDamage(sail));
                lastReported[sail] = d;
                patched[sail] = Mathf.Clamp(saved[1], 0f, MaxDamage(sail) - d);
                var st = new Stud();
                for (int side = 0; side < 2; side++)
                {
                    st.Damage[side] = st.Reported[side] = saved[2 + side * 2];
                    st.Patched[side] = Mathf.Clamp(saved[3 + side * 2], 0f, 1f - st.Damage[side]);
                }
                studs[sail] = st;
                Plugin.Log.LogInfo($"LOAD {sail.sailName} [{key}] damage={F(d * 100f)}% patched={F(patched[sail] * 100f)}%" +
                                   (HasStuddingMarks(sail) ? $" studding port {F(st.Damage[0] * 100f)}%/{F(st.Patched[0] * 100f)}%, " +
                                                             $"starboard {F(st.Damage[1] * 100f)}%/{F(st.Patched[1] * 100f)}%" : ""));
            }
            damage[sail] = d;
            return d;
        }

        // ---------------------------------------------------------------- saving

        // The save record is found by content, not by Sail object: boat sceneIndex, mast orderIndex, sail prefab
        // and how many sails of that prefab come before it on the mast (vanilla saves and loads mast.sails in order).
        internal static string SaveKey(Sail sail)
        {
            Transform parent = sail.transform.parent;
            Mast mast = parent != null ? parent.GetComponent<Mast>() : null;
            SaveableObject boat = sail.GetComponentInParent<SaveableObject>();
            if (boat == null && sail.shipRigidbody != null)
                boat = sail.shipRigidbody.GetComponentInParent<SaveableObject>();
            if (mast == null || boat == null || mast.sails == null)
                return null;

            int n = 0;
            foreach (GameObject go in mast.sails)
            {
                if (go == sail.gameObject)
                    break;
                Sail other = go != null ? go.GetComponent<Sail>() : null;
                if (other != null && other.prefabIndex == sail.prefabIndex)
                    n++;
            }
            return $"{boat.sceneIndex}:{mast.orderIndex}:{sail.prefabIndex}:{n}";
        }

        internal string Serialize()
        {
            var all = new Dictionary<string, float[]>(pending);
            foreach (KeyValuePair<Sail, float> kv in damage)
            {
                if (kv.Key == null)
                    continue;
                patched.TryGetValue(kv.Key, out float p);
                studs.TryGetValue(kv.Key, out Stud st);
                if (kv.Value <= 0f && p <= 0f && !HasStuddingMarks(kv.Key))
                    continue;
                string key = SaveKey(kv.Key);
                if (key != null)
                    all[key] = st != null
                        ? new[] { kv.Value, p, st.Damage[0], st.Patched[0], st.Damage[1], st.Patched[1] }
                        : new[] { kv.Value, p, 0f, 0f, 0f, 0f };
            }

            // key=damage, key=damage/patched when the sail carries patches (0.5.x), key=damage/patched/
            // port studding damage/patched/starboard studding damage/patched when its studding sails are marked (0.6.1;
            // 0.6.0 wrote one studding damage/patched for both sides).
            var parts = new List<string>();
            foreach (KeyValuePair<string, float[]> kv in all)
            {
                float[] v = kv.Value;
                int n = v[2] > 0f || v[3] > 0f || v[4] > 0f || v[5] > 0f ? 6 : v[1] > 0f ? 2 : 1;
                var s = new StringBuilder(kv.Key).Append('=');
                for (int i = 0; i < n; i++)
                    s.Append(i > 0 ? "/" : "").Append(v[i].ToString("R", CultureInfo.InvariantCulture));
                parts.Add(s.ToString());
            }
            return string.Join(";", parts.ToArray());
        }

        internal void Deserialize(string data)
        {
            damage.Clear();
            patched.Clear();
            studs.Clear();
            lastSpeed.Clear();
            lastReported.Clear();
            pending.Clear();
            if (string.IsNullOrEmpty(data))
                return;

            foreach (string part in data.Split(';'))
            {
                int eq = part.LastIndexOf('=');
                if (eq <= 0)
                    continue;
                string[] values = part.Substring(eq + 1).Split('/');
                if (!float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                    continue;
                var v = new float[6];
                v[0] = Mathf.Clamp01(d);
                for (int i = 1; i < values.Length && i < 6; i++)
                    if (float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                        v[i] = Mathf.Clamp01(x);
                if (values.Length == 4)
                {
                    v[4] = v[2]; // 0.6.0: one value for both sides
                    v[5] = v[3];
                }
                pending[part.Substring(0, eq)] = v;
            }
        }

        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
