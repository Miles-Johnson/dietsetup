# ModConfig-examples

Reference copies only. The three files here document the shape of config that lives
outside this repo, at `VintagestoryData/ModConfig/dietsetup/` on each machine/server —
never packaged, never uploaded by `deploy.ps1`, and (aside from the tracked
`assets/dietsetup/config/foodtags.json` base file, which `foodtags.json.example` below
overrides per tag id) not shipped with the mod at all. Edit the live files by hand,
through each server's own panel; these `.example` files are for onboarding and drift
comparison, not for copying into place as-is.

## bindings.json.example

Read by `src/Rules/DietLoadPipeline.cs` (`ModConfigBindingsFile`, `dietsetup/bindings.json`).
Maps a raceframework trait code to a diet id; `default` is the diet id used when no
listed trait is present. A trait present in-game with no row here — or the whole file
missing — falls through to `default`. The engine logs `bindings: {N} mapped` on every
load/`/dietreload`; use that count to catch a server's file silently drifting from what
was intended (fewer mapped entries than expected means fewer races resolve their own
diet, not `base`).

## food-overrides.json.example

Read by `src/Grants/FoodOverrideRegistry.cs` (`ModConfigFile`, `dietsetup/food-overrides.json`).
Grants base-game edibility (`NutritionProps`) to a wildcard pattern of item codes that
don't already have it, so a diet's rules have something to react to. A grant is refused
outright — as a whole-file no-op, not a per-row skip — when every collectible the
pattern currently matches already carries `NutritionProps`; some other mod or vanilla
itself already made it edible, so the grant would do nothing. Check the server's
`[dietsetup]` log lines (not just the load-summary line) to tell that refusal apart from
"file wasn't read."

## foodtags.json.example

Read by `src/Rules/DietLoadPipeline.cs` (`ModConfigFoodTagsFile`, `dietsetup/foodtags.json`),
added in commit `c24ce834` (2026-09-03). Overrides the shipped
`assets/dietsetup/config/foodtags.json` per tag id: a tag id listed here *replaces* that
id's whole pattern list (not a merge), across any of the three axes (`source`/`state`/
`form`) — an id introduced here that wasn't in the shipped file just adds a new tag. A
missing file is normal, not a warning; nothing is logged when it's absent. A malformed
file logs an error and the shipped asset tags are kept unchanged.
