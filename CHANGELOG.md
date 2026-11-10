# Changelog

## 0.4.0

- **Pictures in the decor panel.** Most things showed a blank square: the game's materials only draw for its own camera, and after a dozen blanks in a row the panel stopped trying. Pictures are now drawn with plain copies of each thing's textures, and every thing gets one.
- **Plain names.** Things are called what they are (Metal bunk bed, Tall gun safe, Strip light, Pile of scrap) rather than by the game's file names (bed_metal_bunk, scontainer_gunsafe_tall, Lum_lamp_hideout, heap_scrap_3). Different models of the same thing are numbered (Armchair, Armchair 2...). The same names show when you point at things in the hideout.
- **New categories, in a list down the side** with how many things each holds: Furniture, Storage, Lighting, Electronics, Kitchen, Medical, Weapons & gear, Gym & range, Tools, Clothing, Books & paper, Decoration, Seasonal, Pets, Bathroom, Pipes & cables, Building, Junk. Mods' own categories (Defences) go after them. The list has room for more where the row of tabs didn't.
- The panel no longer offers colliders, decals, sound emitters and other parts of the scene that aren't things.

## 0.3.0

- **Every one of the hideout's things, not only the areas'.** The lamps on the walls and ceiling, the barrels and clutter that belong to no area, the seasonal decorations (Halloween's pumpkins, candles and bats, and the like), and things standing beside an area rather than in one of its levels can all be moved, turned, hidden, given to an area and copied from the panel. Things that belong to no area show as "the hideout (no area)" and stay where you put them.
- **Things in groups the mod didn't know are separate.** Some areas keep their things in a group of their own (the gear rack's and the cultists' room's PROPS); such a group counted as one thing, too big to pick up. Each thing in it is now its own.
- Walls, floors, ceilings and the long cable runs are still the room: anything over 6 m across stays put.

## 0.2.1

- **The bunk bed, tractor tyre and aluminium ladder by the gym can be moved and hidden.** They're the Defective Wall's leftovers, kept in a group the mod didn't know, so the three counted as one thing too big to pick up. Each one (and the rest of that clutter) is now a thing of its own.

## 0.2.0

- **Your arrangement in Tushonka: Hideout Under Siege.** Things you've moved, given away or hidden, and your decorations, are in the siege's copy of your hideout. In co-op everyone gets the host's.
- **For modders:** `HideoutInteriorsApi.RaidCopySnapshot()` and `BuildRaidCopy(snapshot)`.
- **For modders:** `HideoutInteriorsApi.AddDecor(key, name, kind, make)` puts a thing of your mod's in the decor panel, and `DecorOf(key)` finds the ones placed. A `kind` of your own becomes a tab of its own, and `AddDecor(key, name, kind, make, limit)` caps how many a hideout can have (the panel shows how many are in). Tushonka: Hideout Under Siege adds a Defences tab this way.
- Nothing runs in a raid copy once it's set up (it was looking for the hideout's overview every second, a stall you could feel).

## 0.1.0

First release. Needs SPT 4.1.x (made on 4.1.6); client only. Works with or without Hideout Extensions, and alongside Fika 2.4.

- **Decor mode** from a button on the hideout's overview.
- **Point at any of the hideout's things** to outline it and see what it is and which area it belongs to; click to select, Shift or Ctrl+click to add, drag on the floor to box-select.
- **Move** a selection by dragging it onto any surface, or exactly with the **X / Y / Z arrows** (Ctrl: 5 cm steps). **Turn** it with the **rings** (Ctrl: 15° steps) or R / Shift+R. G switches arrows and rings.
- **Give things to another area** by clicking its icon, or keep them with no area so they stay put when areas move.
- **Hide** the hideout's own things (Delete) and show them again from the Hidden tab; **Backspace** puts things back as the game has them; **RESET ALL** puts everything back.
- **Decorate with copies** of anything the hideout has, at every area level: a panel of every kind of thing, with pictures, by category, with a search box. Drag one into the hideout.
- The game's bar of areas is out of the way while decorating.
- Settings for the gizmo's size and thickness and the switch key.
