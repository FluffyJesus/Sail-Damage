using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailDamage
{
    // Virtual Crew (com.zorkinian.virtualcrew) mends sails too (Кэп, 1.0.0): a "Mend Sails" button in its Maintenance
    // window sends a free deckhand to sew, from a bolt of sailcloth on board, like the player does. He walks about the
    // deck with the bolt in hand; his speed follows his dexterity (1..5): 3 sews like the player.
    // Virtual Crew is not referenced: its types are reached by reflection, as Swab Decks does its work
    // (SwabDecksRequest): our task object is the crewman's CurrentTask, and the navigation coordinator moves him.
    // If any part is missing, the button is simply not there.
    public class VirtualCrewCompat : MonoBehaviour
    {
        private const float CycleSeconds = 5f;

        private static bool tried, present;
        private static PropertyInfo managerInstance, crewList, coordinatorInstance;
        private static PropertyInfo currentTask, dexterity, crewName, exhausted;
        private static MethodInfo deckhandCapable, randomDeck, holdItem, releaseItem, cancel, activeBoat;

        private static VirtualCrewCompat instance;

        // The running job: its owner object for Virtual Crew, and the crewman doing it.
        private sealed class MendTask
        {
            internal object Crewman;
            internal string Name;
            internal int Dexterity;
            internal float CycleStart = -100f;
            internal bool Holding;
        }

        private MendTask task;
        private float pollTimer;
        private float holes, cloth;
        private bool freeHand;

        private void Awake() => instance = this;

        private void Update()
        {
            if (!GameState.playing)
                return;
            TryInit();
            if (!present)
                return;
            pollTimer -= Time.unscaledDeltaTime;
            if (pollTimer <= 0f)
            {
                pollTimer = 1f;
                Poll();
            }
            if (task != null)
                TickTask();
        }

        private static void TryInit()
        {
            if (tried)
                return;
            tried = true;
            Type window = AccessTools.TypeByName("SailwindVirtualCrew.MaintenanceWindow");
            Type manager = AccessTools.TypeByName("SailwindVirtualCrew.VirtualCrewManager");
            Type crewman = AccessTools.TypeByName("SailwindVirtualCrew.Crewman");
            Type coordinator = AccessTools.TypeByName("SailwindVirtualCrew.CrewNavigationCoordinator");
            Type resolver = AccessTools.TypeByName("SailwindVirtualCrew.CrewBoatContextResolver");
            if (window == null || manager == null || crewman == null || coordinator == null)
                return;
            MethodInfo draw = AccessTools.Method(window, "DrawWindow");
            Type resizer = AccessTools.TypeByName("SailwindVirtualCrew.WindowResizer");
            MethodInfo resize = resizer != null ? AccessTools.Method(resizer, "HandleInWindow") : null;
            managerInstance = AccessTools.Property(manager, "Instance");
            crewList = AccessTools.Property(manager, "Crew");
            deckhandCapable = AccessTools.Method(manager, "IsDeckhandCapable", new[] { crewman });
            currentTask = AccessTools.Property(crewman, "CurrentTask");
            dexterity = AccessTools.Property(crewman, "Dexterity");
            crewName = AccessTools.Property(crewman, "Name");
            exhausted = AccessTools.Property(crewman, "IsExhausted");
            coordinatorInstance = AccessTools.Property(coordinator, "Instance");
            randomDeck = AccessTools.Method(coordinator, "TryBeginRandomDeckMovement", new[] { typeof(object), crewman, typeof(string) });
            holdItem = AccessTools.Method(coordinator, "HoldTemporaryItem", new[] { crewman, typeof(object), typeof(GameObject), typeof(string) });
            releaseItem = AccessTools.Method(coordinator, "ReleaseTemporaryItem", new[] { crewman, typeof(object) });
            cancel = AccessTools.Method(coordinator, "Cancel", new[] { typeof(object) });
            activeBoat = resolver != null ? AccessTools.Method(resolver, "GetActiveTopBoat") : null;
            if (draw == null || resize == null || managerInstance == null || crewList == null || deckhandCapable == null || currentTask == null ||
                dexterity == null || exhausted == null || coordinatorInstance == null || randomDeck == null || cancel == null)
            {
                Plugin.Log.LogWarning("CREW Virtual Crew found, but not the parts we need: no Mend Sails button");
                return;
            }
            // The window ends its scrolling content in WindowResizer.HandleInWindow and pushes the resize handle to the
            // bottom; anything drawn after that fell off the window (Кэп, 1.0.0). Our block goes in just before it.
            var harmony = new Harmony(Plugin.Guid + ".virtualcrew");
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(VirtualCrewCompat), nameof(DrawWindowPrefix)),
                                postfix: new HarmonyMethod(typeof(VirtualCrewCompat), nameof(DrawWindowPostfix)));
            harmony.Patch(resize, prefix: new HarmonyMethod(typeof(VirtualCrewCompat), nameof(HandleInWindowPrefix)));
            present = true;
            Plugin.Log.LogInfo("CREW Virtual Crew found: Mend Sails button in its Maintenance window");
        }

        // ------------------------------------------------------------ boat, bolts, crew

        private static Transform Boat()
        {
            Transform b = null;
            try { b = activeBoat?.Invoke(null, null) as Transform; } catch { }
            return b != null ? b : GameState.currentBoat != null ? GameState.currentBoat : GameState.lastBoat;
        }

        private static bool OnBoat(ShipItem item, Transform boat)
        {
            Transform at = item.currentActualBoat;
            return item.transform.IsChildOf(boat) || at != null && (at == boat || at.IsChildOf(boat) || boat.IsChildOf(at));
        }

        private static ShipItemSailcloth Bolt(Transform boat)
        {
            ShipItemSailcloth best = null;
            foreach (ShipItemSailcloth c in FindObjectsOfType<ShipItemSailcloth>())
                if (c.sold && c.amount > 0.0001f && c.held == null && OnBoat(c, boat) && (best == null || c.amount < best.amount))
                    best = c; // the most used one first
            return best;
        }

        private static IList Crew()
        {
            object manager = managerInstance.GetValue(null, null);
            return manager != null ? crewList.GetValue(manager, null) as IList : null;
        }

        // The free deckhand with the best hands.
        private static object FreeHand()
        {
            object manager = managerInstance.GetValue(null, null);
            IList crew = Crew();
            if (manager == null || crew == null)
                return null;
            object best = null;
            int bestDex = -1;
            foreach (object c in crew)
            {
                if (c == null || currentTask.GetValue(c, null) != null || (bool)exhausted.GetValue(c, null))
                    continue;
                if (!(bool)deckhandCapable.Invoke(manager, new[] { c }))
                    continue;
                int dex = (int)dexterity.GetValue(c, null);
                if (dex > bestDex)
                {
                    best = c;
                    bestDex = dex;
                }
            }
            return best;
        }

        private void Poll()
        {
            holes = cloth = 0f;
            freeHand = false;
            Transform boat = Boat();
            if (boat == null || DamageModel.Instance == null)
                return;
            foreach (Sail s in DamageModel.BoatSails(boat))
                holes += DamageModel.Instance.HoleArea(s);
            foreach (ShipItemSailcloth c in FindObjectsOfType<ShipItemSailcloth>())
                if (c.sold && c.held == null && OnBoat(c, boat))
                    cloth += c.amount / Sailcloth.FullAmount * Plugin.SailclothArea.Value;
            try { freeHand = FreeHand() != null; } catch { }
        }

        // ------------------------------------------------------------ the job

        private void Begin()
        {
            object c;
            try { c = FreeHand(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"CREW cannot pick a deckhand: {e.Message}");
                return;
            }
            if (c == null)
                return;
            task = new MendTask
            {
                Crewman = c,
                Name = crewName?.GetValue(c, null) as string ?? "A deckhand",
                Dexterity = Mathf.Clamp((int)dexterity.GetValue(c, null), 1, 5),
            };
            currentTask.SetValue(c, task, null);
            Plugin.Log.LogInfo($"CREW {task.Name} (dexterity {task.Dexterity}) mends the sails");
            Notify($"{task.Name} is mending the sails");
        }

        private void Stop(string why)
        {
            MendTask t = task;
            task = null;
            if (t == null)
                return;
            try
            {
                object coordinator = coordinatorInstance.GetValue(null, null);
                if (coordinator != null)
                {
                    cancel.Invoke(coordinator, new[] { (object)t });
                    releaseItem?.Invoke(coordinator, new[] { t.Crewman, t });
                }
                if (currentTask.GetValue(t.Crewman, null) == t)
                    currentTask.SetValue(t.Crewman, null, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"CREW stopping the job: {e.Message}");
            }
            Plugin.Log.LogInfo($"CREW {t.Name} stops mending: {why}");
            Notify($"{t.Name}: {why}");
            pollTimer = 0f;
        }

        private void TickTask()
        {
            MendTask t = task;
            object c = t.Crewman;
            IList crew = Crew();
            if (crew == null || !crew.Contains(c) || currentTask.GetValue(c, null) != t)
            {
                task = null; // Virtual Crew gave him other work (or sent him to bed); it cleans up its own side
                Plugin.Log.LogInfo($"CREW {t.Name} was called away from mending");
                return;
            }
            if ((bool)exhausted.GetValue(c, null))
            {
                Stop("too tired to go on");
                return;
            }
            Transform boat = Boat();
            if (boat == null)
            {
                Stop("no boat");
                return;
            }
            Sail target = SailMending.WorstSail(boat, out _);
            if (target == null)
            {
                Stop("the sails are mended");
                return;
            }
            ShipItemSailcloth bolt = Bolt(boat);
            if (bolt == null)
            {
                Stop("out of sailcloth");
                return;
            }

            if (Time.time >= t.CycleStart + CycleSeconds)
            {
                t.CycleStart = Time.time;
                object coordinator = coordinatorInstance.GetValue(null, null);
                if (coordinator != null)
                {
                    randomDeck.Invoke(coordinator, new[] { t, c, "mend sails" });
                    if (!t.Holding && holdItem != null && Sailcloth.Template != null)
                        t.Holding = (bool)holdItem.Invoke(coordinator, new[] { c, t, Sailcloth.Template, "sailcloth" });
                }
            }
            SailMending.Sew(target, bolt, Plugin.MendRate.Value * t.Dexterity / 3f * Time.deltaTime, "by " + t.Name);
        }

        private static void Notify(string text)
        {
            if (NotificationUi.instance != null)
                NotificationUi.instance.ShowNotification(text);
        }

        // ------------------------------------------------------------ Maintenance window

        private static bool inMaintenance;

        private static void DrawWindowPrefix() => inMaintenance = true;

        private static void DrawWindowPostfix() => inMaintenance = false;

        private static void HandleInWindowPrefix()
        {
            if (!inMaintenance)
                return;
            inMaintenance = false;
            try
            {
                DrawSails();
            }
            catch (Exception e)
            {
                GUI.enabled = true;
                Plugin.Log.LogWarning($"CREW Maintenance window: {e.Message}");
            }
        }

        private static void DrawSails()
        {
            VirtualCrewCompat me = instance;
            if (me == null)
                return;
            GUILayout.Space(4f);
            GUILayout.Label("Sails", Array.Empty<GUILayoutOption>());
            GUILayout.Label($"  Holes: {me.holes:F0} m², sailcloth on board: {me.cloth:F0} m²", Array.Empty<GUILayoutOption>());
            if (me.task != null)
            {
                GUILayout.Label($"  {me.task.Name} is mending (dexterity {me.task.Dexterity})", Array.Empty<GUILayoutOption>());
                if (GUILayout.Button("Stop Mending", Array.Empty<GUILayoutOption>()))
                    me.Stop("stopped");
                return;
            }
            bool can = me.holes > 0.01f && me.cloth > 0.01f && me.freeHand;
            GUI.enabled = can;
            if (GUILayout.Button("Mend Sails", Array.Empty<GUILayoutOption>()))
                me.Begin();
            GUI.enabled = true;
            if (!can && me.holes > 0.01f)
                GUILayout.Label(me.cloth <= 0.01f ? "  No sailcloth on board" : "  No free deckhand", Array.Empty<GUILayoutOption>());
        }
    }
}
