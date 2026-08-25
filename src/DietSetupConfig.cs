using System.Collections.Generic;

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

    /// <summary>Floor applied to each blended "dietsetup:&lt;tag&gt;Mult" entity stat before it
    /// multiplies into GetTagMultiplier's result. Without it, stacked negative trait deltas on a
    /// single tag can blend below 0 (WeightedSum base 1 + deltas), and a negative multiplier
    /// would remove food instead of granting none.</summary>
    public float TagMultiplierFloor { get; set; } = 0f;

    // ── Rot intake (Phase G3, for rfmechanics' goblin rot aura) ──

    /// <summary>Master toggle for rot-intake accrual (RotIntakeAccrualPatch). If false, every eat is a no-op for the accumulator -- it neither rises nor decays.</summary>
    public bool EnableRotIntakeTracking { get; set; } = true;

    /// <summary>Accumulator delta per qualifying eat, scaled by that eat's TransitionLevel (0..1) -- eating something at TransitionLevel 1.0 (fully spoiled) adds this full amount; fresh food (TransitionLevel 0) adds nothing.</summary>
    public double RotIntakePerBite { get; set; } = 0.08;

    /// <summary>Exponential decay half-life on the in-game calendar clock (not real-world time) --
    /// the write side has no tick loop, and calendar-hours is already food's own rot clock, free
    /// on both read and write sides. Keyed by intake tag ("rot" is the only tag written in v1).
    /// The "rot" entry must match rfmechanics' own GoblinRotAuraIntakeHalfLifeHours exactly.</summary>
    public Dictionary<string, double> IntakeHalfLifeHours { get; set; } = new() { ["rot"] = 48.0 };

    /// <summary>Accumulator ceiling.</summary>
    public double RotIntakeCap { get; set; } = 1.0;
}
