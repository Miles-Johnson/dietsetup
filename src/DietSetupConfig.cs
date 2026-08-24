using System;

namespace dietsetup;

/// <summary>
/// Configuration for the Diet Setup mod, loaded from dietsetup.json. Profile/tag/grant content
/// lives in assets/dietsetup/config/*.json instead (JSON-patchable) -- this is pure server-admin
/// toggles, no diet content.
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

    /// <summary>Exponential decay half-life on the in-game calendar clock (not real-world time) --
    /// the write side has no tick loop, and calendar-hours is already food's own rot clock, free
    /// on both read and write sides. Must match rfmechanics' GoblinRotAuraIntakeHalfLifeHours exactly.</summary>
    public double RotIntakeHalfLifeHours { get; set; } = 48.0;

    /// <summary>Accumulator ceiling.</summary>
    public double RotIntakeCap { get; set; } = 1.0;

    // ── Goblin inverse-freshness satiety curve (Phase G3 companion) ──
    // Inverts vanilla's spoilage penalty for goblins -- rises as food decays. Lives here, not
    // rfmechanics, because it's a satiety-math chokepoint. Design rationale:
    // notes/dietsetup-patch-internals.md#goblin-inverse-freshness-config--dietsetupconfigcs.

    /// <summary>Master toggle for the goblin inverse-freshness satiety curve. If false, goblins
    /// get vanilla's own FoodSpoilageSatLossMul penalty like anyone else.</summary>
    public bool EnableGoblinInverseFreshness { get; set; } = true;

    /// <summary>Trait code identifying the goblin race. Must match rfmechanics' own
    /// RFMechanicsConfig.GoblinTraitCode default -- duplicated rather than assembly-linked, same
    /// as RotIntakeHalfLifeHours / GoblinRotAuraIntakeHalfLifeHours above.</summary>
    public string GoblinTraitCode { get; set; } = "rf-goblin-positive";

    /// <summary>Satiety multiplier at TransitionLevel 1.0 (fully rotten, pre-transformation into
    /// game:rot). Setting this to 1.0 pins the curve's output at 1.0 across the whole range --
    /// "no penalty, no bonus" -- a real fallback available without a rebuild.</summary>
    public double GoblinInverseFreshnessMaxMultiplier { get; set; } = 1.75;

    /// <summary>Shape of the ramp from TransitionLevel 0 (always 1.0) to 1 (MaxMultiplier).
    /// LateWeighted (default) concentrates the bonus near full decay (t^2) -- pairs with the rot
    /// aura's larder-hold ceiling parking food near the top of the range.</summary>
    public GoblinInverseFreshnessCurveMode GoblinInverseFreshnessCurve { get; set; } = GoblinInverseFreshnessCurveMode.LateWeighted;

    /// <summary>Wildcard item codes (WildcardUtil.Match, e.g. "game:redmeat-raw") exempt from the
    /// curve. An exempt stack gets multiplier 1.0 AND keeps vanilla's own spoilage penalty on top
    /// -- behaves exactly as it would for a non-goblin. Empty by default; routed through a single
    /// AppliesTo predicate so a future tag-based rule touches one method.</summary>
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
