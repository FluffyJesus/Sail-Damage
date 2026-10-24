using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Step 1 of the plan: log per-sail pressure so thresholds can be tuned from real numbers.
    // Pressure is Sail.appliedWindForce: per unit of area already (area only enters in ApplyForce),
    // includes currentUnroll, wind angle, height and shadow. See notes/game-code.md.
    public class PressureLogger : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<Sail, float> Forward =
            AccessTools.FieldRefAccess<Sail, float>("unamplifiedForwardForce");
        private static readonly AccessTools.FieldRef<Sail, float> Sideway =
            AccessTools.FieldRefAccess<Sail, float>("unamplifiedSidewayForce");

        private const float MessageSeconds = 2.5f;
        private const float LogInterval = 0.2f;

        private bool logging;
        private float timer;
        private Sail[] sails = new Sail[0];
        // Peak |hinge speed| per sail since the last log line: a swing that slams into the limit
        // lasts less than one 0.2 s sample, so the instantaneous value alone would miss it.
        private readonly Dictionary<Sail, float> peakSwing = new Dictionary<Sail, float>();
        private int markCount;
        private string message;
        private float messageUntil;
        private GUIStyle messageStyle;

        internal static PressureLogger Instance;

        private void Start()
        {
            Instance = this;
        }

        private void Update()
        {
            // F8 (test keys) toggles logging; every switch-on drops a numbered marker so test manoeuvres are easy to find.
            if (Plugin.TestKeys.Value && Input.GetKeyDown(KeyCode.F8))
            {
                logging = !logging;
                timer = 0f;
                if (logging)
                {
                    Plugin.Log.LogInfo($"===== LOG ON, MARK {++markCount} at t={T()} =====");
                    ShowMessage($"Sail Damage: log ON (mark {markCount})");
                }
                else
                {
                    Plugin.Log.LogInfo($"===== LOG OFF after MARK {markCount} at t={T()} =====");
                    ShowMessage("Sail Damage: log OFF");
                }
            }

            if (!logging || !GameState.playing)
                return;

            TrackSwing();

            timer += Time.deltaTime;
            if (timer < LogInterval)
                return;
            timer = 0f;

            Transform boat = GameState.currentBoat != null ? GameState.currentBoat : GameState.lastBoat;
            if (boat == null)
                return;
            if (sails.Length == 0)
                sails = boat.GetComponentsInChildren<Sail>();

            var line = new StringBuilder();
            line.Append("t=").Append(T());
            line.Append(" wind=").Append(Wind.currentWind.magnitude.ToString("F1", CultureInfo.InvariantCulture));

            foreach (Sail sail in sails)
            {
                if (sail == null || sail.shipRigidbody == null)
                    continue;
                line.Append(" | ").Append(sail.sailName)
                    .Append(" P=").Append(F(sail.appliedWindForce))
                    .Append(" fwd=").Append(F(Forward(sail)))
                    .Append(" side=").Append(F(Sideway(sail)))
                    .Append(" unroll=").Append(F(sail.currentUnroll))
                    .Append(" area=").Append(F(sail.sailArea))
                    .Append(" dmg=").Append(F1((DamageModel.Instance != null ? DamageModel.Instance.GetDamage(sail) : 0f) * 100f));

                HingeJoint hinge = sail.GetComponent<HingeJoint>();
                if (hinge != null)
                {
                    peakSwing.TryGetValue(sail, out float peak);
                    line.Append(" ang=").Append(F1(hinge.angle))
                        .Append(" lim=").Append(F1(hinge.limits.min)).Append("..").Append(F1(hinge.limits.max))
                        .Append(" w=").Append(F1(hinge.velocity))
                        .Append(" wmax=").Append(F1(peak));
                }
            }

            Plugin.Log.LogInfo(line.ToString());

            sails = boat.GetComponentsInChildren<Sail>();
            peakSwing.Clear();
        }

        // Hinge speed in degrees per second, checked every frame.
        private void TrackSwing()
        {
            foreach (Sail sail in sails)
            {
                if (sail == null)
                    continue;
                HingeJoint hinge = sail.GetComponent<HingeJoint>();
                if (hinge == null)
                    continue;
                float speed = Mathf.Abs(hinge.velocity);
                if (!peakSwing.TryGetValue(sail, out float peak) || speed > peak)
                    peakSwing[sail] = speed;
            }
        }

        internal void ShowMessage(string text)
        {
            message = text;
            messageUntil = Time.unscaledTime + MessageSeconds;
        }

        private void OnGUI()
        {
            if (message == null || Time.unscaledTime > messageUntil)
                return;
            if (messageStyle == null)
                messageStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.UpperCenter };

            var rect = new Rect(0f, Screen.height * 0.12f, Screen.width, 40f);
            // Dark offset copy as a cheap outline so the text reads over sky and sea.
            GUI.color = Color.black;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), message, messageStyle);
            GUI.color = Color.white;
            GUI.Label(rect, message, messageStyle);
        }

        private static string T() => Time.time.ToString("F1", CultureInfo.InvariantCulture);

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static string F1(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
