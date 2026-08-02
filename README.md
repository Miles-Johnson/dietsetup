# Diet Setup

A Vintage Story mod that lets each character pick a diet profile — Balanced, Carnivore,
Herbivore, or a custom one you author — that scales how much satiety/nutrition they gain from
each vanilla food category (Fruit, Vegetable, Protein, Grain, Dairy). Profiles biologically
incompatible with a category can react (damage on eating), raw meat can be granted edibility
under the right profile, and everything composes correctly across solid food, liquids, and
(with the caveat below) meals.

New characters are prompted to pick a profile once, via a dialog with three buttons. Existing
characters keep playing unmodified until they pick one.

## Installation

Drop the release zip/folder into your `Mods` folder (client and server both — this mod is
required on both sides; a mismatched install is rejected at connection time, not silently
ignored). No dependencies beyond vanilla Vintage Story 1.21.0+.

Config lives at `ModConfig/dietsetup.json` after first run:
- `EnableDietSystem` (default `true`) — master on/off switch.
- `AutoPromptNewCharacters` (default `true`) — auto-opens the profile picker for brand-new
  characters.
- `DefaultProfileId` (default `"balanced"`) — profile used for players who never picked one.

## Chat commands

| Command | Who | What |
|---|---|---|
| `/dietsel` | Any player | Reopen the profile-picker dialog (requires admin-granted permission or Creative mode). |
| `/dietselgrant <player>` | Moderator+ | Grant a player one-time permission to reopen `/dietsel`. |
| `/dietdrainsatiety` | Moderator+ | Debug: zero your own satiety bar without touching nutrition levels, for faster testing. |
| `/diettagmult <tag> [delta]` | Admin only | Debug: simulate a race-mod tag multiplier on yourself without needing another mod installed — see below. Not intended for normal play. |
| `/dietdiag [itemcode]` | Any player (client-side) | Diagnostic dump of your resolved profile/state, or how a specific item code resolves. |

## Race-mod / third-party integration

Other mods can grant a per-tag satiety/nutrition multiplier to an entity with zero dependency
on dietsetup's code — just set an entity stat:

```
entity.Stats.Set("dietsetup:<tag>Mult", "<sourcename>", <delta>, false);
```

Where `<tag>` is one of dietsetup's registered tags (see `assets/dietsetup/config/tags.json`,
e.g. `mushroom`). **Important:** entity stats blend additively from a base of `1`, so a delta of
`0.3` produces a **1.3x** multiplier — pass `0.3` for "+30% benefit," not `1.3`.

Mod authors can also call `DietProfileRegistry.RegisterProfile`/`RegisterTag`/`RegisterGrantRule`
directly to add their own content. This API is functional but not yet considered stable —
expect it to evolve.

## Known limitations

- **Meals don't respect custom diet profiles yet.** A cooked meal's nutrition currently uses
  vanilla numbers regardless of your active profile — this is a stated v1 limitation, not a bug.
- **Tag matching is wildcard-based** (e.g. `game:mushroom-*`), so it only matches vanilla item
  codes by default. Modded items with different code prefixes (a modded mushroom, say) won't
  pick up tag-based grants/multipliers unless a tag pattern is added for them.
- **Not tested for compatibility with other food-affecting mods** (e.g. ACulinaryArtillery,
  Expanded Foods, Wildcraft). This mod patches `GetNutritionProperties` on `CollectibleObject`
  and `BlockLiquidContainerBase` via Harmony — other mods patching the same methods may conflict.
  If you hit an issue running alongside another food mod, please report which one.
- **Uninstalling is safe.** All per-player state this mod writes is stored as plain
  string/bool values on the player entity; removing the mod leaves those as inert, harmlessly
  orphaned data rather than breaking save loading.

## Reporting issues

Please include: the mod version, your Vintage Story version, whether you're on a dedicated
server or singleplayer, any other food-related mods installed, and the relevant lines from
`client-main.log` / the server log (search for `[dietsetup]`).
