using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Leaving the shipyard (DischargeShip -> CancelOrder) or cancelling an order calls SaveableBoatCustomization.LoadData
    // with the records GetData took when the order was opened or paid: every sail is destroyed and built again from
    // its prefab (Mast.LoadSail). Up to 1.0.1 the damage stayed with the old Sail objects, so the shipyard mended all
    // sails for free just by visiting it. Each record now carries its sail's damage (a table on the SaveSailData object,
    // as Studding Sails keeps its own data in the record), and the rebuilt sail takes it over.
    internal static class ShipyardKeep
    {
        private static readonly ConditionalWeakTable<SaveSailData, float[]> records = new ConditionalWeakTable<SaveSailData, float[]>();

        [HarmonyPatch(typeof(SaveableBoatCustomization), "GetData")]
        private static class GetDataPatch
        {
            private static readonly FieldInfo Refs = AccessTools.Field(typeof(SaveableBoatCustomization), "refs");

            private static void Postfix(SaveableBoatCustomization __instance, SaveBoatCustomizationData __result)
            {
                DamageModel model = DamageModel.Instance;
                var refs = Refs?.GetValue(__instance) as BoatRefs;
                if (model == null || refs == null || refs.masts == null || __result?.sails == null)
                    return;
                List<SaveSailData> list = __result.sails;
                var used = new bool[list.Count];
                int i = 0;
                // GetData walks the same masts and sails in the same order; check each record anyway (another mod could
                // add or drop some), as Studding Sails does.
                foreach (Mast mast in refs.masts)
                {
                    if (!mast || !mast.gameObject.activeSelf)
                        continue;
                    foreach (GameObject go in mast.sails)
                    {
                        int guess = i++;
                        Sail sail = go != null ? go.GetComponent<Sail>() : null;
                        float[] v = sail != null ? model.Snapshot(sail) : null;
                        if (v == null)
                            continue;
                        int at = Find(list, used, guess, mast, sail);
                        if (at < 0)
                        {
                            Plugin.Log.LogWarning($"SHIPYARD no record for {sail.sailName}, its damage is not kept");
                            continue;
                        }
                        used[at] = true;
                        records.Remove(list[at]);
                        records.Add(list[at], v);
                    }
                }
            }

            private static bool Same(SaveSailData d, Mast mast, Sail s) =>
                d != null && d.mastIndex == mast.orderIndex && d.prefabIndex == s.prefabIndex &&
                Mathf.Abs(d.installHeight - s.GetCurrentInstallHeight()) < 0.01f &&
                Mathf.Abs(d.scaleY - s.GetScaleY()) < 0.001f && Mathf.Abs(d.scaleZ - s.GetScaleZ()) < 0.001f;

            private static int Find(List<SaveSailData> list, bool[] used, int guess, Mast mast, Sail s)
            {
                if (guess < list.Count && !used[guess] && Same(list[guess], mast, s))
                    return guess;
                for (int k = 0; k < list.Count; k++)
                    if (!used[k] && Same(list[k], mast, s))
                        return k;
                return -1;
            }
        }

        [HarmonyPatch(typeof(Mast), "LoadSail")]
        private static class LoadSailPatch
        {
            private static void Prefix(SaveSailData sailData, out float[] __state)
            {
                __state = null;
                if (sailData != null)
                    records.TryGetValue(sailData, out __state);
            }

            // LoadSail adds the new sail to the mast's list and attaches it: it is the last one.
            private static void Postfix(Mast __instance, float[] __state)
            {
                if (__state == null || DamageModel.Instance == null || __instance.sails == null || __instance.sails.Count == 0)
                    return;
                GameObject go = __instance.sails[__instance.sails.Count - 1];
                Sail sail = go != null ? go.GetComponent<Sail>() : null;
                if (sail == null)
                    return;
                DamageModel.Instance.Restore(sail, __state);
                Plugin.Log.LogInfo($"SHIPYARD {sail.sailName} built again, damage {__state[0] * 100f:F1}% kept");
            }
        }

        private static readonly AccessTools.FieldRef<Shipyard, SaveBoatCustomizationData> OriginalData =
            AccessTools.FieldRefAccess<Shipyard, SaveBoatCustomizationData>("originalData");

        /// <summary>
        /// Sails mended with the order: ConfirmOrder already took the records (ResetOrder) before they were mended,
        /// so take them again, or leaving the yard would bring the holes back.
        /// </summary>
        internal static void Retake(Shipyard yard)
        {
            GameObject boat = yard != null ? yard.GetCurrentBoat() : null;
            var custom = boat != null ? boat.GetComponent<SaveableBoatCustomization>() : null;
            if (custom != null)
                OriginalData(yard) = custom.GetData();
        }
    }
}
