using HarmonyLib;

namespace SailDamage
{
    // Thrust loss: UpdateWindForceOnSail (Update) computes the two private unamplified forces that
    // ApplyForce (FixedUpdate) turns into force on the hull. Scaling them here cuts thrust and side force
    // together, while appliedWindForce (the pressure the damage model reads) stays as it was.
    [HarmonyPatch(typeof(Sail), "UpdateWindForceOnSail")]
    internal static class ThrustLossPatch
    {
        private static readonly AccessTools.FieldRef<Sail, float> Forward =
            AccessTools.FieldRefAccess<Sail, float>("unamplifiedForwardForce");
        private static readonly AccessTools.FieldRef<Sail, float> Sideway =
            AccessTools.FieldRefAccess<Sail, float>("unamplifiedSidewayForce");

        private static void Postfix(Sail __instance)
        {
            DamageModel model = DamageModel.Instance;
            if (model == null)
                return;
            float damage = model.GetDamage(__instance);
            if (damage <= 0f)
                return;

            float k = 1f - damage * Plugin.ThrustLoss.Value;
            if (k < 0f)
                k = 0f;
            Forward(__instance) *= k;
            Sideway(__instance) *= k;
        }
    }

    // A torn-off rag: WindCloth.Update sets cloth.damping every frame (up to 0.8 while the sail holds wind), which
    // kept the rag stiff as a visor in 0.4.1. For torn cloth it is let flap: little damping and random gusts.
    [HarmonyPatch(typeof(WindCloth), "Update")]
    internal static class TornClothPatch
    {
        private static readonly AccessTools.FieldRef<WindCloth, Sail> SailOf =
            AccessTools.FieldRefAccess<WindCloth, Sail>("sail");
        // Share of the game's wind push on the rag (1 = like a set sail), and how hard it flaps in random gusts.
        private const float RagWind = 0.4f;
        private const float RagFlutter = 0.7f;

        private static void Postfix(WindCloth __instance)
        {
            if (SailVisuals.TornCloths.Count == 0)
                return;
            UnityEngine.Cloth cloth = __instance.GetCloth();
            if (cloth == null || !SailVisuals.TornCloths.Contains(cloth))
                return;
            Sail sail = SailOf(__instance);
            float wind = sail != null ? sail.apparentWind.magnitude : 10f;
            cloth.damping = 0.02f;
            // A rag catches far less wind than a set sail; full storm wind on it stretched the cloth.
            cloth.externalAcceleration *= RagWind;
            float gusts = SailVisuals.GentleCloths.Contains(cloth) ? 0.5f : 1f;
            cloth.randomAcceleration = UnityEngine.Vector3.one * ((2f + 0.6f * wind) * RagFlutter * gusts);
        }
    }

    // Saving: vanilla calls these empty hooks around writing and after reading SaveContainer.modData
    // (= GameState.modData). SaveSailData.health is already taken by Studding Sails, so we keep our own key.
    [HarmonyPatch(typeof(SaveLoadManager), "SaveModData")]
    internal static class SaveModDataPatch
    {
        private static void Postfix()
        {
            if (DamageModel.Instance == null)
                return;
            string data = DamageModel.Instance.Serialize();
            if (string.IsNullOrEmpty(data))
                GameState.modData.Remove(Plugin.Guid);
            else
                GameState.modData[Plugin.Guid] = data;
            Plugin.Debug($"SAVE {(string.IsNullOrEmpty(data) ? "no damaged sails" : data)}");
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
    internal static class LoadModDataPatch
    {
        private static void Postfix()
        {
            if (DamageModel.Instance == null)
                return;
            GameState.modData.TryGetValue(Plugin.Guid, out string data);
            DamageModel.Instance.Deserialize(data);
            Plugin.Debug($"LOAD save data: {(string.IsNullOrEmpty(data) ? "none" : data)}");
        }
    }
}
