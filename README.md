# Diet Setup

A Vintage Story mod that scores every food item against a character's diet using a
tag-based rules engine, scaling how much satiety and nutrition they gain per vanilla food
category (Fruit, Vegetable, Protein, Grain, Dairy) — and, for a diet fundamentally
incompatible with what was eaten, marking it Harmful or Inedible instead.

## Installation

Drop the release zip/folder into your `Mods` folder (client and server both — this mod is
required on both sides; a mismatched install is rejected at connection time by the game
engine's own mod-sync check, triggered by this mod's `requiredOnClient`/`requiredOnServer`
manifest flags, not by any check this mod's own code performs). No dependencies beyond
vanilla Vintage Story 1.22.0+ and its bundled `survival` mod at 1.22.0+.

Config lives at `ModConfig/dietsetup.json` after first run:
- `EnableDietSystem` (default `true`) — master on/off switch. When off, every patch is a
  no-op and the vanilla numbers apply untouched.

## How it works

Every food item is tagged along three axes — source, state, and form (see
`assets/dietsetup/config/foodtags.json`, e.g. `meat`, `preserved`, `liquid`) — resolved
from its item code and live spoilage/transition state.

A **diet** (`assets/dietsetup/config/diets/*.json` — currently `base`, `human`, `elf`,
`dwarf`, `orc`, `goblin`) is a priority-ordered list of rules, each matching a set of
required/excluded tags to a verdict (Edible, Nourishing, Harmful, or Inedible) and a
satiety/nutrition multiplier — flat, or a curve keyed to how spoiled the food is. A diet
can `extend` another diet's category capacities. An item that matches no rule falls back
to the diet's own flat multiplier.

A character's diet is resolved fresh on every eat, tooltip render, and nutrition query —
nothing is cached per player. The order is: an explicit per-player override, then the
first race trait (via `raceframework`/vanilla `CharacterSystem`) matched against
`ModConfig/dietsetup/bindings.json`'s trait→diet map, then that file's own default, then
`base`. With no race mod installed, every player just gets the bindings file's default.

## What you see in game

- A tooltip line on any food that isn't plain Edible for your diet — "Especially
  nourishing for you", "Harmful for you", or "Inedible for you" — including on pies.
- A "Diet & Nutrition" page in the survival handbook.
- Otherwise nothing changes visually: your satiety and nutrition bars just move by
  different amounts depending on what you eat.

## Chat commands

| Command | Privilege | What |
|---|---|---|
| `/diettags` | Any player (client) | Print the resolved tag set for the item in your hand. |
| `/dietresolve <dietId>` | Any player (client) | Resolve the item in your hand against a named diet, without eating it. |
| `/dietdiag [itemcode]` | Player | Dump your resolved diet, hunger/health state, and held-item tags — or how a given item code resolves. |
| `/dietshow <dietId>` | Player | Print one compiled diet: capacities, fallback, and every rule in win order. |
| `/dietfactsqueue` | Player | Diagnostic: your pending real-eat nutrition-multiplier queue counts. |
| `/dietdrainsatiety` | Player | Debug: zero your own satiety without touching nutrition levels. |
| `/dietassignrules <dietId\|clear>` | Admin | Override your own diet directly, bypassing race-trait resolution. |
| `/dietreload` | Admin | Re-run the diet load pipeline (tags, diets, extends, compile, validate) and re-sync all clients. |
| `/dietsetnutrition <category\|all> <value>` | Admin | Debug: set a nutrition level directly. |
| `/dietrotintake [value]` | Admin | Debug: get/set/clear your rot-intake accumulator (see below). |

## Cross-mod integration

Each player entity carries a decaying per-tag intake accumulator, readable by other mods
with zero dependency on Diet Setup:

| Key | Type | Units |
|---|---|---|
| `dietsetup:intake:<tag>` | double | 0..cap, unitless (0 = none eaten recently, cap = saturated) |
| `dietsetup:intake:<tag>:updatedHours` | double | `world.Calendar.TotalHours` at the last write |

Only `dietsetup:intake:rot` is written today. The value decays exponentially on an
in-game calendar-hour half-life (not real time); a reader computes the live value on
demand from the raw value and the elapsed hours since `updatedHours` — see `rfmechanics`'
`GoblinRotAuraBehavior.ReadLiveRotIntake` for a worked example. This is the current key
shape, not a stability promise — the mod is still on `-pre` tags and this shape may change.

- **Uninstalling is safe.** All per-player state this mod writes — the two `dietsetup:intake:*`
  doubles above and the `dietsetup:dietOverride` string set by `/dietassignrules` — lives in
  the player entity's generic attribute tree, which loads unrecognized keys without any
  schema check; removing the mod just leaves them as unread, harmless data.

## Reporting issues

Please include: the mod version, your Vintage Story version, whether you're on a
dedicated server or singleplayer, any other food-related mods installed, and the
relevant lines from `client-main.log` / the server log (search for `[dietsetup]`).
