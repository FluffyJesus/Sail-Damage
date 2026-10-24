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

        private void Update()
        {
            if (clip == null || !Plugin.StormBell.Value || !GameState.playing)
                return;
            checkTimer -= Time.deltaTime;
            if (checkTimer > 0f)
                return;
            checkTimer = 1f;

            Transform boat = GameState.currentBoat;
            float pressure = boat != null ? MaxFullPressure(boat) : 0f;
            bool now = pressure > Plugin.OverloadStart.Value;
            if (now && !storm)
            {
                Plugin.Log.LogInfo($"BELL storm: full-sail pressure {pressure:F1} > {Plugin.OverloadStart.Value:F1}");
                nextRing = Time.time; // ring at once when the storm starts
            }
            else if (!now && storm)
                Plugin.Log.LogInfo($"BELL storm over: full-sail pressure {pressure:F1}");
            storm = now;

            if (storm && Time.time >= nextRing)
            {
                source.PlayOneShot(clip, Plugin.BellVolume.Value);
                nextRing = Time.time + Random.Range(Plugin.BellMinInterval.Value, Plugin.BellMaxInterval.Value);
            }
        }

        private static float MaxFullPressure(Transform boat)
        {
            float max = 0f;
            foreach (Sail sail in boat.GetComponentsInChildren<Sail>())
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
