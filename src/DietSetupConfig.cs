using System;

namespace dietsetup;

/// <summary>
/// Configuration for the Diet Setup mod. Loaded from dietsetup.json in the ModConfig folder.
/// Profile/tag/grant content lives in assets/dietsetup/config/*.json instead (JSON-patchable) --
/// this is pure server-admin toggles, no diet content.
/// </summary>
public class DietSetupConfig
{
    /// <summary>Master toggle. If false, every diet patch is a no-op (falls through to vanilla) and no dialog is ever shown.</summary>
    public bool EnableDietSystem { get; set; } = true;

    /// <summary>If false, new characters are never auto-prompted; the feature becomes purely admin-grant/opt-in via /dietselgrant + /dietsel.</summary>
    public bool AutoPromptNewCharacters { get; set; } = true;

    /// <summary>Profile id assigned to a player before they've ever picked one (and used by "Use Defaults" in the dialog). Must match a registered DietProfile.Id.</summary>
    public string DefaultProfileId { get; set; } = "balanced";

    // ── Rot intake (Phase G3, for rfmechanics' goblin rot aura) ──

    /// <summary>Master toggle for rot-intake accrual (RotIntakeAccrualPatch). If false, every eat is a no-op for the accumulator -- it neither rises nor decays.</summary>
    public bool EnableRotIntakeTracking { get; set; } = true;

    /// <summary>Accumulator delta per qualifying eat, scaled by that eat's TransitionLevel (0..1) -- eating something at TransitionLevel 1.0 (fully spoiled) adds this full amount; fresh food (TransitionLevel 0) adds nothing.</summary>
    public double RotIntakePerBite { get; set; } = 0.08;

    /// <summary>Exponential decay half-life, in in-game calendar hours (world.Calendar.TotalHours), not real-world time -- deliberate deviation from this mod's other timers, chosen because the write side has no tick loop and calendar-hours is already the clock food itself rots on, available on both the write (eat event) and read (rfmechanics sweep) sides at zero extra machinery. Must match rfmechanics' own GoblinRotAuraIntakeHalfLifeHours -- see the cross-reference comment there.</summary>
    public double RotIntakeHalfLifeHours { get; set; } = 48.0;

    /// <summary>Accumulator ceiling.</summary>
    public double RotIntakeCap { get; set; } = 1.0;

    // ── Goblin inverse-freshness satiety curve (Phase G3 companion) ──
    // Inverts vanilla's spoilage satiety penalty for goblins: value RISES as normal food decays,
    // capped at GoblinInverseFreshnessMaxMultiplier at TransitionLevel 1.0. Lives here (not
    // rfmechanics) because it's a satiety-math chokepoint -- every other satiety multiplier
    // (DietSaturationPatch, DietMealNutritionPatch, DietLiquidNutritionPropertiesPatch) already
    // lives in dietsetup -- and because the exemption predicate is expected to grow into a
    // tag-based rule sourced from this mod's own tags.json/itemToTags index (DietProfileRegistry),
    // which is cheapest to consult in-process rather than across the (deliberately unlinked,
    // WatchedAttributes-only) mod boundary with rfmechanics. See GoblinInverseFreshnessSatietyPatch.cs.

    /// <summary>Master toggle for the goblin inverse-freshness satiety curve. If false, goblins
    /// get vanilla's own FoodSpoilageSatLossMul penalty like anyone else.</summary>
    public bool EnableGoblinInverseFreshness { get; set; } = true;

    /// <summary>Trait code identifying the goblin race. Documented cross-reference with
    /// rfmechanics' own RFMechanicsConfig.GoblinTraitCode -- duplicated rather than assembly-linked
    /// (same category of duplication already accepted for RotIntakeHalfLifeHours /
    /// GoblinRotAuraIntakeHalfLifeHours above). Must match rfmechanics' own default
    /// ("rf-goblin-positive") or a goblin recognized by one mod won't be recognized by the other.</summary>
    public string GoblinTraitCode { get; set; } = "rf-goblin-positive";

    /// <summary>Satiety multiplier at TransitionLevel 1.0 (fully rotten, pre-transformation into
    /// game:rot). Setting this to 1.0 pins the curve's output at 1.0 across the whole range --
    /// "no penalty, no bonus" -- a real fallback available without a rebuild.</summary>
    public double GoblinInverseFreshnessMaxMultiplier { get; set; } = 1.75;

    /// <summary>Shape of the ramp from TransitionLevel 0 (multiplier always 1.0) to TransitionLevel
    /// 1 (GoblinInverseFreshnessMaxMultiplier). LateWeighted (default) concentrates the bonus near
    /// full decay (t^2) -- pairs with the rot aura's larder-hold ceiling (rfmechanics'
    /// GoblinRotAuraHoldFraction, currently 0.85) parking carried/nearby food near the top of the
    /// range, which is where the payoff is meant to land.</summary>
    public GoblinInverseFreshnessCurveMode GoblinInverseFreshnessCurve { get; set; } = GoblinInverseFreshnessCurveMode.LateWeighted;

    /// <summary>Wildcard item codes (WildcardUtil.Match, e.g. "game:redmeat-raw" or
    /// "game:cheese-*") exempt from the curve. An exempt stack gets multiplier 1.0 AND keeps
    /// vanilla's own spoilage penalty applied on top -- i.e. it behaves exactly as it would for a
    /// non-goblin. Empty by default. A future design pass will likely replace/extend this with a
    /// tag-based rule sourced from this mod's own tags.json -- the curve funnels every application
    /// through a single AppliesTo predicate specifically so that swap touches one method, not the
    /// curve math.</summary>
    public string[] GoblinInverseFreshnessExemptCodes { get; set; } = Array.Empty<string>();
}

/// <summary>Ramp shape for GoblinInverseFreshnessCurve. See DietSetupConfig.GoblinInverseFreshnessCurve.</summary>
public enum GoblinInverseFreshnessCurveMode
{
    /// <summary>multiplier = 1 + (max - 1) * t</summary>
    Linear,

    /// <summary>multiplier = 1 + (max - 1) * t^2 -- bonus concentrated near full decay.</summary>
    LateWeighted
}
