using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace SailDamage
{
    // Storm warning: a ship's bell every 30-60 s while the wind could tear a set sail, instead of text on screen.
    // "Could tear" = full-sail pressure above OverloadStart for any sail of the boat you are on. Sail.appliedWindForce
    // already includes max(0.015, currentUnroll), so dividing it back out gives the pressure the sail would take
    // set (wind angle and height included), furled sails too.
    public class BellWarning : MonoBehaviour
    {
        private const string FileName = "ship-bell";
        private static readonly (string ext, AudioType type)[] Formats =
        {
            (".ogg", AudioType.OGGVORBIS), (".wav", AudioType.WAV), (".mp3", AudioType.MPEG),
        };

        private AudioSource source;
        private AudioClip clip;
        private float checkTimer;
        private float nextRing;
        private bool storm;

        private IEnumerator Start()
        {
            string dir = Path.GetDirectoryName(typeof(BellWarning).Assembly.Location);
            foreach (var (ext, type) in Formats)
            {
                string path = Path.Combine(dir, FileName + ext);
                if (!File.Exists(path))
                    continue;
                using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), type))
                {
                    yield return req.SendWebRequest();
                    if (req.isNetworkError || req.isHttpError)
                    {
                        Plugin.Log.LogError($"BELL cannot read {path}: {req.error}");
                        continue;
                    }
                    clip = DownloadHandlerAudioClip.GetContent(req);
                }
                if (clip != null && clip.length > 0f)
                {
                    Plugin.Log.LogInfo($"BELL loaded {Path.GetFileName(path)}, {clip.length:F1} s");
                    break;
                }
                Plugin.Log.LogError($"BELL {path} decoded to an empty clip");
                clip = null;
            }
            if (clip == null)
            {
                Plugin.Log.LogWarning($"BELL no {FileName}.ogg/.wav/.mp3 next to the DLL, storm bell is off");
                yield break;
            }
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }

        // In game hours (GameState.day * 24 + Sun.globalTime) of the last time the bell woke the player.
        private float lastWake = float.NegativeInfinity;
        private const float WakeCooldownHours = 3f;

        private void Update()
        {
            if (!Plugin.StormBell.Value || !GameState.playing)
                return;
            // Unscaled: asleep the game runs 16 times faster, and the bell rang every few seconds (Кэп, 1.0.0).
            checkTimer -= Time.unscaledDeltaTime;
            if (checkTimer > 0f)
                return;
            checkTimer = 1f;
            WakeIfTearing();
            if (clip == null)
                return;

            Transform boat = GameState.currentBoat;
            float pressure = boat != null ? MaxFullPressure(boat) : 0f;
            bool now = pressure > Plugin.OverloadStart.Value;
            if (now && !storm)
            {
                Plugin.Log.LogInfo($"BELL storm: full-sail pressure {pressure:F1} > {Plugin.OverloadStart.Value:F1}");
                nextRing = Time.unscaledTime; // ring at once when the storm starts
            }
            else if (!now && storm)
                Plugin.Log.LogInfo($"BELL storm over: full-sail pressure {pressure:F1}");
            storm = now;

            if (storm && Time.unscaledTime >= nextRing)
            {
                source.PlayOneShot(clip, Plugin.BellVolume.Value);
                nextRing = Time.unscaledTime + Random.Range(Plugin.BellMinInterval.Value, Plugin.BellMaxInterval.Value);
            }
        }

        // Asleep on board while a set sail tears: the bell wakes you (Кэп, 1.0.0). Only when sails really tear, not for
        // a storm over furled canvas, and not again for WakeCooldownHours of game time after it woke you.
        private void WakeIfTearing()
        {
            if (!GameState.sleeping || !GameState.eyesFullyClosed || Sleep.instance == null || Sun.sun == null ||
                DamageModel.Instance == null || !DamageModel.Instance.Tearing)
                return;
            float now = GameState.day * 24f + Sun.sun.globalTime;
            if (now - lastWake < WakeCooldownHours)
                return;
            lastWake = now;
            Plugin.Log.LogInfo($"BELL woke the player: sails are tearing (day {GameState.day}, {Sun.sun.globalTime:F1} h)");
            if (clip != null && source != null)
                source.PlayOneShot(clip, Plugin.BellVolume.Value);
            nextRing = Time.unscaledTime + Random.Range(Plugin.BellMinInterval.Value, Plugin.BellMaxInterval.Value);
            Sleep.instance.WakeUp();
            PressureLogger.Instance?.ShowMessage("The storm bell woke you: the sails are tearing!");
        }

        private static float MaxFullPressure(Transform boat)
        {
            float max = 0f;
            foreach (Sail sail in DamageModel.BoatSails(boat))
            {
                if (sail.shipRigidbody == null)
                    continue;
                // A furled sail's pressure is 1.5% of the set one, so its noise is magnified 66x; still within the
                // 9-19 range the first log gave for furled sails in wind 19 (notes/game-code.md).
                float p = sail.appliedWindForce / Mathf.Max(0.015f, sail.currentUnroll);
                if (p > max)
                    max = p;
            }
            return max;
        }
    }
}
