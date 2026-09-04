using System;

namespace dietsetup.Rules;

/// <summary>A rule's satiety or nutrition value -- flat, or a curve over the resolve's spoil
/// level (anchors linearly interpolated, clamped flat outside the range). Reintroduced
/// 2026-09-04 after ee2f142 deleted curve capability; DietCompiler now refuses a rule that
/// authors both forms for one field (rule 16) instead of silently preferring the curve.</summary>
public readonly struct CompiledValue
{
    private readonly float flat;
    private readonly CurveAnchor[]? curve;

    private CompiledValue(float flat, CurveAnchor[]? curve)
    {
        this.flat = flat;
        this.curve = curve;
    }

    public static CompiledValue Flat(float value) => new(value, null);

    /// <summary>anchors must already be sorted ascending by Spoil -- DietCompiler sorts before
    /// calling this.</summary>
    public static CompiledValue FromCurve(CurveAnchor[] anchors) => new(0f, anchors);

    public float Evaluate(float spoilLevel)
    {
        if (curve == null || curve.Length == 0) return flat;

        int last = curve.Length - 1;
        if (spoilLevel <= curve[0].Spoil) return curve[0].Value;
        if (spoilLevel >= curve[last].Spoil) return curve[last].Value;

        for (int i = 0; i < last; i++)
        {
            CurveAnchor a = curve[i];
            CurveAnchor b = curve[i + 1];
            if (spoilLevel < a.Spoil || spoilLevel > b.Spoil) continue;

            float t = (spoilLevel - a.Spoil) / (b.Spoil - a.Spoil);
            return a.Value + (b.Value - a.Value) * t;
        }

        return curve[last].Value;
    }

    public bool IsCurve => curve != null && curve.Length > 0;

    // Rule 10's uncovered-category check (DietCompiler.CheckUncoveredCategories) needs "can this
    // rule ever produce nutrition," not a value at one spoil level -- checking anchors is exact
    // for monotonic-ish curves and good enough for a warning-only heuristic.
    public bool CanBePositive => curve == null ? flat > 0f : Array.Exists(curve, a => a.Value > 0f);
}
