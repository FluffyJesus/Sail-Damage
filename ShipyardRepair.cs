using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Step 5 of the plan: mending sails at the shipyard. A "Patch Sails" button below "Repair Ship" adds a line
    // to the order; each sail costs up to 70% of a new one of its kind, in proportion to its damage. Paid sails
    // come back whole (new cloth, no patches).
    internal static class ShipyardRepair
    {
        // Not one of the vanilla ButtonFunction values, so vanilla OnActivate does nothing with it.
        internal const ShipyardButton.ButtonFunction PatchSails = (ShipyardButton.ButtonFunction)1000;

        internal static bool Ordered;
        private static TextMesh buttonText;
        private static bool confirming, confirmed;

        private static readonly AccessTools.FieldRef<ShipyardUI, TextMesh> RepairText =
            AccessTools.FieldRefAccess<ShipyardUI, TextMesh>("repairButtonText");
        private static readonly AccessTools.FieldRef<ShipyardUI, TextMesh> CleanText =
            AccessTools.FieldRefAccess<ShipyardUI, TextMesh>("hullCleaningButtonText");
        private static readonly AccessTools.FieldRef<Shipyard, int> OrderTotal =
            AccessTools.FieldRefAccess<Shipyard, int>("currentOrderTotal");
        private static readonly AccessTools.FieldRef<Shipyard, string> OrderText =
            AccessTools.FieldRefAccess<Shipyard, string>("currentOrderText");

        private static Sail[] Sails(Shipyard yard)
        {
            GameObject boat = yard != null ? yard.GetCurrentBoat() : null;
            return boat != null ? DamageModel.BoatSails(boat.transform) : new Sail[0];
        }

        // Average damage of the boat's installed sails (studding sails included), weighted by area, for the button text.
        private static float BoatDamage(Shipyard yard)
        {
            float area = 0f, sum = 0f;
            foreach (Sail s in Sails(yard))
            {
                if (!s.IsInstalled())
                    continue;
                area += Mathf.Max(1f, s.sailArea) + StuddingCompat.FullArea(s);
                sum += DamageModel.Instance.HoleArea(s);
            }
            return area > 0f ? sum / area : 0f;
        }

        // The sail price includes its studding sails (StuddingSails raises it), so the damage is the share of the whole cloth.
        private static int Cost(Shipyard yard)
        {
            float rate = yard.GetCurrencyRate(), cost = 0f;
            foreach (Sail s in Sails(yard))
                if (s.IsInstalled())
                    cost += s.GetSailPrice() * rate * Plugin.ShipyardRepairShare.Value *
                            DamageModel.Instance.HoleArea(s) / (Mathf.Max(1f, s.sailArea) + StuddingCompat.FullArea(s));
            return Mathf.RoundToInt(cost);
        }

        private static void MakeButton(ShipyardUI ui)
        {
            if (buttonText != null)
                return;
            TextMesh repair = RepairText(ui), clean = CleanText(ui);
            if (repair == null)
            {
                Plugin.Log.LogWarning("SHIPYARD no repair button to copy, no Patch Sails button");
                return;
            }
            Transform src = repair.transform.parent;
            GameObject copy = Object.Instantiate(src.gameObject, src.parent);
            copy.name = "SailDamage patch sails button";
            // Buttons are stacked: step down by the distance between "Clean Ship" and "Repair Ship".
            Vector3 step = clean != null ? src.localPosition - clean.transform.parent.localPosition : Vector3.zero;
            if (step.magnitude < 1e-4f)
                step = new Vector3(0f, -src.localScale.y * 1.2f, 0f);
            copy.transform.localPosition = src.localPosition + step;
            copy.transform.localRotation = src.localRotation;
            copy.transform.localScale = src.localScale;
            copy.GetComponent<ShipyardButton>().function = PatchSails;
            buttonText = copy.GetComponentInChildren<TextMesh>();
            Plugin.Log.LogInfo($"SHIPYARD Patch Sails button at {copy.transform.localPosition} (repair at {src.localPosition}, step {step})");
        }

        [HarmonyPatch(typeof(ShipyardUI), "ShowUI")]
        private static class ShowUIPatch
        {
            private static void Prefix(ShipyardUI __instance) => MakeButton(__instance);
        }

        [HarmonyPatch(typeof(ShipyardUI), "UpdateRepairButton")]
        private static class ButtonTextPatch
        {
            private static void Postfix(ShipyardUI __instance)
            {
                if (buttonText == null || GameState.currentShipyard == null || DamageModel.Instance == null)
                    return;
                buttonText.text = (Ordered ? "X Patch Sails" : "Patch Sails") +
                                  $"\n ( {Mathf.RoundToInt(BoatDamage(GameState.currentShipyard) * 100f)}% damage)";
                buttonText.transform.parent.GetComponent<Renderer>().sharedMaterial =
                    Ordered ? __instance.darkParchmentMaterial : __instance.parchmentMaterial;
            }
        }

        [HarmonyPatch(typeof(ShipyardButton), "OnActivate")]
        private static class ClickPatch
        {
            private static bool Prefix(ShipyardButton __instance)
            {
                if (__instance.function != PatchSails)
                    return true;
                UISoundPlayer.instance.PlayUISound(UISounds.buttonHover, 0.33f, 3f);
                Ordered = !Ordered;
                GameState.currentShipyard?.UpdateOrder();
                ShipyardUI.instance.RefreshButtons();
                return false;
            }
        }

        [HarmonyPatch(typeof(Shipyard), "ResetOrder")]
        private static class ResetPatch
        {
            private static void Postfix()
            {
                Ordered = false;
                if (confirming)
                    confirmed = true;
            }
        }

        // The vanilla order is built in one go and ends with the total line; add ours and set the total again.
        [HarmonyPatch(typeof(Shipyard), "UpdateOrder", typeof(bool))]
        private static class OrderPatch
        {
            private static void Postfix(Shipyard __instance)
            {
                if (!Ordered || DamageModel.Instance == null)
                    return;
                int cost = Cost(__instance);
                string currency = PlayerGold.GetCurrencyName(__instance.region);
                int before = OrderTotal(__instance);
                int total = OrderTotal(__instance) = before + cost;
                string text = OrderText(__instance) ?? "";
                int at = text.LastIndexOf("\n\n TOTAL: ", System.StringComparison.Ordinal);
                if (at >= 0)
                    text = text.Substring(0, at);
                OrderText(__instance) = text + "\n" + cost + ": sail repair" + "\n\n TOTAL: " + total + " " + currency;

                ShipyardUIOrderText lines = ShipyardUI.instance.shipyardOrderText;
                lines.AddLine(cost + ": sail repair");
                lines.SetTotalLine(total < 0 ? "TOTAL: " + -total + " " + currency + " refund" : "TOTAL: " + total + " " + currency);
                lines.DisplayCurrentText();
            }
        }

        [HarmonyPatch(typeof(Shipyard), "ConfirmOrder")]
        private static class ConfirmPatch
        {
            private static void Prefix(out bool __state)
            {
                __state = Ordered;
                confirming = true;
                confirmed = false;
            }

            // ResetOrder runs only when the order went through (paid).
            private static void Postfix(Shipyard __instance, bool __state)
            {
                confirming = false;
                if (!__state || !confirmed || DamageModel.Instance == null)
                    return;
                int n = 0;
                foreach (Sail s in Sails(__instance))
                {
                    // Only the sails that were paid for (Cost counts installed ones).
                    DamageModel m = DamageModel.Instance;
                    if (!s.IsInstalled() || m.GetDamage(s) <= 0f && m.GetPatched(s) <= 0f && !m.HasStuddingMarks(s))
                        continue;
                    DamageModel.Instance.Mend(s);
                    n++;
                }
                Plugin.Log.LogInfo($"SHIPYARD {n} sails mended");
                ShipyardUI.instance.RefreshButtons();
            }
        }
    }
}
