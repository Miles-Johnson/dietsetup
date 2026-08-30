# Diet Setup

> This README describes the currently shipped profile system. A rewrite is planned that
> replaces it with a single tag-based rules engine — see `notes/dietsetup-architecture.md`
> in the workspace repo for the target design. Not yet implemented.

A Vintage Story mod that lets each character pick a diet profile — Balanced, Carnivore,
Herbivore, or a custom one you author — that scales how much satiety/nutrition they gain from
each vanilla food category (Fruit, Vegetable, Protein, Grain, Dairy). Profiles biologically
incompatible with a category can react (damage on eating), and everything composes correctly
across solid food, liquids, and (with the caveat below) meals.

New characters are prompted to pick a profile once, via a dialog with three buttons. Existing
characters keep playing unmodified until they pick one.

## Installation

Drop the release zip/folder into your `Mods` folder (client and server both — this mod is
required on both sides; a mismatched install is rejected at connection time, not silently
ignored). No dependencies beyond vanilla Vintage Story 1.21.0+.

Config lives at `ModConfig/dietsetup.json` after first run:
- `EnableDietSystem` (default `true`) — master on/off switch.
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

Where `<tag>` is one of dietsetup's registered food tags (see `assets/dietsetup/config/foodtags.json`,
e.g. `preserved`, `grain`). Applies to both satiety and nutrient-bar gain. **Important:** entity
stats blend additively from a base of `1`, so a delta of `0.3` produces a **1.3x** multiplier —
pass `0.3` for "+30% benefit," not `1.3`.

Mod authors can call `DietProfileRegistry.RegisterProfile` to add their own profiles at runtime,
or ship a `config/profiles.json` in their own domain (merged via `Api.Assets.GetMany`, same as
`config/foodtags.json` below — no code dependency on dietsetup needed either way). A duplicate
profile `Id` across two domains is a hard startup error naming both. This API is functional but
not yet considered stable — expect it to evolve.

`raceframework` ships its Elf profile this way — see `config/profiles.json` in its own assets.

### Per-tag intake accumulator

For race mods that want to read "how much of tag X has this player recently eaten" without a
dependency on dietsetup, each player entity carries a decaying WatchedAttributes pair per tag:

| Key | Type | Units |
|---|---|---|
| `dietsetup:intake:<tag>` | double | 0..cap, unitless (0 = none eaten recently, cap = saturated) |
| `dietsetup:intake:<tag>:updatedHours` | double | `world.Calendar.TotalHours` at the last write |

Only `dietsetup:intake:rot` is written in v1. The value decays exponentially on an in-game
calendar-hour half-life (not real time); a reader computes the live value on demand from the raw
value and the elapsed hours since `updatedHours` — see `rfmechanics`' `GoblinRotAuraBehavior.
ReadLiveRotIntake` for a worked example. This key shape is a public, stable contract: renaming or
restructuring it breaks any mod already reading it.

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
- **`BlockMeal.GetContentNutritionProperties` is a full prefix replacement**, not a behavior
  delta on top of vanilla — it re-implements the method body to fix a vanilla bug (spoilage is
  resolved against the outer meal stack instead of the ingredient being scored) and to fold in
  this mod's own meal-reaction aggregation. Two consequences: it will drift silently on a future
  Vintage Story update to that method, and any other mod prefixing the same overload races this
  one for which prefix's result actually takes effect.
- **Pies and liquid containers still bypass the per-ingredient meal path.** `BlockPie` and
  `BlockLiquidContainerBase` call `FoodSpoilageSatLossMul`/`HealthLossMul` directly with their
  own outer stack (the whole pie, the liquid content) rather than routing through
  `BlockMeal.GetContentNutritionProperties` — so a pie resolves as one whole `meal`-tagged item
  through the entity's diet curve, not per filling ingredient. Decided out of scope for the
  current spoilage-curve patch; closing this means neutralizing `BlockPie.GetNutritionHealthMul`'s
  own direct call (see `notes/dietsetup-tag-engine-handover.md`, amended prompt 7 target 4).
- **Uninstalling is safe.** All per-player state this mod writes is stored as plain
  string/bool values on the player entity; removing the mod leaves those as inert, harmlessly
  orphaned data rather than breaking save loading.

## Reporting issues

Please include: the mod version, your Vintage Story version, whether you're on a dedicated
server or singleplayer, any other food-related mods installed, and the relevant lines from
`client-main.log` / the server log (search for `[dietsetup]`).



Here's the write-up:

Hi, everyone. Diet Setup 1.0.0 is now available for Vintage Story 1.21.0+.

Diet Setup turns food choice into a character-defining decision without touching item balance. Each character picks a diet profile that changes how much satiety and nutrition they get from each vanilla food category — no new items, no recipe changes, just a different lens on the food you already have.

Features

Choose a diet profile at character creation; reopen the picker anytime with /dietsel.
Four built-in profiles — Balanced, Carnivore, Herbivore, and Elf — each with distinct per-category satiety/nutrition multipliers.
Eating outside your profile isn't just weaker: Carnivore and Herbivore trigger a damage reaction on incompatible categories.
Custom profiles and food tags are fully data-driven (profiles.json) so pack authors can add their own.
Race-mod integration hook: any mod can grant a per-tag nutrition multiplier to an entity with a single stat call and zero code dependency on Diet Setup — multipliers stack multiplicatively with the active profile.
Applies across both solid food and liquid containers.
Config toggles for enabling the system, auto-prompting new characters, and setting a default profile.
Diagnostic and admin commands (/diettagmult, /dietdiag, /dietselgrant, /dietdrainsatiety) for testing profiles and race-mod-style grants without a second mod installed.
Existing characters are unaffected until they opt in. All per-player state is plain data on the entity, so uninstalling is safe — no orphaned dependencies.

Known limitations, disclosed up front: meals don't yet respect diet profiles, modded food items need tag patterns added before they participate, and there's been no dedicated-server or cross-mod compatibility testing yet (ACulinaryArtillery, Expanded Foods, Wildcraft, etc.).

Required on both client and server; mismatched versions are rejected on connect.

Planned next: meal support, and compatibility testing against other food/nutrition mods.

Repo and installation:
https://github.com/Miles-Johnson/dietsetup

Feedback, testing (especially on dedicated servers), and race-mod integration reports are very welcome.