namespace dietsetup.Rules;

/// <summary>Answer from DietResolver.Resolve. Satiety/Nutrition are multipliers, not absolute
/// values -- a caller multiplies them into the food's own vanilla value (architecture 5.3's "apply"
/// step). Health is never carried here; it derives from capacity and is never authored (5.1).</summary>
public readonly struct DietResolveResult
{
    public readonly DietVerdict Verdict;
    public readonly float Satiety;
    public readonly float Nutrition;
    public readonly CompiledEffect[] Effects;

    /// <summary>True when an authored rule won; false when nothing matched and the diet's
    /// fallback governed instead. Explicit rather than inferred from values, because a caller
    /// (spoilage resolution) needs to tell "base's neutral fallback" apart from "an authored
    /// rule that happens to also resolve to 1.0" -- the former must leave vanilla's own curve
    /// untouched (architecture 4.4, "vanilla numbers and nothing else"), the latter must not.</summary>
    public readonly bool Matched;

    public DietResolveResult(DietVerdict verdict, float satiety, float nutrition, CompiledEffect[] effects, bool matched)
    {
        Verdict = verdict;
        Satiety = satiety;
        Nutrition = nutrition;
        Effects = effects;
        Matched = matched;
    }
}
