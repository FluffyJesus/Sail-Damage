using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SailDamage
{
    // Sailcloth for sale: a stack of two bolts at the shops that sell oakum. Кэп placed them by hand in game
    // (0.5.9-0.5.10, F7, now removed); a place is the floor point and turn relative to the shop's first oakum spawner.
    public class SailclothShops : MonoBehaviour
    {
        private const int Stack = 2;
        // oakum spawner path = x|y|z|yaw, separated by ;
        private const string Placements =
            "island 15 M (Fort) scenery/shop item (278)=-0.467|0.003|-0.055|144.457;" +
            "island 1 A (gold rock) scenery/shop item (218)=2.142|-0.922|-1.103|-62.107;" +
            "island 9 E (dragon cliffs) scenery/shop item spawner (229)=0.564|0.004|-0.368|-52.798;" +
            "island scenery Lagoon Shipyard/shop item (244)=0.013|-0.059|-0.428|96.923;" +
            "island 21 M (siren song) scenery/shop item (99)=1.219|0.001|-1.137|35.538;" +
            "island 3 A (neverdin) scenery/shop spawner debug (22)=-0.063|-0.847|-1.058|-22.891;" +
            "island 37 E/shop item (132)=-0.066|-0.629|0.95|177.377;" +
            "island 12 E (new port) scenery/shop item supplies (13)=2.762|-0.84|-0.489|-3.019;" +
            "island 11 E (crab beach) scenery/shop spawner crab cakes (72)=-0.231|-0.597|0.636|44.414;" +
            "island 2 A (alnilem) scenery/shop item (bottle) (34)=0.112|-0.83|-0.675|-45.105;" +
            "island 16 M (sunspire) scenery/shop item (62)=0.068|-0.003|0.575|-93.325";

        private static Dictionary<string, string> places;
        private readonly HashSet<ShopItemSpawner> seen = new HashSet<ShopItemSpawner>();
        private float nextTick;

        private void Update()
        {
            if (!GameState.playing || !Sailcloth.Ready || Time.unscaledTime < nextTick)
                return;
            nextTick = Time.unscaledTime + 2f;
            Tick();
        }

        // Shops of the islands loaded now (the game loads an island scene within 1800 m and unloads it further away).
        private void Tick()
        {
            seen.RemoveWhere(s => s == null);
            List<ShopItemSpawner> oakums = FindObjectsOfType<ShopItemSpawner>().Where(IsOakum).ToList();
            foreach (ShopItemSpawner s in oakums)
            {
                if (seen.Contains(s))
                    continue;
                List<ShopItemSpawner> members = oakums.Where(o => Vector3.Distance(o.transform.position, s.transform.position) < 3f).ToList();
                foreach (ShopItemSpawner m in members)
                    seen.Add(m);
                ShopItemSpawner anchor = members.OrderBy(m => Path(m.transform), StringComparer.Ordinal).First();
                Build(anchor, Path(anchor.transform));
            }
        }

        private static bool IsOakum(ShopItemSpawner s) => s != null && s.itemPrefab != null && s.itemPrefab.GetComponent<ShipItemOakum>() != null;

        private static void Build(ShopItemSpawner anchor, string key)
        {
            if (!Places().TryGetValue(key, out string v))
                return;
            string[] parts = v.Split('|');
            var f = new float[4];
            if (parts.Length != 4 || Enumerable.Range(0, 4).Any(i => !float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])))
            {
                Plugin.Log.LogWarning($"SHOP '{key}': bad placement '{v}'");
                return;
            }
            Transform t = anchor.transform;
            Vector3 floor = t.TransformPoint(new Vector3(f[0], f[1], f[2]));
            Quaternion rot = Quaternion.Euler(0f, Yaw(t.forward) + f[3], 0f);
            Bounds b = Footprint(rot);
            Vector3 pos = floor - new Vector3(b.center.x, 0f, b.center.z);
            pos.y = floor.y - b.min.y + 0.01f;
            for (int i = 0; i < Stack; i++)
                Spawn(anchor, pos + Vector3.up * ((b.size.y + 0.01f) * i), rot);
            Plugin.Log.LogInfo($"SHOP sailcloth stack ({Stack}) at '{key}'");
        }

        private static void Spawn(ShopItemSpawner oakum, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject("SailDamage sailcloth spawner");
            go.SetActive(false);
            go.transform.SetParent(oakum.transform.parent, true);
            go.transform.position = pos;
            go.transform.rotation = rot;
            var sp = go.AddComponent<ShopItemSpawner>();
            sp.itemPrefab = Sailcloth.Template;
            sp.availableAtNight = oakum.availableAtNight;
            sp.priceMult = oakum.priceMult;
            go.SetActive(true);
        }

        // World-axis bounds of the bolt's mesh around its pivot, turned by rot.
        private static Bounds Footprint(Quaternion rot)
        {
            MeshFilter mf = Sailcloth.Template.GetComponent<MeshFilter>();
            Bounds local = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.3f);
            Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, rot, Sailcloth.Template.transform.localScale);
            var res = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                res.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
            return res;
        }

        private static Dictionary<string, string> Places()
        {
            if (places != null)
                return places;
            places = new Dictionary<string, string>();
            foreach (string e in Placements.Split(';'))
            {
                int i = e.LastIndexOf('=');
                if (i > 0)
                    places[e.Substring(0, i).Trim()] = e.Substring(i + 1).Trim();
            }
            return places;
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null; x = x.parent)
                p = x.name + "/" + p;
            return p;
        }

        private static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
    }
}
