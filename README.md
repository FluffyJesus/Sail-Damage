# ⛵ Sail Damage

**Sails that tear, flap, and need mending.** A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Sailwind**.

In vanilla Sailwind you can carry every stitch of canvas through any storm. With Sail Damage, too much sail in too much wind will cost you: the cloth rips, the holes grow, the boat slows down, and in the end the sail tears away and flaps from its yard like a flag. Reef in time, or bring sailcloth and a needle.

<!-- screenshot: a brig in a storm with torn sails -->

---

## Contents

- [Features at a glance](#features-at-a-glance)
- [How sails get damaged](#how-sails-get-damaged)
- [What damage does](#what-damage-does)
- [What it looks like](#what-it-looks-like)
- [The storm bell](#the-storm-bell)
- [Repairs](#repairs)
- [Studding Sails compatibility](#studding-sails-compatibility)
- [Installation](#installation)
- [Configuration](#configuration)
- [Compatibility and saves](#compatibility-and-saves)

---

## Features at a glance

| | |
|---|---|
| 🌬️ **Overload** | A set sail in storm-force wind slowly tears. |
| 💥 **Swing hits** | A boom slamming into its sheet limit (an uncontrolled gybe, a slack sheet) rips the cloth in one blow. |
| 📉 **Lost thrust** | A torn sail pushes less, in proportion to its damage. |
| 🕳️ **Real holes** | Tears and ragged notches are cut into the canvas itself, and they grow with the damage. |
| 🏳️ **Tear-off** | At 100% the lower part of the sail is gone and the rag flaps loose from the yard. |
| 🔔 **Storm bell** | A ship's bell warns you while the wind is strong enough to tear your sails. |
| 🧵 **Mending at sea** | Buy a bolt of sailcloth and sew patches over the holes yourself. |
| ⚓ **Shipyard repair** | Pay the shipyard to make your sails as good as new. |
| 🪢 **Studding Sails** | Full support: studding sails tear, lose thrust and get mended on their own, side by side. |

---

## How sails get damaged

Every sail has its own damage, from **0%** (whole) to **100%** (torn away). Furled sails never take damage.

### 🌬️ Overload

The mod reads the actual wind pressure on each sail, the same number the game uses to push your boat. It already includes wind speed, the angle of the sail to the wind, the sail's height above the water (upper sails catch more wind), and the shadow of other sails.

- In normal sailing weather the pressure stays below the limit and nothing happens.
- In a storm a fully set sail goes over the limit and starts tearing, slowly at first and faster the harder it blows.
- Reefing a sail lowers its pressure. Taking in canvas in time is the whole point.

### 💥 Swing hits

Fore-and-aft sails (gaff, lateen, jibs and the like) swing on their hinge. When a sail flies across and stops dead at the end of its sheet, the cloth takes a blow:

- a hauled sheet and a controlled tack are safe;
- an uncontrolled gybe or a fully slack sheet in fresh wind can rip a big piece out of the sail at once;
- the harder the slam, the worse the damage (it grows with the square of the swing speed).

Every hit shows a short message on screen with the damage it did.

### 🧱 Battened sails

Junk and fin sails are stiffened with battens. They still wear and lose thrust, but they never drop below **25% health** and never tear away.

---

## What damage does

- **Thrust** drops with damage: a sail at 40% damage gives 60% of its force. A sail torn away gives nothing.
- **Holes** show in the canvas (see below).
- At **100%** the sail tears away.

---

## What it looks like

<!-- screenshot: close-up of holes in a sail -->

Holes are cut into the sail's own canvas, not painted on top, so you can see through them:

- **Tears** run along the seams of the cloth, wander a little, sometimes fork, and widen into gaps as the damage grows.
- **Notches** are bitten out of the edges, where a real sail gives way first.
- The pattern belongs to that sail. It is the same after loading a save, and it grows from the holes you already have instead of jumping around.
- The sail's colour is kept, so painted sails tear just as nicely.

### 🏳️ Torn away

<!-- screenshot: a torn-off square sail flapping from its yard -->

At 100% the lower half of the sail is gone. What is left hangs from the yard with a ragged edge and flaps in the wind. Sheets and lines that were tied to the lost corners disappear with it. A lateen sail tears loose at its tack instead and hangs whole from its yard.

### 🩹 Patches

<!-- screenshot: a sail with grey patches -->

Mended holes are covered with patches of plain, undyed grey sailcloth: octagons with a dark hem and a line of stitches. Holes are closed **one by one, each with its own patch**, smallest first, so you can watch your work. Patches stay on the sail as its history until a shipyard replaces the cloth.

If the sail tears again, the newest patches give way first and their holes open up again.

---

## The storm bell

Instead of text on the screen, a **ship's bell** rings every 30 to 60 seconds while the wind is strong enough to tear a fully set sail of the boat you are on. It rings at once when the storm starts and falls silent when it is over.

The bell warns about *any* sail that would be overloaded if it were fully set, furled ones included. If you hear it, check your canvas.

---

## Repairs

### ⚓ At the shipyard

<!-- screenshot: the Patch Sails button at the shipyard -->

A **Patch Sails** button appears under *Repair Ship*. It shows the average damage of your sails and adds a line to the order. Each sail costs up to **70% of a new sail** of its kind, in proportion to its damage. Paid sails come back as new: no holes and no patches.

### 🧵 At sea: sailcloth

<!-- screenshot: a bolt of sailcloth in a shop -->

A new item, **sailcloth**: a bundle of folded grey canvas tied with rope.

- **Where:** at eleven shops that sell oakum, across all three regions. Each keeps two bolts on display, next to the oakum.
- **How to use:** put the bolt down on deck, look at it and **hold the right mouse button**. A needle starts sewing and a progress bar fills. The most damaged sail of your boat is mended first.
- **How much:** one bolt mends **100 m²** of holes (sail area × damage), at **2 m² per second**. The bolt shows how much cloth is left, and it is used up in the end.

---

## Studding Sails compatibility

<!-- screenshot: studding sails with their own holes -->

Sail Damage fully supports the **Studding Sails** mod. Studding sails are treated as sails of their own, and **each side (port and starboard) is separate**:

- they tear from overload **only while they are set**, faster the more they are set; a side that is taken in never tears;
- they work on their own: studding sails set from their winch tear even when their parent sail is furled;
- their thrust drops with their own damage;
- they show **their own holes and patches**, not the parent sail's;
- sailcloth mends the parent sail first, then its studding sails;
- the shipyard repair includes them in the price and mends them together with their sail;
- their damage is saved with the game.

No settings needed. If Studding Sails is not installed, the mod simply works without it.

---

## Installation

1. Install **BepInEx 5** for Sailwind.
2. Download the release archive and unpack it into the Sailwind folder, so that you get:
   ```
   Sailwind/BepInEx/plugins/SailDamage/SailDamage.dll
   Sailwind/BepInEx/plugins/SailDamage/ship-bell.wav
   ```
3. Start the game. The settings file appears at `BepInEx/config/kap.saildamage.cfg` after the first launch.

Keep `ship-bell.wav` next to the DLL: without it the storm bell is silent.
---

## Configuration

All settings are in `BepInEx/config/kap.saildamage.cfg`.

| Section | Setting | Default | What it does |
|---|---|---|---|
| Damage | `OverloadStart` | 25 | Sail pressure above which overload damage starts. Normal wind gives about 14-22, a storm 25-53. |
| Damage | `OverloadFull` | 45 | Pressure at which overload damage reaches `OverloadRate`. |
| Damage | `OverloadRate` | 0.01 | Damage per second at `OverloadFull` (0.01 = 1% per second). |
| Damage | `SwingSafe` | 120 | Swing speed (degrees per second) below which a sail stopping at its sheet takes no damage. |
| Damage | `SwingFull` | 400 | Swing speed at which one hit does `SwingMaxDamage`. |
| Damage | `SwingMaxDamage` | 0.25 | Damage of one hit at `SwingFull`. |
| Damage | `JunkMinHealth` | 0.25 | Junk and fin sails never drop below this health. |
| Damage | `ThrustLoss` | 1 | How much thrust is lost per unit of damage (1 = a torn sail gives nothing). |
| Visual | `ShowHoles` | true | Draw holes in damaged sails. |
| Visual | `TearOff` | true | At 100% the lower part of the sail tears away. |
| Repair | `ShipyardRepairShare` | 0.7 | Shipyard price of mending a fully torn sail, as a share of a new one. |
| Repair | `SailclothPrice` | 300 | Price of a bolt of sailcloth. |
| Repair | `SailclothArea` | 100 | Square metres of holes one bolt mends. |
| Repair | `MendRate` | 2 | Square metres mended per second of sewing. |
| Repair | `SailclothPrefabIndex` | 20531 | Item slot of the sailcloth. Change only if another mod uses it, and never once you have sailcloth in a save. |
| Warning | `StormBell` | true | Ring the storm bell. |
| Warning | `BellVolume` | 0.8 | Bell volume, 0 to 1. |
| Warning | `BellMinInterval` / `BellMaxInterval` | 30 / 60 | Pause between rings, in seconds. |
| Warning | `OverloadMessages` | false | Also show a text line for every percent of overload damage. |
| Debug | `TestKeys` | false | Test keys: **F9** +10% damage to every set sail, **Shift+F9** mend all sails, **F6** a bolt of sailcloth into your hands, **F8** log sail pressure, **F10** log how sails are drawn. |

---

## Compatibility and saves

- **Saves:** damage, patches and studding sail damage are stored in the save's own mod data, matched to each sail by its place on the mast. Saves without the mod load fine.
- **Sailcloth** is a new item; bolts lying on your boat need the mod to load.
- **Studding Sails:** full support, see above.
- Works with modded boats and sails that use the game's own sail system.

---

<p align="center"><i>Fair winds, and watch your canvas.</i></p>
