# test-fixtures/diets

14 dev diet documents used to exercise `DietLoadPipeline`/`DietExtendsResolver`/
`DietCompiler` (architecture §6) by hand, through `/dietreload` and `/dietresolve`
against a `ModConfig/dietsetup/diets/` directory. They are not loaded from here — see
"Not loaded automatically" below — and are not packaged; see the repo root
`Package.ps1` note.

Each fixture below is read directly, cross-checked against `src/Rules/DietCompiler.cs`,
`src/Rules/DietExtendsResolver.cs`, and (where noted) an in-game result already recorded
in `notes/dietsetup-handover.md` or `notes/dietsetup-rebuild-handover-2026-08-31-cont.md`.

| Fixture | Exercises |
|---|---|
| `fixture-cycle-a.json` + `fixture-cycle-b.json` | `extends` cycle (`a` → `b` → `a`). `DietExtendsResolver`'s `chain.Contains(id)` check refuses both — rule 3. |
| `fixture-depth-0.json` … `fixture-depth-8.json` | A 9-file `extends` chain, `depth-0` → `depth-1` → … → `depth-8` (terminal, no `extends`). `DietExtendsResolver.MaxDepth = 8` refuses only `fixture-depth-0` (a 9-node walk, exceeds the cap) — rule 3. `depth-1` through `depth-8` each walk ≤8 nodes and load. Confirmed in `dietsetup-rebuild-handover-2026-08-31-cont.md` §2c — a 10-file version of this chain was tried first and refused two fixtures, not one; this 9-file chain is the deliberately narrowed version. |
| `fixture-rule9.json` | A rule with no `requires` (matches every item) whose `effects` list writes `satietyMult` twice. `DietCompiler.CompileEffects`'s `writtenFields` set catches the second write — rule 9, fatal, diet refused. |
| `capacity-fixture.json` | Category derivation with one capacity at exactly 0 (`Fruit`) alongside four nonzero categories (`Protein` 1.0, `Grain` 0.2, `Vegetable` 1.0, `Dairy` 1.0). Exercises `DietCompiler.DeriveCategory`'s zero-capacity special case (`gainScale = 0`, not a division) without tripping rule 8 (not all five are zero) or the rule-11 floor-clamp warning (0 is exempt from the floor check entirely; `0.2` already clears the `CapacityFloor` default of `0.05`). |
| `fixture-tagcheck.json` | `requires: ["meat"]`, `verdict: edible`, `satietyMult: 0.5`, `nutritionMult: 1.0`, default capacities. The tooltip/eat verification pair for phase 3 — holding `game:redmeat-raw` under this diet: tooltip satiety 200 → 100, eaten saturation delta +100. Confirmed in-game, `dietsetup-rebuild-handover-2026-08-31-cont.md` §3. |
| `fixture-tagcheck-spoiled.json` | `requires: ["meat", "spoiled"]` — a second, non-player-facing fixture kept loaded on every reload alongside `fixture-tagcheck.json` so the `spoiled` tag bit is exercised by the multi-tag `requires` mask path even when nothing tests it directly. |
| `task7-damage.json` | `requires: ["meat"]`, effects `damage` (instant, `-2.0`) + `custom` (`dietsetup:debuglog`, a registered handler — no rule-13 warning). Verified in-game: held cooked meat, health 12.9 → 10.9 (`dietsetup-handover.md` Phase 5a). |
| `task7-effects-form.json` | `requires: ["meat", "raw"]`, `satietyMult`/`nutritionMult`/`verdict` all authored via the `effects` list instead of the top-level rule fields. Exercises architecture §7.1: both authoring spellings must fold into the same `CompiledRule` field. Verified in-game: `.dietresolve task7-effects-form` prints `nutritionMult 0.30`, `matched True`, `effects 3` (`dietsetup-handover.md` Phase 5f). |
| `task7-inedible.json` | `requires: ["fruit"]`, `verdict: inedible`, `damage` effect `-1.0`. Exercises the Inedible-as-reaction model (architecture §7.4/§7.5): the eat still completes and the damage effect still fires, only satiety/nutrition are zeroed. Verified in-game: held apple consumed, zero satiety, 1.0 damage (`dietsetup-handover.md` Phase 5c). |
| `task7-verdicts.json` | Two rules — `dairy` → `harmful`, `grain` → `nourishing`, both `satietyMult`/`nutritionMult` 1.0. Exercises that `Harmful`/`Nourishing` are tooltip labels only and don't change feeding math. Verified in-game: both feed normally, no damage (`dietsetup-handover.md` Phase 5d). |

## Not loaded automatically

`src/Rules/DietLoadPipeline.cs` loads diet documents from `ModConfig/dietsetup/diets/`
on this machine (`DietLoadPipeline.cs:231`, via `GetMany`/directory scan under
`GamePaths.ModConfig`), not from anywhere in this repo. That directory is currently
empty. Nothing in `src/` or `Package.ps1` references `test-fixtures/`; putting a file
back under `ModConfig/dietsetup/diets/` — including by copying one back from here —
makes the pipeline load it as a real diet on the next run. To use one of these
fixtures, copy it into that live directory by hand and `/dietreload`.

The original copies also remain at
`VintagestoryData/ModConfig/dietsetup/diets-fixtures-backup-20260903/` on this machine
(outside git) — this directory is the tracked counterpart, not a replacement.
