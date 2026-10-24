using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Studding sails (Кэп's StuddingSails mod) have their own damage (Кэп, 0.6.0): it builds up only while they are
    // set, their thrust drops with it, and their canvas shows their own holes instead of the parent sail's.
    // StuddingSails is not referenced: its StuddingRig is reached by reflection and patched here when it is loaded.
    //  - StuddingRig.SideArea(side): the set canvas area of one side, used for its thrust and mass; scaled here.
    //  - StuddingRig.SyncColor(): copies the parent sail's material onto the studding canvas (in the default
    //    UseSailTexture mode, with the parent's UV layout); our postfix swaps in the studding sails' own texture.
    internal static class StuddingCompat
    {
        internal static bool Present { get; private set; }
        private static bool tried;

        private static MethodInfo of, sideArea;
        private static FieldInfo area, sailField, clothMat, sides, panelMesh, uvFitted, useSailTexture;
        [ThreadStatic] private static bool raw;

        internal static void TryInit()
        {
            if (tried)
                return;
            tried = true;
            Type rig = AccessTools.TypeByName("StuddingSails.StuddingRig");
            if (rig == null)
                return;
            of = AccessTools.Method(rig, "Of", new[] { typeof(Sail) });
            sideArea = AccessTools.Method(rig, "SideArea", new[] { typeof(int) });
            MethodInfo sync = AccessTools.Method(rig, "SyncColor");
            area = AccessTools.Field(rig, "Area");
            sailField = AccessTools.Field(rig, "sail");
            clothMat = AccessTools.Field(rig, "clothMat");
            sides = AccessTools.Field(rig, "sides");
            uvFitted = AccessTools.Field(rig, "uvFitted");
            useSailTexture = AccessTools.Field(rig, "useSailTexture");
            Type side = AccessTools.Inner(rig, "Side");
            panelMesh = side != null ? AccessTools.Field(side, "PanelMesh") : null;
            if (of == null || sideArea == null || area == null)
            {
                Plugin.Log.LogWarning("STUDDING StuddingSails found, but not the parts we need: studding sails share the sail's damage");
                return;
            }
            var harmony = new Harmony(Plugin.Guid + ".studding");
            harmony.Patch(sideArea, postfix: new HarmonyMethod(typeof(StuddingCompat), nameof(SideAreaPostfix)));
            bool visuals = sync != null && sailField != null && clothMat != null && sides != null && panelMesh != null && uvFitted != null;
            if (visuals)
                harmony.Patch(sync, postfix: new HarmonyMethod(typeof(StuddingCompat), nameof(SyncColorPostfix)));
            Present = true;
            Plugin.Log.LogInfo($"STUDDING StuddingSails found: own damage for studding sails{(visuals ? ", own holes" : ", no holes (canvas parts not found)")}");
        }

        private static object Rig(Sail sail) => Present && sail != null ? of.Invoke(null, new object[] { sail }) : null;

        /// <summary>Full canvas area of the sail's studding sails (both sides), 0 without them.</summary>
        internal static float FullArea(Sail sail)
        {
            object rig = Rig(sail);
            return rig != null ? (float)area.GetValue(rig) : 0f;
        }

        /// <summary>How far one side (0 port, 1 starboard) of the studding sails is set, 0..1, before damage.</summary>
        internal static float SetShare(Sail sail, int side)
        {
            object rig = Rig(sail);
            if (rig == null)
                return 0f;
            float half = (float)area.GetValue(rig) * 0.5f;
            if (half <= 0f)
                return 0f;
            raw = true;
            try
            {
                return Mathf.Clamp01((float)sideArea.Invoke(rig, new object[] { side }) / half);
            }
            finally
            {
                raw = false;
            }
        }

        // Thrust (and mass) of a damaged studding sail drop like a sail's, side by side.
        private static void SideAreaPostfix(object __instance, int __0, ref float __result)
        {
            if (raw || __result <= 0f || DamageModel.Instance == null || sailField == null || __0 < 0 || __0 > 1)
                return;
            float d = DamageModel.Instance.GetStuddingDamage(sailField.GetValue(__instance) as Sail, __0);
            if (d > 0f)
                __result *= Mathf.Max(0f, 1f - d * Plugin.ThrustLoss.Value);
        }

        /// <summary>Studding canvas meshes, their UVs in the parent sail's layout; null unless that layout is in use.</summary>
        internal static Mesh[] Panels(Sail sail)
        {
            object rig = Rig(sail);
            if (rig == null || panelMesh == null || !(bool)uvFitted.GetValue(rig) || useSailTexture != null && !(bool)useSailTexture.GetValue(rig))
                return null;
            var arr = sides.GetValue(rig) as Array;
            if (arr == null)
                return null;
            var meshes = new Mesh[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                meshes[i] = arr.GetValue(i) != null ? panelMesh.GetValue(arr.GetValue(i)) as Mesh : null;
            return meshes;
        }

        // The studding canvas copies the parent's material, our torn one included, so the parent's holes showed on it.
        private static void SyncColorPostfix(object __instance)
        {
            if (!(bool)uvFitted.GetValue(__instance) || useSailTexture != null && !(bool)useSailTexture.GetValue(__instance))
                return;
            var m = clothMat.GetValue(__instance) as Material;
            Sail sail = sailField.GetValue(__instance) as Sail;
            if (m == null || sail == null)
                return;
            SailVisuals.DressStudding(sail, m);
        }
    }
}
