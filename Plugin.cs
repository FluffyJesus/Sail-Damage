using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SailDamage
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "kap.saildamage";
        public const string Name = "Sail Damage";
        public const string Version = "1.0.2";

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> OverloadStart;
        internal static ConfigEntry<float> OverloadFull;
        internal static ConfigEntry<float> OverloadRate;
        internal static ConfigEntry<float> SwingSafe;
        internal static ConfigEntry<float> SwingFull;
        internal static ConfigEntry<float> SwingMaxDamage;
        internal static ConfigEntry<float> ThrustLoss;
        internal static ConfigEntry<bool> ShowHoles;
        internal static ConfigEntry<bool> TestKeys;
        internal static ConfigEntry<bool> TearOff;
        internal static ConfigEntry<float> JunkMinHealth;
        internal static ConfigEntry<bool> StormBell;
        internal static ConfigEntry<float> BellVolume;
        internal static ConfigEntry<float> BellMinInterval;
        internal static ConfigEntry<float> BellMaxInterval;
        internal static ConfigEntry<bool> OverloadMessages;
        internal static ConfigEntry<float> ShipyardRepairShare;
        internal static ConfigEntry<int> SailclothPrefabIndex;
        internal static ConfigEntry<int> SailclothPrice;
        internal static ConfigEntry<float> SailclothArea;
        internal static ConfigEntry<float> MendRate;

        private void Awake()
        {
            Log = Logger;
            OverloadStart = Config.Bind("Damage", "OverloadStart", 25f,
                "Sail pressure (appliedWindForce) above which overload damage starts. Normal wind gives 14-22, storm 25-53.");
            OverloadFull = Config.Bind("Damage", "OverloadFull", 45f,
                "Pressure at which overload damage reaches OverloadRate.");
            OverloadRate = Config.Bind("Damage", "OverloadRate", 0.01f,
                "Damage per second (1 = torn) at OverloadFull pressure, linear from OverloadStart.");
            SwingSafe = Config.Bind("Damage", "SwingSafe", 120f,
                "Hinge speed in deg/s below which a sail stopping at its limit does no damage. Hauled sheets gave 66.");
            SwingFull = Config.Bind("Damage", "SwingFull", 400f,
                "Hinge speed in deg/s at which a hit does SwingMaxDamage. Fully eased sheets gave ~250 in wind 15, gybe in wind 28 gave 317.");
            SwingMaxDamage = Config.Bind("Damage", "SwingMaxDamage", 0.25f,
                "Damage of one hit at SwingFull speed (grows with the square of speed above SwingSafe).");
            JunkMinHealth = Config.Bind("Damage", "JunkMinHealth", 0.25f,
                "Junk and fin sails (battened) never drop below this health (0.25 = at most 75% damage) and never tear away.");
            ThrustLoss = Config.Bind("Damage", "ThrustLoss", 1f,
                "Share of thrust lost per unit of damage: force = 1 - damage * ThrustLoss (1 = a torn sail gives nothing).");
            ShowHoles = Config.Bind("Visual", "ShowHoles", true,
                "Draw tears in damaged sails (cutout material with a hole mask).");
            TearOff = Config.Bind("Visual", "TearOff", true,
                "At 100% damage the sail tears away below: the lower half is gone and the rag on the upper yard flaps loose.");
            ShipyardRepairShare = Config.Bind("Repair", "ShipyardRepairShare", 0.7f,
                "Shipyard price of mending a fully torn sail, as a share of a new sail of the same kind (scales with damage).");
            SailclothPrefabIndex = Config.Bind("Repair", "SailclothPrefabIndex", 20531,
                "Item slot of the sailcloth in PrefabsDirectory. Must not clash with other mods' items (HatchTarp uses 20517, " +
                "HangingHammock 900). Changing it breaks sailcloth already in saves.");
            SailclothPrice = Config.Bind("Repair", "SailclothPrice", 300, "Price of a bolt of sailcloth.");
            SailclothArea = Config.Bind("Repair", "SailclothArea", 100f,
                "Square metres of holes (sail area x damage) one full bolt of sailcloth mends.");
            MendRate = Config.Bind("Repair", "MendRate", 2f, "Square metres of holes mended per second of sewing.");
            TestKeys = Config.Bind("Debug", "TestKeys", false,
                "Keys for testing. F9: +10% damage to every set sail of your boat. Shift+F9: reset damage and patches of " +
                "your boat's sails. F6: a bolt of sailcloth into your hands. F8: log sail pressure (toggle). " +
                "F10: log how every sail is drawn.");
            StormBell = Config.Bind("Warning", "StormBell", true,
                "Ring a ship's bell while the wind could tear a set sail (full-sail pressure above OverloadStart). " +
                "Sound: ship-bell.ogg, .wav or .mp3 next to the DLL.");
            BellVolume = Config.Bind("Warning", "BellVolume", 0.8f, "Bell volume, 0..1.");
            BellMinInterval = Config.Bind("Warning", "BellMinInterval", 30f, "Shortest pause between rings, seconds.");
            BellMaxInterval = Config.Bind("Warning", "BellMaxInterval", 60f, "Longest pause between rings, seconds.");
            OverloadMessages = Config.Bind("Warning", "OverloadMessages", false,
                "Show an on-screen line for every percent of overload damage (the bell replaces it).");

            gameObject.AddComponent<PressureLogger>();
            gameObject.AddComponent<DamageModel>();
            gameObject.AddComponent<SailVisuals>();
            gameObject.AddComponent<BellWarning>();
            gameObject.AddComponent<SailMending>();
            gameObject.AddComponent<SailclothShops>();
            gameObject.AddComponent<VirtualCrewCompat>();
            new Harmony(Guid).PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
